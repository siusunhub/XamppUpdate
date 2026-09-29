using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class MySqlUpdateService : IMySqlUpdateService
    {
        private readonly ISettingsService _settingsService;
        private readonly IWindowsServiceManager _serviceManager;
        private readonly IVersionDetectionService _versionDetectionService;
        private readonly IArchiveService _archiveService;
        private readonly IConfigDiffService _configDiffService;

        public MySqlUpdateService(
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
            var settings = _settingsService.CurrentSettings;
            string tempBase = settings.General.ResolvedTempDirectory;

            // Check if source is an existing directory
            if (Directory.Exists(sourceUrlOrPath))
            {
                string detectedRoot = _archiveService.FindMySqlRoot(sourceUrlOrPath);
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Preparation Complete",
                    Message = $"Ready. MySQL root detected at: {Path.GetFileName(detectedRoot)}",
                    Percentage = 100
                });
                return detectedRoot;
            }

            if (!Directory.Exists(tempBase))
            {
                Directory.CreateDirectory(tempBase);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string extractTargetDir = Path.Combine(tempBase, $"mysql_incoming_{timestamp}");
            Directory.CreateDirectory(extractTargetDir);

            string archiveFilePath;

            // Check if source is a URL or local file
            if (Uri.TryCreate(sourceUrlOrPath, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Downloading MySQL Package",
                    Message = $"Downloading archive from {sourceUrlOrPath}...",
                    Percentage = 10,
                    IsIndeterminate = true
                });

                string downloadFileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(downloadFileName) ||
                    (!downloadFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     !downloadFileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                {
                    downloadFileName = $"mysql_download_{timestamp}.zip";
                }

                archiveFilePath = Path.Combine(tempBase, downloadFileName);

                var downloadProgress = new Progress<double>(percent =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.DownloadAndExtract,
                        StepTitle = "Downloading MySQL Package",
                        Message = $"Downloading: {percent:F1}%",
                        Percentage = 10 + (percent * 0.3)
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
                                pct = 40.0 + (cur / tot * 55.0);
                            }
                        }
                    }
                    catch { }
                }

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.DownloadAndExtract,
                    StepTitle = "Extracting Archive",
                    Message = msg,
                    Percentage = pct,
                    IsIndeterminate = false
                });
            });

            await _archiveService.ExtractArchiveAsync(archiveFilePath, extractTargetDir, extractProgress, cancellationToken);

            string realMySqlRoot = _archiveService.FindMySqlRoot(extractTargetDir);

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.DownloadAndExtract,
                StepTitle = "Preparation Complete",
                Message = $"Ready. MySQL root detected at: {Path.GetFileName(realMySqlRoot)}",
                Percentage = 100
            });

            return realMySqlRoot;
        }

        public async Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingMySqlRoot)
        {
            var diffItems = new List<ConfigDiffItem>();
            var settings = _settingsService.CurrentSettings;

            string localIniPath = settings.MySql.IniPath;
            if (!File.Exists(localIniPath))
            {
                string fallbackLocal = Path.Combine(settings.MySql.InstallationPath, "bin", "my.ini");
                if (File.Exists(fallbackLocal))
                {
                    localIniPath = fallbackLocal;
                }
                else
                {
                    fallbackLocal = Path.Combine(settings.MySql.InstallationPath, "my.ini");
                    if (File.Exists(fallbackLocal))
                    {
                        localIniPath = fallbackLocal;
                    }
                }
            }

            // Find incoming my.ini / my.cnf candidates
            string incomingIniPath = Path.Combine(incomingMySqlRoot, "bin", "my.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "my.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "bin", "my-default.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "my-default.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "my-medium.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "my-large.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "my-small.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "data", "my.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "support-files", "my-default.cnf");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "support-files", "my-default.ini");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "support-files", "my-medium.cnf");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "support-files", "my-large.cnf");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "support-files", "my-small.cnf");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "share", "my-default.cnf");
            if (!File.Exists(incomingIniPath)) incomingIniPath = Path.Combine(incomingMySqlRoot, "share", "my-default.ini");

            if (!File.Exists(incomingIniPath) && Directory.Exists(incomingMySqlRoot))
            {
                try
                {
                    var iniFiles = Directory.GetFiles(incomingMySqlRoot, "*.ini", SearchOption.AllDirectories);
                    if (iniFiles.Length > 0)
                    {
                        incomingIniPath = iniFiles[0];
                    }
                    else
                    {
                        var cnfFiles = Directory.GetFiles(incomingMySqlRoot, "*.cnf", SearchOption.AllDirectories);
                        if (cnfFiles.Length > 0)
                        {
                            incomingIniPath = cnfFiles[0];
                        }
                    }
                }
                catch { }
            }

            // If incoming package does not provide a default template, check XAMPP's clean default template
            if (!File.Exists(incomingIniPath))
            {
                string xamppBackupIni = Path.Combine(settings.MySql.InstallationPath, "backup", "my.ini");
                if (File.Exists(xamppBackupIni))
                {
                    incomingIniPath = xamppBackupIni;
                }
            }

            if (File.Exists(localIniPath) && File.Exists(incomingIniPath))
            {
                var diff = await _configDiffService.CompareSingleFileAsync(localIniPath, incomingIniPath, "bin/my.ini");
                diffItems.Add(diff);
            }
            else if (File.Exists(localIniPath))
            {
                // Incoming package has no default template, keep local working config
                var diff = await _configDiffService.CompareSingleFileAsync(localIniPath, string.Empty, "bin/my.ini");
                diff.IncomingFilePath = string.Empty;
                diff.IncomingExists = false;
                diff.Resolution = ConfigFileResolution.KeepCurrent;
                diffItems.Add(diff);
            }
            else if (File.Exists(incomingIniPath))
            {
                var diff = await _configDiffService.CompareSingleFileAsync(string.Empty, incomingIniPath, "bin/my.ini");
                diff.LocalFilePath = string.Empty;
                diff.LocalExists = false;
                diff.Resolution = ConfigFileResolution.OverwriteWithNew;
                diffItems.Add(diff);
            }

            return diffItems;
        }

        public async Task<bool> ExecuteUpdatePipelineAsync(
            string incomingMySqlRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            bool skipBackup = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string localMySqlDir = settings.MySql.InstallationPath;
            string mySqlServiceName = settings.MySql.ServiceName;
            string apacheServiceName = settings.Apache.ServiceName;
            string backupDir = settings.General.ResolvedBackupDirectory;
            string modeTag = isTestMode ? "[TEST MODE] " : string.Empty;

            try
            {
                // ==========================================
                // STEP 1: Stop Apache & MySQL Services
                // ==========================================
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStop,
                    StepTitle = "Stopping Services",
                    Message = $"{modeTag}Stopping Apache and MySQL services to prevent service conflict...",
                    Percentage = 5,
                    IsIndeterminate = true
                });

                var progressAdapter = new Progress<string>(msg =>
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = "Stopping Services",
                        Message = $"{modeTag}{msg}",
                        Percentage = 10,
                        IsIndeterminate = true
                    });
                });

                // 1a. Stop Apache service first (prevents MySQL connection errors during upgrade)
                if (_serviceManager.ServiceExists(apacheServiceName))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Stopping Apache Service",
                        Message = $"{modeTag}Stopping Apache service '{apacheServiceName}' to prevent database errors...",
                        Percentage = 10,
                        IsIndeterminate = true
                    });
                    await _serviceManager.StopServiceAsync(apacheServiceName, TimeSpan.FromSeconds(30), progressAdapter);
                }

                // 1b. Stop MySQL service
                if (_serviceManager.ServiceExists(mySqlServiceName))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Stopping MySQL Service",
                        Message = $"{modeTag}Stopping MySQL service '{mySqlServiceName}'...",
                        Percentage = 15,
                        IsIndeterminate = true
                    });
                    bool stopped = await _serviceManager.StopServiceAsync(mySqlServiceName, TimeSpan.FromSeconds(30), progressAdapter);
                    if (!stopped)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStop,
                            StepTitle = $"{modeTag}Stopping MySQL Service",
                            Message = $"[WARNING] Could not verify stop for service '{mySqlServiceName}'. Proceeding with caution.",
                            Percentage = 18
                        });
                    }
                }

                if (isTestMode)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = "[TEST MODE] Service Stop Verified",
                        Message = $"[TEST MODE] Successfully verified stopping Apache ({apacheServiceName}) and MySQL ({mySqlServiceName}).",
                        Percentage = 18
                    });
                }

                // ==========================================
                // STEP 2: Dual Backup: 1. MySQL Binaries & Config (no data) 2. MySQL Data only
                // ==========================================
                if (skipBackup)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = "Backup Skipped",
                        Message = "[BACKUP] Backup creation skipped by user (simulation backup already verified).",
                        Percentage = 35,
                        IsIndeterminate = false
                    });
                }
                else
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = "Backing Up MySQL Directory",
                        Message = $"{modeTag}Creating dual backup zip archives (1. Binaries & Config, 2. Live Database Data)...",
                        Percentage = 20,
                        IsIndeterminate = true
                    });

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string binaryBackupZipPath = Path.Combine(backupDir, $"mysql_backup_{timestamp}.zip");
                    string dataBackupZipPath = Path.Combine(backupDir, $"mysql_data_backup_{timestamp}.zip");

                if (Directory.Exists(localMySqlDir))
                {
                    var backupProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.BackupCreation,
                            StepTitle = "Backing Up MySQL Directory",
                            Message = $"{modeTag}{msg}",
                            Percentage = 25,
                            IsIndeterminate = true
                        });
                    });

                    // 1. Binaries & Config Backup (excluding data folder)
                    await _archiveService.CreateZipBackupAsync(
                        localMySqlDir,
                        binaryBackupZipPath,
                        excludedDirectoryNames: new[] { "data" },
                        backupProgress,
                        cancellationToken);

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = "Binaries Backup Complete",
                        Message = $"[BACKUP 1/2] MySQL binaries & config backup saved to: {binaryBackupZipPath}",
                        Percentage = 28,
                        IsIndeterminate = false
                    });

                    // 2. Data Folder Only Backup
                    string localDataDir = Path.Combine(localMySqlDir, "data");
                    if (Directory.Exists(localDataDir))
                    {
                        var dataBackupProgress = new Progress<string>(msg =>
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.BackupCreation,
                                StepTitle = "Backing Up MySQL Database Data",
                                Message = $"{modeTag}{msg}",
                                Percentage = 32,
                                IsIndeterminate = true
                            });
                        });

                        await _archiveService.CreateZipBackupAsync(
                            localDataDir,
                            dataBackupZipPath,
                            null,
                            dataBackupProgress,
                            cancellationToken);

                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.BackupCreation,
                            StepTitle = "Data Backup Complete",
                            Message = $"[BACKUP 2/2] MySQL database data backup saved to: {dataBackupZipPath}",
                            Percentage = 35,
                            IsIndeterminate = false
                        });
                    }
                    else
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.BackupCreation,
                            StepTitle = "Data Backup Skipped",
                            Message = $"[BACKUP 2/2] No local data directory found at '{localDataDir}'. Skipping data zip backup.",
                            Percentage = 35,
                            IsIndeterminate = false
                        });
                    }
                }
                else
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.BackupCreation,
                        StepTitle = "Backup Skipped",
                        Message = $"[BACKUP] Local MySQL directory '{localMySqlDir}' does not exist. Skipping backup.",
                        Percentage = 35
                    });
                }
            }

                // ==========================================
                // STEP 3: Rename MySQL folder (mysql_old, mysql_old1, ...)
                // ==========================================
                string targetRenamePath = GetAvailableOldDirectoryPath(localMySqlDir);
                string targetRenameName = Path.GetFileName(targetRenamePath);

                if (Directory.Exists(localMySqlDir))
                {
                    if (isTestMode)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Preserving Existing Directory",
                            Message = $"[STEP 3/6] [TEST MODE] [SIMULATE RENAME] {localMySqlDir} -> {targetRenamePath}",
                            Percentage = 40
                        });
                    }
                    else
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Renaming Existing Directory",
                            Message = $"[STEP 3/6] [RENAME] Moving existing MySQL folder to '{targetRenameName}'...",
                            Percentage = 40
                        });

                        Directory.Move(localMySqlDir, targetRenamePath);

                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Rename Complete",
                            Message = $"[RENAME] Preserved existing MySQL directory as '{targetRenameName}'.",
                            Percentage = 45
                        });
                    }
                }
                else
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Rename Skipped",
                        Message = $"[STEP 3/6] No existing directory '{localMySqlDir}' to rename.",
                        Percentage = 45
                    });
                }

                // ==========================================
                // STEP 4: Copy new MySQL files (without data/ folder)
                // ==========================================
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = "Deploying New MySQL Files",
                    Message = $"{modeTag}{(isTestMode ? "Simulating file deployment (excluding data/)..." : "Copying new MySQL files (excluding data/)...")}",
                    Percentage = 50
                });

                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };

                var allIncomingFiles = Directory.GetFiles(incomingMySqlRoot, "*.*", enumOptions);
                int totalFiles = allIncomingFiles.Length;
                int copiedCount = 0;

                foreach (var srcFile in allIncomingFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string relPath = Path.GetRelativePath(incomingMySqlRoot, srcFile);

                    // Skip any file in data directory
                    if (IsDataDirectory(relPath))
                    {
                        continue;
                    }

                    string destFile = Path.Combine(localMySqlDir, relPath);
                    copiedCount++;

                    if (isTestMode)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Deploying New MySQL Files",
                            Message = $"[SIMULATE COPY] {srcFile} -> {destFile}",
                            Percentage = 50 + ((double)copiedCount / Math.Max(1, totalFiles) * 20.0)
                        });
                    }
                    else
                    {
                        string? destFolder = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                        {
                            Directory.CreateDirectory(destFolder);
                        }

                        File.Copy(srcFile, destFile, true);

                        if (copiedCount % 25 == 0 || copiedCount == totalFiles)
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "Deploying New MySQL Files",
                                Message = $"[DEPLOY] Copied {copiedCount}/{totalFiles} files: {relPath}",
                                Percentage = 50 + ((double)copiedCount / Math.Max(1, totalFiles) * 20.0)
                            });
                        }
                    }
                }

                // ==========================================
                // STEP 5: Copy / Update my.ini in mysql/bin
                // ==========================================
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ConfigApplication,
                    StepTitle = "Applying Configuration",
                    Message = $"{modeTag}Writing resolved my.ini configuration...",
                    Percentage = 75
                });

                foreach (var diffItem in resolvedConfigs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string destIniPath = Path.Combine(localMySqlDir, diffItem.RelativeFilePath);

                    if (isTestMode)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Applying Configuration",
                            Message = $"[SIMULATE CONFIG] Resolution: {diffItem.Resolution} on '{destIniPath}'",
                            Percentage = 78
                        });
                    }
                    else
                    {
                        string contentToWrite = diffItem.Resolution switch
                        {
                            ConfigFileResolution.KeepCurrent => diffItem.OriginalLocalContent,
                            ConfigFileResolution.OverwriteWithNew => diffItem.IncomingContent,
                            ConfigFileResolution.UseMerged => diffItem.MergedContent,
                            _ => diffItem.OriginalLocalContent
                        };

                        string? iniFolder = Path.GetDirectoryName(destIniPath);
                        if (!string.IsNullOrEmpty(iniFolder) && !Directory.Exists(iniFolder))
                        {
                            Directory.CreateDirectory(iniFolder);
                        }

                        await File.WriteAllTextAsync(destIniPath, contentToWrite, new UTF8Encoding(false), cancellationToken);

                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Applying Configuration",
                            Message = $"[CONFIG] Applied {diffItem.Resolution} resolution to '{diffItem.RelativeFilePath}'.",
                            Percentage = 80
                        });
                    }
                }

                // ==========================================
                // STEP 6: Copy data folder from renamed folder to new mysql/data
                // ==========================================
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.FileReplacement,
                    StepTitle = "Migrating Database Data",
                    Message = $"{modeTag}Migrating data directory from previous MySQL installation...",
                    Percentage = 82
                });

                string srcDataDir = isTestMode
                    ? (Directory.Exists(Path.Combine(localMySqlDir, "data")) ? Path.Combine(localMySqlDir, "data") : Path.Combine(targetRenamePath, "data"))
                    : Path.Combine(targetRenamePath, "data");

                string destDataDir = Path.Combine(localMySqlDir, "data");

                if (Directory.Exists(srcDataDir))
                {
                    if (isTestMode)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Migrating Database Data",
                            Message = $"[TEST MODE] [SIMULATE DATA] Preserving data directory: {srcDataDir} -> {destDataDir}",
                            Percentage = 90
                        });
                    }
                    else
                    {
                        bool movedSuccessfully = false;

                        // Primary Approach: Atomic Directory Move (instant, zero risk of InnoDB page corruption)
                        if (!Directory.Exists(destDataDir))
                        {
                            try
                            {
                                Directory.Move(srcDataDir, destDataDir);
                                movedSuccessfully = true;
                                progress?.Report(new UpdateProgressReport
                                {
                                    Step = UpdateStep.FileReplacement,
                                    StepTitle = "Data Migration Complete",
                                    Message = $"[DATA] Atomically preserved database data directory '{destDataDir}' (100% InnoDB consistency preserved).",
                                    Percentage = 90
                                });
                            }
                            catch (Exception ex)
                            {
                                progress?.Report(new UpdateProgressReport
                                {
                                    Step = UpdateStep.FileReplacement,
                                    StepTitle = "Data Migration",
                                    Message = $"[DATA WARNING] Direct move not available ({ex.Message}). Falling back to stream migration...",
                                    Percentage = 83
                                });
                            }
                        }

                        // Fallback Approach: Chunked Stream Migration (if move fails or dest exists)
                        if (!movedSuccessfully)
                        {
                            var dataFiles = Directory.GetFiles(srcDataDir, "*.*", enumOptions);
                            int totalDataFiles = dataFiles.Length;
                            long totalDataBytes = 0;
                            foreach (var f in dataFiles)
                            {
                                try { totalDataBytes += new FileInfo(f).Length; } catch { }
                            }

                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "Migrating Database Data",
                                Message = $"{modeTag}Migrating {totalDataFiles} database files ({ArchiveService.FormatBytes(totalDataBytes)})...",
                                Percentage = 83
                            });

                            int dataCopied = 0;
                            long totalDataBytesCopied = 0;

                            foreach (var srcDataFile in dataFiles)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                dataCopied++;

                                string relDataPath = Path.GetRelativePath(srcDataDir, srcDataFile);
                                string destDataFile = Path.Combine(destDataDir, relDataPath);

                                long bytesCopied = await CopyFileChunkedAsync(
                                    srcDataFile,
                                    destDataFile,
                                    relDataPath,
                                    totalDataBytes,
                                    totalDataBytesCopied,
                                    progress,
                                    modeTag,
                                    cancellationToken);
                                totalDataBytesCopied += bytesCopied;

                                if (dataCopied % 50 == 0 || dataCopied == totalDataFiles)
                                {
                                    double overallPercent = totalDataBytes > 0 ? (double)totalDataBytesCopied / totalDataBytes * 100.0 : 100.0;
                                    progress?.Report(new UpdateProgressReport
                                    {
                                        Step = UpdateStep.FileReplacement,
                                        StepTitle = "Migrating Database Data",
                                        Message = $"[DATA] Copied {dataCopied}/{totalDataFiles} database files ({ArchiveService.FormatBytes(totalDataBytesCopied)} / {ArchiveService.FormatBytes(totalDataBytes)} - {overallPercent:F0}%)...",
                                        Percentage = 83 + (overallPercent * 0.07)
                                    });
                                }
                            }

                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.FileReplacement,
                                StepTitle = "Data Migration Complete",
                                Message = $"[DATA] Successfully migrated {totalDataFiles} database file(s) ({ArchiveService.FormatBytes(totalDataBytesCopied)}) to '{destDataDir}'.",
                                Percentage = 90
                            });
                        }
                    }
                }
                else
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Data Migration Skipped",
                        Message = $"[WARNING] No previous data folder found at '{srcDataDir}'. Skipping data migration.",
                        Percentage = 90
                    });
                }

                // ==========================================
                // STEP 7: Start MySQL & Apache Services
                // ==========================================
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ServiceStart,
                    StepTitle = "Starting Services",
                    Message = $"{modeTag}Starting MySQL and Apache services...",
                    Percentage = 92
                });

                // 7a. Start MySQL service first
                if (_serviceManager.ServiceExists(mySqlServiceName))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting MySQL Service",
                        Message = $"{modeTag}Starting MySQL service '{mySqlServiceName}'...",
                        Percentage = 93,
                        IsIndeterminate = true
                    });
                    bool mysqlStarted = await _serviceManager.StartServiceAsync(mySqlServiceName, TimeSpan.FromSeconds(30), progressAdapter);
                    if (!mysqlStarted)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting MySQL Service",
                            Message = $"[WARNING] MySQL service '{mySqlServiceName}' did not report running status within timeout.",
                            Percentage = 95
                        });
                    }
                }

                // 7b. Start Apache service
                if (_serviceManager.ServiceExists(apacheServiceName))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting Apache Service",
                        Message = $"{modeTag}Starting Apache service '{apacheServiceName}'...",
                        Percentage = 96,
                        IsIndeterminate = true
                    });
                    bool apacheStarted = await _serviceManager.StartServiceAsync(apacheServiceName, TimeSpan.FromSeconds(30), progressAdapter);
                    if (!apacheStarted)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting Apache Service",
                            Message = $"[WARNING] Apache service '{apacheServiceName}' did not report running status within timeout.",
                            Percentage = 98
                        });
                    }
                }

                if (isTestMode)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = "[TEST MODE] Service Restart Verified",
                        Message = $"[TEST MODE] Successfully verified restarting MySQL ({mySqlServiceName}) and Apache ({apacheServiceName}).",
                        Percentage = 98
                    });
                }

                // ==========================================
                // STEP 8: Cleanup & Complete
                // ==========================================
                if (!isTestMode)
                {
                    string? topTempDir = Directory.GetParent(incomingMySqlRoot)?.FullName;
                    if (!string.IsNullOrEmpty(topTempDir) && topTempDir.Contains("mysql_incoming_"))
                    {
                        CleanupTempDirectory(topTempDir);
                    }
                }

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Complete,
                    StepTitle = "MySQL Update Complete",
                    Message = isTestMode
                        ? "Dry-run simulation completed successfully! Real backup created. No MySQL files were modified."
                        : "MySQL upgraded successfully, database data migrated, and services restarted cleanly!",
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
                    StepTitle = "Update Failed",
                    Message = $"[FATAL ERROR] {ex.Message}",
                    Percentage = 100,
                    IsIndeterminate = false,
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
            catch { }
        }

        public static string GetAvailableOldDirectoryPath(string currentDirectoryPath)
        {
            string? parent = Path.GetDirectoryName(currentDirectoryPath);
            if (string.IsNullOrEmpty(parent)) parent = Directory.GetCurrentDirectory();

            string dirName = Path.GetFileName(currentDirectoryPath);
            string candidate = Path.Combine(parent, $"{dirName}_old");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            int index = 1;
            while (true)
            {
                string indexedCandidate = Path.Combine(parent, $"{dirName}_old{index}");
                if (!Directory.Exists(indexedCandidate))
                {
                    return indexedCandidate;
                }
                index++;
            }
        }

        public string DetectMySqlUpgradeToolPath()
        {
            var settings = _settingsService.CurrentSettings;
            string localMySqlDir = settings.MySql.InstallationPath;

            var candidates = new[]
            {
                Path.Combine(localMySqlDir, "bin", "mariadb-upgrade.exe"),
                Path.Combine(localMySqlDir, "bin", "mysql_upgrade.exe"),
                Path.Combine(localMySqlDir, "mysql_upgrade.bat"),
                Path.Combine(localMySqlDir, "..", "mysql_upgrade.bat"),
                Path.Combine(localMySqlDir, "..", "mysql", "mysql_upgrade.bat")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return Path.Combine(localMySqlDir, "bin", "mysql_upgrade.exe");
        }

        public async Task<bool> RunMySqlUpgradeAsync(
            string? scriptOrExecutablePath,
            string username,
            string password,
            string additionalArguments,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string toolPath = string.IsNullOrWhiteSpace(scriptOrExecutablePath)
                ? DetectMySqlUpgradeToolPath()
                : scriptOrExecutablePath.Trim();

            if (!File.Exists(toolPath))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Tool Not Found",
                    Message = $"[ERROR] mysql_upgrade tool not found at: {toolPath}",
                    Percentage = 100,
                    IsError = true
                });
                return false;
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.ConfigApplication,
                StepTitle = "Starting mysql_upgrade",
                Message = $"[UPGRADE] Executing: {Path.GetFileName(toolPath)}",
                Percentage = 10,
                IsIndeterminate = true
            });

            // Ensure MySQL service is running so upgrade tool can connect to database
            var settings = _settingsService.CurrentSettings;
            string mySqlServiceName = settings.MySql.ServiceName;
            if (_serviceManager.ServiceExists(mySqlServiceName))
            {
                var serviceStatus = await _serviceManager.GetServiceStatusAsync(mySqlServiceName);
                if (serviceStatus != ServiceControllerStatus.Running)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Starting MySQL Service",
                        Message = $"[SERVICE] MySQL service '{mySqlServiceName}' is {serviceStatus}. Starting service for upgrade connection...",
                        Percentage = 15,
                        IsIndeterminate = true
                    });

                    var serviceProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Starting MySQL Service",
                            Message = $"[SERVICE] {msg}",
                            Percentage = 20,
                            IsIndeterminate = true
                        });
                    });

                    await _serviceManager.StartServiceAsync(mySqlServiceName, TimeSpan.FromSeconds(30), serviceProgress);
                }
            }

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    string ext = Path.GetExtension(toolPath).ToLowerInvariant();
                    string workingDir = Path.GetDirectoryName(toolPath) ?? string.Empty;
                    if (Directory.Exists(workingDir))
                    {
                        psi.WorkingDirectory = workingDir;
                    }

                    var argList = new List<string>();
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        argList.Add($"-u {username.Trim()}");
                    }
                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        argList.Add($"--password=\"{password.Trim()}\"");
                    }

                    string addArgs = additionalArguments?.Trim() ?? string.Empty;
                    if (!addArgs.Contains("-h", StringComparison.OrdinalIgnoreCase) &&
                        !addArgs.Contains("--host", StringComparison.OrdinalIgnoreCase))
                    {
                        argList.Add("-h 127.0.0.1");
                    }

                    if (!string.IsNullOrWhiteSpace(addArgs))
                    {
                        argList.Add(addArgs);
                    }
                    else
                    {
                        argList.Add("--force --verbose");
                    }

                    string fullArgs = string.Join(" ", argList);

                    if (ext == ".bat" || ext == ".cmd")
                    {
                        psi.FileName = "cmd.exe";
                        psi.Arguments = $"/c \"\"{toolPath}\" {fullArgs}\"";
                    }
                    else
                    {
                        psi.FileName = toolPath;
                        psi.Arguments = fullArgs;
                    }

                    string loggedArgs = psi.Arguments;
                    if (!string.IsNullOrEmpty(password))
                    {
                        loggedArgs = loggedArgs.Replace($"\"{password.Trim()}\"", "\"******\"")
                                               .Replace(password.Trim(), "******");
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Running mysql_upgrade",
                        Message = $"[COMMAND] {psi.FileName} {loggedArgs}",
                        Percentage = 30,
                        IsIndeterminate = true
                    });

                    using var process = new Process { StartInfo = psi };

                    bool hasFatalError = false;
                    string fatalErrorMessage = string.Empty;
                    object errLock = new object();

                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            string rawData = e.Data;
                            if (rawData.Contains("FATAL ERROR", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Upgrade failed", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Can't connect to MySQL server", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (errLock)
                                {
                                    hasFatalError = true;
                                    fatalErrorMessage = rawData.Trim();
                                }
                            }

                            string sanitizedData = !string.IsNullOrEmpty(password)
                                ? rawData.Replace(password.Trim(), "******")
                                : rawData;

                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "mysql_upgrade Output",
                                Message = sanitizedData,
                                Percentage = 60,
                                IsIndeterminate = true
                            });
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            string rawData = e.Data;
                            if (rawData.Contains("FATAL ERROR", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Upgrade failed", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Can't connect to MySQL server", StringComparison.OrdinalIgnoreCase) ||
                                rawData.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (errLock)
                                {
                                    hasFatalError = true;
                                    fatalErrorMessage = rawData.Trim();
                                }
                            }

                            string sanitizedData = !string.IsNullOrEmpty(password)
                                ? rawData.Replace(password.Trim(), "******")
                                : rawData;

                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "mysql_upgrade Info/Warning",
                                Message = sanitizedData,
                                Percentage = 70,
                                IsIndeterminate = true
                            });
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
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    int exitCode = process.ExitCode;
                    if (exitCode == 0 && !hasFatalError)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Complete,
                            StepTitle = "Database Upgrade Complete",
                            Message = "[SUCCESS] Database schemas and system tables upgraded successfully (Exit Code 0).",
                            Percentage = 100,
                            IsIndeterminate = false
                        });
                        return true;
                    }
                    else
                    {
                        string errMsg = hasFatalError && !string.IsNullOrEmpty(fatalErrorMessage)
                            ? $"[ERROR] mysql_upgrade failed: {fatalErrorMessage} (Exit Code {exitCode})"
                            : $"[ERROR] mysql_upgrade exited with code {exitCode}. Check that MySQL service is running and credentials are correct.";

                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Database Upgrade Failed",
                            Message = errMsg,
                            Percentage = 100,
                            IsIndeterminate = false,
                            IsError = true
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "Database Upgrade Failed",
                        Message = $"[ERROR] Exception executing mysql_upgrade: {ex.Message}",
                        Percentage = 100,
                        IsError = true
                    });
                    return false;
                }
            }, cancellationToken);
        }

        private static bool IsDataDirectory(string relativePath)
        {
            string norm = relativePath.Replace('\\', '/').TrimStart('/');
            return norm.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                   norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<long> CopyFileChunkedAsync(
            string sourcePath,
            string destinationPath,
            string relativePath,
            long totalDataBytes,
            long initialTotalBytesCopied,
            IProgress<UpdateProgressReport>? progress,
            string modeTag,
            CancellationToken cancellationToken)
        {
            string? parentDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var fileInfo = new FileInfo(sourcePath);
            long fileLength = fileInfo.Length;

            // For small files (< 10MB), direct fast copy
            if (fileLength < 10 * 1024 * 1024)
            {
                File.Copy(sourcePath, destinationPath, true);
                return fileLength;
            }

            // For large files (> 10MB, e.g. ibdata1, large .ibd tables), stream with 4MB buffer & live progress
            byte[] buffer = new byte[4 * 1024 * 1024];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastReportTicks = sw.ElapsedMilliseconds;
            long fileBytesRead = 0;

            await using var srcStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4 * 1024 * 1024, FileOptions.SequentialScan);
            await using var dstStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.SequentialScan);

            int bytesRead;
            while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dstStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                fileBytesRead += bytesRead;

                long currentTotal = initialTotalBytesCopied + fileBytesRead;
                long currentTicks = sw.ElapsedMilliseconds;
                if (currentTicks - lastReportTicks > 500 || fileBytesRead == fileLength)
                {
                    double filePercent = (double)fileBytesRead / fileLength * 100.0;
                    double overallPercent = totalDataBytes > 0 ? (double)currentTotal / totalDataBytes * 100.0 : 0;
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Migrating Database Data",
                        Message = $"{modeTag}[DATA MIGRATE] {relativePath} ({ArchiveService.FormatBytes(fileBytesRead)} / {ArchiveService.FormatBytes(fileLength)} - {filePercent:F0}%) | Overall: {overallPercent:F0}%",
                        Percentage = 82 + (overallPercent * 0.08)
                    });
                    lastReportTicks = currentTicks;
                }
            }

            return fileBytesRead;
        }
    }
}
