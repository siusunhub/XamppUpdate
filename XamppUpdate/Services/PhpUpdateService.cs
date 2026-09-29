using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class PhpUpdateService : IPhpUpdateService
    {
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;
        private readonly IVersionDetectionService _versionDetectionService;
        private readonly IArchiveService _archiveService;
        private readonly IConfigDiffService _configDiffService;

        public PhpUpdateService(
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

            // If already a local directory containing php.exe
            if (Directory.Exists(sourceUrlOrPath))
            {
                string foundRoot = _archiveService.FindPhpRoot(sourceUrlOrPath);
                if (File.Exists(Path.Combine(foundRoot, "php.exe")))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Directory Validated",
                        Message = $"Found PHP root directory: {Path.GetFileName(foundRoot)}",
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
            string extractTargetDir = Path.Combine(tempBase, $"php_extracted_{timestamp}");

            string archiveFilePath;

            // Handle URL download
            if (Uri.TryCreate(sourceUrlOrPath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Downloading PHP Package",
                    Message = $"Downloading archive from {sourceUrlOrPath}...",
                    Percentage = 10,
                    IsIndeterminate = true
                });

                string downloadFileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(downloadFileName) || (!downloadFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !downloadFileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                {
                    downloadFileName = $"php_download_{timestamp}.zip";
                }

                archiveFilePath = Path.Combine(tempBase, downloadFileName);

                var downloadProgress = new Progress<double>(percent =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Downloading PHP Package",
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
                StepTitle = "Extracting PHP Archive",
                Message = "Unpacking files into temporary workspace...",
                Percentage = 40,
                IsIndeterminate = false
            });

            var extractProgress = new Progress<string>(msg =>
            {
                double pct = 50;
                if (msg.StartsWith("Extracting (") && msg.Contains('/'))
                {
                    try
                    {
                        int start = "Extracting (".Length;
                        int slash = msg.IndexOf('/', start);
                        int end = msg.IndexOf(')', slash);
                        if (slash > start && end > slash)
                        {
                            if (double.TryParse(msg[start..slash], out double cur) &&
                                double.TryParse(msg[(slash + 1)..end], out double tot) && tot > 0)
                            {
                                pct = 40.0 + (cur / tot * 55.0); // 40% to 95%
                            }
                        }
                    }
                    catch { }
                }

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Extracting PHP Archive",
                    Message = msg,
                    Percentage = pct,
                    IsIndeterminate = false
                });
            });

            await _archiveService.ExtractArchiveAsync(archiveFilePath, extractTargetDir, extractProgress, cancellationToken);

            string realPhpRoot = _archiveService.FindPhpRoot(extractTargetDir);

            if (!File.Exists(Path.Combine(realPhpRoot, "php.exe")))
            {
                throw new InvalidOperationException("Extracted package does not contain php.exe. Please ensure a valid Windows PHP build was provided.");
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Preparation Complete",
                Message = $"Ready. PHP root detected at: {Path.GetFileName(realPhpRoot)}",
                Percentage = 100
            });

            return realPhpRoot;
        }

        public async Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingPhpRoot)
        {
            var settings = _settingsService.CurrentSettings;
            string localPhpRoot = settings.Php.InstallationPath;

            return await _configDiffService.ComparePhpConfigFilesAsync(localPhpRoot, incomingPhpRoot);
        }

        public async Task<bool> ExecuteUpdatePipelineAsync(
            string incomingPhpRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            bool skipBackup = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string modeTag = isTestMode ? "[TEST MODE] " : string.Empty;
            var settings = _settingsService.CurrentSettings;
            string localPhpRoot = settings.Php.InstallationPath;

            // Step 1: Pre-validation
            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.SourceValidation,
                StepTitle = $"{modeTag}Pre-Update Health Check",
                Message = $"{modeTag}Validating target PHP directory and incoming binaries...",
                Percentage = 5,
                IsIndeterminate = false
            });

            if (!Directory.Exists(incomingPhpRoot) || !File.Exists(Path.Combine(incomingPhpRoot, "php.exe")))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Validation Failed",
                    Message = "Incoming PHP directory is invalid or missing php.exe.",
                    IsError = true
                });
                return false;
            }

            // Step 2: Backup Creation
            string backupDir = settings.General.ResolvedBackupDirectory;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupZipPath = Path.Combine(backupDir, $"php_backup_{timestamp}.zip");

            if (skipBackup)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.BackupCreation,
                    StepTitle = "Backup Skipped",
                    Message = "[BACKUP] Backup skipped by user (simulation backup already verified).",
                    Percentage = 25,
                    IsIndeterminate = false
                });
            }
            else
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.BackupCreation,
                    StepTitle = $"{modeTag}Creating Full Backup",
                    Message = $"{modeTag}Archiving existing PHP files to {Path.GetFileName(backupZipPath)}...",
                    Percentage = 15,
                    IsIndeterminate = false
                });

                var backupProgress = new Progress<string>(msg =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = $"{modeTag}Creating Full Backup",
                        Message = $"{modeTag}{msg}",
                        Percentage = 22,
                        IsIndeterminate = false
                    });
                });

                if (Directory.Exists(localPhpRoot))
                {
                    await _archiveService.CreateZipBackupAsync(localPhpRoot, backupZipPath, backupProgress, cancellationToken);
                }
            }

            // Step 3: Stop Apache Service (to release locks on php8apache2_4.dll and extensions)
            string apacheServiceName = settings.Apache.ServiceName;
            bool apacheServiceExists = _serviceManager.ServiceExists(apacheServiceName);

            if (apacheServiceExists)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = $"{modeTag}Stopping Apache Service",
                    Message = $"{modeTag}Stopping Windows service '{apacheServiceName}' to release locked PHP files...",
                    Percentage = 35,
                    IsIndeterminate = false
                });

                var serviceStopProgress = new Progress<string>(msg =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Stopping Apache Service",
                        Message = $"{modeTag}{msg}",
                        Percentage = 38,
                        IsIndeterminate = false
                    });
                });

                bool stopped = await _serviceManager.StopServiceAsync(apacheServiceName, TimeSpan.FromSeconds(30), serviceStopProgress);
                if (!stopped)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "Apache Service Stop Failed",
                        Message = $"Could not stop Apache service '{apacheServiceName}'. Aborting update to avoid file lock conflicts.",
                        IsError = true
                    });
                    return false;
                }
            }
            else
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = $"{modeTag}Service Check",
                    Message = $"{modeTag}Apache service '{apacheServiceName}' is not registered as Windows Service. Skipping service stop.",
                    Percentage = 40,
                    IsIndeterminate = false
                });
            }

            try
            {
                // Step 4: Copy updated PHP files & apply php.ini resolutions
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = isTestMode ? "[TEST MODE] Simulating File Copy" : "Updating PHP Binaries & Extensions",
                    Message = isTestMode ? "Simulating file replacement (no live files modified)..." : "Copying new PHP files into PHP directory...",
                    Percentage = 50,
                    IsIndeterminate = false
                });

                var preservedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var customMergedConfigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var diff in resolvedConfigs)
                {
                    string norm = diff.RelativeFilePath.Replace('\\', '/');

                    if (diff.Resolution == ConfigFileResolution.KeepCurrent)
                    {
                        preservedRelativePaths.Add(norm);
                    }
                    else if (diff.Resolution == ConfigFileResolution.UseMerged)
                    {
                        customMergedConfigs[norm] = diff.MergedContent;
                    }
                }

                await Task.Run(() =>
                {
                    var allIncomingFiles = Directory.GetFiles(incomingPhpRoot, "*.*", SearchOption.AllDirectories);
                    int totalFiles = allIncomingFiles.Length;
                    int current = 0;

                    foreach (var sourceFile in allIncomingFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        current++;

                        string relPath = Path.GetRelativePath(incomingPhpRoot, sourceFile).Replace('\\', '/');
                        string destFile = Path.Combine(localPhpRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

                        // If user chose KeepCurrent for this config, do not overwrite with incoming
                        if (preservedRelativePaths.Contains(relPath) && File.Exists(destFile))
                        {
                            if (isTestMode)
                            {
                                progress?.Report(new UpdateProgressReport
                                {
                                    Step = UpdateStep.FileReplacement,
                                    StepTitle = "[TEST MODE] Preserving Config",
                                    Message = $"[SIMULATE PRESERVE] Keeping current local: {relPath}",
                                    Percentage = 50 + (int)(30.0 * current / totalFiles),
                                    IsIndeterminate = false
                                });
                            }
                            continue;
                        }

                        if (!isTestMode)
                        {
                            string? dir = Path.GetDirectoryName(destFile);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            File.Copy(sourceFile, destFile, true);
                        }
                        else
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "[TEST MODE] Simulating Copy",
                                Message = $"[SIMULATE COPY] {relPath} -> {destFile}",
                                Percentage = 50 + (int)(30.0 * current / totalFiles),
                                IsIndeterminate = false
                            });
                        }

                        if (current % 25 == 0 || current == totalFiles)
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = isTestMode ? "[TEST MODE] Simulating PHP Files" : "Copying PHP Files",
                                Message = isTestMode ? $"[TEST MODE] Validating {current}/{totalFiles} files..." : $"Copying updated PHP files ({current}/{totalFiles})...",
                                Percentage = 50 + (int)(30.0 * current / totalFiles),
                                IsIndeterminate = false
                            });
                        }
                    }

                    // Apply merged configs
                    foreach (var kvp in customMergedConfigs)
                    {
                        string destFile = Path.Combine(localPhpRoot, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (!isTestMode)
                        {
                            File.WriteAllText(destFile, kvp.Value);
                        }
                        else
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "[TEST MODE] Custom Merged Config",
                                Message = $"[SIMULATE MERGE] Writing merged configuration: {kvp.Key}",
                                Percentage = 82,
                                IsIndeterminate = false
                            });
                        }
                    }

                    // Special check for php.ini when user chose OverwriteWithNew
                    var phpIniDiff = resolvedConfigs.FirstOrDefault(c => c.RelativeFilePath.Equals("php.ini", StringComparison.OrdinalIgnoreCase));
                    if (phpIniDiff != null && phpIniDiff.Resolution == ConfigFileResolution.OverwriteWithNew)
                    {
                        string targetPhpIni = Path.Combine(localPhpRoot, "php.ini");
                        if (!string.IsNullOrEmpty(phpIniDiff.IncomingFilePath) && File.Exists(phpIniDiff.IncomingFilePath))
                        {
                            if (!isTestMode)
                            {
                                File.Copy(phpIniDiff.IncomingFilePath, targetPhpIni, true);
                            }
                        }
                    }
                }, cancellationToken);

                // Step 5: Start Apache Service
                if (apacheServiceExists)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting Apache Service",
                        Message = $"{modeTag}Restarting Windows service '{apacheServiceName}'...",
                        Percentage = 85,
                        IsIndeterminate = false
                    });

                    var serviceStartProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting Apache Service",
                            Message = $"{modeTag}{msg}",
                            Percentage = 90,
                            IsIndeterminate = false
                        });
                    });

                    bool started = await _serviceManager.StartServiceAsync(apacheServiceName, TimeSpan.FromSeconds(30), serviceStartProgress);
                    if (!started)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Apache Service Start Failed",
                            Message = $"Apache service '{apacheServiceName}' failed to restart. Backup is available at {backupZipPath}.",
                            IsError = true
                        });
                        return false;
                    }
                }

                // Step 6: Post-update verification
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.HealthCheck,
                    StepTitle = $"{modeTag}Verifying Updated PHP Engine",
                    Message = $"{modeTag}Detecting active PHP version...",
                    Percentage = 95,
                    IsIndeterminate = false
                });

                string finalVersion = await _versionDetectionService.DetectPhpVersionAsync(isTestMode ? incomingPhpRoot : localPhpRoot);

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = isTestMode ? "[TEST MODE] PHP Update Simulation Completed" : "PHP Update Completed Successfully",
                    Message = isTestMode
                        ? $"[TEST MODE] Verified PHP {finalVersion}. No live files modified."
                        : $"Successfully updated to PHP {finalVersion}. Apache restarted cleanly.",
                    Percentage = 100,
                    IsIndeterminate = false
                });

                return true;
            }
            catch (Exception ex)
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Update Exception",
                    Message = $"Fatal error during PHP update: {ex.Message}. Backup saved at: {backupZipPath}",
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
                Directory.Delete(tempDirectoryPath, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clean up temp directory '{tempDirectoryPath}': {ex.Message}");
            }
        }
    }
}
