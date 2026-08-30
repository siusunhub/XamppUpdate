using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class SslManagerViewModel : ObservableObject
    {
        private readonly ISslCertificateService _sslService;
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;

        [ObservableProperty]
        private string _apachePath = string.Empty;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private SslCertificateInfo? _selectedCertificate;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private int _healthyCount;

        [ObservableProperty]
        private int _expiringSoonCount;

        [ObservableProperty]
        private int _problemCount;

        [ObservableProperty]
        private bool _hasCertificates;

        // In-place Certificate Update Panel properties
        [ObservableProperty]
        private bool _isUpdatePanelOpen;

        [ObservableProperty]
        private SslCertificateInfo? _updateTargetCert;

        [ObservableProperty]
        private string _newCertFilePath = string.Empty;

        [ObservableProperty]
        private string _newKeyFilePath = string.Empty;

        [ObservableProperty]
        private string _newChainFilePath = string.Empty;

        [ObservableProperty]
        private bool _backupExisting = true;

        [ObservableProperty]
        private string _updateMessage = string.Empty;

        [ObservableProperty]
        private bool _isUpdating;

        [ObservableProperty]
        private bool _isUpdateSuccess;

        [ObservableProperty]
        private bool _isRestartingApache;

        public ObservableCollection<SslCertificateInfo> Certificates { get; } = new();

        public SslManagerViewModel(
            ISslCertificateService sslService,
            ISettingsService settingsService,
            IWindowsServiceManager serviceManager)
        {
            _sslService = sslService;
            _settingsService = settingsService;
            _serviceManager = serviceManager;
            ApachePath = _settingsService.CurrentSettings.Apache.InstallationPath;
        }

        public async Task InitializeAsync()
        {
            await LoadCertificatesAsync();
        }

        [RelayCommand]
        public async Task LoadCertificatesAsync()
        {
            if (IsLoading) return;

            IsLoading = true;
            StatusMessage = "Scanning Apache configuration for active SSL certificates...";
            Certificates.Clear();
            IsUpdatePanelOpen = false;

            try
            {
                var detected = await _sslService.DetectCertificatesAsync(ApachePath);
                foreach (var cert in detected)
                {
                    Certificates.Add(cert);
                }

                if (Certificates.Count > 0)
                {
                    SelectedCertificate = Certificates[0];
                }

                TotalCount = Certificates.Count;
                HasCertificates = TotalCount > 0;
                HealthyCount = Certificates.Count(c => c.Status == SslCertificateStatus.Valid);
                ExpiringSoonCount = Certificates.Count(c => c.Status == SslCertificateStatus.ExpiringSoon);
                ProblemCount = Certificates.Count(c => c.Status == SslCertificateStatus.Expired || c.Status == SslCertificateStatus.FileNotFound || c.Status == SslCertificateStatus.ParseError);

                StatusMessage = TotalCount > 0
                    ? $"Discovered {TotalCount} SSL certificate configurations in Apache."
                    : "No active SSLCertificateFile directives detected in current Apache path.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error scanning certificates: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public void BrowseApacheDirectory()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Apache Installation Directory",
                InitialDirectory = Directory.Exists(ApachePath) ? ApachePath : "C:\\"
            };

            if (dialog.ShowDialog() == true)
            {
                ApachePath = dialog.FolderName;
                _ = LoadCertificatesAsync();
            }
        }

        [RelayCommand]
        public void OpenCertFolder(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target != null && !string.IsNullOrEmpty(target.CertificateFilePath))
            {
                _sslService.OpenFileInExplorer(target.CertificateFilePath);
            }
        }

        [RelayCommand]
        public void OpenKeyFolder(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target != null && !string.IsNullOrEmpty(target.KeyFilePath))
            {
                _sslService.OpenFileInExplorer(target.KeyFilePath);
            }
        }

        [RelayCommand]
        public void OpenCertInViewer(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target != null && !string.IsNullOrEmpty(target.CertificateFilePath))
            {
                _sslService.OpenCertificateInViewer(target.CertificateFilePath);
            }
        }

        [RelayCommand]
        public void OpenChainFolder(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target != null && !string.IsNullOrEmpty(target.ChainFilePath))
            {
                _sslService.OpenFileInExplorer(target.ChainFilePath);
            }
        }

        [RelayCommand]
        public void OpenChainInViewer(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target != null && !string.IsNullOrEmpty(target.ChainFilePath))
            {
                _sslService.OpenCertificateInViewer(target.ChainFilePath);
            }
        }

        [RelayCommand]
        public void StartUpdate(SslCertificateInfo? cert)
        {
            var target = cert ?? SelectedCertificate;
            if (target == null) return;

            UpdateTargetCert = target;
            NewCertFilePath = string.Empty;
            NewKeyFilePath = string.Empty;
            NewChainFilePath = string.Empty;
            UpdateMessage = string.Empty;
            IsUpdateSuccess = false;
            IsUpdatePanelOpen = true;
        }

        [RelayCommand]
        public void CancelUpdate()
        {
            IsUpdatePanelOpen = false;
            UpdateTargetCert = null;
            UpdateMessage = string.Empty;
        }

        [RelayCommand]
        public void BrowseNewCertFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select New SSL Certificate File (.crt, .cer, .pem)",
                Filter = "Certificate Files (*.crt;*.cer;*.pem)|*.crt;*.cer;*.pem|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                NewCertFilePath = dialog.FileName;
            }
        }

        [RelayCommand]
        public void BrowseNewKeyFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Private Key File (.key, .pem)",
                Filter = "Key Files (*.key;*.pem)|*.key;*.pem|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                NewKeyFilePath = dialog.FileName;
            }
        }

        [RelayCommand]
        public void BrowseNewChainFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Intermediate CA Chain File (Optional)",
                Filter = "Certificate Files (*.crt;*.cer;*.pem)|*.crt;*.cer;*.pem|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                NewChainFilePath = dialog.FileName;
            }
        }

        [RelayCommand]
        public async Task ExecuteUpdateAsync()
        {
            if (UpdateTargetCert == null) return;

            if (string.IsNullOrWhiteSpace(NewCertFilePath) || !File.Exists(NewCertFilePath))
            {
                UpdateMessage = "Please choose a valid new certificate file (.crt or .pem).";
                return;
            }

            if (string.IsNullOrWhiteSpace(NewKeyFilePath) || !File.Exists(NewKeyFilePath))
            {
                UpdateMessage = "Please choose a valid private key file (.key or .pem).";
                return;
            }

            IsUpdating = true;
            UpdateMessage = "Applying new certificate files and backing up existing...";

            try
            {
                bool ok = await _sslService.UpdateCertificateAsync(
                    UpdateTargetCert,
                    NewCertFilePath,
                    NewKeyFilePath,
                    string.IsNullOrWhiteSpace(NewChainFilePath) ? null : NewChainFilePath,
                    BackupExisting);

                if (ok)
                {
                    // Refresh counts
                    HealthyCount = Certificates.Count(c => c.Status == SslCertificateStatus.Valid);
                    ExpiringSoonCount = Certificates.Count(c => c.Status == SslCertificateStatus.ExpiringSoon);
                    ProblemCount = Certificates.Count(c => c.Status == SslCertificateStatus.Expired || c.Status == SslCertificateStatus.FileNotFound || c.Status == SslCertificateStatus.ParseError);

                    UpdateMessage = "✓ SSL Certificate updated successfully! Restart Apache service to apply.";
                    IsUpdateSuccess = true;
                }
            }
            catch (Exception ex)
            {
                UpdateMessage = $"Update failed: {ex.Message}";
                IsUpdateSuccess = false;
            }
            finally
            {
                IsUpdating = false;
            }
        }

        [RelayCommand]
        public async Task RestartApacheServiceAsync()
        {
            if (IsRestartingApache) return;

            IsRestartingApache = true;
            string serviceName = _settingsService.CurrentSettings.Apache.ServiceName;
            UpdateMessage = $"Attempting to restart Apache service '{serviceName}'...";

            try
            {
                if (_serviceManager.ServiceExists(serviceName))
                {
                    var status = await _serviceManager.GetServiceStatusAsync(serviceName);
                    if (status == System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        UpdateMessage = $"Stopping Apache service '{serviceName}'...";
                        await _serviceManager.StopServiceAsync(serviceName, TimeSpan.FromSeconds(25));
                    }

                    UpdateMessage = $"Starting Apache service '{serviceName}'...";
                    bool started = await _serviceManager.StartServiceAsync(serviceName, TimeSpan.FromSeconds(25));
                    if (started)
                    {
                        UpdateMessage = $"✓ Apache service '{serviceName}' restarted successfully!";
                    }
                    else
                    {
                        UpdateMessage = $"Could not start Apache service '{serviceName}'. Please check Apache error logs.";
                    }
                }
                else
                {
                    // Fallback to httpd.exe -k restart
                    string httpdExe = Path.Combine(ApachePath, "bin", "httpd.exe");
                    if (File.Exists(httpdExe))
                    {
                        UpdateMessage = "Executing 'httpd.exe -k restart'...";
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = httpdExe,
                            Arguments = "-k restart",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync();
                            UpdateMessage = proc.ExitCode == 0
                                ? "✓ Executed 'httpd.exe -k restart' successfully!"
                                : $"httpd.exe returned code {proc.ExitCode}. Please check logs.";
                        }
                    }
                    else
                    {
                        UpdateMessage = $"Service '{serviceName}' is not installed as a Windows service. Please restart Apache via XAMPP Control Panel.";
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateMessage = $"Error restarting Apache: {ex.Message}";
            }
            finally
            {
                IsRestartingApache = false;
            }
        }
    }
}
