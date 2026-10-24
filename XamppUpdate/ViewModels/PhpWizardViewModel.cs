using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        public Func<string, string, bool> ConfirmAction { get; set; } = (msg, title) =>
            MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

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

        partial void OnCurrentInstalledVersionChanged(string value)
        {
            if (!IsExecuting && !IsCompleted)
            {
                BaselineInstalledVersion = value;
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private string _incomingDetectedVersion = "Pending...";

        [ObservableProperty]
        private string _baselineInstalledVersion = string.Empty;

        [ObservableProperty]
        private PhpBuildInfo? _baselineInstalledBuild;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private PhpBuildInfo? _currentPhpBuild;

        partial void OnCurrentPhpBuildChanged(PhpBuildInfo? value)
        {
            if (!IsExecuting && !IsCompleted && value != null)
            {
                BaselineInstalledBuild = value;
                BaselineInstalledVersion = value.Version;
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private PhpBuildInfo? _incomingPhpBuild;

        [ObservableProperty]
        private bool _isIncomingCompatibleWithApache = true;

        public string VersionTransitionDisplay
        {
            get
            {
                string fromVer = BaselineInstalledBuild != null && !string.IsNullOrWhiteSpace(BaselineInstalledBuild.Version) && BaselineInstalledBuild.Version != "Unknown" && !BaselineInstalledBuild.Version.Contains("Not Installed")
                    ? BaselineInstalledBuild.Version
                    : (CurrentPhpBuild != null && !string.IsNullOrWhiteSpace(CurrentPhpBuild.Version) && CurrentPhpBuild.Version != "Unknown" && !CurrentPhpBuild.Version.Contains("Not Installed")
                        ? CurrentPhpBuild.Version
                        : "Current");

                string toVer = IncomingPhpBuild != null && !string.IsNullOrWhiteSpace(IncomingPhpBuild.Version) && IncomingPhpBuild.Version != "Unknown"
                    ? IncomingPhpBuild.Version
                    : (string.IsNullOrWhiteSpace(IncomingDetectedVersion) || IncomingDetectedVersion == "Pending..." ? "Target" : IncomingDetectedVersion);

                if (fromVer == toVer && IsCompleted && IsSuccess && !IsTestMode)
                {
                    return $"PHP successfully updated to v{toVer}";
                }

                return $"Update PHP from v{fromVer} → v{toVer}";
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

        [ObservableProperty]
        private string _uploadProgressStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EnableUploadProgressCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableUploadProgressCommand))]
        private bool _isTogglingUploadProgress;

        private bool CanToggleUploadProgress() => !IsTogglingUploadProgress;

        [ObservableProperty]
        private bool _canSkipBackup;

        [ObservableProperty]
        private bool _skipBackupDuringRealUpdate;

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
            var detected = await _versionDetectionService.DetectPhpBuildInfoAsync(settings.Php.InstallationPath);
            CurrentPhpBuild = detected;
            CurrentInstalledVersion = detected.FullDisplayString;

            if (BaselineInstalledBuild == null || string.IsNullOrWhiteSpace(BaselineInstalledVersion))
            {
                BaselineInstalledBuild = detected;
                BaselineInstalledVersion = detected.Version;
                OnPropertyChanged(nameof(VersionTransitionDisplay));
            }
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

        /// <summary>Returns the php.ini path from settings, falling back to the PHP installation folder.</summary>
        private string ResolvePhpIniPath()
        {
            var settings = _settingsService.CurrentSettings;
            string[] candidates =
            {
                settings.Php.IniPath,
                Path.Combine(settings.Php.InstallationPath, "php.ini")
            };

            string? existing = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
            return existing ?? candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "php.ini";
        }

        [RelayCommand]
        public async Task OpenPhpIniAsync()
        {
            try
            {
                string iniPath = ResolvePhpIniPath();
                if (!File.Exists(iniPath))
                {
                    UploadProgressStatus = string.Empty;
                    ErrorMessage = $"php.ini not found at '{iniPath}'.";
                    return;
                }

                string editor = await Task.Run(ComposerViewModel.ResolveTextEditorExecutable);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = $"\"{iniPath}\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = $"\"{iniPath}\"",
                        UseShellExecute = true
                    });
                    editor = "notepad.exe";
                }

                ErrorMessage = string.Empty;
                UploadProgressStatus = $"Opened php.ini using {Path.GetFileNameWithoutExtension(editor)}: {iniPath}";
            }
            catch (Exception ex)
            {
                UploadProgressStatus = string.Empty;
                ErrorMessage = $"Could not open php.ini: {ex.Message}";
            }
        }

        [RelayCommand(CanExecute = nameof(CanToggleUploadProgress))]
        public async Task EnableUploadProgressAsync()
        {
            await SetUploadProgressAsync(true);
        }

        [RelayCommand(CanExecute = nameof(CanToggleUploadProgress))]
        public async Task DisableUploadProgressAsync()
        {
            await SetUploadProgressAsync(false);
        }

        public async Task SetUploadProgressAsync(bool enable)
        {
            // Serialize Enable/Disable: both menu items disable while one write is in flight.
            if (IsTogglingUploadProgress) return;

            IsTogglingUploadProgress = true;
            try
            {
                string iniPath = ResolvePhpIniPath();
                if (!File.Exists(iniPath))
                {
                    UploadProgressStatus = string.Empty;
                    ErrorMessage = $"php.ini not found at '{iniPath}'.";
                    return;
                }

                try
                {
                    var result = await PhpIniSessionSettings.ApplyAsync(iniPath, enable);

                    string state = enable ? "enabled (= 1)" : "disabled (= 0)";
                    string detail = result.DirectiveWasCommented
                        ? (enable
                            ? " The commented directive was re-enabled."
                            : " The commented directive was uncommented and set to 0.")
                        : result.DirectiveWasAppended
                            ? " The directive was added to php.ini."
                            : string.Empty;

                    UploadProgressStatus = result.Changed
                        ? $"session.upload_progress.enabled is now {state}.{detail} Restart Apache for the change to take effect."
                        : $"session.upload_progress.enabled was already {state}.{detail}";
                    ErrorMessage = string.Empty;
                }
                catch (UnauthorizedAccessException)
                {
                    UploadProgressStatus = string.Empty;
                    ErrorMessage = $"Access denied while writing '{iniPath}'. Run {AppInfo.AppName} as administrator and try again.";
                }
                catch (Exception ex)
                {
                    UploadProgressStatus = string.Empty;
                    ErrorMessage = $"Could not update upload_progress: {ex.Message}";
                }
            }
            finally
            {
                IsTogglingUploadProgress = false;
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
                bool shouldSkipBackup = !IsTestMode && CanSkipBackup && SkipBackupDuringRealUpdate;
                bool success = await _updateService.ExecuteUpdatePipelineAsync(PreparedIncomingRoot, diffList, IsTestMode, shouldSkipBackup, progress, _cts.Token);

                IsSuccess = success;
                IsCompleted = true;

                if (success)
                {
                    if (IsTestMode)
                    {
                        IsTestCompleted = true;
                        CanSkipBackup = true;
                        StatusMessage = "Simulation completed successfully! Ready for real update.";
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
            if (IsExecuting) return;

            bool confirmed = ConfirmAction(
                "Are you sure you want to proceed with the REAL PHP Update?\n\n" +
                "• Your existing PHP directory will be backed up to a .zip archive.\n" +
                "• Apache service will be stopped, new PHP binaries will be installed, and configuration applied.\n\n" +
                "Do you want to proceed?",
                "Confirm Real PHP Update");

            if (!confirmed)
            {
                StatusMessage = "PHP update cancelled by user.";
                return;
            }

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
        public void CopyExecutionLogs()
        {
            if (ExecutionLogs.Count == 0) return;
            try
            {
                string text = string.Join(Environment.NewLine, ExecutionLogs);
                Clipboard.SetText(text);
            }
            catch { }
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
