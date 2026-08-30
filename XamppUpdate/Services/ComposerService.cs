using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public class ComposerService : IComposerService
    {
        private readonly ISettingsService _settingsService;

        public ComposerService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public async Task<string> DetectVersionAsync(string composerExecutable)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(composerExecutable))
                {
                    return "Not Configured";
                }

                try
                {
                    var (fileName, arguments) = ResolveCommandAndArgs(composerExecutable, "-V");
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = fileName,
                            Arguments = arguments,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);

                    string combined = $"{output}\n{error}";
                    var match = Regex.Match(combined, @"Composer\s+version\s+([0-9\.]+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        public async Task<bool> RunSelfUpdateAsync(string composerExecutable, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            progress.Report($"[START] Running Composer self-update...");
            return await ExecuteComposerProcessAsync(composerExecutable, "self-update", string.Empty, progress, cancellationToken);
        }

        public async Task<bool> RunCheckOutdatedAsync(string composerExecutable, string workingDirectory, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                progress.Report($"[ERROR] Working directory does not exist: {workingDirectory}");
                return false;
            }

            progress.Report($"[START] Checking for outdated packages in: {workingDirectory}");
            return await ExecuteComposerProcessAsync(composerExecutable, "outdated", workingDirectory, progress, cancellationToken);
        }

        public async Task<bool> RunUpdatePackagesAsync(string composerExecutable, string workingDirectory, string? backupDestinationDirectory, bool isTestMode, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                progress.Report($"[ERROR] Target project WWW directory does not exist: {workingDirectory}");
                return false;
            }

            if (isTestMode)
            {
                progress.Report($"[TEST MODE] Simulating package update (dry-run) in: {workingDirectory}");
                return await ExecuteComposerProcessAsync(composerExecutable, "update --dry-run --no-interaction", workingDirectory, progress, cancellationToken);
            }

            // Real Update: 1. Backup vendor folder first
            progress.Report($"[STEP 1/2] Creating backup of current 'vendor' folder...");
            string? backupZip = await BackupVendorFolderAsync(workingDirectory, backupDestinationDirectory, progress, cancellationToken);
            if (backupZip != null)
            {
                progress.Report($"[BACKUP] Vendor backup preserved in: {backupZip}");
            }

            // 2. Run real composer update
            progress.Report($"[STEP 2/2] Running 'composer update'...");
            return await ExecuteComposerProcessAsync(composerExecutable, "update --no-interaction", workingDirectory, progress, cancellationToken);
        }

        public async Task<string?> BackupVendorFolderAsync(string workingDirectory, string? destinationBackupDirectory, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string vendorPath = Path.Combine(workingDirectory, "vendor");
                    if (!Directory.Exists(vendorPath))
                    {
                        progress.Report($"[BACKUP] No existing 'vendor' folder found in {workingDirectory} (skipping backup).");
                        return null;
                    }

                    string targetBackupDir = !string.IsNullOrWhiteSpace(destinationBackupDirectory)
                        ? destinationBackupDirectory
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup");

                    if (!Directory.Exists(targetBackupDir))
                    {
                        Directory.CreateDirectory(targetBackupDir);
                    }

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string backupZipPath = Path.Combine(targetBackupDir, $"vendor_backup_{timestamp}.zip");

                    progress.Report($"[BACKUP] Compressing vendor directory to zip archive...");
                    ZipFile.CreateFromDirectory(vendorPath, backupZipPath, CompressionLevel.Optimal, false);

                    var fileInfo = new FileInfo(backupZipPath);
                    double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                    progress.Report($"[BACKUP] ✓ Backup created successfully: {Path.GetFileName(backupZipPath)} in {targetBackupDir} ({sizeMb:F2} MB)");
                    return backupZipPath;
                }
                catch (Exception ex)
                {
                    progress.Report($"[WARNING] Vendor backup failed: {ex.Message}");
                    return null;
                }
            }, cancellationToken);
        }

        private async Task<bool> ExecuteComposerProcessAsync(
            string composerExecutable,
            string composerArgs,
            string workingDirectory,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var (fileName, arguments) = ResolveCommandAndArgs(composerExecutable, composerArgs);

                    progress.Report($"[COMMAND] {fileName} {arguments}");
                    if (!string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        progress.Report($"[CWD] {workingDirectory}");
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                    {
                        psi.WorkingDirectory = workingDirectory;
                    }

                    using var process = new Process { StartInfo = psi };

                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            progress.Report(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            progress.Report(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    while (!process.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                process.Kill(true);
                            }
                            catch { }
                            progress.Report("[CANCELLED] Process terminated by user.");
                            return false;
                        }
                    }

                    int exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        progress.Report($"[COMPLETED] Operation finished successfully (Exit Code: 0).");
                        return true;
                    }
                    else
                    {
                        progress.Report($"[WARNING] Process finished with non-zero exit code: {exitCode}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress.Report($"[FATAL ERROR] {ex.Message}");
                    return false;
                }
            }, cancellationToken);
        }

        private (string fileName, string arguments) ResolveCommandAndArgs(string composerExecutable, string composerArgs)
        {
            string cleanPath = composerExecutable.Trim('\"', ' ');

            if (cleanPath.EndsWith(".phar", StringComparison.OrdinalIgnoreCase))
            {
                string phpPath = _settingsService.CurrentSettings.Php.ExecutablePath;
                if (string.IsNullOrWhiteSpace(phpPath) || !File.Exists(phpPath))
                {
                    phpPath = "php.exe";
                }
                return (phpPath, $"\"{cleanPath}\" {composerArgs}");
            }

            if (cleanPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || cleanPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                return ("cmd.exe", $"/c \"\"{cleanPath}\" {composerArgs}\"");
            }

            if (File.Exists(cleanPath))
            {
                return (cleanPath, composerArgs);
            }

            // Fallback to cmd.exe invoking composer from PATH
            return ("cmd.exe", $"/c \"composer {composerArgs}\"");
        }
    }
}
