using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class ApacheWizardViewModel : ViewModelBase
    {
        private readonly IApacheUpdateService _updateService;
        private readonly ISettingsService _settingsService;
        private readonly IVersionDetectionService _versionDetectionService;
        private CancellationTokenSource? _cts;

        [ObservableProperty]
        private int _currentStepIndex;

        [ObservableProperty]
        private string _sourcePathOrUrl = string.Empty;

        [ObservableProperty]
        private bool _isLocalSource = true;

        [ObservableProperty]
        private string _preparedIncomingRoot = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private string _currentInstalledVersion = "Detecting...";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private string _incomingDetectedVersion = "Pending...";

        public string VersionTransitionDisplay
        {
            get
            {
                string fromVer = string.IsNullOrWhiteSpace(CurrentInstalledVersion) || CurrentInstalledVersion == "Detecting..."
                    ? "Unknown"
                    : CurrentInstalledVersion;
                string toVer = string.IsNullOrWhiteSpace(IncomingDetectedVersion) || IncomingDetectedVersion == "Pending..."
                    ? "Unknown"
                    : IncomingDetectedVersion;
                return $"Update from {fromVer} to {toVer}";
            }
        }

        [ObservableProperty]
        private ConfigDiffItem? _selectedDiffItem;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isExecuting;

        [ObservableProperty]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isTestMode;

        [ObservableProperty]
        private bool _isTestCompleted;

        [ObservableProperty]
        private bool _isSuccess;

        [ObservableProperty]
        private string _statusMessage = "Ready to start.";

        [ObservableProperty]
        private double _progressPercentage;

        [ObservableProperty]
        private bool _isIndeterminateProgress;

        [ObservableProperty]
        private string _errorMessage = string.Empty;

        public ObservableCollection<ConfigDiffItem> DiffItems { get; } = new();
        public ObservableCollection<string> ExecutionLogs { get; } = new();

        public event Action? RequestClose;

        public ApacheWizardViewModel(
            IApacheUpdateService updateService,
            ISettingsService settingsService,
            IVersionDetectionService versionDetectionService)
        {
            _updateService = updateService;
            _settingsService = settingsService;
            _versionDetectionService = versionDetectionService;

            CurrentStepIndex = 0;
            _ = LoadCurrentVersionAsync();
        }

        public async Task LoadCurrentVersionAsync()
        {
            var settings = _settingsService.CurrentSettings;
            CurrentInstalledVersion = await _versionDetectionService.DetectApacheVersionAsync(settings.Apache.InstallationPath);
        }

        [RelayCommand]
        public void BrowseLocalFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Apache Distribution Archive (.zip or .7z)",
                Filter = "Apache Archives (*.zip;*.7z)|*.zip;*.7z|ZIP Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FileName;
                IsLocalSource = true;
            }
        }

        [RelayCommand]
        public void OpenApacheLoungeUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.apachelounge.com/download/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not open web browser: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task ProcessStep1Async()
        {
            if (string.IsNullOrWhiteSpace(SourcePathOrUrl))
            {
                ErrorMessage = "Please enter a download URL or select a local archive file.";
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            IsIndeterminateProgress = true;
            ProgressPercentage = 10;
            StatusMessage = "Extracting & preparing incoming Apache package...";

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<UpdateProgressReport>(report =>
                {
                    StatusMessage = report.Message;
                    if (report.Percentage > 0)
                    {
                        ProgressPercentage = report.Percentage;
                        IsIndeterminateProgress = report.IsIndeterminate;
                    }
                });

                PreparedIncomingRoot = await _updateService.PrepareIncomingSourceAsync(SourcePathOrUrl, progress, _cts.Token);
                IncomingDetectedVersion = await _versionDetectionService.DetectApacheVersionAsync(PreparedIncomingRoot);

                StatusMessage = "Comparing configuration files...";
                var diffs = await _updateService.InspectConfigurationsAsync(PreparedIncomingRoot);

                DiffItems.Clear();
                foreach (var d in diffs)
                {
                    DiffItems.Add(d);
                }

                if (DiffItems.Count > 0)
                {
                    SelectedDiffItem = DiffItems[0];
                }

                // Proceed to Step 2
                CurrentStepIndex = 1;
                ProgressPercentage = 0;
                IsIndeterminateProgress = false;
                StatusMessage = "Configuration comparison ready.";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to prepare source: {ex.Message}";
                StatusMessage = "Preparation failed.";
            }
            finally
            {
                IsBusy = false;
                IsIndeterminateProgress = false;
            }
        }

        [RelayCommand]
        public void GoBackToStep1()
        {
            CurrentStepIndex = 0;
            ProgressPercentage = 0;
            IsIndeterminateProgress = false;
            ErrorMessage = string.Empty;
        }

        [RelayCommand]
        public void ProceedToStep3()
        {
            CurrentStepIndex = 2;
            ErrorMessage = string.Empty;
            IsCompleted = false;
            IsTestCompleted = false;
            ProgressPercentage = 0;
            IsIndeterminateProgress = false;
            StatusMessage = "Ready to apply update.";
        }

        [RelayCommand]
        public void GoBackToStep2()
        {
            if (IsExecuting || (IsCompleted && !IsTestCompleted)) return;
            CurrentStepIndex = 1;
            ProgressPercentage = 0;
            IsIndeterminateProgress = false;
            ErrorMessage = string.Empty;
        }

        [RelayCommand]
        public async Task ExecuteUpdatePipelineAsync()
        {
            if (IsExecuting) return;

            IsExecuting = true;
            IsBusy = true;
            IsCompleted = false;
            IsTestCompleted = false;
            IsSuccess = false;
            ErrorMessage = string.Empty;
            ExecutionLogs.Clear();
            ProgressPercentage = 0;

            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                StatusMessage = report.Message;
                ProgressPercentage = report.Percentage;
                IsIndeterminateProgress = report.IsIndeterminate;

                string logEntry = $"[{DateTime.Now:HH:mm:ss}] {report.StepTitle}: {report.Message}";
                ExecutionLogs.Add(logEntry);

                if (report.IsError)
                {
                    ErrorMessage = report.Message;
                }
            });

            try
            {
                var diffList = new System.Collections.Generic.List<ConfigDiffItem>(DiffItems);
                bool success = await _updateService.ExecuteUpdatePipelineAsync(PreparedIncomingRoot, diffList, IsTestMode, progress, _cts.Token);

                IsSuccess = success;
                IsCompleted = true;

                if (success)
                {
                    if (IsTestMode)
                    {
                        IsTestCompleted = true;
                        StatusMessage = "Test simulation completed successfully! Ready to re-test or execute real update.";
                    }
                    else
                    {
                        await LoadCurrentVersionAsync();
                        StatusMessage = "Apache updated successfully!";
                    }
                }
                else
                {
                    StatusMessage = "Process encountered errors.";
                }
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                IsCompleted = true;
                ErrorMessage = $"Pipeline exception: {ex.Message}";
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] FATAL ERROR: {ex.Message}");
                StatusMessage = "Process aborted due to an error.";
            }
            finally
            {
                IsExecuting = false;
                IsBusy = false;
                IsIndeterminateProgress = false;
            }
        }

        [RelayCommand]
        public async Task ReTestSimulationAsync()
        {
            IsTestMode = true;
            IsCompleted = false;
            IsTestCompleted = false;
            IsSuccess = false;
            await ExecuteUpdatePipelineAsync();
        }

        [RelayCommand]
        public async Task ExecuteRealUpdateAfterTestAsync()
        {
            IsTestMode = false;
            IsCompleted = false;
            IsTestCompleted = false;
            IsSuccess = false;
            await ExecuteUpdatePipelineAsync();
        }

        [RelayCommand]
        public void CancelOrClose()
        {
            if (IsExecuting)
            {
                _cts?.Cancel();
            }

            if (!string.IsNullOrEmpty(PreparedIncomingRoot))
            {
                string? topTempDir = Directory.GetParent(PreparedIncomingRoot)?.FullName;
                if (!string.IsNullOrEmpty(topTempDir) && topTempDir.Contains("apache_incoming_"))
                {
                    _updateService.CleanupTempDirectory(topTempDir);
                }
                else
                {
                    _updateService.CleanupTempDirectory(PreparedIncomingRoot);
                }
            }

            RequestClose?.Invoke();
        }
    }
}
