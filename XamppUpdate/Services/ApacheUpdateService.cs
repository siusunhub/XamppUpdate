using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class ApacheUpdateService : IApacheUpdateService
    {
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;
        private readonly IVersionDetectionService _versionDetectionService;
        private readonly IArchiveService _archiveService;
        private readonly IConfigDiffService _configDiffService;

        public ApacheUpdateService(
            ISettingsService settingsService,
            IWindowsServiceManager serviceManager,
            IVersionDetectionService versionDetectionService,
            IArchiveService archiveService,
            IConfigDiffService configDiffService)
        {
            _settingsService = settingsService;
            _serviceManager = serviceManager;
            _versionDetectionService = versionDetectionService;
            _archiveService = archiveService;
            _configDiffService = configDiffService;
        }

        public async Task<string> PrepareIncomingSourceAsync(string sourceUrlOrPath, IProgress<UpdateProgressReport>? progress = null, CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string tempBase = settings.General.ResolvedTempDirectory;

            if (!Directory.Exists(tempBase))
            {
                Directory.CreateDirectory(tempBase);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string extractTargetDir = Path.Combine(tempBase, $"apache_incoming_{timestamp}");
            Directory.CreateDirectory(extractTargetDir);

            string archiveFilePath;

            // Check if source is a URL or local file
            if (Uri.TryCreate(sourceUrlOrPath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Downloading Apache Package",
                    Message = $"Downloading archive from {sourceUrlOrPath}...",
                    Percentage = 10,
                    IsIndeterminate = true
                });

                string downloadFileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(downloadFileName) || (!downloadFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !downloadFileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                {
                    downloadFileName = $"apache_download_{timestamp}.zip";
                }

                archiveFilePath = Path.Combine(tempBase, downloadFileName);

                var downloadProgress = new Progress<double>(percent =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Downloading Apache Package",
                        Message = $"Downloading: {percent:F1}%",
                        Percentage = 10 + (percent * 0.3) // 10% to 40%
                    });
                });

                await _archiveService.DownloadFileAsync(sourceUrlOrPath, archiveFilePath, downloadProgress, cancellationToken);
            }
            else
            {
                if (!File.Exists(sourceUrlOrPath))
                {
                    throw new FileNotFoundException($"Selected local archive file was not found: {sourceUrlOrPath}");
                }
                archiveFilePath = sourceUrlOrPath;
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Extracting Archive",
                Message = "Unpacking files into temporary workspace...",
                Percentage = 45,
                IsIndeterminate = true
            });

            var extractProgress = new Progress<string>(msg =>
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Extracting Archive",
                    Message = msg,
                    Percentage = 50,
                    IsIndeterminate = true
                });
            });

            await _archiveService.ExtractArchiveAsync(archiveFilePath, extractTargetDir, extractProgress, cancellationToken);

            string realApacheRoot = _archiveService.FindApacheRoot(extractTargetDir);

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Preparation Complete",
                Message = $"Ready. Apache root detected at: {Path.GetFileName(realApacheRoot)}",
                Percentage = 100
            });

            return realApacheRoot;
        }

        public async Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingApacheRoot)
        {
            var settings = _settingsService.CurrentSettings;
            string localApacheRoot = settings.Apache.InstallationPath;
            var configFiles = settings.Apache.ConfigFiles;

            return await _configDiffService.CompareConfigFilesAsync(localApacheRoot, incomingApacheRoot, configFiles);
        }

        public async Task<bool> ExecuteUpdatePipelineAsync(
            string incomingApacheRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string localApacheRoot = settings.Apache.InstallationPath;
            string serviceName = settings.Apache.ServiceName;
            string backupDir = settings.General.ResolvedBackupDirectory;
            string modeTag = isTestMode ? "[TEST MODE] " : string.Empty;

            if (!Directory.Exists(localApacheRoot))
            {
                throw new DirectoryNotFoundException($"Active Apache directory not found: {localApacheRoot}");
            }

            if (!Directory.Exists(incomingApacheRoot))
            {
                throw new DirectoryNotFoundException($"Prepared incoming Apache directory not found: {incomingApacheRoot}");
            }

            string currentVersion = await _versionDetectionService.DetectApacheVersionAsync(localApacheRoot);
            if (string.IsNullOrWhiteSpace(currentVersion) || currentVersion.Contains("Not Installed") || currentVersion.Contains("Unknown"))
            {
                currentVersion = "unknown";
            }
            // Clean invalid filename characters from version
            currentVersion = string.Concat(currentVersion.Split(Path.GetInvalidFileNameChars()));

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupZipPath = Path.Combine(backupDir, $"apache_{currentVersion}_{timestamp}.zip");

            // Step 1: Backup (Real in both modes)
            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.BackupCreation,
                StepTitle = $"{modeTag}Creating Full Backup",
                Message = $"{modeTag}Backing up active Apache directory to {Path.GetFileName(backupZipPath)}...",
                Percentage = 15,
                IsIndeterminate = false
            });

            var backupProgress = new Progress<string>(msg =>
            {
                int pct = 20;
                if (msg.Contains('(') && msg.Contains('/'))
                {
                    try
                    {
                        int start = msg.IndexOf('(') + 1;
                        int slash = msg.IndexOf('/', start);
                        int end = msg.IndexOf(' ', slash);
                        if (start > 0 && slash > start && end > slash)
                        {
                            if (int.TryParse(msg[start..slash], out int cur) &&
                                int.TryParse(msg[(slash + 1)..end], out int tot) && tot > 0)
                            {
                                pct = 15 + (int)(10.0 * cur / tot);
                            }
                        }
                    }
                    catch { }
                }

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.BackupCreation,
                    StepTitle = $"{modeTag}Creating Full Backup",
                    Message = $"{modeTag}{msg}",
                    Percentage = pct,
                    IsIndeterminate = false
                });
            });

            await _archiveService.CreateZipBackupAsync(localApacheRoot, backupZipPath, backupProgress, cancellationToken);
            EnforceBackupRetention(backupDir, settings.General.MaxBackupRetentionCount);

            if (isTestMode)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.BackupCreation,
                    StepTitle = "[TEST MODE] Backup Verified",
                    Message = $"[TEST MODE] Successfully tested zip backup: {Path.GetFileName(backupZipPath)} created.",
                    Percentage = 25
                });
            }

            bool serviceExists = _serviceManager.ServiceExists(serviceName);

            // Step 2: Stop Service (Real in both modes if installed)
            if (serviceExists)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = $"{modeTag}Stopping Windows Service",
                    Message = $"{modeTag}Sending stop request to '{serviceName}'...",
                    Percentage = 30
                });

                var serviceStopProgress = new Progress<string>(msg =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Stopping Windows Service",
                        Message = $"{modeTag}{msg}",
                        Percentage = 35
                    });
                });

                bool stopped = await _serviceManager.StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), serviceStopProgress);
                if (!stopped)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "Service Stop Failed",
                        Message = $"Could not stop service '{serviceName}'. Aborting to avoid corruption.",
                        IsError = true
                    });
                    return false;
                }

                if (isTestMode)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = "[TEST MODE] Service Stop Verified",
                        Message = $"[TEST MODE] Successfully tested stopping Windows service '{serviceName}'.",
                        Percentage = 40
                    });
                }
            }
            else
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = $"{modeTag}Service Check",
                    Message = $"{modeTag}Service '{serviceName}' is not installed as a Windows Service. Skipping stop.",
                    Percentage = 40
                });
            }

            try
            {
                // Step 3: Copy new files (Simulated if isTestMode, real if not)
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = isTestMode ? "[TEST MODE] Simulating File Copy" : "Updating Apache Binaries & Modules",
                    Message = isTestMode ? "Simulating file replacement (no live files modified)..." : "Copying updated files into Apache directory...",
                    Percentage = 50
                });

                var preservedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var customMergedConfigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var diff in resolvedConfigs)
                {
                    string norm = NormalizeRelativePath(diff.RelativeFilePath);
                    if (diff.Resolution == ConfigFileResolution.KeepCurrent)
                    {
                        preservedRelativePaths.Add(norm);
                    }
                    else if (diff.Resolution == ConfigFileResolution.UseMerged && !string.IsNullOrEmpty(diff.MergedContent))
                    {
                        customMergedConfigs[norm] = diff.MergedContent;
                    }
                }

                if (isTestMode)
                {
                    await SimulateCopyDirectoryAsync(incomingApacheRoot, localApacheRoot, preservedRelativePaths, progress, cancellationToken);
                }
                else
                {
                    await CopyDirectoryAsync(incomingApacheRoot, localApacheRoot, preservedRelativePaths, cancellationToken);
                }

                // Step 4: Config resolution reporting and application
                foreach (var diff in resolvedConfigs)
                {
                    string norm = NormalizeRelativePath(diff.RelativeFilePath);
                    string resolutionDesc = diff.Resolution switch
                    {
                        ConfigFileResolution.KeepCurrent => "[CONFIG] Preserve current working file (skip overwrite)",
                        ConfigFileResolution.OverwriteWithNew => "[CONFIG] Overwrite with incoming new default",
                        ConfigFileResolution.UseMerged => $"[CONFIG] Apply custom merged edits ({diff.MergedContent.Length} chars)",
                        _ => "[CONFIG] Default"
                    };

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = isTestMode ? "[TEST MODE] Config Resolution" : "Applying Config Resolution",
                        Message = $"{resolutionDesc}: {norm}",
                        Percentage = 72
                    });
                }

                // Write merged configurations to disk ONLY in live mode
                if (!isTestMode && customMergedConfigs.Count > 0)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Applying Config Merge",
                        Message = "Writing merged configuration files...",
                        Percentage = 75
                    });

                    foreach (var pair in customMergedConfigs)
                    {
                        string targetPath = Path.Combine(localApacheRoot, pair.Key);
                        string? dir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        await File.WriteAllTextAsync(targetPath, pair.Value, Encoding.UTF8, cancellationToken);
                    }
                }

                // Step 5: Start Service (Real in both modes if installed)
                if (serviceExists)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting Windows Service",
                        Message = $"{modeTag}Starting service '{serviceName}'...",
                        Percentage = 85
                    });

                    var serviceStartProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting Windows Service",
                            Message = $"{modeTag}{msg}",
                            Percentage = 90
                        });
                    });

                    bool started = await _serviceManager.StartServiceAsync(serviceName, TimeSpan.FromSeconds(30), serviceStartProgress);
                    if (!started)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Service Start Failed",
                            Message = $"Service '{serviceName}' failed to start. Backup is available at {backupZipPath}.",
                            IsError = true
                        });
                        return false;
                    }

                    if (isTestMode)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = "[TEST MODE] Service Start Verified",
                            Message = $"[TEST MODE] Successfully tested starting Windows service '{serviceName}'.",
                            Percentage = 92
                        });
                    }
                }
                else
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Service Check",
                        Message = $"{modeTag}Service '{serviceName}' is not installed as a Windows Service. Skipping start.",
                        Percentage = 90
                    });
                }

                if (isTestMode)
                {
                    // Step 6 (TEST MODE): Hold workspace
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Cleanup,
                        StepTitle = "[TEST MODE] Holding Workspace",
                        Message = $"[TEST MODE] Clean up workspace HELD! Prepared files kept in '{incomingApacheRoot}' for immediate re-test or real execution.",
                        Percentage = 95
                    });

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Complete,
                        StepTitle = "Test Mode Simulation Complete",
                        Message = "[TEST COMPLETE] All tests passed! Zip backup created, service verified, copy and config merges simulated. Ready to re-test or execute real update.",
                        Percentage = 100
                    });

                    return true;
                }

                // Step 6 (LIVE MODE): Cleanup and Complete
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Cleanup,
                    StepTitle = "Cleaning Up",
                    Message = "Removing temporary incoming files...",
                    Percentage = 95
                });

                // Find top-level temp extract container
                string? topTempDir = Directory.GetParent(incomingApacheRoot)?.FullName;
                if (!string.IsNullOrEmpty(topTempDir) && topTempDir.Contains("apache_incoming_"))
                {
                    CleanupTempDirectory(topTempDir);
                }
                else
                {
                    CleanupTempDirectory(incomingApacheRoot);
                }

                string newVersion = await _versionDetectionService.DetectApacheVersionAsync(localApacheRoot);

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = "Update Successful",
                    Message = $"Apache successfully updated to version {newVersion}. Windows service is running.",
                    Percentage = 100
                });

                return true;
            }
            catch (Exception ex)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Update Pipeline Error",
                    Message = $"An error occurred during update pipeline: {ex.Message}. Backup is saved at '{backupZipPath}'.",
                    IsError = true
                });
                return false;
            }
        }

        private static async Task SimulateCopyDirectoryAsync(
            string sourceDir,
            string destinationDir,
            HashSet<string> preservedRelativePaths,
            IProgress<UpdateProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) return;

            var allFiles = dir.GetFiles("*", SearchOption.AllDirectories);
            int total = allFiles.Length;
            int copiedCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = allFiles[i];

                string relativePath = Path.GetRelativePath(sourceDir, file.FullName);
                string normRelative = NormalizeRelativePath(relativePath);
                string destFile = Path.Combine(destinationDir, relativePath);

                if (preservedRelativePaths.Contains(normRelative) && File.Exists(destFile))
                {
                    skippedCount++;
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Simulate File Copy",
                        Message = $"[SIMULATE SKIP] {normRelative} -> Preserving existing working file",
                        Percentage = 50 + (int)(18.0 * (i + 1) / total)
                    });
                }
                else
                {
                    copiedCount++;
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Simulate File Copy",
                        Message = $"[SIMULATE COPY] {file.FullName} -> {destFile}",
                        Percentage = 50 + (int)(18.0 * (i + 1) / total)
                    });
                }

                if (i % 20 == 0)
                {
                    await Task.Delay(5, cancellationToken);
                }
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.FileReplacement,
                StepTitle = "Simulation Summary",
                Message = $"[SIMULATE SUMMARY] Total {total} files checked: {copiedCount} would be replaced/copied, {skippedCount} preserved.",
                Percentage = 68
            });
        }

        public void CleanupTempDirectory(string tempDirectoryPath)
        {
            try
            {
                if (Directory.Exists(tempDirectoryPath))
                {
                    Directory.Delete(tempDirectoryPath, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clean up temp directory '{tempDirectoryPath}': {ex.Message}");
            }
        }

        private static async Task CopyDirectoryAsync(string sourceDir, string destinationDir, HashSet<string> preservedRelativePaths, CancellationToken cancellationToken)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) return;

            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(sourceDir, file.FullName);
                string normRelative = NormalizeRelativePath(relativePath);

                // If user chose to preserve local config and local file exists, skip overwriting
                string destFile = Path.Combine(destinationDir, relativePath);
                if (preservedRelativePaths.Contains(normRelative) && File.Exists(destFile))
                {
                    continue;
                }

                string? destSubDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destSubDir) && !Directory.Exists(destSubDir))
                {
                    Directory.CreateDirectory(destSubDir);
                }

                // Copy with retry for locked files
                await CopyFileWithRetryAsync(file.FullName, destFile, 3);
            }
        }

        private static async Task CopyFileWithRetryAsync(string sourceFile, string destFile, int retryCount)
        {
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    File.Copy(sourceFile, destFile, true);
                    return;
                }
                catch (IOException) when (i < retryCount - 1)
                {
                    await Task.Delay(500);
                }
            }
        }

        private static void EnforceBackupRetention(string backupDir, int maxBackups)
        {
            if (maxBackups <= 0 || !Directory.Exists(backupDir)) return;

            try
            {
                var files = Directory.GetFiles(backupDir, "apache_*.zip")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                if (files.Count > maxBackups)
                {
                    for (int i = maxBackups; i < files.Count; i++)
                    {
                        files[i].Delete();
                    }
                }
            }
            catch
            {
                // Non-fatal if backup cleanup fails
            }
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }
    }
}
