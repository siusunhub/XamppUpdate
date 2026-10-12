using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XamppUpdate.Services.Uat;

namespace XamppUpdate.ViewModels
{
    public sealed class UatStageRow : ObservableObject
    {
        private UatStageStatus _status = UatStageStatus.Pending;
        private string _message = string.Empty;

        public int Number { get; init; }
        public string Name { get; init; } = string.Empty;
        public UatStageStatus Status { get => _status; set => SetProperty(ref _status, value); }
        public string Message { get => _message; set => SetProperty(ref _message, value); }
    }

    public partial class UatUpgradeViewModel : ObservableObject
    {
        private readonly IUatUpgradeService _service;
        private readonly UatOptions _defaults;
        private CancellationTokenSource? _cts;

        public UatUpgradeViewModel(IUatUpgradeService service, UatOptions defaults)
        {
            _service = service;
            _defaults = defaults;
            _targetRoot = defaults.TargetRoot;
            _port = defaults.Port;
            _testAsService = defaults.TestAsService;
            _serviceName = defaults.ServiceName;
            _adminUser = defaults.AdminUser;
            _adminPassword = defaults.AdminPassword;
        }

        public ObservableCollection<UatStageRow> Stages { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        [ObservableProperty] private string _targetRoot = string.Empty;
        [ObservableProperty] private int _port = 13306;
        [ObservableProperty] private bool _testAsService = true;
        [ObservableProperty] private string _serviceName = "mysql_uat";
        [ObservableProperty] private string _adminUser = "root";
        [ObservableProperty] private string _adminPassword = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(DeleteCopyCommand))]
        private bool _isRunning;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeleteCopyCommand))]
        private UatReport? _lastReport;

        [ObservableProperty] private string _summary = string.Empty;

        /// <summary>True when a finished run owns folders that "Delete UAT copy" may remove.</summary>
        public bool CanDeleteCopy => !IsRunning && LastReport is { OwnedPaths.Count: > 0 };

        partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanDeleteCopy));
        partial void OnLastReportChanged(UatReport? value) => OnPropertyChanged(nameof(CanDeleteCopy));

        private bool CanRun() => !IsRunning;
        private bool CanCancel() => IsRunning && _cts is { IsCancellationRequested: false };
        private bool CanDelete() => CanDeleteCopy;

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task RunAsync()
        {
            IsRunning = true;
            LastReport = null;
            Stages.Clear();
            LogLines.Clear();
            Summary = string.Empty;

            var options = _defaults with
            {
                TargetRoot = TargetRoot?.Trim() ?? string.Empty,
                Port = Port,
                StopProductionFirst = true,       // Phase 1: cold copy only
                TestAsService = TestAsService,
                ServiceName = ServiceName?.Trim() ?? string.Empty,
                AdminUser = AdminUser,
                AdminPassword = AdminPassword
            };

            _cts = new CancellationTokenSource();
            var progress = new Progress<UatEvent>(e =>
            {
                var row = Stages.FirstOrDefault(s => s.Number == e.StageNumber);
                if (row == null)
                {
                    row = new UatStageRow { Number = e.StageNumber, Name = e.StageName };
                    Stages.Add(row);
                }
                row.Status = e.Status;
                row.Message = e.Message;
                LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {e.StageName}: {e.Status} {e.Message}".TrimEnd());
            });

            try
            {
                var report = await _service.RunAsync(options, progress, _cts.Token);

                // Progress<T> callbacks are posted asynchronously; reconcile from the final report so the list is complete.
                foreach (var s in report.Stages)
                {
                    var row = Stages.FirstOrDefault(r => r.Number == s.Number);
                    if (row == null)
                    {
                        row = new UatStageRow { Number = s.Number, Name = s.Name };
                        Stages.Add(row);
                    }
                    row.Status = s.Status;
                    row.Message = s.Message;
                }

                LastReport = report;
                Summary = report.Success
                    ? "UAT upgrade SUCCEEDED. Production was restored to its original state."
                    : "UAT upgrade FAILED. See the failing stage. Production state: " +
                      (report.ProductionRestored ? "restored." : "NOT restored - " + report.RecoveryInstructions);
            }
            catch (Exception ex)
            {
                Summary = "UAT run aborted: " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel()
        {
            if (_cts is { IsCancellationRequested: false })
            {
                _cts.Cancel();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand(CanExecute = nameof(CanDelete))]
        private async Task DeleteCopyAsync()
        {
            if (LastReport == null) return;

            IsRunning = true;
            try
            {
                var progress = new Progress<string>(m => LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {m}".TrimEnd()));
                bool ok = await _service.DeleteCopyAsync(LastReport, progress);

                if (ok)
                {
                    LastReport.OwnedPaths.Clear();
                    Summary = "UAT copy was deleted.";
                }
                else
                {
                    Summary = "UAT copy deletion was refused or failed (see log).";
                }
            }
            catch (Exception ex)
            {
                LogLines.Add($"[{DateTime.Now:HH:mm:ss}] Delete failed: {ex.Message}".TrimEnd());
                Summary = "UAT copy deletion failed: " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                OnPropertyChanged(nameof(CanDeleteCopy));
                DeleteCopyCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// Everything useful for a bug report in one plain-text block: summary, per-stage status and message,
        /// the live log, the report's notes (command lines / exit codes) and recovery text.
        /// The admin password is redacted. Pure (no clipboard access) so it can be unit-tested.
        /// </summary>
        public string BuildLogText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"UAT upgrade log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            string serviceState = TestAsService ? "checked" : "unchecked";
            sb.AppendLine($"Target: {TargetRoot}   Port: {Port}   Test as service: {serviceState} ({ServiceName})   User: {AdminUser}");
            if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine($"Summary: {Summary}");

            if (Stages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Stages ==");
                foreach (var s in Stages.OrderBy(s => s.Number))
                {
                    sb.AppendLine($"[{s.Status}] {s.Name}");
                    if (!string.IsNullOrWhiteSpace(s.Message)) sb.AppendLine($"    {s.Message}");
                }
            }

            if (LogLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Log ==");
                foreach (var line in LogLines.ToArray()) sb.AppendLine(line);
            }

            var report = LastReport;
            if (report != null)
            {
                string[] notes;
                try { notes = report.Notes.ToArray(); } catch { notes = Array.Empty<string>(); }

                if (notes.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("== Report notes (commands, exit codes, service guidance) ==");
                    foreach (var n in notes) sb.AppendLine(n);
                }

                sb.AppendLine();
                sb.AppendLine($"Production state: {report.ProductionState}; restored: {report.ProductionRestored}");
                if (!string.IsNullOrWhiteSpace(report.RecoveryInstructions))
                    sb.AppendLine($"RECOVERY: {report.RecoveryInstructions}");
                if (report.OwnedPaths.Count > 0)
                    sb.AppendLine("Owned paths: " + string.Join("; ", report.OwnedPaths));
                if (!string.IsNullOrEmpty(report.OwnedServiceName))
                    sb.AppendLine("Owned service: " + report.OwnedServiceName);
            }

            string text = sb.ToString();
            if (!string.IsNullOrWhiteSpace(AdminPassword))
            {
                text = text.Replace(AdminPassword, "******");
            }
            return text;
        }

        [RelayCommand]
        private void OpenFolder()
        {
            if (!string.IsNullOrWhiteSpace(TargetRoot) && Directory.Exists(TargetRoot))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = TargetRoot,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }
    }
}
