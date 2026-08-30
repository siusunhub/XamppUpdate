using System;
using System.Collections.ObjectModel;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XamppUpdate.Models;
using XamppUpdate.Services;
using ServiceType = XamppUpdate.Models.ServiceType;

namespace XamppUpdate.ViewModels
{
    public partial class MainViewModel : ViewModelBase
    {
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;
        private readonly IVersionDetectionService _versionDetectionService;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        public string WindowTitle => AppInfo.WindowTitle;
        public string AppVersionDisplay => $"v{AppInfo.Version} (Build {AppInfo.BuildNumber} · {AppInfo.BuildDate})";

        public ObservableCollection<ServiceItem> Services { get; } = new();

        public event Action? RequestOpenSettings;
        public event Action? RequestOpenApacheWizard;
        public event Action? RequestOpenPhpWizard;
        public event Action? RequestOpenSslManager;
        public event Action? RequestOpenComposer;

        public MainViewModel(
            ISettingsService settingsService,
            IWindowsServiceManager serviceManager,
            IVersionDetectionService versionDetectionService)
        {
            _settingsService = settingsService;
            _serviceManager = serviceManager;
            _versionDetectionService = versionDetectionService;

            _settingsService.SettingsChanged += async (_, _) => await RefreshStatusesAsync();
            InitializeServices();
        }

        private void InitializeServices()
        {
            Services.Clear();

            // 1. Apache
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Apache,
                Name = "Apache",
                DisplayName = "Apache HTTP Server",
                Description = "Core web server executable and modules",
                IconGlyph = "🪶",
                CanUpdate = true,
                IsUnderConstruction = false,
                Status = ServiceStatus.Unknown,
                StatusText = "Checking..."
            });

            // 2. PHP
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Php,
                Name = "PHP",
                DisplayName = "PHP Hypertext Preprocessor",
                Description = "PHP scripting runtime engine and extensions",
                IconGlyph = "🐘",
                CanUpdate = true,
                IsUnderConstruction = false,
                Status = ServiceStatus.Running,
                StatusText = "Active"
            });

            // 3. MySQL / MariaDB
            Services.Add(new ServiceItem
            {
                Type = ServiceType.MySql,
                Name = "MySQL",
                DisplayName = "MySQL / MariaDB Database",
                Description = "Database server daemon and storage engine",
                IconGlyph = "🐬",
                CanUpdate = false,
                IsUnderConstruction = true,
                Status = ServiceStatus.Unknown,
                StatusText = "Checking..."
            });

            // 4. SSL Certificates
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Ssl,
                Name = "SSL",
                DisplayName = "SSL / TLS Certificates",
                Description = "Active server certificate, private key, and chain bundle",
                IconGlyph = "🔒",
                CanManageSsl = true,
                CanUpdate = false,
                IsUnderConstruction = false,
                Status = ServiceStatus.Running,
                StatusText = "Active"
            });

            // 5. Composer
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Composer,
                Name = "Composer",
                DisplayName = "Composer Dependency Manager",
                Description = "PHP package dependency manager executable",
                IconGlyph = "📦",
                CanManageComposer = true,
                CanUpdate = false,
                IsUnderConstruction = false,
                Status = ServiceStatus.Running,
                StatusText = "Active"
            });
        }

        [RelayCommand]
        public async Task RefreshStatusesAsync()
        {
            if (IsBusy) return;

            IsBusy = true;
            StatusMessage = "Refreshing service statuses...";

            var settings = _settingsService.CurrentSettings;

            foreach (var item in Services)
            {
                item.IsBusy = true;

                switch (item.Type)
                {
                    case ServiceType.Apache:
                        item.InstallPath = settings.Apache.InstallationPath;
                        item.ServiceName = settings.Apache.ServiceName;
                        item.Version = await _versionDetectionService.DetectApacheVersionAsync(item.InstallPath);
                        if (_serviceManager.ServiceExists(item.ServiceName))
                        {
                            var svcStatus = await _serviceManager.GetServiceStatusAsync(item.ServiceName);
                            item.Status = svcStatus == ServiceControllerStatus.Running ? ServiceStatus.Running : ServiceStatus.Stopped;
                            item.StatusText = svcStatus.HasValue ? svcStatus.Value.ToString() : "Stopped";
                        }
                        else
                        {
                            item.Status = ServiceStatus.Stopped;
                            item.StatusText = "Service Not Installed";
                        }
                        break;

                    case ServiceType.Php:
                        item.InstallPath = settings.Php.InstallationPath;
                        item.Version = await _versionDetectionService.DetectPhpVersionAsync(item.InstallPath);
                        item.Status = ServiceStatus.Running;
                        item.StatusText = "Active";
                        break;

                    case ServiceType.MySql:
                        item.InstallPath = settings.MySql.InstallationPath;
                        item.ServiceName = settings.MySql.ServiceName;
                        item.Version = await _versionDetectionService.DetectMySqlVersionAsync(item.InstallPath);
                        break;

                    case ServiceType.Ssl:
                        item.InstallPath = Path.Combine(settings.Apache.InstallationPath, "conf");
                        item.Version = "SSL / TLS 1.3";
                        item.Status = ServiceStatus.Running;
                        item.StatusText = "Active";
                        item.CanManageSsl = true;
                        item.CanUpdate = false;
                        item.IsUnderConstruction = false;
                        break;

                    case ServiceType.Composer:
                        item.InstallPath = settings.Composer.ExecutablePath;
                        item.Version = await _versionDetectionService.DetectComposerVersionAsync(item.InstallPath);
                        item.CanManageComposer = true;
                        item.CanUpdate = false;
                        item.IsUnderConstruction = false;
                        item.Status = ServiceStatus.Running;
                        item.StatusText = "Active";
                        break;
                }

                item.IsBusy = false;
            }

            IsBusy = false;
            StatusMessage = $"Updated at {DateTime.Now:HH:mm:ss}";
        }

        [RelayCommand]
        public void OpenSettings()
        {
            RequestOpenSettings?.Invoke();
        }

        [RelayCommand]
        public void StartApacheUpdate()
        {
            RequestOpenApacheWizard?.Invoke();
        }

        [RelayCommand]
        public void StartPhpUpdate()
        {
            RequestOpenPhpWizard?.Invoke();
        }

        [RelayCommand]
        public void StartComposerManagement()
        {
            RequestOpenComposer?.Invoke();
        }

        [RelayCommand]
        public void OpenComposer()
        {
            RequestOpenComposer?.Invoke();
        }

        [RelayCommand]
        public void StartServiceUpdate(ServiceItem? item)
        {
            if (item == null) return;
            if (item.Type == ServiceType.Apache)
            {
                StartApacheUpdate();
            }
            else if (item.Type == ServiceType.Php)
            {
                StartPhpUpdate();
            }
            else if (item.Type == ServiceType.Composer)
            {
                StartComposerManagement();
            }
        }

        [RelayCommand]
        public void OpenSslManager()
        {
            RequestOpenSslManager?.Invoke();
        }
    }
}
