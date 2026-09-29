using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

            string jsonPath = Path.Combine(workingDirectory, "composer.json");
            await StripBomIfPresentAsync(jsonPath, cancellationToken);

            progress.Report($"[START] Inspecting all installed packages and latest versions in: {workingDirectory}");
            return await ExecuteComposerProcessAsync(composerExecutable, "outdated --all", workingDirectory, progress, cancellationToken);
        }

        public async Task<bool> RunUpdatePackagesAsync(string composerExecutable, string workingDirectory, bool withAllDependencies, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                progress.Report($"[ERROR] Target download directory does not exist: {workingDirectory}");
                return false;
            }

            string jsonPath = Path.Combine(workingDirectory, "composer.json");
            await StripBomIfPresentAsync(jsonPath, cancellationToken);

            string args = withAllDependencies
                ? "update --with-all-dependencies --no-interaction"
                : "update --no-interaction";

            progress.Report($"[START] Running 'composer {args}' in: {workingDirectory}");
            return await ExecuteComposerProcessAsync(composerExecutable, args, workingDirectory, progress, cancellationToken);
        }

        public static async Task StripBomIfPresentAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    byte[] stripped = new byte[bytes.Length - 3];
                    Buffer.BlockCopy(bytes, 3, stripped, 0, stripped.Length);
                    await File.WriteAllBytesAsync(filePath, stripped, cancellationToken);
                }
            }
            catch { }
        }

        public async Task<bool> RunDeployToWwwAsync(
            string downloadSourceDirectory,
            string wwwTargetDirectory,
            string? backupDestinationDirectory,
            bool isTestMode,
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadSourceDirectory) || !Directory.Exists(downloadSourceDirectory))
            {
                progress.Report($"[ERROR] Download Target directory does not exist: '{downloadSourceDirectory}'");
                return false;
            }

            if (string.IsNullOrWhiteSpace(wwwTargetDirectory) || !Directory.Exists(wwwTargetDirectory))
            {
                progress.Report($"[ERROR] WWW Target directory does not exist: '{wwwTargetDirectory}'");
                return false;
            }

            string srcVendor = Path.Combine(downloadSourceDirectory, "vendor");
            if (!Directory.Exists(srcVendor))
            {
                progress.Report($"[ERROR] Source 'vendor' directory not found in '{downloadSourceDirectory}'. Please download/update packages first in Step 3.");
                return false;
            }

            string destVendor = Path.Combine(wwwTargetDirectory, "vendor");
            string modeTag = isTestMode ? "[TEST MODE] " : string.Empty;

            // 1. Real Backup of WWW vendor folder (always real backup)
            progress.Report($"[STEP 1/3] Backing up existing WWW vendor directory in: {wwwTargetDirectory}...");
            string? backupZip = await BackupVendorFolderAsync(wwwTargetDirectory, backupDestinationDirectory, progress, cancellationToken);
            if (backupZip != null)
            {
                progress.Report($"[BACKUP] WWW vendor backup preserved in: {backupZip}");
            }

            // 2. Rename existing destination vendor folder before deployment
            if (Directory.Exists(destVendor))
            {
                string targetRenamePath = GetAvailableOldVendorDirectoryPath(wwwTargetDirectory);
                string targetRenameName = Path.GetFileName(targetRenamePath);

                if (isTestMode)
                {
                    progress.Report($"[STEP 2/3] [TEST MODE] [SIMULATE RENAME] {destVendor} -> {targetRenamePath}");
                }
                else
                {
                    progress.Report($"[STEP 2/3] [RENAME] Moving existing vendor folder to '{targetRenameName}'...");
                    try
                    {
                        Directory.Move(destVendor, targetRenamePath);
                        progress.Report($"[RENAME] Preserved existing vendor directory as '{targetRenameName}'.");
                    }
                    catch (Exception ex)
                    {
                        progress.Report($"[ERROR] Could not rename '{destVendor}' to '{targetRenameName}': {ex.Message}");
                        return false;
                    }
                }
            }
            else
            {
                progress.Report($"[STEP 2/3] No existing '{destVendor}' directory found to rename.");
            }

            // 3. Scan and Deploy / Simulate Copy strictly inside vendor folder
            progress.Report($"[STEP 3/3] {modeTag}{(isTestMode ? "Simulating vendor/ deployment to WWW Target..." : "Deploying vendor/ packages to WWW Target...")}");

            return await Task.Run(() =>
            {
                try
                {
                    var enumOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                    };

                    var vendorFiles = Directory.GetFiles(srcVendor, "*.*", enumOptions);
                    if (vendorFiles.Length == 0)
                    {
                        progress.Report($"[WARNING] No files found inside source vendor directory '{srcVendor}'.");
                        return false;
                    }

                    int total = vendorFiles.Length;
                    int count = 0;

                    foreach (var sourceFile in vendorFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        count++;

                        string relPath = Path.GetRelativePath(downloadSourceDirectory, sourceFile);
                        string destFile = Path.Combine(wwwTargetDirectory, relPath);

                        if (isTestMode)
                        {
                            progress.Report($"[SIMULATE COPY] {sourceFile} -> {destFile}");
                        }
                        else
                        {
                            string? destFolder = Path.GetDirectoryName(destFile);
                            if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                            {
                                Directory.CreateDirectory(destFolder);
                            }

                            File.Copy(sourceFile, destFile, true);
                            progress.Report($"[DEPLOY] {relPath} -> {destFile}");
                        }
                    }

                    if (isTestMode)
                    {
                        progress.Report($"[COMPLETED] [TEST MODE] Simulation finished successfully! {total} vendor files inspected for deployment (no WWW files modified).");
                    }
                    else
                    {
                        progress.Report($"[COMPLETED] Deployment finished successfully! {total} vendor files copied to WWW Target directory ({destVendor}).");
                    }

                    return true;
                }
                catch (OperationCanceledException)
                {
                    progress.Report("[CANCELLED] Deployment operation was cancelled.");
                    return false;
                }
                catch (Exception ex)
                {
                    progress.Report($"[ERROR] Deployment failed: {ex.Message}");
                    return false;
                }
            }, cancellationToken);
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

        public static string GetAvailableOldVendorDirectoryPath(string wwwTargetDirectory)
        {
            string candidate = Path.Combine(wwwTargetDirectory, "vendor_old");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            int index = 1;
            while (true)
            {
                string indexedCandidate = Path.Combine(wwwTargetDirectory, $"vendor_old{index}");
                if (!Directory.Exists(indexedCandidate))
                {
                    return indexedCandidate;
                }
                index++;
            }
        }
    }
}
