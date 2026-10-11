using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class VHostEntry : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private string _serverName = string.Empty;

        [ObservableProperty]
        private string _serverAlias = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private string _documentRoot = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PortDisplayText))]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private int _port = 80;

        partial void OnPortChanged(int value)
        {
            if (value == 443 && !IsSsl)
            {
                IsSsl = true;
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PortDisplayText))]
        private string _listenAddress = "*";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
        private bool _isEnabled = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SslBadgeText))]
        private bool _isSsl;

        partial void OnIsSslChanged(bool value)
        {
            if (value && Port == 80)
            {
                Port = 443;
            }
            else if (!value && Port == 443)
            {
                Port = 80;
            }
        }

        [ObservableProperty]
        private string _sslCertificateFile = string.Empty;

        [ObservableProperty]
        private string _sslCertificateKeyFile = string.Empty;

        [ObservableProperty]
        private string _sslCertificateChainFile = string.Empty;

        [ObservableProperty]
        private string _errorLog = string.Empty;

        [ObservableProperty]
        private string _customLog = string.Empty;

        [ObservableProperty]
        private string _directoryIndex = string.Empty;

        [ObservableProperty]
        private bool _useLetsEncrypt;

        [ObservableProperty]
        private bool _isParseError;

        [ObservableProperty]
        private string _parseErrorMessage = string.Empty;

        public List<string> RawLines { get; set; } = new();
        public List<string> LeadingLines { get; set; } = new();
        public List<string> AdditionalDirectives { get; set; } = new();

        // UI Computed Properties
        public string DisplayText => string.IsNullOrWhiteSpace(ServerName) ? "(Unnamed vHost)" : ServerName;

        public string PortDisplayText => $"{ListenAddress}:{Port}";

        public string StatusBadgeText => IsEnabled ? "Enabled" : "Disabled";

        public string SslBadgeText => IsSsl ? "🔒 SSL" : "HTTP";

        public string SummaryText => $"{DisplayText} ({PortDisplayText}) -> {DocumentRoot}";

        public VHostEntry Clone()
        {
            return new VHostEntry
            {
                ServerName = ServerName,
                ServerAlias = ServerAlias,
                DocumentRoot = DocumentRoot,
                Port = Port,
                ListenAddress = ListenAddress,
                IsEnabled = IsEnabled,
                IsSsl = IsSsl,
                SslCertificateFile = SslCertificateFile,
                SslCertificateKeyFile = SslCertificateKeyFile,
                SslCertificateChainFile = SslCertificateChainFile,
                ErrorLog = ErrorLog,
                CustomLog = CustomLog,
                DirectoryIndex = DirectoryIndex,
                UseLetsEncrypt = UseLetsEncrypt,
                IsParseError = IsParseError,
                ParseErrorMessage = ParseErrorMessage,
                RawLines = new List<string>(RawLines),
                LeadingLines = new List<string>(LeadingLines),
                AdditionalDirectives = new List<string>(AdditionalDirectives)
            };
        }
    }
}
