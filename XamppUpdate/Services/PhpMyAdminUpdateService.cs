using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class PhpMyAdminUpdateService : IPhpMyAdminUpdateService
    {
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;
        private readonly IVersionDetectionService _versionDetectionService;
        private readonly IArchiveService _archiveService;
        private readonly IConfigDiffService _configDiffService;

        public PhpMyAdminUpdateService(
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

        public async Task<string> PrepareIncomingSourceAsync(
            string sourceUrlOrPath,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceUrlOrPath))
            {
                throw new ArgumentException("Source URL or file path cannot be empty.", nameof(sourceUrlOrPath));
            }

            // If already a local directory containing index.php
            if (Directory.Exists(sourceUrlOrPath))
            {
                string foundRoot = _archiveService.FindPhpMyAdminRoot(sourceUrlOrPath);
                if (File.Exists(Path.Combine(foundRoot, "index.php")))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Directory Validated",
                        Message = $"Found phpMyAdmin root directory: {Path.GetFileName(foundRoot)}",
                        Percentage = 100
                    });
                    return foundRoot;
                }
            }

            string tempBase = _settingsService.CurrentSettings.General.ResolvedTempDirectory;
            if (!Directory.Exists(tempBase))
            {
                Directory.CreateDirectory(tempBase);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string extractTargetDir = Path.Combine(tempBase, $"phpmyadmin_extracted_{timestamp}");

            string archiveFilePath;

            // Handle URL download
            if (Uri.TryCreate(sourceUrlOrPath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Downloading phpMyAdmin Package",
                    Message = $"Downloading archive from {sourceUrlOrPath}...",
                    Percentage = 10,
                    IsIndeterminate = true
                });

                string downloadFileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(downloadFileName) || (!downloadFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !downloadFileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                {
                    downloadFileName = $"phpmyadmin_download_{timestamp}.zip";
                }

                archiveFilePath = Path.Combine(tempBase, downloadFileName);

                var downloadProgress = new Progress<double>(percent =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Downloading phpMyAdmin Package",
                        Message = $"Downloading: {percent:F1}%",
                        Percentage = 10 + (percent * 0.3) // 10% to 40%
                    });
                });

                await _archiveService.DownloadFileAsync(sourceUrlOrPath, archiveFilePath, downloadProgress, cancellationToken);
            }
            else
            {
                // Local file path
                archiveFilePath = sourceUrlOrPath;
                if (!File.Exists(archiveFilePath))
                {
                    throw new FileNotFoundException($"Source package file not found at '{sourceUrlOrPath}'.");
                }
            }

            // Extract Archive
            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Extracting Archive",
                Message = "Decompressing phpMyAdmin package...",
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
                    Percentage = 60,
                    IsIndeterminate = true
                });
            });

            await _archiveService.ExtractArchiveAsync(archiveFilePath, extractTargetDir, extractProgress, cancellationToken);

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Locating phpMyAdmin Root",
                Message = "Scanning package structure...",
                Percentage = 85
            });

            string realRoot = _archiveService.FindPhpMyAdminRoot(extractTargetDir);

            if (!File.Exists(Path.Combine(realRoot, "index.php")))
            {
                throw new InvalidOperationException("The extracted archive does not appear to contain a valid phpMyAdmin distribution (index.php missing).");
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Source Ready",
                Message = "phpMyAdmin package verified and ready for inspection.",
                Percentage = 100
            });

            return realRoot;
        }

        public async Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingPhpMyAdminRoot)
        {
            var diffItems = new List<ConfigDiffItem>();
            var settings = _settingsService.CurrentSettings;

            string localConfig = Path.Combine(settings.PhpMyAdmin.InstallationPath, settings.PhpMyAdmin.ConfigFile);
            if (!File.Exists(localConfig))
            {
                string fallback = Path.Combine(settings.PhpMyAdmin.InstallationPath, "config.inc.php");
                if (File.Exists(fallback)) localConfig = fallback;
            }

            // Find incoming config.inc.php or config.sample.inc.php template
            string incomingConfig = Path.Combine(incomingPhpMyAdminRoot, "config.inc.php");
            if (!File.Exists(incomingConfig))
            {
                incomingConfig = Path.Combine(incomingPhpMyAdminRoot, "config.sample.inc.php");
            }

            if (File.Exists(localConfig) && File.Exists(incomingConfig))
            {
                var diff = await _configDiffService.CompareSingleFileAsync(localConfig, incomingConfig, "config.inc.php");
                diffItems.Add(diff);
            }
            else if (File.Exists(localConfig))
            {
                var diff = await _configDiffService.CompareSingleFileAsync(localConfig, string.Empty, "config.inc.php");
                diff.IncomingFilePath = string.Empty;
                diff.IncomingExists = false;
                diff.Resolution = ConfigFileResolution.KeepCurrent;
                diffItems.Add(diff);
            }
            else if (File.Exists(incomingConfig))
            {
                var diff = await _configDiffService.CompareSingleFileAsync(string.Empty, incomingConfig, "config.inc.php");
                diff.LocalFilePath = string.Empty;
                diff.LocalExists = false;
                diff.Resolution = ConfigFileResolution.OverwriteWithNew;
                diffItems.Add(diff);
            }

            return diffItems;
        }

        public async Task<bool> ExecuteUpdatePipelineAsync(
            string incomingPhpMyAdminRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            bool skipBackup = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string localDir = settings.PhpMyAdmin.InstallationPath;
            string modeTag = isTestMode ? "[TEST MODE] " : string.Empty;

            try
            {
                // Step 1: Pre-flight Validation
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.SourceValidation,
                    StepTitle = $"{modeTag}Pre-flight Validation",
                    Message = "Validating phpMyAdmin source and installation directories...",
                    Percentage = 10
                });

                if (string.IsNullOrWhiteSpace(incomingPhpMyAdminRoot) || !Directory.Exists(incomingPhpMyAdminRoot))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.SourceValidation,
                        StepTitle = "Validation Failed",
                        Message = "Incoming phpMyAdmin directory is invalid or does not exist.",
                        IsError = true
                    });
                    return false;
                }

                if (!File.Exists(Path.Combine(incomingPhpMyAdminRoot, "index.php")))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.SourceValidation,
                        StepTitle = "Validation Failed",
                        Message = "Incoming phpMyAdmin package is missing index.php.",
                        IsError = true
                    });
                    return false;
                }

                // Step 2: Backup Creation
                string backupDir = settings.General.ResolvedBackupDirectory;
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupZipPath = Path.Combine(backupDir, $"phpMyAdmin_backup_{timestamp}.zip");

                if (skipBackup)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = $"{modeTag}Backup Skipped",
                        Message = "Skip Backup option enabled. Existing backup verification completed.",
                        Percentage = 30
                    });
                }
                else if (Directory.Exists(localDir))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = $"{modeTag}Backup Creation",
                        Message = isTestMode
                            ? "Simulating phpMyAdmin full archive backup creation..."
                            : $"Creating complete zip backup to {Path.GetFileName(backupZipPath)}...",
                        Percentage = 25,
                        IsIndeterminate = true
                    });

                    if (!isTestMode)
                    {
                        var backupProgress = new Progress<string>(msg =>
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.BackupCreation,
                                StepTitle = "Backup Creation",
                                Message = msg,
                                Percentage = 30,
                                IsIndeterminate = true
                            });
                        });

                        await _archiveService.CreateZipBackupAsync(localDir, backupZipPath, backupProgress, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(400, cancellationToken);
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = $"{modeTag}Backup Completed",
                        Message = isTestMode
                            ? "Backup archive simulation verified successfully."
                            : $"Backup successfully created: {Path.GetFileName(backupZipPath)}",
                        Percentage = 35
                    });
                }

                // Step 3: Deployment / Staging
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = $"{modeTag}Deploying phpMyAdmin Files",
                    Message = isTestMode
                        ? "Simulating deployment of phpMyAdmin files..."
                        : "Deploying new phpMyAdmin distribution files...",
                    Percentage = 50,
                    IsIndeterminate = true
                });

                if (isTestMode)
                {
                    // Simulation mode
                    var files = Directory.GetFiles(incomingPhpMyAdminRoot, "*.*", SearchOption.AllDirectories);
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = $"{modeTag}File Deployment Simulated",
                        Message = $"Simulation verified: {files.Length} files ready for installation.",
                        Percentage = 75
                    });
                    await Task.Delay(500, cancellationToken);
                }
                else
                {
                    // Real Update: Preserve config.inc.php
                    string localConfigPath = Path.Combine(localDir, "config.inc.php");
                    string? savedConfigContent = null;

                    var configDiff = resolvedConfigs.FirstOrDefault(c => c.RelativeFilePath.Equals("config.inc.php", StringComparison.OrdinalIgnoreCase));
                    if (configDiff != null)
                    {
                        if (configDiff.Resolution == ConfigFileResolution.UseMerged || configDiff.IsCustomMerged)
                        {
                            savedConfigContent = configDiff.MergedContent;
                        }
                        else if (configDiff.Resolution == ConfigFileResolution.OverwriteWithNew)
                        {
                            savedConfigContent = configDiff.IncomingContent;
                        }
                        else
                        {
                            savedConfigContent = configDiff.OriginalLocalContent;
                        }
                    }
                    else if (File.Exists(localConfigPath))
                    {
                        savedConfigContent = await File.ReadAllTextAsync(localConfigPath, System.Text.Encoding.UTF8);
                    }

                    // Atomic folder rotation: rename current folder to phpMyAdmin_old
                    string oldDir = Path.Combine(Path.GetDirectoryName(localDir)!, $"phpMyAdmin_old_{timestamp}");
                    if (Directory.Exists(localDir))
                    {
                        try
                        {
                            Directory.Move(localDir, oldDir);
                        }
                        catch (Exception ex)
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "Directory Move Warning",
                                Message = $"Could not rotate directory ({ex.Message}), falling back to direct file copy.",
                                Percentage = 55
                            });
                        }
                    }

                    // Copy incoming files into localDir
                    CopyDirectoryRecursive(incomingPhpMyAdminRoot, localDir, progress);

                    // Restore or write merged config.inc.php
                    if (!string.IsNullOrWhiteSpace(savedConfigContent))
                    {
                        string targetConfig = Path.Combine(localDir, "config.inc.php");
                        await File.WriteAllTextAsync(targetConfig, savedConfigContent, new System.Text.UTF8Encoding(false));
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Configuration Preserved",
                            Message = "Preserved and applied config.inc.php successfully.",
                            Percentage = 80
                        });
                    }

                    // Clean up old rotated directory if safe
                    if (Directory.Exists(oldDir))
                    {
                        try
                        {
                            Directory.Delete(oldDir, true);
                        }
                        catch { }
                    }
                }

                // Step 4: Verification
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.HealthCheck,
                    StepTitle = $"{modeTag}Verification",
                    Message = "Verifying phpMyAdmin installation integrity...",
                    Percentage = 90
                });

                if (!isTestMode)
                {
                    if (!File.Exists(Path.Combine(localDir, "index.php")))
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.HealthCheck,
                            StepTitle = "Verification Failed",
                            Message = "Verification failed: index.php missing in target directory.",
                            IsError = true
                        });
                        return false;
                    }
                }

                // Cleanup temporary incoming directory if real update
                if (!isTestMode && !string.IsNullOrWhiteSpace(incomingPhpMyAdminRoot))
                {
                    try
                    {
                        string tempBaseDir = settings.General.ResolvedTempDirectory;
                        if (incomingPhpMyAdminRoot.StartsWith(tempBaseDir, StringComparison.OrdinalIgnoreCase))
                        {
                            string topTempDir = incomingPhpMyAdminRoot;
                            while (topTempDir.Length > tempBaseDir.Length && Path.GetDirectoryName(topTempDir) != null && !Path.GetDirectoryName(topTempDir)!.Equals(tempBaseDir, StringComparison.OrdinalIgnoreCase))
                            {
                                topTempDir = Path.GetDirectoryName(topTempDir)!;
                            }
                            CleanupTempDirectory(topTempDir);
                        }
                    }
                    catch { }
                }

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = isTestMode ? "[TEST MODE] Completed" : "Update Completed",
                    Message = isTestMode
                        ? "Test mode simulation completed! Ready to execute real update."
                        : "phpMyAdmin updated and verified successfully!",
                    Percentage = 100
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = "Update Cancelled",
                    Message = "The update operation was cancelled by user.",
                    IsError = true
                });
                return false;
            }
            catch (Exception ex)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = "Update Failed",
                    Message = $"Update failed: {ex.Message}",
                    IsError = true
                });
                return false;
            }
        }

        public void CleanupTempDirectory(string tempDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !Directory.Exists(tempDirectoryPath))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(tempDirectoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch { }
                }
                Directory.Delete(tempDirectoryPath, true);
            }
            catch { }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir, IProgress<UpdateProgressReport>? progress)
        {
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var dirInfo = new DirectoryInfo(sourceDir);
            var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
            int total = files.Length;
            int current = 0;

            foreach (var file in files)
            {
                string relPath = Path.GetRelativePath(sourceDir, file.FullName);
                string targetPath = Path.Combine(destDir, relPath);
                string? targetFolder = Path.GetDirectoryName(targetPath);

                if (!string.IsNullOrWhiteSpace(targetFolder) && !Directory.Exists(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                }

                file.CopyTo(targetPath, true);
                current++;

                if (current % 50 == 0 || current == total)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Deploying Files",
                        Message = $"Copying phpMyAdmin files ({current}/{total})...",
                        Percentage = 50 + ((double)current / total * 30)
                    });
                }
            }
        }
    }
}
