using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class VHostManagerViewModel : ObservableObject
    {
        private readonly IVHostService _vhostService;
        private readonly ISettingsService _settingsService;
        private List<string> _preambleLines = new();
        private List<string> _trailingLines = new();

        public ObservableCollection<VHostEntry> Entries { get; } = new();

        public ObservableCollection<string> CertificateAgreementOptions { get; } = new()
        {
            "accepted",
        };

        public ObservableCollection<string> CertificateAuthorityOptions { get; } = new()
        {
            "https://acme-v02.api.letsencrypt.org/directory",
            "https://acme-staging-v02.api.letsencrypt.org/directory",
            "https://acme.zerossl.com/v2/DV90"
        };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEditingVHostsFile))]
        [NotifyPropertyChangedFor(nameof(IsEditingSslFile))]
        [NotifyPropertyChangedFor(nameof(IsEditingHttpdConfFile))]
        [NotifyPropertyChangedFor(nameof(CurrentConfigFileName))]
        private string _vHostFilePath = string.Empty;

        public bool IsEditingVHostsFile => Path.GetFileName(VHostFilePath).Equals("httpd-vhosts.conf", StringComparison.OrdinalIgnoreCase);
        public bool IsEditingSslFile => Path.GetFileName(VHostFilePath).Equals("httpd-ssl.conf", StringComparison.OrdinalIgnoreCase);
        public bool IsEditingHttpdConfFile => Path.GetFileName(VHostFilePath).Equals("httpd.conf", StringComparison.OrdinalIgnoreCase);
        public string CurrentConfigFileName => string.IsNullOrWhiteSpace(VHostFilePath) ? "" : Path.GetFileName(VHostFilePath);

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isSaving;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _hasUnsavedChanges;

        [ObservableProperty]
        private VHostEntry? _selectedEntry;

        partial void OnSelectedEntryChanged(VHostEntry? value)
        {
            _ = RefreshCurrentCertInfoAsync();
            OnPropertyChanged(nameof(IsModMdWarningVisible));
        }

        [ObservableProperty]
        private SslCertificateInfo? _currentCertInfo;

        [ObservableProperty]
        private bool _isLoadingCertInfo;

        [ObservableProperty]
        private VHostGlobalSettings _globalSettings = new();

        [ObservableProperty]
        private bool _isEditPanelOpen;

        [ObservableProperty]
        private bool _isGlobalSettingsPanelOpen;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsModMdWarningVisible))]
        private bool _isModMdConfigured;

        public bool IsModMdWarningVisible =>
            SelectedEntry != null &&
            SelectedEntry.IsSsl &&
            SelectedEntry.UseLetsEncrypt &&
            !IsModMdConfigured;

        [ObservableProperty]
        private string _modMdStatusMessage = "Checking mod_md status...";

        [ObservableProperty]
        private bool _isOperatingModMd;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private int _enabledCount;

        [ObservableProperty]
        private int _disabledCount;

        [ObservableProperty]
        private int _sslCount;

        [ObservableProperty]
        private string _filterText = string.Empty;

        private readonly ISslCertificateService _sslCertificateService;

        public VHostManagerViewModel(
            IVHostService vhostService,
            ISettingsService settingsService,
            ISslCertificateService? sslCertificateService = null)
        {
            _vhostService = vhostService;
            _settingsService = settingsService;
            _sslCertificateService = sslCertificateService ?? new SslCertificateService(_settingsService);

            string apachePath = _settingsService.CurrentSettings.Apache.InstallationPath;
            if (string.IsNullOrWhiteSpace(apachePath))
            {
                apachePath = @"C:\xampp\apache";
            }

            VHostFilePath = Path.Combine(apachePath, "conf", "extra", "httpd-vhosts.conf");
        }

        public async Task InitializeAsync()
        {
            await RefreshModMdStatusAsync();
            await LoadEntriesAsync();
        }

        [RelayCommand]
        public async Task LoadEntriesAsync()
        {
            if (IsLoading) return;

            IsLoading = true;
            StatusMessage = "Reading VirtualHost configurations...";
            Entries.Clear();
            SelectedEntry = null;
            IsEditPanelOpen = false;

            try
            {
                var result = await _vhostService.ParseVHostFileAsync(VHostFilePath);
                if (!result.Success)
                {
                    StatusMessage = $"Warning: Failed to parse configuration completely: {result.ErrorMessage}";
                }
                else
                {
                    _preambleLines = result.PreambleLines;
                    _trailingLines = result.TrailingLines;
                    GlobalSettings = result.GlobalSettings;

                    // The writer treats mod_md as enabled whenever an active vhost uses Let's Encrypt,
                    // so reflect that in the checkbox even if no global MD* directives were saved yet.
                    if (!GlobalSettings.EnableLetsEncrypt && result.Entries.Any(e => e.IsEnabled && e.UseLetsEncrypt))
                    {
                        string agreement = GlobalSettings.CertificateAgreement;
                        GlobalSettings.EnableLetsEncrypt = true;
                        GlobalSettings.CertificateAgreement = agreement;
                    }

                    if (string.IsNullOrWhiteSpace(GlobalSettings.CertificateAgreement))
                    {
                        GlobalSettings.CertificateAgreement = "accepted";
                    }

                    if (!string.IsNullOrWhiteSpace(GlobalSettings.CertificateAgreement) &&
                        !CertificateAgreementOptions.Contains(GlobalSettings.CertificateAgreement))
                    {
                        CertificateAgreementOptions.Add(GlobalSettings.CertificateAgreement);
                    }

                    if (!string.IsNullOrWhiteSpace(GlobalSettings.MdCertificateAuthority) &&
                        !CertificateAuthorityOptions.Contains(GlobalSettings.MdCertificateAuthority))
                    {
                        CertificateAuthorityOptions.Add(GlobalSettings.MdCertificateAuthority);
                    }

                    foreach (var entry in result.Entries)
                    {
                        // Subscribe to changes on each entry to track dirty state
                        entry.PropertyChanged += (s, e) =>
                        {
                            HasUnsavedChanges = true;
                            UpdateStats();
                            if (entry == SelectedEntry && (e.PropertyName == nameof(VHostEntry.IsSsl) ||
                                                           e.PropertyName == nameof(VHostEntry.UseLetsEncrypt) ||
                                                           e.PropertyName == nameof(VHostEntry.ServerName) ||
                                                           e.PropertyName == nameof(VHostEntry.ServerAlias) ||
                                                           e.PropertyName == nameof(VHostEntry.SslCertificateFile)))
                            {
                                _ = RefreshCurrentCertInfoAsync();
                                OnPropertyChanged(nameof(IsModMdWarningVisible));
                            }
                        };
                        Entries.Add(entry);
                    }

                    GlobalSettings.PropertyChanged += (s, e) =>
                    {
                        HasUnsavedChanges = true;
                    };

                    UpdateStats();
                    await RefreshModMdStatusAsync();
                    HasUnsavedChanges = false;

                    StatusMessage = Entries.Count > 0
                        ? $"Successfully loaded {Entries.Count} VirtualHost block(s) from {CurrentConfigFileName}."
                        : $"No <VirtualHost> entries found in {CurrentConfigFileName}." + (IsEditingHttpdConfFile ? " (Base server directives in httpd.conf are configured at root level)." : "");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading VirtualHost file: {ex.Message}";
                MessageBox.Show($"Failed to load VirtualHost file:\n{ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task SelectPresetConfigFileAsync(string presetName)
        {
            if (HasUnsavedChanges)
            {
                var confirm = MessageBox.Show(
                    "You have unsaved changes in the current file. Discard changes and switch files?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            string apacheRoot = GetApacheRoot();
            string targetPath;

            switch (presetName?.ToLowerInvariant())
            {
                case "ssl":
                case "httpd-ssl":
                case "httpd-ssl.conf":
                    targetPath = Path.Combine(apacheRoot, "conf", "extra", "httpd-ssl.conf");
                    break;
                case "httpd":
                case "httpd.conf":
                    targetPath = Path.Combine(apacheRoot, "conf", "httpd.conf");
                    break;
                case "vhosts":
                case "httpd-vhosts":
                case "httpd-vhosts.conf":
                default:
                    targetPath = Path.Combine(apacheRoot, "conf", "extra", "httpd-vhosts.conf");
                    break;
            }

            VHostFilePath = targetPath;
            await LoadEntriesAsync();
        }

        [RelayCommand]
        public async Task SaveEntriesAsync()
        {
            if (IsSaving) return;

            // Validate
            foreach (var entry in Entries)
            {
                if (entry.IsEnabled && string.IsNullOrWhiteSpace(entry.ServerName) && string.IsNullOrWhiteSpace(entry.DocumentRoot))
                {
                    var confirm = MessageBox.Show(
                        "One or more enabled VirtualHost blocks have neither ServerName nor DocumentRoot set.\nSave anyway?",
                        "Validation Warning",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;
                    break;
                }
            }

            if ((GlobalSettings.EnableLetsEncrypt || Entries.Any(e => e.IsEnabled && e.UseLetsEncrypt)) && !IsModMdConfigured)
            {
                var ask = MessageBox.Show(
                    "Let's Encrypt (mod_md) is enabled in your VirtualHost configuration, but 'mod_md' is not enabled in Apache's httpd.conf.\n\n" +
                    "Without mod_md loaded, Apache cannot manage Let's Encrypt certificates (safe fallback wrapping has been applied so Apache will not crash).\n\n" +
                    "Would you like to automatically enable mod_md and mod_watchdog in httpd.conf now?",
                    "Enable mod_md in httpd.conf?",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Cancel)
                {
                    return;
                }
                if (ask == MessageBoxResult.Yes)
                {
                    var (ok, msg) = await _vhostService.EnableModMdInHttpdConfAsync(GetApacheRoot());
                    await RefreshModMdStatusAsync();
                    if (ok)
                    {
                        GlobalSettings.EnableLetsEncrypt = true;
                    }
                    else
                    {
                        MessageBox.Show($"Could not automatically update httpd.conf:\n{msg}", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            IsSaving = true;
            StatusMessage = "Backing up and saving VirtualHost configurations...";

            try
            {
                string backup = await _vhostService.BackupConfigFileAsync(VHostFilePath);
                await _vhostService.SaveVHostFileAsync(VHostFilePath, Entries.ToList(), _preambleLines, GlobalSettings, _trailingLines);

                HasUnsavedChanges = false;
                StatusMessage = string.IsNullOrEmpty(backup)
                    ? $"Successfully saved changes to {CurrentConfigFileName}."
                    : $"Saved successfully. Backup created: {Path.GetFileName(backup)}";

                MessageBox.Show("VirtualHost configuration has been saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save configurations: {ex.Message}";
                MessageBox.Show($"Error writing VirtualHost file:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsSaving = false;
            }
        }

        [RelayCommand]
        public void AddEntry()
        {
            string apachePath = _settingsService.CurrentSettings.Apache.InstallationPath;
            string defaultHtdocs = Path.Combine(Path.GetDirectoryName(apachePath) ?? @"C:\xampp", "htdocs");

            var newEntry = new VHostEntry
            {
                ServerName = string.Empty,
                ServerAlias = string.Empty,
                DocumentRoot = defaultHtdocs,
                DirectoryIndex = "index.php index.html index.htm",
                ListenAddress = "*",
                Port = 80,
                IsEnabled = true,
                IsSsl = false,
                ErrorLog = string.Empty,
                CustomLog = string.Empty
            };

            newEntry.PropertyChanged += (s, e) =>
            {
                HasUnsavedChanges = true;
                UpdateStats();
                if (newEntry == SelectedEntry && (e.PropertyName == nameof(VHostEntry.IsSsl) ||
                                               e.PropertyName == nameof(VHostEntry.UseLetsEncrypt) ||
                                               e.PropertyName == nameof(VHostEntry.ServerName) ||
                                               e.PropertyName == nameof(VHostEntry.ServerAlias) ||
                                               e.PropertyName == nameof(VHostEntry.SslCertificateFile)))
                {
                    _ = RefreshCurrentCertInfoAsync();
                    OnPropertyChanged(nameof(IsModMdWarningVisible));
                }
            };

            _isAddingNewEntry = true;
            _entrySnapshot = null;
            Entries.Add(newEntry);
            SelectedEntry = newEntry;
            IsEditPanelOpen = true;
            HasUnsavedChanges = true;
            UpdateStats();
            StatusMessage = "New VirtualHost added. Configure settings below and click 'Save & Close' or 'Discard & Close'.";
        }

        [RelayCommand]
        public void DeleteEntry(VHostEntry? entry)
        {
            var target = entry ?? SelectedEntry;
            if (target == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the VirtualHost entry '{target.DisplayText}' (Port {target.Port})?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                Entries.Remove(target);
                if (SelectedEntry == target)
                {
                    SelectedEntry = null;
                    IsEditPanelOpen = false;
                }
                HasUnsavedChanges = true;
                UpdateStats();
                StatusMessage = $"Removed VirtualHost '{target.DisplayText}'.";
            }
        }

        [RelayCommand]
        public void ToggleEnabled(VHostEntry? entry)
        {
            var target = entry ?? SelectedEntry;
            if (target == null) return;

            target.IsEnabled = !target.IsEnabled;
            HasUnsavedChanges = true;
            UpdateStats();
            StatusMessage = $"VirtualHost '{target.DisplayText}' is now {(target.IsEnabled ? "Enabled" : "Disabled")}.";
        }

        private bool _isAddingNewEntry;
        private VHostEntry? _entrySnapshot;

        [RelayCommand]
        public void EditEntry(VHostEntry? entry)
        {
            if (entry != null)
            {
                _isAddingNewEntry = false;
                _entrySnapshot = entry.Clone();
                SelectedEntry = entry;
                IsEditPanelOpen = true;
                _ = RefreshCurrentCertInfoAsync();
                StatusMessage = $"Editing VirtualHost '{entry.DisplayText}'. Click 'Save & Close' when finished.";
            }
        }

        [RelayCommand]
        public void SaveAndCloseEdit()
        {
            if (SelectedEntry == null)
            {
                IsEditPanelOpen = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedEntry.ServerName) && string.IsNullOrWhiteSpace(SelectedEntry.DocumentRoot))
            {
                var confirm = MessageBox.Show(
                    "Both ServerName and DocumentRoot are blank. Do you want to keep this VirtualHost anyway?",
                    "Missing Information",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            _isAddingNewEntry = false;
            _entrySnapshot = null;
            IsEditPanelOpen = false;
            HasUnsavedChanges = true;
            UpdateStats();
            StatusMessage = $"Saved settings for '{SelectedEntry.DisplayText}'. Click 'Save Changes' to apply to file.";
        }

        [RelayCommand]
        public void DiscardAndCloseEdit()
        {
            if (SelectedEntry == null)
            {
                IsEditPanelOpen = false;
                return;
            }

            if (_isAddingNewEntry)
            {
                Entries.Remove(SelectedEntry);
                SelectedEntry = null;
                _isAddingNewEntry = false;
                _entrySnapshot = null;
                StatusMessage = "Discarded new VirtualHost.";
            }
            else if (_entrySnapshot != null)
            {
                SelectedEntry.ServerName = _entrySnapshot.ServerName;
                SelectedEntry.ServerAlias = _entrySnapshot.ServerAlias;
                SelectedEntry.DocumentRoot = _entrySnapshot.DocumentRoot;
                SelectedEntry.Port = _entrySnapshot.Port;
                SelectedEntry.ListenAddress = _entrySnapshot.ListenAddress;
                SelectedEntry.IsEnabled = _entrySnapshot.IsEnabled;
                SelectedEntry.IsSsl = _entrySnapshot.IsSsl;
                SelectedEntry.SslCertificateFile = _entrySnapshot.SslCertificateFile;
                SelectedEntry.SslCertificateKeyFile = _entrySnapshot.SslCertificateKeyFile;
                SelectedEntry.SslCertificateChainFile = _entrySnapshot.SslCertificateChainFile;
                SelectedEntry.ErrorLog = _entrySnapshot.ErrorLog;
                SelectedEntry.CustomLog = _entrySnapshot.CustomLog;
                SelectedEntry.DirectoryIndex = _entrySnapshot.DirectoryIndex;
                SelectedEntry.UseLetsEncrypt = _entrySnapshot.UseLetsEncrypt;
                _entrySnapshot = null;
                StatusMessage = $"Unchanged: Discarded edits for '{SelectedEntry.DisplayText}'.";
            }

            IsEditPanelOpen = false;
            UpdateStats();
        }

        [RelayCommand]
        public void CloseEditPanel()
        {
            SaveAndCloseEdit();
        }

        [RelayCommand]
        public void ToggleGlobalSettingsPanel()
        {
            IsGlobalSettingsPanelOpen = !IsGlobalSettingsPanelOpen;
            if (IsGlobalSettingsPanelOpen)
            {
                _ = RefreshModMdStatusAsync();
            }
        }

        public string GetApacheRoot()
        {
            if (!string.IsNullOrWhiteSpace(VHostFilePath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(VHostFilePath) ?? string.Empty;
                    if (dir.EndsWith("conf\\extra", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("conf/extra", StringComparison.OrdinalIgnoreCase))
                    {
                        string? confDir = Path.GetDirectoryName(dir);
                        string? rootDir = Path.GetDirectoryName(confDir);
                        if (!string.IsNullOrWhiteSpace(rootDir) && Directory.Exists(rootDir)) return rootDir;
                    }
                    else if (dir.EndsWith("conf", StringComparison.OrdinalIgnoreCase))
                    {
                        string? rootDir = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrWhiteSpace(rootDir) && Directory.Exists(rootDir)) return rootDir;
                    }
                }
                catch { }
            }

            string fromSettings = _settingsService?.CurrentSettings?.Apache?.InstallationPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromSettings) && Directory.Exists(fromSettings)) return fromSettings;

            return @"C:\xampp\apache";
        }

        public async Task RefreshModMdStatusAsync()
        {
            var status = await _vhostService.CheckModMdStatusAsync(GetApacheRoot());
            IsModMdConfigured = status.IsFullyConfigured;
            ModMdStatusMessage = status.StatusMessage;
            OnPropertyChanged(nameof(IsModMdWarningVisible));
        }

        [RelayCommand]
        public async Task EnableModMdAsync()
        {
            if (IsOperatingModMd) return;

            string apacheRoot = GetApacheRoot();
            var confirm = MessageBox.Show(
                $"This will enable 'LoadModule md_module' and 'LoadModule watchdog_module' in Apache's httpd.conf so Let's Encrypt automated certificates can function.\n\nApache Path: {apacheRoot}\nA backup of httpd.conf will be created automatically before making changes.\n\nDo you want to proceed?",
                "Enable mod_md in httpd.conf",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsOperatingModMd = true;
            try
            {
                StatusMessage = "Enabling mod_md in httpd.conf...";
                var (ok, msg) = await _vhostService.EnableModMdInHttpdConfAsync(apacheRoot);
                await RefreshModMdStatusAsync();
                StatusMessage = msg;

                if (ok)
                {
                    // Enabling the module means the user wants automated SSL: tick the global option so it is
                    // written to the vhost file on the next save (this also flags unsaved changes).
                    GlobalSettings.EnableLetsEncrypt = true;

                    MessageBox.Show(
                        msg + "\n\n'Enable mod_md Automated SSL' has been ticked; click Save to write the global settings." +
                        "\nNote: If Apache is currently running, restart Apache to load the new modules.",
                        "mod_md Enabled",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to enable mod_md in httpd.conf:\n{msg}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                IsOperatingModMd = false;
            }
        }

        [RelayCommand]
        public void OnSslToggled()
        {
            if (SelectedEntry == null) return;

            if (SelectedEntry.IsSsl && SelectedEntry.Port == 80)
            {
                SelectedEntry.Port = 443;
            }
            else if (!SelectedEntry.IsSsl && SelectedEntry.Port == 443)
            {
                SelectedEntry.Port = 80;
            }

            _ = RefreshCurrentCertInfoAsync();
            OnPropertyChanged(nameof(IsModMdWarningVisible));
        }

        [RelayCommand]
        public void SetDefaultDirectoryIndex()
        {
            if (SelectedEntry != null)
            {
                SelectedEntry.DirectoryIndex = "index.php index.html index.htm";
                HasUnsavedChanges = true;
            }
        }

        [RelayCommand]
        public void BrowseVHostFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Apache httpd-vhosts.conf File",
                Filter = "Apache Config (*.conf)|*.conf|All Files (*.*)|*.*",
                FileName = VHostFilePath,
                InitialDirectory = File.Exists(VHostFilePath) ? Path.GetDirectoryName(VHostFilePath) : @"C:\xampp\apache\conf\extra"
            };

            if (dlg.ShowDialog() == true)
            {
                VHostFilePath = dlg.FileName;
                _ = LoadEntriesAsync();
            }
        }

        [RelayCommand]
        public void BrowseDocumentRoot()
        {
            if (SelectedEntry == null) return;

            var dlg = new OpenFolderDialog
            {
                Title = "Select VirtualHost DocumentRoot Directory",
                InitialDirectory = Directory.Exists(SelectedEntry.DocumentRoot)
                    ? SelectedEntry.DocumentRoot
                    : @"C:\xampp\htdocs"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.DocumentRoot = dlg.FolderName;
                HasUnsavedChanges = true;
            }
        }

        [RelayCommand]
        public void BrowseSslCert()
        {
            if (SelectedEntry == null) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select SSL Certificate File (SSLCertificateFile)",
                Filter = "Certificate Files (*.crt;*.cer;*.pem)|*.crt;*.cer;*.pem|All Files (*.*)|*.*",
                InitialDirectory = @"C:\xampp\apache\conf\ssl.crt"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.SslCertificateFile = dlg.FileName;
                SelectedEntry.IsSsl = true;
                HasUnsavedChanges = true;
                _ = RefreshCurrentCertInfoAsync();
            }
        }

        [RelayCommand]
        public void BrowseSslKey()
        {
            if (SelectedEntry == null) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select SSL Private Key File (SSLCertificateKeyFile)",
                Filter = "Key Files (*.key;*.pem)|*.key;*.pem|All Files (*.*)|*.*",
                InitialDirectory = @"C:\xampp\apache\conf\ssl.key"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.SslCertificateKeyFile = dlg.FileName;
                SelectedEntry.IsSsl = true;
                HasUnsavedChanges = true;
                _ = RefreshCurrentCertInfoAsync();
            }
        }

        [RelayCommand]
        public void BrowseSslChain()
        {
            if (SelectedEntry == null) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select SSL Certificate Chain File (SSLCertificateChainFile)",
                Filter = "Certificate Files (*.crt;*.cer;*.pem)|*.crt;*.cer;*.pem|All Files (*.*)|*.*",
                InitialDirectory = @"C:\xampp\apache\conf\ssl.crt"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.SslCertificateChainFile = dlg.FileName;
                SelectedEntry.IsSsl = true;
                HasUnsavedChanges = true;
                _ = RefreshCurrentCertInfoAsync();
            }
        }

        [RelayCommand]
        public void BrowseErrorLog()
        {
            if (SelectedEntry == null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Select or Specify Error Log File",
                Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                FileName = !string.IsNullOrWhiteSpace(SelectedEntry.ErrorLog)
                    ? Path.GetFileName(SelectedEntry.ErrorLog)
                    : $"{SelectedEntry.DisplayText}-error.log",
                InitialDirectory = @"C:\xampp\apache\logs"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.ErrorLog = dlg.FileName;
                HasUnsavedChanges = true;
            }
        }

        [RelayCommand]
        public void BrowseCustomLog()
        {
            if (SelectedEntry == null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Select or Specify Access Log File",
                Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                FileName = !string.IsNullOrWhiteSpace(SelectedEntry.CustomLog)
                    ? Path.GetFileName(SelectedEntry.CustomLog)
                    : $"{SelectedEntry.DisplayText}-access.log",
                InitialDirectory = @"C:\xampp\apache\logs"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedEntry.CustomLog = dlg.FileName;
                HasUnsavedChanges = true;
            }
        }

        [RelayCommand]
        public void OpenInTextEditor()
        {
            if (File.Exists(VHostFilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = $"\"{VHostFilePath}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open editor: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show($"File does not exist yet:\n{VHostFilePath}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        public void OpenDocRootInExplorer(VHostEntry? entry)
        {
            var target = entry ?? SelectedEntry;
            if (target != null && Directory.Exists(target.DocumentRoot))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target.DocumentRoot,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public async Task RefreshCurrentCertInfoAsync()
        {
            if (SelectedEntry == null || !SelectedEntry.IsSsl)
            {
                CurrentCertInfo = null;
                return;
            }

            try
            {
                IsLoadingCertInfo = true;
                string apachePath = _settingsService.CurrentSettings.Apache.InstallationPath;
                CurrentCertInfo = await _sslCertificateService.ResolveVHostCertificateAsync(SelectedEntry, apachePath);
            }
            catch (Exception ex)
            {
                CurrentCertInfo = new SslCertificateInfo
                {
                    HostName = SelectedEntry.DisplayText,
                    Status = SslCertificateStatus.ParseError,
                    ErrorMessage = $"Error resolving certificate: {ex.Message}"
                };
            }
            finally
            {
                IsLoadingCertInfo = false;
            }
        }

        [RelayCommand]
        public async Task RefreshCertInfoAsync()
        {
            await RefreshCurrentCertInfoAsync();
        }

        [RelayCommand]
        public void ViewCertificateInViewer()
        {
            if (CurrentCertInfo != null && !string.IsNullOrWhiteSpace(CurrentCertInfo.CertificateFilePath))
            {
                _sslCertificateService.OpenCertificateInViewer(CurrentCertInfo.CertificateFilePath);
            }
            else
            {
                MessageBox.Show("No certificate file is available to view.", "View Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        public void OpenCertFolderInExplorer()
        {
            if (CurrentCertInfo != null && !string.IsNullOrWhiteSpace(CurrentCertInfo.CertificateFilePath))
            {
                _sslCertificateService.OpenFileInExplorer(CurrentCertInfo.CertificateFilePath);
            }
        }

        [RelayCommand]
        public async Task ViewEntryCertificateAsync(VHostEntry? entry)
        {
            var target = entry ?? SelectedEntry;
            if (target == null) return;

            if (!target.IsSsl)
            {
                MessageBox.Show($"VirtualHost '{target.DisplayText}' does not have SSL enabled.", "SSL Not Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EditEntry(target);
            await RefreshCurrentCertInfoAsync();

            if (CurrentCertInfo != null && CurrentCertInfo.CertificateFileExists)
            {
                _sslCertificateService.OpenCertificateInViewer(CurrentCertInfo.CertificateFilePath);
            }
        }

        private void UpdateStats()
        {
            TotalCount = Entries.Count;
            EnabledCount = Entries.Count(e => e.IsEnabled);
            DisabledCount = Entries.Count(e => !e.IsEnabled);
            SslCount = Entries.Count(e => e.IsSsl);
        }
    }
}
