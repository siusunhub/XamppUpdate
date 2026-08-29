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
                Percentage = 60
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
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string localApacheRoot = settings.Apache.InstallationPath;
            string serviceName = settings.Apache.ServiceName;
            string backupDir = settings.General.ResolvedBackupDirectory;

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

            // Step 1: Backup
            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.BackupCreation,
                StepTitle = "Creating Full Backup",
                Message = $"Backing up active Apache directory to {Path.GetFileName(backupZipPath)}...",
                Percentage = 15,
                IsIndeterminate = true
            });

            var backupProgress = new Progress<string>(msg =>
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.BackupCreation,
                    StepTitle = "Creating Full Backup",
                    Message = msg,
                    Percentage = 20,
                    IsIndeterminate = true
                });
            });

            await _archiveService.CreateZipBackupAsync(localApacheRoot, backupZipPath, backupProgress, cancellationToken);
            EnforceBackupRetention(backupDir, settings.General.MaxBackupRetentionCount);

            // Step 2: Stop Service
            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.ServiceStop,
                StepTitle = "Stopping Windows Service",
                Message = $"Sending stop request to '{serviceName}'...",
                Percentage = 30
            });

            var serviceStopProgress = new Progress<string>(msg =>
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = "Stopping Windows Service",
                    Message = msg,
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
                    Message = $"Could not stop service '{serviceName}'. Aborting update to avoid corruption.",
                    IsError = true
                });
                return false;
            }

            try
            {
                // Step 3: Copy new files, handling preserved configurations
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = "Updating Apache Binaries & Modules",
                    Message = "Copying updated files into Apache directory...",
                    Percentage = 50
                });

                // Build set of preserved config relative paths (normalized)
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

                await CopyDirectoryAsync(incomingApacheRoot, localApacheRoot, preservedRelativePaths, cancellationToken);

                // Step 4: Write merged configurations if any
                if (customMergedConfigs.Count > 0)
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

                // Step 5: Start Service
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStart,
                    StepTitle = "Starting Windows Service",
                    Message = $"Starting service '{serviceName}'...",
                    Percentage = 85
                });

                var serviceStartProgress = new Progress<string>(msg =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = "Starting Windows Service",
                        Message = msg,
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
                        Message = $"Service '{serviceName}' failed to start after update. Backup is available at {backupZipPath}.",
                        IsError = true
                    });
                    return false;
                }

                // Step 6: Verify health & Cleanup
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
                    Message = $"An error occurred during file replacement: {ex.Message}. A full backup was saved to '{backupZipPath}'.",
                    IsError = true
                });
                return false;
            }
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
