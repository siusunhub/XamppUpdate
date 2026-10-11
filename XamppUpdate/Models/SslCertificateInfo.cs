using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public enum SslCertificateStatus
    {
        Valid,
        ExpiringSoon,
        Expired,
        FileNotFound,
        ParseError,
        PendingIssuance
    }

    public partial class SslCertificateInfo : ObservableObject
    {
        private static readonly SolidColorBrush GreenBg = CreateFrozenBrush("#DCFCE7");
        private static readonly SolidColorBrush GreenFg = CreateFrozenBrush("#15803D");
        private static readonly SolidColorBrush AmberBg = CreateFrozenBrush("#FEF3C7");
        private static readonly SolidColorBrush AmberFg = CreateFrozenBrush("#B45309");
        private static readonly SolidColorBrush RedBg = CreateFrozenBrush("#FEE2E2");
        private static readonly SolidColorBrush RedFg = CreateFrozenBrush("#B91C1C");
        private static readonly SolidColorBrush SlateBg = CreateFrozenBrush("#F1F5F9");
        private static readonly SolidColorBrush SlateFg = CreateFrozenBrush("#64748B");
        private static readonly SolidColorBrush BlueBg = CreateFrozenBrush("#E0F2FE");
        private static readonly SolidColorBrush BlueFg = CreateFrozenBrush("#0284C7");

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        [ObservableProperty]
        private string _hostName = "Global Server / Default";

        [ObservableProperty]
        private string _port = "443";

        [ObservableProperty]
        private string _certSourceType = "Custom Config";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
        private string _certificateFilePath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeyStatusDisplayText))]
        private string _keyFilePath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasChainFile))]
        [NotifyPropertyChangedFor(nameof(ChainFileDisplayText))]
        private string _chainFilePath = string.Empty;

        [ObservableProperty]
        private string _configSourceFile = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanViewCertificate))]
        [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
        private bool _certificateFileExists;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeyStatusDisplayText))]
        private bool _keyFileExists;

        [ObservableProperty]
        private bool _chainFileExists;

        public bool HasChainFile => !string.IsNullOrWhiteSpace(ChainFilePath);

        public string ChainFileDisplayText => HasChainFile
            ? ChainFilePath
            : "None (Standalone cert or bundled in fullchain.pem)";

        [ObservableProperty]
        private string _subject = string.Empty;

        [ObservableProperty]
        private string _commonName = string.Empty;

        [ObservableProperty]
        private string _sanList = string.Empty;

        [ObservableProperty]
        private string _issuer = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidFromDisplayText))]
        private DateTime? _validFrom;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidToDisplayText))]
        private DateTime? _validTo;

        [ObservableProperty]
        private int _daysRemaining;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplayText))]
        [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
        [NotifyPropertyChangedFor(nameof(StatusBadgeForeground))]
        [NotifyPropertyChangedFor(nameof(CanViewCertificate))]
        private SslCertificateStatus _status = SslCertificateStatus.FileNotFound;

        [ObservableProperty]
        private string _errorMessage = string.Empty;

        public string ValidToDisplayText => ValidTo.HasValue ? ValidTo.Value.ToString("yyyy-MM-dd HH:mm") : "N/A";

        public string ValidFromDisplayText => ValidFrom.HasValue ? ValidFrom.Value.ToString("yyyy-MM-dd HH:mm") : "N/A";

        public string KeyStatusDisplayText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(KeyFilePath)) return "Not configured";
                return KeyFileExists ? "✓ Present on disk" : "✕ File missing on disk";
            }
        }

        public bool CanViewCertificate => CertificateFileExists && (Status == SslCertificateStatus.Valid || Status == SslCertificateStatus.ExpiringSoon || Status == SslCertificateStatus.Expired);

        public bool CanOpenFolder
        {
            get
            {
                if (string.IsNullOrWhiteSpace(CertificateFilePath)) return false;
                if (CertificateFileExists) return true;
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(CertificateFilePath);
                    return !string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir);
                }
                catch
                {
                    return false;
                }
            }
        }

        public string StatusDisplayText
        {
            get
            {
                return Status switch
                {
                    SslCertificateStatus.Valid => $"Valid ({DaysRemaining} days left)",
                    SslCertificateStatus.ExpiringSoon => $"Expiring Soon ({DaysRemaining} days left)",
                    SslCertificateStatus.Expired => $"Expired ({Math.Abs(DaysRemaining)} days ago)",
                    SslCertificateStatus.FileNotFound => "Cert File Not Found",
                    SslCertificateStatus.PendingIssuance => "Pending ACME Issuance (mod_md)",
                    SslCertificateStatus.ParseError => "Certificate Parse Error",
                    _ => "Unknown Status"
                };
            }
        }

        public Brush StatusBadgeBackground
        {
            get
            {
                return Status switch
                {
                    SslCertificateStatus.Valid => GreenBg,
                    SslCertificateStatus.ExpiringSoon => AmberBg,
                    SslCertificateStatus.Expired => RedBg,
                    SslCertificateStatus.FileNotFound => RedBg,
                    SslCertificateStatus.PendingIssuance => BlueBg,
                    _ => SlateBg
                };
            }
        }

        public Brush StatusBadgeForeground
        {
            get
            {
                return Status switch
                {
                    SslCertificateStatus.Valid => GreenFg,
                    SslCertificateStatus.ExpiringSoon => AmberFg,
                    SslCertificateStatus.Expired => RedFg,
                    SslCertificateStatus.FileNotFound => RedFg,
                    SslCertificateStatus.PendingIssuance => BlueFg,
                    _ => SlateFg
                };
            }
        }
    }
}
