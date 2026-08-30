using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class ComposerViewModel : ObservableObject
    {
        private readonly IComposerService _composerService;
        private readonly ISettingsService _settingsService;
        private CancellationTokenSource? _cts;

        [ObservableProperty]
        private string _composerExecutablePath = string.Empty;

        [ObservableProperty]
        private string _workingDirectory = string.Empty;

        [ObservableProperty]
        private string _composerVersion = "Detecting...";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isExecuting;

        [ObservableProperty]
        private bool _isTestMode;

        [ObservableProperty]
        private string _statusMessage = "Ready.";

        public ObservableCollection<string> ExecutionLogs { get; } = new();

        public event Action? RequestClose;

        public ComposerViewModel(IComposerService composerService, ISettingsService settingsService)
        {
            _composerService = composerService;
            _settingsService = settingsService;

            var settings = _settingsService.CurrentSettings;
            ComposerExecutablePath = settings.Composer.ExecutablePath;

            // Pick reasonable working directory default
            if (!string.IsNullOrWhiteSpace(settings.Composer.WwwTargetPath) && Directory.Exists(settings.Composer.WwwTargetPath))
            {
                WorkingDirectory = settings.Composer.WwwTargetPath;
            }
            else if (!string.IsNullOrWhiteSpace(settings.Composer.DownloadTargetPath) && Directory.Exists(settings.Composer.DownloadTargetPath))
            {
                WorkingDirectory = settings.Composer.DownloadTargetPath;
            }
            else if (Directory.Exists(@"C:\xampp\htdocs"))
            {
                WorkingDirectory = @"C:\xampp\htdocs";
            }

            _ = RefreshVersionAsync();
        }

        public async Task RefreshVersionAsync()
        {
            if (string.IsNullOrWhiteSpace(ComposerExecutablePath))
            {
                ComposerVersion = "Not Configured";
                return;
            }

            ComposerVersion = "Detecting...";
            ComposerVersion = await _composerService.DetectVersionAsync(ComposerExecutablePath);
        }

        [RelayCommand]
        public void BrowseComposerExecutable()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Composer Executable (composer.bat or composer.phar)",
                Filter = "Composer Executables (*.bat;*.phar;*.exe;*.cmd)|*.bat;*.phar;*.exe;*.cmd|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ComposerExecutablePath = dialog.FileName;
                _ = RefreshVersionAsync();
            }
        }

        [RelayCommand]
        public void BrowseWorkingDirectory()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Project Working Directory (Folder with composer.json)",
                InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : @"C:\xampp\htdocs"
            };

            if (dialog.ShowDialog() == true)
            {
                WorkingDirectory = dialog.FolderName;
            }
        }

        [RelayCommand]
        public async Task RunSelfUpdateAsync()
        {
            if (IsBusy) return;

            PrepareExecution("Composer Self-Update");
            var progress = CreateProgressReporter();

            try
            {
                bool success = await _composerService.RunSelfUpdateAsync(ComposerExecutablePath, progress, _cts!.Token);
                StatusMessage = success ? "Composer self-update completed successfully!" : "Composer self-update reported warnings/errors.";
                await RefreshVersionAsync();
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Self-update failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public async Task RunCheckOutdatedAsync()
        {
            if (IsBusy) return;

            PrepareExecution("Check New Components (composer outdated)");
            var progress = CreateProgressReporter();

            try
            {
                bool success = await _composerService.RunCheckOutdatedAsync(ComposerExecutablePath, WorkingDirectory, progress, _cts!.Token);
                StatusMessage = success ? "Component inspection completed." : "Component inspection finished with warnings/errors.";
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Check outdated failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public async Task RunPackageUpdateAsync()
        {
            if (IsBusy) return;

            string opName = IsTestMode ? "Package Update Simulation (dry-run)" : "Live Package Update & Vendor Backup";
            PrepareExecution(opName);
            var progress = CreateProgressReporter();

            try
            {
                bool success = await _composerService.RunUpdatePackagesAsync(ComposerExecutablePath, WorkingDirectory, IsTestMode, progress, _cts!.Token);
                if (success)
                {
                    StatusMessage = IsTestMode
                        ? "Dry-run simulation completed successfully! No files were changed."
                        : "Packages updated successfully! Vendor backup preserved.";
                }
                else
                {
                    StatusMessage = "Package update reported warnings/errors.";
                }
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Package update failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public void ClearLogs()
        {
            ExecutionLogs.Clear();
            StatusMessage = "Logs cleared.";
        }

        [RelayCommand]
        public void CopyLogs()
        {
            try
            {
                string text = string.Join(Environment.NewLine, ExecutionLogs);
                Clipboard.SetText(text);
                StatusMessage = "Logs copied to clipboard!";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not copy logs: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CancelOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusMessage = "Cancelling operation...";
            }
        }

        [RelayCommand]
        public void CloseWindow()
        {
            RequestClose?.Invoke();
        }

        private void PrepareExecution(string operationName)
        {
            IsBusy = true;
            IsExecuting = true;
            StatusMessage = $"Running: {operationName}...";
            ExecutionLogs.Add($"==================================================================");
            ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] STARTING: {operationName}");
            ExecutionLogs.Add($"==================================================================");
            _cts = new CancellationTokenSource();
        }

        private void EndExecution()
        {
            IsBusy = false;
            IsExecuting = false;
            _cts?.Dispose();
            _cts = null;
        }

        private IProgress<string> CreateProgressReporter()
        {
            return new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
                }
            });
        }
    }
}
