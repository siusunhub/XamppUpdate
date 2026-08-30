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
        ParseError
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
        private string _certificateFilePath = string.Empty;

        [ObservableProperty]
        private string _keyFilePath = string.Empty;

        [ObservableProperty]
        private string _chainFilePath = string.Empty;

        [ObservableProperty]
        private string _configSourceFile = string.Empty;

        [ObservableProperty]
        private bool _certificateFileExists;

        [ObservableProperty]
        private bool _keyFileExists;

        [ObservableProperty]
        private string _subject = string.Empty;

        [ObservableProperty]
        private string _commonName = string.Empty;

        [ObservableProperty]
        private string _issuer = string.Empty;

        [ObservableProperty]
        private DateTime? _validFrom;

        [ObservableProperty]
        private DateTime? _validTo;

        [ObservableProperty]
        private int _daysRemaining;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplayText))]
        [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
        [NotifyPropertyChangedFor(nameof(StatusBadgeForeground))]
        private SslCertificateStatus _status = SslCertificateStatus.FileNotFound;

        [ObservableProperty]
        private string _errorMessage = string.Empty;

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
                    _ => SlateFg
                };
            }
        }
    }
}
