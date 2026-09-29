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
    public partial class PhpMyAdminWizardViewModel : ViewModelBase
    {
        private readonly IPhpMyAdminUpdateService _updateService;
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

        public string VersionTransitionDisplay
        {
            get
            {
                string fromVer = !string.IsNullOrWhiteSpace(BaselineInstalledVersion) && BaselineInstalledVersion != "Detecting..." && !BaselineInstalledVersion.Contains("Not Installed")
                    ? BaselineInstalledVersion
                    : (!string.IsNullOrWhiteSpace(CurrentInstalledVersion) && CurrentInstalledVersion != "Detecting..." ? CurrentInstalledVersion : "Current");

                string toVer = !string.IsNullOrWhiteSpace(IncomingDetectedVersion) && IncomingDetectedVersion != "Pending..."
                    ? IncomingDetectedVersion
                    : "Target";

                if (fromVer == toVer && IsCompleted && IsSuccess && !IsTestMode)
                {
                    return $"phpMyAdmin successfully updated to v{toVer}";
                }

                return $"Update phpMyAdmin from v{fromVer} → v{toVer}";
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
        private bool _canSkipBackup;

        [ObservableProperty]
        private bool _skipBackupDuringRealUpdate;

        public ObservableCollection<ConfigDiffItem> DiffItems { get; } = new();
        public ObservableCollection<string> ExecutionLogs { get; } = new();

        public event Action? RequestClose;

        public PhpMyAdminWizardViewModel(
            IPhpMyAdminUpdateService updateService,
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
            CurrentInstalledVersion = await _versionDetectionService.DetectPhpMyAdminVersionAsync(settings.PhpMyAdmin.InstallationPath);
            if (string.IsNullOrWhiteSpace(BaselineInstalledVersion) || BaselineInstalledVersion == "Detecting...")
            {
                BaselineInstalledVersion = CurrentInstalledVersion;
                OnPropertyChanged(nameof(VersionTransitionDisplay));
            }
        }

        [RelayCommand]
        public void BrowseLocalFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select phpMyAdmin Archive Package",
                Filter = "Archive Files (*.zip;*.7z;*.tar.gz)|*.zip;*.7z;*.tar.gz|All Files (*.*)|*.*"
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
                Title = "Select Pre-Extracted phpMyAdmin Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FolderName;
                IsLocalSource = true;
            }
        }

        [RelayCommand]
        public void OpenPhpMyAdminOfficialUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.phpmyadmin.net/downloads/",
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
                ErrorMessage = "Please provide a valid phpMyAdmin archive path or URL.";
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            StatusMessage = "Extracting and inspecting phpMyAdmin package...";
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
                IncomingDetectedVersion = await _versionDetectionService.DetectPhpMyAdminVersionAsync(PreparedIncomingRoot);

                // Load config diffs
                StatusMessage = "Comparing phpMyAdmin configuration files (config.inc.php)...";
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
                        StatusMessage = "phpMyAdmin updated successfully!";
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
                "Are you sure you want to proceed with the REAL phpMyAdmin Update?\n\n" +
                "• Your existing phpMyAdmin directory will be backed up to a .zip archive.\n" +
                "• phpMyAdmin files will be updated, preserving config.inc.php settings.\n\n" +
                "Do you want to proceed?",
                "Confirm Real phpMyAdmin Update");

            if (!confirmed)
            {
                StatusMessage = "phpMyAdmin update cancelled by user.";
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
        public async Task OpenConfigFileAsync()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                string configPath = Path.Combine(settings.PhpMyAdmin.InstallationPath, settings.PhpMyAdmin.ConfigFile);
                if (!File.Exists(configPath))
                {
                    configPath = Path.Combine(settings.PhpMyAdmin.InstallationPath, "config.inc.php");
                }

                if (File.Exists(configPath))
                {
                    string editor = await Task.Run(() => ComposerViewModel.ResolveTextEditorExecutable());
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = $"\"{configPath}\"",
                        UseShellExecute = true
                    });
                }
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
                try
                {
                    string tempBase = _settingsService.CurrentSettings.General.ResolvedTempDirectory;
                    if (PreparedIncomingRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
                    {
                        string topTempDir = PreparedIncomingRoot;
                        while (topTempDir.Length > tempBase.Length && Path.GetDirectoryName(topTempDir) != null && !Path.GetDirectoryName(topTempDir)!.Equals(tempBase, StringComparison.OrdinalIgnoreCase))
                        {
                            topTempDir = Path.GetDirectoryName(topTempDir)!;
                        }
                        _updateService.CleanupTempDirectory(topTempDir);
                    }
                    else
                    {
                        _updateService.CleanupTempDirectory(PreparedIncomingRoot);
                    }
                }
                catch { }
            }

            RequestClose?.Invoke();
        }
    }
}
