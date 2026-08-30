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
    public partial class PhpWizardViewModel : ViewModelBase
    {
        private readonly IPhpUpdateService _updateService;
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

        [ObservableProperty]
        private PhpBuildInfo? _currentPhpBuild;

        [ObservableProperty]
        private PhpBuildInfo? _incomingPhpBuild;

        [ObservableProperty]
        private bool _isIncomingCompatibleWithApache = true;

        public string VersionTransitionDisplay
        {
            get
            {
                string fromVer = CurrentPhpBuild != null && !string.IsNullOrWhiteSpace(CurrentPhpBuild.Version) && CurrentPhpBuild.Version != "Unknown"
                    ? $"PHP {CurrentPhpBuild.FullDisplayString}"
                    : (string.IsNullOrWhiteSpace(CurrentInstalledVersion) || CurrentInstalledVersion == "Detecting..." ? "Unknown" : CurrentInstalledVersion);

                string toVer = IncomingPhpBuild != null && !string.IsNullOrWhiteSpace(IncomingPhpBuild.Version) && IncomingPhpBuild.Version != "Unknown"
                    ? $"PHP {IncomingPhpBuild.FullDisplayString}"
                    : (string.IsNullOrWhiteSpace(IncomingDetectedVersion) || IncomingDetectedVersion == "Pending..." ? "Unknown" : IncomingDetectedVersion);

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

        public PhpWizardViewModel(
            IPhpUpdateService updateService,
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
            CurrentPhpBuild = await _versionDetectionService.DetectPhpBuildInfoAsync(settings.Php.InstallationPath);
            CurrentInstalledVersion = CurrentPhpBuild.FullDisplayString;
        }

        [RelayCommand]
        public void BrowseLocalFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select PHP Distribution Archive (.zip or .7z)",
                Filter = "PHP Archives (*.zip;*.7z)|*.zip;*.7z|ZIP Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FileName;
                IsLocalSource = true;
            }
        }

        [RelayCommand]
        public void BrowseLocalFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Pre-Extracted PHP Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FolderName;
                IsLocalSource = true;
            }
        }

        [RelayCommand]
        public void OpenPhpOfficialUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.php.net",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not open browser: {ex.Message}";
            }
        }

        [RelayCommand]
        public void OpenPhpDownloadUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://windows.php.net/download/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not open browser: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task Step1NextAsync()
        {
            if (string.IsNullOrWhiteSpace(SourcePathOrUrl))
            {
                ErrorMessage = "Please provide a valid PHP archive path or URL.";
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            StatusMessage = "Extracting and inspecting PHP distribution...";
            ProgressPercentage = 0;
            IsIndeterminateProgress = true;
            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                StatusMessage = report.Message;
                ProgressPercentage = report.Percentage;
                IsIndeterminateProgress = report.IsIndeterminate;
            });

            try
            {
                PreparedIncomingRoot = await _updateService.PrepareIncomingSourceAsync(SourcePathOrUrl, progress, _cts.Token);
                IncomingPhpBuild = await _versionDetectionService.DetectPhpBuildInfoAsync(PreparedIncomingRoot);
                IncomingDetectedVersion = IncomingPhpBuild.FullDisplayString;
                IsIncomingCompatibleWithApache = IncomingPhpBuild.IsApacheCompatible;

                // Load config diffs
                StatusMessage = "Comparing PHP configuration files (php.ini)...";
                DiffItems.Clear();
                var diffs = await _updateService.InspectConfigurationsAsync(PreparedIncomingRoot);

                foreach (var item in diffs)
                {
                    DiffItems.Add(item);
                }

                if (DiffItems.Count > 0)
                {
                    SelectedDiffItem = DiffItems[0];
                }

                CurrentStepIndex = 1;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Extraction cancelled.";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to prepare package: {ex.Message}";
                StatusMessage = "Preparation failed.";
            }
            finally
            {
                IsBusy = false;
                IsIndeterminateProgress = false;
            }
        }

        [RelayCommand]
        public void Step2Back()
        {
            CurrentStepIndex = 0;
        }

        [RelayCommand]
        public void Step2Next()
        {
            CurrentStepIndex = 2;
        }

        [RelayCommand]
        public void Step3Back()
        {
            if (!IsExecuting)
            {
                CurrentStepIndex = 1;
            }
        }

        public async Task ExecuteUpdatePipelineAsync()
        {
            if (IsExecuting) return;

            IsExecuting = true;
            IsBusy = true;
            ErrorMessage = string.Empty;
            ExecutionLogs.Clear();
            ProgressPercentage = 0;
            IsIndeterminateProgress = true;
            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                StatusMessage = report.Message;
                ProgressPercentage = report.Percentage;
                IsIndeterminateProgress = report.IsIndeterminate;

                if (!string.IsNullOrWhiteSpace(report.Message))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {report.Message}");
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
                        StatusMessage = "PHP updated successfully! Apache restarted cleanly.";
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
        public async Task RunTestModeSimulationAsync()
        {
            IsTestMode = true;
            IsCompleted = false;
            IsTestCompleted = false;
            IsSuccess = false;
            await ExecuteUpdatePipelineAsync();
        }

        [RelayCommand]
        public async Task ExecuteRealUpdateAsync()
        {
            IsTestMode = false;
            IsCompleted = false;
            IsTestCompleted = false;
            IsSuccess = false;
            await ExecuteUpdatePipelineAsync();
        }

        [RelayCommand]
        public async Task ReTestSimulationAsync()
        {
            await RunTestModeSimulationAsync();
        }

        [RelayCommand]
        public async Task ExecuteRealUpdateAfterTestAsync()
        {
            await ExecuteRealUpdateAsync();
        }

        [RelayCommand]
        public void CancelOrClose()
        {
            if (IsExecuting)
            {
                _cts?.Cancel();
                StatusMessage = "Cancelling operations...";
                return;
            }

            if (!string.IsNullOrWhiteSpace(PreparedIncomingRoot))
            {
                _updateService.CleanupTempDirectory(PreparedIncomingRoot);
            }

            RequestClose?.Invoke();
        }
    }
}
