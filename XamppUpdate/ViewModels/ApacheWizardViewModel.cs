using System;
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
    public partial class ApacheWizardViewModel : ViewModelBase
    {
        private readonly IApacheUpdateService _updateService;
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
        private string _currentInstalledVersion = "Detecting...";

        partial void OnCurrentInstalledVersionChanged(string value)
        {
            if (!IsExecuting && !IsCompleted)
            {
                SourceInstalledVersion = value;
            }
        }

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

        [ObservableProperty]
        private bool _canSkipBackup;

        [ObservableProperty]
        private bool _skipBackupDuringRealUpdate;

        public ObservableCollection<ConfigDiffItem> DiffItems { get; } = new();
        public ObservableCollection<string> ExecutionLogs { get; } = new();

        public event Action? RequestClose;
        public event Action? RequestOpenGenerateDefaultCert;
        public event Action? RequestOpenVHostManager;

        [RelayCommand]
        public void OpenGenerateDefaultCert()
        {
            RequestOpenGenerateDefaultCert?.Invoke();
        }

        [RelayCommand]
        public void OpenVHostManager()
        {
            RequestOpenVHostManager?.Invoke();
        }

        private readonly ISslCertificateService _sslCertificateService;
        public ISslCertificateService SslCertificateService => _sslCertificateService;
        private readonly IVHostService _vhostService;
        public IVHostService VHostService => _vhostService;
        public ISettingsService SettingsService => _settingsService;
        private readonly IWindowsServiceManager _serviceManager;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceInstallButtonText))]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        private bool _isServiceInstalled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isServiceRunning;

        /// <summary>True while the Windows service is in a Start/Stop/Pause/Continue pending state.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isServiceTransitioning;

        /// <summary>Install/Remove Service is only allowed in the fully stopped state.</summary>
        public bool CanToggleServiceInstall => !IsOperatingService && !IsServiceRunning && !IsServiceTransitioning;

        /// <summary>
        /// True when Start/Stop must go through the Windows service. False when the service is not installed,
        /// or it is installed but stopped while a standalone httpd process is running (then Stop acts on the process).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServiceRunButtonText))]
        private bool _useServiceControl;

        private System.Windows.Threading.DispatcherTimer? _statusTimer;
        private bool _isRefreshingStatus;

        [ObservableProperty]
        private ServiceStatus _serviceStatus = ServiceStatus.Unknown;

        [ObservableProperty]
        private string _serviceStatusDisplay = "Checking...";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanToggleServiceInstall))]
        private bool _isOperatingService;

        public string ServiceInstallButtonText => IsServiceInstalled ? "Remove Service" : "Install Service";

        public string ServiceRunButtonText => IsServiceRunning
            ? (UseServiceControl ? "Stop Service" : "Stop Process")
            : (UseServiceControl ? "Start Service" : "Start Process");

        public ApacheWizardViewModel(
            IApacheUpdateService updateService,
            ISettingsService settingsService,
            IVersionDetectionService versionDetectionService,
            ISslCertificateService? sslCertificateService = null,
            IVHostService? vhostService = null,
            IWindowsServiceManager? serviceManager = null)
        {
            _updateService = updateService;
            _settingsService = settingsService;
            _versionDetectionService = versionDetectionService;
            _sslCertificateService = sslCertificateService ?? new SslCertificateService();
            _vhostService = vhostService ?? new VHostService();
            _serviceManager = serviceManager ?? new WindowsServiceManager();

            CurrentStepIndex = 0;
            _ = LoadCurrentVersionAsync();
            _ = RefreshServiceStatusAsync();
            StartStatusMonitor();
        }

        /// <summary>Polls the service/process state so button labels follow changes made outside the app too.</summary>
        public void StartStatusMonitor()
        {
            if (_statusTimer != null) return;
            _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (_, _) =>
            {
                // Stay out of the way of long operations (extracting/downloading the package, executing the update)
                // and of in-flight install/remove/start/stop, which refresh on completion.
                if (IsOperatingService || IsBusy || IsExecuting) return;
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
                string serviceName = settings.Apache.ServiceName;
                string apachePath = settings.Apache.InstallationPath;

                // Service/process enumeration can be slow; keep it off the UI thread.
                bool exists = await Task.Run(() => _serviceManager.ServiceExists(serviceName));
                bool procRunning = await Task.Run(() => _serviceManager.IsApacheProcessRunning(apachePath));

                if (exists)
                {
                    var status = await _serviceManager.GetServiceStatusAsync(serviceName);
                    // StartPending counts as running so the button offers Stop while the service comes up.
                    bool svcRunning = status == ServiceControllerStatus.Running ||
                                      status == ServiceControllerStatus.StartPending ||
                                      status == ServiceControllerStatus.ContinuePending;

                    // A running httpd while the service is stopped is a standalone process: control it as a process.
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
            if (IsOperatingService || IsBusy) return;
            if (!CanToggleServiceInstall) return; // only allowed in the stopped state

            var settings = _settingsService.CurrentSettings;
            string apachePath = settings.Apache.InstallationPath;
            string serviceName = settings.Apache.ServiceName;

            if (!IsServiceInstalled)
            {
                if (!ConfirmAction($"Are you sure you want to install Apache service '{serviceName}' in Windows Services?", "Confirm Install Service"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Installing Apache service '{serviceName}'...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    bool ok = await _serviceManager.InstallApacheServiceAsync(apachePath, serviceName, progress);
                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? $"Apache service '{serviceName}' installed successfully." : $"Failed to install Apache service '{serviceName}'.";
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
                if (!ConfirmAction($"Are you sure you want to remove Apache service '{serviceName}' from Windows Services?", "Confirm Remove Service"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Removing Apache service '{serviceName}'...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    bool ok = await _serviceManager.RemoveApacheServiceAsync(apachePath, serviceName, progress);
                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? $"Apache service '{serviceName}' removed successfully." : $"Failed to remove Apache service '{serviceName}'.";
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

        [RelayCommand]
        public async Task ToggleServiceRunAsync()
        {
            if (IsOperatingService || IsBusy) return;

            var settings = _settingsService.CurrentSettings;
            string apachePath = settings.Apache.InstallationPath;
            string serviceName = settings.Apache.ServiceName;

            // Capture once so the action always matches the label the user clicked.
            bool useService = UseServiceControl;

            if (IsServiceRunning)
            {
                string actionTarget = useService ? $"service '{serviceName}'" : "background process";
                if (!ConfirmAction($"Are you sure you want to stop Apache {actionTarget}?", "Confirm Stop Apache"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Stopping Apache {actionTarget}...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);

                    bool ok;
                    if (useService)
                    {
                        ok = await _serviceManager.StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                    }
                    else
                    {
                        ok = await _serviceManager.StopApacheProcessAsync(apachePath, progress);
                    }

                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? "Apache stopped successfully." : "Failed to stop Apache.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to stop Apache: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
            else
            {
                string actionTarget = useService ? $"service '{serviceName}'" : "background process";
                if (!ConfirmAction($"Are you sure you want to start Apache {actionTarget}?", "Confirm Start Apache"))
                {
                    return;
                }

                IsOperatingService = true;
                try
                {
                    StatusMessage = $"Starting Apache {actionTarget}...";
                    var progress = new Progress<string>(msg => StatusMessage = msg);

                    bool ok;
                    if (useService)
                    {
                        ok = await _serviceManager.StartServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
                    }
                    else
                    {
                        ok = await _serviceManager.StartApacheProcessAsync(apachePath, progress);
                    }

                    await RefreshServiceStatusAsync();
                    StatusMessage = ok ? "Apache started successfully." : "Failed to start Apache. Check configuration or port conflicts.";
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Failed to start Apache: {ex.Message}";
                }
                finally
                {
                    IsOperatingService = false;
                }
            }
        }

        public async Task LoadCurrentVersionAsync()
        {
            var settings = _settingsService.CurrentSettings;
            string detected = await _versionDetectionService.DetectApacheVersionAsync(settings.Apache.InstallationPath);
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
        public void BrowseLocalFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Pre-Extracted Apache Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                SourcePathOrUrl = dialog.FolderName;
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
                "Are you sure you want to proceed with the REAL Apache Update?\n\n" +
                "• Your existing Apache installation will be backed up to a .zip archive.\n" +
                "• Apache service will be stopped, updated binaries copied, and configuration applied.\n\n" +
                "Do you want to proceed?",
                "Confirm Real Apache Update");

            if (!confirmed)
            {
                StatusMessage = "Apache update cancelled by user.";
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
