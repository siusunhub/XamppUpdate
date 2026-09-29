using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XamppUpdate.Services;

namespace XamppUpdate.ViewModels
{
    public partial class ComposerViewModel : ObservableObject
    {
        private readonly IComposerService _composerService;
        private readonly ISettingsService _settingsService;
        private CancellationTokenSource? _cts;

        public Func<string, string, bool> ConfirmAction { get; set; } = (msg, title) =>
            MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        [ObservableProperty]
        private string _composerExecutablePath = string.Empty;

        [ObservableProperty]
        private string _downloadTargetPath = string.Empty;

        [ObservableProperty]
        private string _wwwTargetPath = string.Empty;

        [ObservableProperty]
        private string _composerVersion = "Detecting...";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isExecuting;

        [ObservableProperty]
        private bool _isTestMode;

        [ObservableProperty]
        private bool _isWithAllDependencies;

        public bool IsForceMajorUpgrade
        {
            get => IsWithAllDependencies;
            set => IsWithAllDependencies = value;
        }

        [ObservableProperty]
        private string _statusMessage = "Ready.";

        public ObservableCollection<string> ExecutionLogs { get; } = new();

        public event Action? RequestClose;

        public ComposerViewModel(IComposerService composerService, ISettingsService settingsService)
        {
            _composerService = composerService;
            _settingsService = settingsService;

            var settings = _settingsService.CurrentSettings;
            ComposerExecutablePath = settings.Composer.ExecutablePath;
            DownloadTargetPath = settings.Composer.DownloadTargetPath;
            WwwTargetPath = settings.Composer.WwwTargetPath;

            _ = RefreshVersionAsync();
        }

        public async Task RefreshVersionAsync()
        {
            if (string.IsNullOrWhiteSpace(ComposerExecutablePath))
            {
                ComposerVersion = "Not Configured";
                return;
            }

            ComposerVersion = "Detecting...";
            ComposerVersion = await _composerService.DetectVersionAsync(ComposerExecutablePath);
        }

        [RelayCommand]
        public void BrowseComposerExecutable()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Composer Executable (composer.bat or composer.phar)",
                Filter = "Composer Executables (*.bat;*.phar;*.exe;*.cmd)|*.bat;*.phar;*.exe;*.cmd|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ComposerExecutablePath = dialog.FileName;
                PersistSettings();
                _ = RefreshVersionAsync();
            }
        }

        [RelayCommand]
        public void BrowseDownloadTarget()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Download Target Directory (Archive Retention / Backup Copies)",
                InitialDirectory = Directory.Exists(DownloadTargetPath) ? DownloadTargetPath : @"C:\"
            };

            if (dialog.ShowDialog() == true)
            {
                DownloadTargetPath = dialog.FolderName;
                PersistSettings();
            }
        }

        [RelayCommand]
        public void BrowseWwwTarget()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select WWW Target Directory (Real Folder for Web Access / composer.json)",
                InitialDirectory = Directory.Exists(WwwTargetPath) ? WwwTargetPath : @"C:\xampp\htdocs"
            };

            if (dialog.ShowDialog() == true)
            {
                WwwTargetPath = dialog.FolderName;
                PersistSettings();
            }
        }

        [RelayCommand]
        public async Task OpenComposerJsonAsync()
        {
            try
            {
                string targetDir = !string.IsNullOrWhiteSpace(DownloadTargetPath)
                    ? DownloadTargetPath
                    : WwwTargetPath;

                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Target directory does not exist: '{targetDir}'. Please select a valid Download Target Directory.");
                    StatusMessage = "Download directory not found.";
                    return;
                }

                string jsonPath = Path.Combine(targetDir, "composer.json");
                if (!File.Exists(jsonPath))
                {
                    string defaultJson = "{\n    \"name\": \"app/project\",\n    \"require\": {\n    }\n}\n";
                    await File.WriteAllTextAsync(jsonPath, defaultJson, new System.Text.UTF8Encoding(false));
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [INFO] Created starter composer.json at: {jsonPath}");
                }
                else
                {
                    await ComposerService.StripBomIfPresentAsync(jsonPath);
                }

                string editorExe = await Task.Run(() => ResolveTextEditorExecutable());

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editorExe,
                        Arguments = $"\"{jsonPath}\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Fallback to notepad
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = $"\"{jsonPath}\"",
                        UseShellExecute = true
                    });
                }

                string editorName = Path.GetFileNameWithoutExtension(editorExe);
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [INFO] Opened '{jsonPath}' using {editorName}");
                StatusMessage = $"Opened composer.json using {editorName}.";
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Could not open composer.json: {ex.Message}");
                StatusMessage = $"Could not open composer.json: {ex.Message}";
            }
        }

        public static string ResolveTextEditorExecutable()
        {
            // 1. Notepad++
            string? npp = FindAppInRegistry("notepad++.exe")
                ?? CheckPath(@"C:\Program Files\Notepad++\notepad++.exe")
                ?? CheckPath(@"C:\Program Files (x86)\Notepad++\notepad++.exe")
                ?? CheckPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Notepad++\notepad++.exe"))
                ?? FindInPath("notepad++.exe");

            if (!string.IsNullOrEmpty(npp)) return npp;

            // 2. EmEditor
            string? emeditor = FindAppInRegistry("EmEditor.exe")
                ?? CheckPath(@"C:\Program Files\EmEditor\EmEditor.exe")
                ?? CheckPath(@"C:\Program Files (x86)\EmEditor\EmEditor.exe")
                ?? CheckPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\EmEditor\EmEditor.exe"))
                ?? FindInPath("EmEditor.exe");

            if (!string.IsNullOrEmpty(emeditor)) return emeditor;

            // 3. Other popular code editors (VS Code, Sublime Text, Notepad2)
            string? vscode = FindAppInRegistry("Code.exe")
                ?? CheckPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"))
                ?? CheckPath(@"C:\Program Files\Microsoft VS Code\Code.exe")
                ?? FindInPath("code.cmd");

            if (!string.IsNullOrEmpty(vscode)) return vscode;

            string? sublime = FindAppInRegistry("sublime_text.exe")
                ?? CheckPath(@"C:\Program Files\Sublime Text\sublime_text.exe")
                ?? CheckPath(@"C:\Program Files (x86)\Sublime Text\sublime_text.exe")
                ?? CheckPath(@"C:\Program Files\Sublime Text 3\sublime_text.exe")
                ?? FindInPath("subl.exe");

            if (!string.IsNullOrEmpty(sublime)) return sublime;

            // 4. Windows Native Notepad (guaranteed fallback)
            string systemNotepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            if (File.Exists(systemNotepad)) return systemNotepad;

            return "notepad.exe";
        }

        private static string? FindAppInRegistry(string appExeName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appExeName}")
                             ?? Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appExeName}");
                if (key != null)
                {
                    var defaultVal = key.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(defaultVal) && File.Exists(defaultVal))
                    {
                        return defaultVal;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? CheckPath(string path)
        {
            try
            {
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindInPath(string filename)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathEnv)) return null;

                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string fullPath = Path.Combine(dir.Trim(), filename);
                        if (File.Exists(fullPath)) return fullPath;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void PersistSettings()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                settings.Composer.ExecutablePath = ComposerExecutablePath;
                settings.Composer.DownloadTargetPath = DownloadTargetPath;
                settings.Composer.WwwTargetPath = WwwTargetPath;
                _ = _settingsService.SaveSettingsAsync(settings);
            }
            catch { }
        }

        [RelayCommand]
        public async Task RunSelfUpdateAsync()
        {
            if (IsBusy) return;

            PrepareExecution("Composer Self-Update");
            var progress = CreateProgressReporter();

            try
            {
                bool success = await _composerService.RunSelfUpdateAsync(ComposerExecutablePath, progress, _cts!.Token);
                StatusMessage = success ? "Composer self-update completed successfully!" : "Composer self-update reported warnings/errors.";
                await RefreshVersionAsync();
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Self-update failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public async Task RunCheckOutdatedAsync()
        {
            if (IsBusy) return;

            string targetDir = !string.IsNullOrWhiteSpace(DownloadTargetPath)
                ? DownloadTargetPath
                : WwwTargetPath;

            PrepareExecution($"Inspect All Packages & Versions (composer outdated --all in {targetDir})");
            var progress = CreateProgressReporter();

            try
            {
                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Target directory does not exist: '{targetDir}'. Please select a valid Download Target Directory.");
                    StatusMessage = "Directory not found.";
                    return;
                }

                bool success = await _composerService.RunCheckOutdatedAsync(ComposerExecutablePath, targetDir, progress, _cts!.Token);
                StatusMessage = success ? "Component inspection completed." : "Component inspection finished with warnings/errors.";
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Check outdated failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public async Task RunPackageUpdateAsync()
        {
            if (IsBusy) return;

            string targetDir = DownloadTargetPath;

            string opName = IsWithAllDependencies
                ? $"Update Packages with all dependencies (--with-all-dependencies in {targetDir})"
                : $"Download & Update Packages in {targetDir}";

            PrepareExecution(opName);
            var progress = CreateProgressReporter();

            try
            {
                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Download Target directory does not exist: '{targetDir}'. Please select a valid Download Target Directory.");
                    StatusMessage = "Download directory not found.";
                    return;
                }

                bool success = await _composerService.RunUpdatePackagesAsync(
                    ComposerExecutablePath,
                    targetDir,
                    IsWithAllDependencies,
                    progress,
                    _cts!.Token);

                if (success)
                {
                    StatusMessage = IsWithAllDependencies
                        ? "Packages and all sub-dependencies updated successfully!"
                        : "Packages downloaded and updated successfully in Download Target Directory!";
                }
                else
                {
                    StatusMessage = "Package update reported warnings/errors.";
                }
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Package update failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public async Task RunDeployToWwwAsync()
        {
            if (IsBusy) return;

            if (!IsTestMode)
            {
                bool confirmed = ConfirmAction(
                    "Are you sure you want to proceed with the REAL Backup & Deploy to WWW?\n\n" +
                    "• Existing WWW vendor directory will be backed up to a .zip archive.\n" +
                    "• Existing vendor folder will be renamed to vendor_old.\n" +
                    "• Updated vendor packages will be copied to your live WWW directory.\n\n" +
                    "Do you want to proceed?",
                    "Confirm Real Vendor Deployment");

                if (!confirmed)
                {
                    StatusMessage = "Vendor deployment cancelled by user.";
                    return;
                }
            }

            string backupDir = _settingsService.CurrentSettings.General.BackupDirectory;
            string opName = IsTestMode
                ? $"Backup WWW & Simulate Deploy to {WwwTargetPath} (Test Mode)"
                : $"Backup WWW & Deploy to {WwwTargetPath}";

            PrepareExecution(opName);
            var progress = CreateProgressReporter();

            try
            {
                if (string.IsNullOrWhiteSpace(DownloadTargetPath) || !Directory.Exists(DownloadTargetPath))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Download Target directory does not exist: '{DownloadTargetPath}'. Please select a valid Download Target Directory.");
                    StatusMessage = "Download directory not found.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(WwwTargetPath) || !Directory.Exists(WwwTargetPath))
                {
                    ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] WWW Target directory does not exist: '{WwwTargetPath}'. Please select a valid WWW Target Directory.");
                    StatusMessage = "WWW directory not found.";
                    return;
                }

                bool success = await _composerService.RunDeployToWwwAsync(
                    DownloadTargetPath,
                    WwwTargetPath,
                    backupDir,
                    IsTestMode,
                    progress,
                    _cts!.Token);

                if (success)
                {
                    StatusMessage = IsTestMode
                        ? "Dry-run simulation completed! Real backup created. No WWW files were modified."
                        : "Packages deployed to WWW successfully! Real backup created.";
                }
                else
                {
                    StatusMessage = "Deployment reported warnings/errors.";
                }
            }
            catch (OperationCanceledException)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] [CANCELLED] Operation cancelled by user.");
                StatusMessage = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex.Message}");
                StatusMessage = "Deploy failed with exception.";
            }
            finally
            {
                EndExecution();
            }
        }

        [RelayCommand]
        public void ClearLogs()
        {
            ExecutionLogs.Clear();
            StatusMessage = "Logs cleared.";
        }

        [RelayCommand]
        public void CopyLogs()
        {
            try
            {
                string text = string.Join(Environment.NewLine, ExecutionLogs);
                Clipboard.SetText(text);
                StatusMessage = "Logs copied to clipboard!";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not copy logs: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CancelOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusMessage = "Cancelling operation...";
            }
        }

        [RelayCommand]
        public void CloseWindow()
        {
            RequestClose?.Invoke();
        }

        private void PrepareExecution(string operationName)
        {
            IsBusy = true;
            IsExecuting = true;
            StatusMessage = $"Running: {operationName}...";
            ExecutionLogs.Add($"==================================================================");
            ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] STARTING: {operationName}");
            ExecutionLogs.Add($"==================================================================");
            _cts = new CancellationTokenSource();
        }

        private void EndExecution()
        {
            IsBusy = false;
            IsExecuting = false;
            _cts?.Dispose();
            _cts = null;
        }

        [RelayCommand]
        public void Close()
        {
            RequestClose?.Invoke();
        }

        private IProgress<string> CreateProgressReporter()
        {
            return new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    string entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
                    if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                    {
                        Application.Current.Dispatcher.Invoke(() => ExecutionLogs.Add(entry));
                    }
                    else
                    {
                        ExecutionLogs.Add(entry);
                    }
                }
            });
        }
    }
}
