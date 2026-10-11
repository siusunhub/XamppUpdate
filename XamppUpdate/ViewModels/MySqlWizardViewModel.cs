using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
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
    public partial class MySqlWizardViewModel : ViewModelBase
    {
        private readonly IMySqlUpdateService _updateService;
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
        private string _sourceInstalledVersion = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        [NotifyPropertyChangedFor(nameof(IsMariaDb))]
        [NotifyPropertyChangedFor(nameof(IsMySql))]
        [NotifyPropertyChangedFor(nameof(EngineDisplayName))]
        [NotifyPropertyChangedFor(nameof(ShowMariaDbDownload))]
        [NotifyPropertyChangedFor(nameof(ShowMySqlDownload))]
        private string _currentInstalledVersion = "Detecting...";

        partial void OnCurrentInstalledVersionChanged(string value)
        {
            if (!IsExecuting && !IsCompleted)
            {
                SourceInstalledVersion = value;
            }
        }

        public bool IsMariaDb =>
            !string.IsNullOrWhiteSpace(CurrentInstalledVersion) &&
            CurrentInstalledVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);

        public string EngineDisplayName => IsMariaDb ? "MariaDB Foundation" : "MySQL Community";

        public bool IsMySql =>
            !string.IsNullOrWhiteSpace(CurrentInstalledVersion) &&
            !CurrentInstalledVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) &&
            CurrentInstalledVersion != "Detecting..." &&
            CurrentInstalledVersion != "Not Installed / Path Invalid" &&
            CurrentInstalledVersion != "Executable Not Found" &&
            CurrentInstalledVersion != "Unknown Version";

        public bool ShowMariaDbDownload => IsMariaDb || (!IsMySql && !IsMariaDb);
        public bool ShowMySqlDownload => IsMySql || (!IsMySql && !IsMariaDb);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionTransitionDisplay))]
        private string _incomingDetectedVersion = "Pending...";

        public string VersionTransitionDisplay
        {
            get
            {
                string fromVer = !string.IsNullOrWhiteSpace(SourceInstalledVersion) && SourceInstalledVersion != "Detecting..."
                    ? SourceInstalledVersion
                    : (string.IsNullOrWhiteSpace(CurrentInstalledVersion) || CurrentInstalledVersion == "Detecting..." ? "Unknown" : CurrentInstalledVersion);

                string toVer = string.IsNullOrWhiteSpace(IncomingDetectedVersion) || IncomingDetectedVersion == "Pending..."
                    ? "Unknown"
                    : IncomingDetectedVersion;

                string engineName = IsMariaDb ? "MariaDB" : "MySQL";
                return $"Update {engineName} from {fromVer} to {toVer}";
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
        private string _statusMessage = string.Empty;

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
        public ObservableCollection<string> UpgradeLogs { get; } = new();

        [ObservableProperty]
        private string _mySqlUpgradeToolPath = string.Empty;

        [ObservableProperty]
        private string _mySqlUpgradeUser = "root";

        [ObservableProperty]
        private string _mySqlUpgradePassword = string.Empty;

        [ObservableProperty]
        private string _mySqlUpgradeArguments = "-h 127.0.0.1 --force --verbose";

        [ObservableProperty]
        private bool _isUpgradingDatabase;

        [ObservableProperty]
        private bool _isUpgradeSuccess;

        [ObservableProperty]
        private string _upgradeErrorMessage = string.Empty;

        [ObservableProperty]
        private string _upgradeStatusMessage = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceInstallButtonText))]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        private bool _isServiceInstalled;

        [ObservableProperty]
        private ServiceStatus _serviceStatus = ServiceStatus.Unknown;

        [ObservableProperty]
        private string _serviceStatusDisplay = "Checking...";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        [NotifyPropertyChangedFor(nameof(CanStartService))]
        [NotifyPropertyChangedFor(nameof(CanStopService))]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isServiceRunning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartService))]
        [NotifyPropertyChangedFor(nameof(CanStopService))]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isOperatingService;

        /// <summary>True while the Windows service is in a Start/Stop/Pause/Continue pending state.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isServiceTransitioning;

        /// <summary>Install/Remove Service is only allowed in the fully stopped state.</summary>
        public bool CanToggleServiceInstall => !IsOperatingService && !IsServiceRunning && !IsServiceTransitioning;

        // Start is only meaningful when nothing is running; Stop only when something is.
        public bool CanStartService => !IsOperatingService && !IsServiceRunning;
        public bool CanStopService => !IsOperatingService && IsServiceRunning;

        /// <summary>
        /// True when Start/Stop must go through the Windows service. False when the service is not installed,
        /// or it is installed but stopped while a standalone mysqld/mariadbd process is running.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        [NotifyPropertyChangedFor(nameof(ServiceStartButtonText))]
        [NotifyPropertyChangedFor(nameof(ServiceStopButtonText))]
        private bool _useServiceControl;

        private System.Windows.Threading.DispatcherTimer? _statusTimer;
        private bool _isRefreshingStatus;

        /// <summary>Which mysqld/mariadbd process was detected (empty when none); shown as a tooltip on the status badge.</summary>
        [ObservableProperty]
        private string _processDetail = string.Empty;

        public string ServiceInstallButtonText => IsServiceInstalled ? "Remove Service" : "Install Service";

        public string ServiceRunButtonText => IsServiceRunning
            ? (UseServiceControl ? "Stop Service" : "Stop Process")
            : (UseServiceControl ? "Start Service" : "Start Process");

        // Labels for the separate Start / Stop buttons on the database step.
        public string ServiceStartButtonText => UseServiceControl ? "▶️ Start Service" : "▶️ Start Process";
        public string ServiceStopButtonText => UseServiceControl ? "⏹️ Stop Service" : "⏹️ Stop Process";

        public event Action? RequestClose;

        private readonly IWindowsServiceManager _serviceManager;

        public MySqlWizardViewModel(
            IMySqlUpdateService updateService,
            ISettingsService settingsService,
            IVersionDetectionService versionDetectionService,
            IWindowsServiceManager? serviceManager = null)
        {
            _updateService = updateService;
            _settingsService = settingsService;
            _versionDetectionService = versionDetectionService;
            _serviceManager = serviceManager ?? new WindowsServiceManager();

            CurrentStepIndex = 0;
            _ = LoadCurrentVersionAsync();
            _ = RefreshServiceStatusAsync();
            StartStatusMonitor();
        }

        public async Task LoadCurrentVersionAsync()
        {
            var settings = _settingsService.CurrentSettings;
            string detected = await _versionDetectionService.DetectMySqlVersionAsync(settings.MySql.InstallationPath);
            CurrentInstalledVersion = detected;

            if (string.IsNullOrWhiteSpace(SourceInstalledVersion))
            {
                SourceInstalledVersion = detected;
            }
        }

        [RelayCommand]
        public void BrowseLocalFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select MySQL / MariaDB Distribution Archive (.zip or .7z)",
                Filter = "Database Archives (*.zip;*.7z)|*.zip;*.7z|ZIP Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z|All Files (*.*)|*.*"
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
                Title = "Select Pre-Extracted MySQL / MariaDB Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FolderName;
                IsLocalSource = true;
            }
        }

        [RelayCommand]
        public void OpenMySqlDownloadUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://dev.mysql.com/downloads/mysql/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not open web browser: {ex.Message}";
            }
        }

        [RelayCommand]
        public void OpenMariaDbDownloadUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://mariadb.org/download/",
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
            StatusMessage = "Extracting & preparing incoming MySQL package...";

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
                IncomingDetectedVersion = await _versionDetectionService.DetectMySqlVersionAsync(PreparedIncomingRoot);

                StatusMessage = "Comparing configuration files (bin/my.ini)...";
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
            StatusMessage = "Ready to apply MySQL update.";
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
                bool shouldSkipBackup = !IsTestMode && CanSkipBackup && SkipBackupDuringRealUpdate;
                // Used by the safe shutdown that runs before any file is replaced.
                _updateService.SetShutdownCredentials(MySqlUpgradeUser, MySqlUpgradePassword);
                _updateService.StartForCleanShutdown = StartMySqlFirstForCleanShutdown;
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
                        StatusMessage = "MySQL updated successfully!";
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
                "Are you sure you want to proceed with the REAL MySQL Update?\n\n" +
                "• Apache and MySQL services will be stopped.\n" +
                "• Existing MySQL binaries & config and live database data will be backed up into 2 separate .zip archives (mysql_backup and mysql_data_backup).\n" +
                "• Existing MySQL directory will be renamed to mysql_old.\n" +
                "• New MySQL binaries will be installed, my.ini applied, and data folder migrated.\n" +
                "• MySQL and Apache services will be restarted.\n\n" +
                "Do you want to proceed?",
                "Confirm Real MySQL Update");

            if (!confirmed)
            {
                StatusMessage = "MySQL update cancelled by user.";
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

        private int _step4OriginIndex = 2;

        [RelayCommand]
        public void ProceedToStep4()
        {
            _step4OriginIndex = CurrentStepIndex;
            if (string.IsNullOrWhiteSpace(MySqlUpgradeToolPath))
            {
                MySqlUpgradeToolPath = _updateService.DetectMySqlUpgradeToolPath();
            }
            CurrentStepIndex = 3;
            _ = RefreshServiceStatusAsync();
        }

        [RelayCommand]
        public void GoBackToStep3()
        {
            CurrentStepIndex = _step4OriginIndex;
        }

        [RelayCommand]
        public void GoBackFromStep4()
        {
            CurrentStepIndex = _step4OriginIndex;
        }

        /// <summary>Polls the service/process state so button labels follow changes made outside the app too.</summary>
        public void StartStatusMonitor()
        {
            if (_statusTimer != null) return;
            _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (_, _) =>
            {
                // Don't fight with an in-flight install/remove/start/stop that refreshes on completion.
                if (IsOperatingService) return;
                await RefreshServiceStatusAsync();
            };
            _statusTimer.Start();
        }

        public void StopStatusMonitor()
        {
            _statusTimer?.Stop();
            _statusTimer = null;
        }

        public async Task RefreshServiceStatusAsync()
        {
            if (_isRefreshingStatus) return;
            _isRefreshingStatus = true;
            try
            {
                var settings = _settingsService.CurrentSettings;
                string serviceName = settings.MySql.ServiceName;
                string mySqlPath = settings.MySql.InstallationPath;

                // Service/process enumeration can be slow; keep it off the UI thread.
                bool exists = await Task.Run(() => _serviceManager.ServiceExists(serviceName));
                string? procInfo = await Task.Run(() => _serviceManager.GetRunningMySqlProcessInfo(mySqlPath));
                bool procRunning = procInfo != null;
                ProcessDetail = procInfo ?? string.Empty;

                if (exists)
                {
                    var status = await _serviceManager.GetServiceStatusAsync(serviceName);
                    // StartPending counts as running so the button offers Stop while the service comes up.
                    bool svcRunning = status == ServiceControllerStatus.Running ||
                                      status == ServiceControllerStatus.StartPending ||
                                      status == ServiceControllerStatus.ContinuePending;

                    // A running mysqld while the service is stopped is a standalone process: control it as a process.
                    IsServiceTransitioning = status is ServiceControllerStatus.StartPending
                        or ServiceControllerStatus.StopPending
                        or ServiceControllerStatus.PausePending
                        or ServiceControllerStatus.ContinuePending;
                    UseServiceControl = svcRunning || !procRunning;
                    IsServiceRunning = svcRunning || procRunning;
                    IsServiceInstalled = true;

                    if (status == ServiceControllerStatus.Running)
                    {
                        ServiceStatus = ServiceStatus.Running;
                        ServiceStatusDisplay = "Running";
                    }
                    else if (!svcRunning && procRunning)
                    {
                        ServiceStatus = ServiceStatus.Running;
                        ServiceStatusDisplay = "Process Running (service stopped)";
                    }
                    else
                    {
                        ServiceStatus = ServiceStatus.Stopped;
                        ServiceStatusDisplay = status.HasValue ? status.Value.ToString() : "Stopped";
                    }
                }
                else
                {
                    IsServiceTransitioning = false;
                    UseServiceControl = false;
                    IsServiceRunning = procRunning;
                    IsServiceInstalled = false;

                    if (procRunning)
                    {
                        ServiceStatus = ServiceStatus.Running;
                        ServiceStatusDisplay = "Process Running (no service)";
                    }
                    else
                    {
                        ServiceStatus = ServiceStatus.NotInstalled;
                        ServiceStatusDisplay = "Service Not Installed";
                    }
                }
            }
            catch
            {
                ServiceStatus = ServiceStatus.Unknown;
                ServiceStatusDisplay = "Unknown";
                IsServiceRunning = false;
            }
            finally
            {
                _isRefreshingStatus = false;
            }
        }

        [RelayCommand]
        public async Task ToggleServiceInstallAsync()
        {
            if (IsOperatingService || IsBusy || IsUpgradingDatabase) return;
            if (!CanToggleServiceInstall) return; // only allowed in the stopped state

            var settings = _settingsService.CurrentSettings;
            string mySqlPath = settings.MySql.InstallationPath;
            string serviceName = settings.MySql.ServiceName;
            string iniPath = settings.MySql.IniPath;

            if (!IsServiceInstalled)
            {
                if (!ConfirmAction($"Are you sure you want to install MySQL service '{serviceName}' in Windows Services?", "Confirm Install Service"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Installing MySQL service '{serviceName}'...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    bool ok = await _serviceManager.InstallMySqlServiceAsync(mySqlPath, serviceName, iniPath, progress);
                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? $"MySQL service '{serviceName}' installed successfully." : $"Failed to install MySQL service '{serviceName}'.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to install service: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
            else
            {
                if (!ConfirmAction($"Are you sure you want to remove MySQL service '{serviceName}' from Windows Services?", "Confirm Remove Service"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Removing MySQL service '{serviceName}'...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    bool ok = await _serviceManager.RemoveMySqlServiceAsync(mySqlPath, serviceName, progress);
                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? $"MySQL service '{serviceName}' removed successfully." : $"Failed to remove MySQL service '{serviceName}'.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to remove service: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
        }

        /// <summary>Start the current MySQL once when it is stopped, so InnoDB recovers and shuts down cleanly before files are replaced.</summary>
        [ObservableProperty]
        private bool _startMySqlFirstForCleanShutdown = true;

        [ObservableProperty]
        private string _loginTestMessage = string.Empty;

        [ObservableProperty]
        private bool _loginTestSucceeded;

        [ObservableProperty]
        private bool _isTestingLogin;

        /// <summary>Verifies the admin login used by the pre-upgrade safe shutdown, before the update is started.</summary>
        [RelayCommand]
        public async Task TestLoginAsync()
        {
            if (IsTestingLogin) return;

            IsTestingLogin = true;
            LoginTestMessage = "Testing login...";
            try
            {
                _updateService.SetShutdownCredentials(MySqlUpgradeUser, MySqlUpgradePassword);
                var (ok, message) = await _updateService.TestShutdownLoginAsync();
                LoginTestSucceeded = ok;
                LoginTestMessage = message;
            }
            catch (Exception ex)
            {
                LoginTestSucceeded = false;
                LoginTestMessage = $"Login test failed: {ex.Message}";
            }
            finally
            {
                IsTestingLogin = false;
            }
        }

        [RelayCommand]
        public async Task ToggleServiceRunAsync()
        {
            if (IsOperatingService || IsBusy || IsUpgradingDatabase) return;

            var settings = _settingsService.CurrentSettings;
            string mySqlPath = settings.MySql.InstallationPath;
            string serviceName = settings.MySql.ServiceName;
            string iniPath = settings.MySql.IniPath;

            // Capture once so the action always matches the label the user clicked.
            bool useService = UseServiceControl;

            if (IsServiceRunning)
            {
                string actionTarget = useService ? $"service '{serviceName}'" : "background process";
                if (!ConfirmAction($"Are you sure you want to stop MySQL {actionTarget}?", "Confirm Stop MySQL"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Stopping MySQL {actionTarget}...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);

                    bool ok;
                    if (useService)
                    {
                        ok = await _serviceManager.StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                    }
                    else
                    {
                        ok = await _serviceManager.StopMySqlProcessAsync(mySqlPath, progress);
                    }

                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? "MySQL stopped successfully." : "Failed to stop MySQL.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to stop MySQL: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
            else
            {
                string actionTarget = useService ? $"service '{serviceName}'" : "background process";
                if (!ConfirmAction($"Are you sure you want to start MySQL {actionTarget}?", "Confirm Start MySQL"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Starting MySQL {actionTarget}...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);

                    bool ok;
                    if (useService)
                    {
                        ok = await _serviceManager.StartServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                    }
                    else
                    {
                        ok = await _serviceManager.StartMySqlProcessAsync(mySqlPath, iniPath, progress);
                    }

                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? "MySQL started successfully." : "Failed to start MySQL. Check error log (.err) for details.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to start MySQL: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
        }

        [RelayCommand]
        public async Task StartMySqlServiceAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            if (IsServiceRunning) return; // already running: nothing to start

            bool useService = UseServiceControl;
            string target = useService ? "service" : "background process";
            if (!ConfirmAction($"Are you sure you want to start the MySQL {target}?", "Confirm Start MySQL"))
            {
                return;
            }

            IsOperatingService = true;
            try
            {
                var settings = _settingsService.CurrentSettings;
                string serviceName = settings.MySql.ServiceName;
                string mySqlPath = settings.MySql.InstallationPath;
                string iniPath = settings.MySql.IniPath;
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] Starting MySQL {(useService ? $"service '{serviceName}'" : "process")}...");

                var progress = new Progress<string>(msg =>
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] {msg}");
                });

                bool started;
                if (useService)
                {
                    started = await _serviceManager.StartServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                }
                else
                {
                    started = await _serviceManager.StartMySqlProcessAsync(mySqlPath, iniPath, progress);
                }

                await RefreshServiceStatusAsync();
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] Result: {(started ? "Started successfully." : "Failed to start.")}");
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE ERROR] {ex.Message}");
            }
            finally
            {
                IsOperatingService = false;
            }
        }

        [RelayCommand]
        public async Task StopMySqlServiceAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            if (!IsServiceRunning) return; // nothing running: nothing to stop

            bool useService = UseServiceControl;
            string target = useService ? "service" : "background process";
            if (!ConfirmAction($"Are you sure you want to stop the MySQL {target}?", "Confirm Stop MySQL"))
            {
                return;
            }

            IsOperatingService = true;
            try
            {
                var settings = _settingsService.CurrentSettings;
                string serviceName = settings.MySql.ServiceName;
                string mySqlPath = settings.MySql.InstallationPath;
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] Stopping MySQL {(useService ? $"service '{serviceName}'" : "process")}...");

                var progress = new Progress<string>(msg =>
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] {msg}");
                });

                bool stopped;
                if (useService)
                {
                    stopped = await _serviceManager.StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                }
                else
                {
                    stopped = await _serviceManager.StopMySqlProcessAsync(mySqlPath, progress);
                }

                await RefreshServiceStatusAsync();
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE] Result: {(stopped ? "Stopped successfully." : "Failed to stop.")}");
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [SERVICE ERROR] {ex.Message}");
            }
            finally
            {
                IsOperatingService = false;
            }
        }

        [RelayCommand]
        public async Task RenameConflictLogAndStatFilesAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;
            IsOperatingService = true;

            try
            {
                var settings = _settingsService.CurrentSettings;
                string serviceName = settings.MySql.ServiceName;

                // Stop MySQL first if running, as a service and/or a standalone process
                if (IsServiceRunning || _serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP] Stopping MySQL ({(UseServiceControl ? "service" : "process")}) prior to renaming log/stat files...");
                    await _serviceManager.StopMySqlAnyAsync(serviceName, settings.MySql.InstallationPath);
                    await Task.Delay(1000);
                    await RefreshServiceStatusAsync();
                }

                string mySqlDir = settings.MySql.InstallationPath;
                string dataDir = Path.Combine(mySqlDir, "data");

                if (!Directory.Exists(dataDir))
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP ERROR] Data directory not found at: {dataDir}");
                    UpgradeStatusMessage = "Data directory not found.";
                    return;
                }

                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP] Renaming conflict InnoDB redo logs & optimizer stats files in '{dataDir}'...");

                var targetFiles = new List<string>
                {
                    Path.Combine(dataDir, "ib_logfile0"),
                    Path.Combine(dataDir, "ib_logfile1"),
                    Path.Combine(dataDir, "mysql", "innodb_index_stats.ibd"),
                    Path.Combine(dataDir, "mysql", "innodb_table_stats.ibd")
                };

                int renamedCount = 0;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                foreach (var file in targetFiles)
                {
                    if (File.Exists(file))
                    {
                        string dir = Path.GetDirectoryName(file)!;
                        string fileName = Path.GetFileName(file);
                        string backupFile = Path.Combine(dir, $"{fileName}.bak");
                        if (File.Exists(backupFile))
                        {
                            backupFile = Path.Combine(dir, $"{fileName}_{timestamp}.bak");
                        }

                        try
                        {
                            File.Move(file, backupFile, true);
                            renamedCount++;
                            UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP] Renamed: {fileName} -> {Path.GetFileName(backupFile)}");
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP WARN] Could not rename {fileName}: {ex.Message}");
                        }
                    }
                    else
                    {
                        UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP] Already clean / not present: {Path.GetFileName(file)}");
                    }
                }

                UpgradeStatusMessage = $"Renamed {renamedCount} file(s). You can now Start MySQL and run mysql_upgrade.";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP COMPLETE] Finished renaming {renamedCount} file(s). Safe to start MySQL service now.");
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CLEANUP EXCEPTION] {ex.Message}");
                UpgradeStatusMessage = $"Cleanup failed: {ex.Message}";
            }
            finally
            {
                IsOperatingService = false;
            }
        }

        [RelayCommand]
        public async Task RestoreRenamedFilesAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            if (!ConfirmAction(
                "⚠️ Restoring .bak files will revert renamed backup files in the data directory back to their original names. MySQL service will be stopped if running.\n\nAre you sure you want to proceed?",
                "Confirm Restore Backup Files"))
            {
                return;
            }

            IsOperatingService = true;

            try
            {
                var settings = _settingsService.CurrentSettings;
                string serviceName = settings.MySql.ServiceName;

                if (IsServiceRunning || _serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE] Stopping MySQL ({(UseServiceControl ? "service" : "process")}) prior to restoring files...");
                    await _serviceManager.StopMySqlAnyAsync(serviceName, settings.MySql.InstallationPath);
                    await Task.Delay(1000);
                    await RefreshServiceStatusAsync();
                }

                string mySqlDir = settings.MySql.InstallationPath;
                string dataDir = Path.Combine(mySqlDir, "data");

                if (!Directory.Exists(dataDir))
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE ERROR] Data directory not found at: {dataDir}");
                    return;
                }

                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE] Restoring .bak files in '{dataDir}'...");

                var bakFiles = Directory.GetFiles(dataDir, "*.bak", SearchOption.AllDirectories);
                int restoredCount = 0;

                foreach (var bakFile in bakFiles)
                {
                    string dir = Path.GetDirectoryName(bakFile)!;
                    string fileName = Path.GetFileName(bakFile);

                    string origName = fileName;
                    if (origName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        origName = origName.Substring(0, origName.Length - 4);
                    }
                    int lastUnderscore = origName.LastIndexOf('_');
                    if (lastUnderscore > 0 && origName.Length - lastUnderscore == 16)
                    {
                        origName = origName.Substring(0, lastUnderscore);
                    }

                    string targetOrigPath = Path.Combine(dir, origName);
                    try
                    {
                        File.Move(bakFile, targetOrigPath, true);
                        restoredCount++;
                        UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE] Restored: {fileName} -> {origName}");
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE WARN] Could not restore {fileName}: {ex.Message}");
                    }
                }

                UpgradeStatusMessage = $"Restored {restoredCount} backup file(s).";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE COMPLETE] Restored {restoredCount} file(s). You can now Start MySQL Service.");
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE EXCEPTION] {ex.Message}");
                UpgradeStatusMessage = $"Restore failed: {ex.Message}";
            }
            finally
            {
                IsOperatingService = false;
            }
        }

        [RelayCommand]
        public async Task DumpAllDatabasesAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            var saveDialog = new SaveFileDialog
            {
                Title = "Select Destination for Full Database Dump (.sql)",
                Filter = "SQL Dump Files (*.sql)|*.sql|All Files (*.*)|*.*",
                FileName = $"all_databases_dump_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
            };

            string backupFolder = _settingsService.CurrentSettings.General.ResolvedBackupDirectory;
            if (Directory.Exists(backupFolder))
            {
                saveDialog.InitialDirectory = backupFolder;
            }

            if (saveDialog.ShowDialog() != true)
            {
                return;
            }

            string targetFile = saveDialog.FileName;
            IsUpgradingDatabase = true;
            UpgradeErrorMessage = string.Empty;
            UpgradeStatusMessage = "Dumping all databases...";
            UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [DUMP] Initializing database dump to '{targetFile}'...");

            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                UpgradeStatusMessage = report.Message;
                if (report.IsError)
                {
                    UpgradeErrorMessage = report.Message;
                }
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] {report.Message}");
            });

            try
            {
                bool success = await _updateService.DumpAllDatabasesAsync(
                    targetFile,
                    MySqlUpgradeUser,
                    MySqlUpgradePassword,
                    "127.0.0.1",
                    progress,
                    _cts.Token);

                if (success)
                {
                    UpgradeStatusMessage = "Database dump completed successfully.";
                }
                else
                {
                    UpgradeStatusMessage = "Database dump failed. See output log.";
                }
            }
            catch (Exception ex)
            {
                UpgradeErrorMessage = ex.Message;
                UpgradeStatusMessage = $"Dump failed: {ex.Message}";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [DUMP EXCEPTION] {ex.Message}");
            }
            finally
            {
                IsUpgradingDatabase = false;
                _ = RefreshServiceStatusAsync();
            }
        }

        [RelayCommand]
        public async Task RestoreDatabaseAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            if (!ConfirmAction(
                "⚠️ DANGEROUS OPERATION: Restoring a database from an SQL file will execute all statements in the file and may overwrite or modify existing databases and data.\n\nAre you sure you want to proceed with database restore?",
                "Confirm Database Restore (Dangerous)"))
            {
                return;
            }

            var openDialog = new OpenFileDialog
            {
                Title = "Select SQL Dump File to Restore",
                Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*"
            };

            string backupFolder = _settingsService.CurrentSettings.General.ResolvedBackupDirectory;
            if (Directory.Exists(backupFolder))
            {
                openDialog.InitialDirectory = backupFolder;
            }

            if (openDialog.ShowDialog() != true)
            {
                return;
            }

            string selectedFile = openDialog.FileName;
            IsUpgradingDatabase = true;
            UpgradeErrorMessage = string.Empty;
            UpgradeStatusMessage = $"Restoring database from '{Path.GetFileName(selectedFile)}'...";
            UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE] Restoring database from '{selectedFile}'...");

            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                UpgradeStatusMessage = report.Message;
                if (report.IsError)
                {
                    UpgradeErrorMessage = report.Message;
                }
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] {report.Message}");
            });

            try
            {
                bool success = await _updateService.RestoreDatabaseFromSqlFileAsync(
                    selectedFile,
                    MySqlUpgradeUser,
                    MySqlUpgradePassword,
                    "127.0.0.1",
                    progress,
                    _cts.Token);

                if (success)
                {
                    UpgradeStatusMessage = "Database restore completed successfully.";
                }
                else
                {
                    UpgradeStatusMessage = "Database restore failed. See output log.";
                }
            }
            catch (Exception ex)
            {
                UpgradeErrorMessage = ex.Message;
                UpgradeStatusMessage = $"Restore failed: {ex.Message}";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [RESTORE EXCEPTION] {ex.Message}");
            }
            finally
            {
                IsUpgradingDatabase = false;
                _ = RefreshServiceStatusAsync();
            }
        }

        [RelayCommand]
        public async Task RunMariaDbInstallDbAsync()
        {
            if (IsOperatingService || IsUpgradingDatabase) return;

            if (!ConfirmAction(
                "⚠️ MAINTENANCE / DANGEROUS OPERATION: Running mariadb-install-db will initialize or recreate internal database system tables in the data folder. The MySQL service will be stopped if currently running.\n\nAre you sure you want to run mariadb-install-db?",
                "Confirm MariaDB Install DB (Maintenance)"))
            {
                return;
            }

            IsUpgradingDatabase = true;
            UpgradeErrorMessage = string.Empty;
            UpgradeStatusMessage = "Running mariadb-install-db.exe...";
            UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [INSTALL-DB] Starting mariadb-install-db...");

            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                UpgradeStatusMessage = report.Message;
                if (report.IsError)
                {
                    UpgradeErrorMessage = report.Message;
                }
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] {report.Message}");
            });

            try
            {
                bool success = await _updateService.RunMariaDbInstallDbAsync(progress, _cts.Token);
                if (success)
                {
                    UpgradeStatusMessage = "mariadb-install-db completed successfully.";
                }
                else
                {
                    UpgradeStatusMessage = "mariadb-install-db failed. See output log.";
                }
            }
            catch (Exception ex)
            {
                UpgradeErrorMessage = ex.Message;
                UpgradeStatusMessage = $"mariadb-install-db exception: {ex.Message}";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [INSTALL-DB EXCEPTION] {ex.Message}");
            }
            finally
            {
                IsUpgradingDatabase = false;
                _ = RefreshServiceStatusAsync();
            }
        }

        [RelayCommand]
        public void OpenMyIniFile()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                string installDir = settings.MySql.InstallationPath;
                string[] candidates = new[]
                {
                    Path.Combine(installDir, "bin", "my.ini"),
                    Path.Combine(installDir, "my.ini"),
                    Path.Combine(installDir, "my.cnf")
                };

                string? targetIni = candidates.FirstOrDefault(File.Exists);
                if (targetIni == null)
                {
                    targetIni = Path.Combine(installDir, "bin", "my.ini");
                }

                if (File.Exists(targetIni))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetIni,
                        UseShellExecute = true
                    });
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CONFIG] Opened '{targetIni}' in default editor.");
                }
                else
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CONFIG ERROR] my.ini not found at: {targetIni}");
                }
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CONFIG ERROR] Failed to open my.ini: {ex.Message}");
            }
        }

        [RelayCommand]
        public void OpenErrorLogFile()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                string installDir = settings.MySql.InstallationPath;
                string dataDir = Path.Combine(installDir, "data");

                var candidates = new List<string>
                {
                    Path.Combine(dataDir, "mysql_error.log"),
                    Path.Combine(dataDir, $"{Environment.MachineName}.err"),
                    Path.Combine(dataDir, "mariadb.err"),
                    Path.Combine(dataDir, "mysql.err"),
                    Path.Combine(dataDir, "mysqld.err")
                };

                if (Directory.Exists(dataDir))
                {
                    var errFiles = Directory.GetFiles(dataDir, "*.err");
                    candidates.AddRange(errFiles);
                }

                string? targetLog = candidates.FirstOrDefault(File.Exists);
                if (targetLog != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetLog,
                        UseShellExecute = true
                    });
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [LOG] Opened error log '{targetLog}' in default viewer.");
                }
                else
                {
                    UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [LOG ERROR] No error log file (.err / mysql_error.log) found in '{dataDir}'.");
                }
            }
            catch (Exception ex)
            {
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [LOG ERROR] Failed to open error log: {ex.Message}");
            }
        }

        [RelayCommand]
        public async Task RunSystemTablesUpgradeOnlyAsync()
        {
            MySqlUpgradeArguments = "-h 127.0.0.1 --upgrade-system-tables --force --verbose";
            await RunMySqlUpgradeAsync();
        }

        [RelayCommand]
        public void BrowseUpgradeTool()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select mysql_upgrade / mariadb-upgrade Executable or Script",
                Filter = "Upgrade Tools (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                MySqlUpgradeToolPath = dialog.FileName;
            }
        }

        [RelayCommand]
        public async Task RunMySqlUpgradeAsync()
        {
            if (IsUpgradingDatabase) return;

            IsUpgradingDatabase = true;
            IsUpgradeSuccess = false;
            UpgradeErrorMessage = string.Empty;
            UpgradeLogs.Clear();
            UpgradeStatusMessage = "Executing database table upgrade...";

            _cts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgressReport>(report =>
            {
                UpgradeStatusMessage = report.Message;
                if (report.IsError)
                {
                    UpgradeErrorMessage = report.Message;
                }
                string logEntry = $"[{DateTime.Now:HH:mm:ss}] {report.Message}";
                UpgradeLogs.Add(logEntry);
            });

            try
            {
                bool success = await _updateService.RunMySqlUpgradeAsync(
                    MySqlUpgradeToolPath,
                    MySqlUpgradeUser,
                    MySqlUpgradePassword,
                    MySqlUpgradeArguments,
                    progress,
                    _cts.Token);

                IsUpgradeSuccess = success;
                if (success)
                {
                    UpgradeErrorMessage = string.Empty;
                    UpgradeStatusMessage = "Database table upgrade completed successfully!";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(UpgradeErrorMessage))
                    {
                        UpgradeErrorMessage = "Database table upgrade failed. See output logs above.";
                    }
                    UpgradeStatusMessage = "Upgrade failed.";
                }
            }
            catch (Exception ex)
            {
                IsUpgradeSuccess = false;
                UpgradeErrorMessage = $"Upgrade exception: {ex.Message}";
                UpgradeStatusMessage = $"Upgrade failed: {ex.Message}";
                UpgradeLogs.Add($"[{DateTime.Now:HH:mm:ss}] [EXCEPTION] {ex.Message}");
            }
            finally
            {
                IsUpgradingDatabase = false;
            }
        }

        [RelayCommand]
        public void CopyUpgradeLogs()
        {
            if (UpgradeLogs.Count == 0) return;
            try
            {
                string text = string.Join(Environment.NewLine, UpgradeLogs);
                Clipboard.SetText(text);
            }
            catch { }
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
            if (IsExecuting || IsUpgradingDatabase)
            {
                _cts?.Cancel();
            }

            if (!string.IsNullOrEmpty(PreparedIncomingRoot))
            {
                string? topTempDir = Directory.GetParent(PreparedIncomingRoot)?.FullName;
                if (!string.IsNullOrEmpty(topTempDir) && topTempDir.Contains("mysql_incoming_"))
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
