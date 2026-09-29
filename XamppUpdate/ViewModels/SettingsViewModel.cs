using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settingsService;

        [ObservableProperty]
        private string _apacheInstallationPath = string.Empty;

        [ObservableProperty]
        private string _apacheServiceName = string.Empty;

        [ObservableProperty]
        private string _phpInstallationPath = string.Empty;

        [ObservableProperty]
        private string _phpIniPath = string.Empty;

        [ObservableProperty]
        private string _mySqlInstallationPath = string.Empty;

        [ObservableProperty]
        private string _mySqlServiceName = string.Empty;

        [ObservableProperty]
        private string _phpMyAdminInstallationPath = string.Empty;

        [ObservableProperty]
        private string _composerExecutablePath = string.Empty;

        [ObservableProperty]
        private string _composerDownloadTargetPath = string.Empty;

        [ObservableProperty]
        private string _composerWwwTargetPath = string.Empty;

        [ObservableProperty]
        private string _sslCertificatesPath = string.Empty;

        [ObservableProperty]
        private string _sslKeysPath = string.Empty;

        [ObservableProperty]
        private string _backupDirectory = string.Empty;

        [ObservableProperty]
        private string _tempDirectory = string.Empty;

        [ObservableProperty]
        private int _maxBackupRetentionCount = 10;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public event Action? RequestClose;

        public SettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            LoadCurrentSettings();
        }

        public void LoadCurrentSettings()
        {
            var s = _settingsService.CurrentSettings;

            ApacheInstallationPath = s.Apache.InstallationPath;
            ApacheServiceName = s.Apache.ServiceName;

            PhpInstallationPath = s.Php.InstallationPath;
            PhpIniPath = s.Php.IniPath;

            MySqlInstallationPath = s.MySql.InstallationPath;
            MySqlServiceName = s.MySql.ServiceName;

            PhpMyAdminInstallationPath = s.PhpMyAdmin.InstallationPath;

            ComposerExecutablePath = s.Composer.ExecutablePath;
            ComposerDownloadTargetPath = s.Composer.DownloadTargetPath;
            ComposerWwwTargetPath = s.Composer.WwwTargetPath;

            SslCertificatesPath = s.Ssl.CertificatesPath;
            SslKeysPath = s.Ssl.KeysPath;

            BackupDirectory = s.General.BackupDirectory;
            TempDirectory = s.General.TempDirectory;
            MaxBackupRetentionCount = s.General.MaxBackupRetentionCount;

            StatusMessage = "Settings loaded.";
        }

        [RelayCommand]
        public async Task SaveSettingsAsync()
        {
            try
            {
                var s = _settingsService.CurrentSettings;

                s.Apache.InstallationPath = ApacheInstallationPath;
                s.Apache.ServiceName = ApacheServiceName;

                s.Php.InstallationPath = PhpInstallationPath;
                s.Php.IniPath = PhpIniPath;

                s.MySql.InstallationPath = MySqlInstallationPath;
                s.MySql.ServiceName = MySqlServiceName;

                s.PhpMyAdmin.InstallationPath = PhpMyAdminInstallationPath;

                s.Composer.ExecutablePath = ComposerExecutablePath;
                s.Composer.DownloadTargetPath = ComposerDownloadTargetPath;
                s.Composer.WwwTargetPath = ComposerWwwTargetPath;

                s.Ssl.CertificatesPath = SslCertificatesPath;
                s.Ssl.KeysPath = SslKeysPath;

                s.General.BackupDirectory = BackupDirectory;
                s.General.TempDirectory = TempDirectory;
                s.General.MaxBackupRetentionCount = MaxBackupRetentionCount;

                await _settingsService.SaveSettingsAsync(s);
                StatusMessage = "Saved successfully!";
                RequestClose?.Invoke();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving: {ex.Message}";
            }
        }

        [RelayCommand]
        public void Cancel()
        {
            RequestClose?.Invoke();
        }

        [RelayCommand]
        public void ResetDefaults()
        {
            _settingsService.ResetToDefaults();
            LoadCurrentSettings();
            StatusMessage = "Reset to default settings.";
        }

        [RelayCommand]
        public void BrowseApacheFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Apache Installation Folder",
                InitialDirectory = Directory.Exists(ApacheInstallationPath) ? ApacheInstallationPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                ApacheInstallationPath = dialog.FolderName;
            }
        }

        [RelayCommand]
        public void BrowsePhpFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select PHP Installation Folder",
                InitialDirectory = Directory.Exists(PhpInstallationPath) ? PhpInstallationPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                PhpInstallationPath = dialog.FolderName;
                PhpIniPath = Path.Combine(dialog.FolderName, "php.ini");
            }
        }

        [RelayCommand]
        public void BrowseMySqlFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select MySQL Installation Folder",
                InitialDirectory = Directory.Exists(MySqlInstallationPath) ? MySqlInstallationPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                MySqlInstallationPath = dialog.FolderName;
            }
        }

        [RelayCommand]
        public void BrowsePhpMyAdminFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select phpMyAdmin Installation Folder",
                InitialDirectory = Directory.Exists(PhpMyAdminInstallationPath) ? PhpMyAdminInstallationPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                PhpMyAdminInstallationPath = dialog.FolderName;
            }
        }

        [RelayCommand]
        public void BrowseComposerExecutable()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Composer Executable (composer.bat or composer.phar)",
                Filter = "Composer Executable (*.bat;*.phar;*.exe)|*.bat;*.phar;*.exe|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                ComposerExecutablePath = dialog.FileName;
            }
        }

        [RelayCommand]
        public void BrowseComposerDownloadTarget()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Composer Download Target Folder (Archive Retention)",
                InitialDirectory = Directory.Exists(ComposerDownloadTargetPath) ? ComposerDownloadTargetPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                ComposerDownloadTargetPath = dialog.FolderName;
            }
        }

        [RelayCommand]
        public void BrowseComposerWwwTarget()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Composer WWW Target Folder (Real Web Access)",
                InitialDirectory = Directory.Exists(ComposerWwwTargetPath) ? ComposerWwwTargetPath : @"C:\"
            };
            if (dialog.ShowDialog() == true)
            {
                ComposerWwwTargetPath = dialog.FolderName;
            }
        }
    }
}
