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

        public ObservableCollection<ServiceItem> Services { get; } = new();

        public event Action? RequestOpenSettings;
        public event Action? RequestOpenApacheWizard;

        public MainViewModel(
            ISettingsService settingsService,
            IWindowsServiceManager serviceManager,
            IVersionDetectionService versionDetectionService)
        {
            _settingsService = settingsService;
            _serviceManager = serviceManager;
            _versionDetectionService = versionDetectionService;

            _settingsService.SettingsChanged += async (_, _) => await RefreshStatusesAsync();
            InitializeServicesList();
        }

        private void InitializeServicesList()
        {
            Services.Clear();

            // 1. Apache (Active)
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Apache,
                Name = "Apache",
                DisplayName = "Apache HTTP Server",
                Description = "Primary Web Server (HTTP/HTTPS) daemon",
                IconGlyph = "🌐",
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
                Description = "Server-side scripting engine",
                IconGlyph = "🐘",
                CanUpdate = false,
                IsUnderConstruction = true,
                Status = ServiceStatus.UnderConstruction,
                StatusText = "Under Construction"
            });

            // 3. MySQL
            Services.Add(new ServiceItem
            {
                Type = ServiceType.MySql,
                Name = "MySQL / MariaDB",
                DisplayName = "MySQL Relational Database",
                Description = "Database management server daemon",
                IconGlyph = "🐬",
                CanUpdate = false,
                IsUnderConstruction = true,
                Status = ServiceStatus.UnderConstruction,
                StatusText = "Under Construction"
            });

            // 4. SSL Certificate
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Ssl,
                Name = "SSL Certificate",
                DisplayName = "Local SSL / TLS Certificates",
                Description = "HTTPS cryptographic key pairs and certificates",
                IconGlyph = "🔒",
                CanUpdate = false,
                IsUnderConstruction = true,
                Status = ServiceStatus.UnderConstruction,
                StatusText = "Under Construction"
            });

            // 5. Composer
            Services.Add(new ServiceItem
            {
                Type = ServiceType.Composer,
                Name = "Composer",
                DisplayName = "Composer Dependency Manager",
                Description = "PHP package dependency manager executable",
                IconGlyph = "📦",
                CanUpdate = false,
                IsUnderConstruction = true,
                Status = ServiceStatus.UnderConstruction,
                StatusText = "Under Construction"
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

                        if (!Directory.Exists(item.InstallPath))
                        {
                            item.Status = ServiceStatus.NotInstalled;
                            item.StatusText = "Folder Not Found";
                        }
                        else
                        {
                            var svcStatus = await _serviceManager.GetServiceStatusAsync(item.ServiceName);
                            if (svcStatus.HasValue)
                            {
                                item.Status = svcStatus.Value == ServiceControllerStatus.Running
                                    ? ServiceStatus.Running
                                    : ServiceStatus.Stopped;
                                item.StatusText = svcStatus.Value.ToString();
                            }
                            else
                            {
                                item.Status = ServiceStatus.NotInstalled;
                                item.StatusText = "Service Not Registered";
                            }
                        }
                        break;

                    case ServiceType.Php:
                        item.InstallPath = settings.Php.InstallationPath;
                        item.Version = await _versionDetectionService.DetectPhpVersionAsync(item.InstallPath);
                        break;

                    case ServiceType.MySql:
                        item.InstallPath = settings.MySql.InstallationPath;
                        item.ServiceName = settings.MySql.ServiceName;
                        item.Version = await _versionDetectionService.DetectMySqlVersionAsync(item.InstallPath);
                        break;

                    case ServiceType.Ssl:
                        item.InstallPath = settings.Ssl.CertificatesPath;
                        item.Version = await _versionDetectionService.DetectSslStatusAsync(item.InstallPath);
                        break;

                    case ServiceType.Composer:
                        item.InstallPath = settings.Composer.ExecutablePath;
                        item.Version = await _versionDetectionService.DetectComposerVersionAsync(item.InstallPath);
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
    }
}
