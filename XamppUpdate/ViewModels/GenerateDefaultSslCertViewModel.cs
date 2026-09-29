using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class GenerateDefaultSslCertViewModel : ViewModelBase
    {
        private readonly ISslCertificateService _sslService;
        private readonly ISettingsService _settingsService;

        public event Action? RequestClose;

        public Func<string, string, bool> ConfirmAction { get; set; } = (msg, title) =>
            MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        [ObservableProperty]
        private SslCertificateInfo? _currentCertInfo;

        [ObservableProperty]
        private bool _isCurrentCertLoaded;

        [ObservableProperty]
        private string _apacheInstallationPath = @"C:\xampp\apache";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _country = "US";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _state = "Local";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _locality = "Local";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _organization = "Development";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _organizationalUnit = "IT";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _commonName = "localhost";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private int _validityDays = 3650;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private int _keySize = 2048;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _dnsNames = "localhost";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigPreviewText))]
        [NotifyPropertyChangedFor(nameof(CommandPreviewText))]
        private string _ipAddresses = "127.0.0.1";

        [ObservableProperty]
        private bool _isGenerating;

        [ObservableProperty]
        private bool _isSuccess;

        [ObservableProperty]
        private string _statusMessage = "Ready to generate default SSL certificate.";

        [ObservableProperty]
        private ObservableCollection<string> _executionLogs = new();

        public string ConfigPreviewText => BuildRequest().GenerateConfigFileContent();

        public string CommandPreviewText => $"cd {ApacheInstallationPath}\r\n" + BuildRequest().GetOpenSslCommandLine();

        public GenerateDefaultSslCertViewModel(ISslCertificateService sslService, ISettingsService settingsService)
        {
            _sslService = sslService;
            _settingsService = settingsService;

            var settings = _settingsService.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.Apache.InstallationPath) && Directory.Exists(settings.Apache.InstallationPath))
            {
                ApacheInstallationPath = settings.Apache.InstallationPath;
            }
            else
            {
                ApacheInstallationPath = @"C:\xampp\apache";
            }

            _ = LoadCurrentDefaultCertificateAsync();
        }

        public async Task LoadCurrentDefaultCertificateAsync()
        {
            try
            {
                StatusMessage = "Reading current default XAMPP SSL certificate...";
                CurrentCertInfo = await _sslService.ReadDefaultXamppCertificateAsync(ApacheInstallationPath);
                IsCurrentCertLoaded = true;

                if (CurrentCertInfo.Status == SslCertificateStatus.Expired)
                {
                    StatusMessage = $"⚠️ Default certificate is EXPIRED (expired on {CurrentCertInfo.ValidTo:yyyy-MM-dd}). Generating a fresh 10-year certificate is highly recommended.";
                }
                else if (CurrentCertInfo.Status == SslCertificateStatus.FileNotFound)
                {
                    StatusMessage = "⚠️ No default server.crt found in conf/ssl.crt/. Generating a new certificate is recommended.";
                }
                else
                {
                    StatusMessage = $"Current certificate expires on {CurrentCertInfo.ValidTo:yyyy-MM-dd} ({CurrentCertInfo.DaysRemaining} days remaining).";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error reading default certificate: {ex.Message}";
                IsCurrentCertLoaded = true;
            }
        }

        private GenerateDefaultSslCertRequest BuildRequest()
        {
            return new GenerateDefaultSslCertRequest
            {
                ApachePath = ApacheInstallationPath,
                Country = Country,
                State = State,
                Locality = Locality,
                Organization = Organization,
                OrganizationalUnit = OrganizationalUnit,
                CommonName = CommonName,
                Days = ValidityDays,
                KeyBits = KeySize,
                DnsNames = DnsNames,
                IpAddresses = IpAddresses
            };
        }

        [RelayCommand]
        public async Task GenerateDefaultCertAsync()
        {
            if (IsGenerating) return;

            string confirmMsg = $"Are you sure you want to generate and install the default XAMPP SSL Certificate?\n\n" +
                               $"Target Directory: {ApacheInstallationPath}\n" +
                               $"Certificate: conf\\ssl.crt\\server.crt\n" +
                               $"Private Key: conf\\ssl.key\\server.key\n" +
                               $"Validity: {ValidityDays} days (10 years)\n\n" +
                               $"Command:\n{BuildRequest().GetOpenSslCommandLine()}";

            if (!ConfirmAction(confirmMsg, "Confirm SSL Certificate Generation"))
            {
                return;
            }

            IsGenerating = true;
            IsSuccess = false;
            ExecutionLogs.Clear();
            AppendLog("[START] Initiating OpenSSL execution...");
            AppendLog($"[TARGET] Apache root directory: {ApacheInstallationPath}");
            StatusMessage = "Generating default SSL certificate with OpenSSL...";

            var progress = new Progress<string>(msg =>
            {
                AppendLog(msg);
            });

            try
            {
                var req = BuildRequest();
                var result = await _sslService.GenerateDefaultCertificateAsync(req, progress);

                if (result.Success)
                {
                    IsSuccess = true;
                    if (result.UpdatedCertInfo != null)
                    {
                        CurrentCertInfo = result.UpdatedCertInfo;
                    }
                    StatusMessage = $"✅ Default SSL certificate successfully generated and installed! Valid until {CurrentCertInfo?.ValidTo:yyyy-MM-dd}.";
                    AppendLog($"[SUCCESS] Installed server.crt (valid until {CurrentCertInfo?.ValidTo:yyyy-MM-dd}).");
                }
                else
                {
                    IsSuccess = false;
                    StatusMessage = $"❌ Failed to generate default certificate: {result.ErrorMessage}";
                    AppendLog($"[ERROR] {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                StatusMessage = $"❌ Error: {ex.Message}";
                AppendLog($"[EXCEPTION] {ex.Message}");
            }
            finally
            {
                IsGenerating = false;
            }
        }

        private void AppendLog(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ExecutionLogs.Add(line));
            }
            else
            {
                ExecutionLogs.Add(line);
            }
        }

        [RelayCommand]
        public void CopyLogs()
        {
            if (ExecutionLogs.Count == 0) return;
            try
            {
                string all = string.Join(Environment.NewLine, ExecutionLogs);
                Clipboard.SetText(all);
            }
            catch { }
        }

        [RelayCommand]
        public void Close()
        {
            RequestClose?.Invoke();
        }
    }
}
