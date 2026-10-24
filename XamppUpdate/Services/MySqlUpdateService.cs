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

        // Credentials for the pre-upgrade safe shutdown (SET GLOBAL innodb_fast_shutdown=0 / mysqladmin shutdown).
        private string _shutdownUser = "root";
        private string _shutdownPassword = string.Empty;

        public bool StartForCleanShutdown { get; set; } = true;

        public void SetShutdownCredentials(string user, string password)
        {
            _shutdownUser = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();
            _shutdownPassword = password ?? string.Empty;
        }

        /// <summary>Returns the most relevant startup failure line from the newest error log (or empty).</summary>
        private static string ReadStartupErrorHint(string dataDir)
        {
            try
            {
                var logFile = new DirectoryInfo(dataDir)
                    .EnumerateFiles("*.err")
                    .Concat(new DirectoryInfo(dataDir).EnumerateFiles("mysql_error.log"))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (logFile == null) return string.Empty;

                string[] lines;
                using (var fs = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    lines = sr.ReadToEnd().Split('\n');
                }

                var tail = lines.Skip(Math.Max(0, lines.Length - 60)).Select(l => l.Trim()).ToList();
                return tail.LastOrDefault(l => l.Contains("Upgrade after a crash", StringComparison.OrdinalIgnoreCase))
                       ?? tail.LastOrDefault(l => l.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                       ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<(bool Success, string Message)> TestShutdownLoginAsync()
        {
            var settings = _settingsService.CurrentSettings;
            return await _serviceManager.TestMySqlLoginAsync(
                settings.MySql.InstallationPath, _shutdownUser, _shutdownPassword, DetectMySqlPort());
        }
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

                // Remember HOW each server was running so the same mode can be restored in Step 7.
                // Service-spawned processes also match the process check, so a process only counts as
                // "standalone" when the service itself is not running.
                string apacheDir = settings.Apache.InstallationPath;
                string mySqlIniPath = settings.MySql.IniPath;

                bool apacheServiceExists = _serviceManager.ServiceExists(apacheServiceName);
                bool apacheSvcRunning = apacheServiceExists &&
                    await _serviceManager.GetServiceStatusAsync(apacheServiceName) == ServiceControllerStatus.Running;
                bool apacheStandaloneWasRunning = !apacheSvcRunning && _serviceManager.IsApacheProcessRunning(apacheDir);

                bool mySqlServiceExists = _serviceManager.ServiceExists(mySqlServiceName);
                bool mySqlSvcRunning = mySqlServiceExists &&
                    await _serviceManager.GetServiceStatusAsync(mySqlServiceName) == ServiceControllerStatus.Running;
                bool mySqlStandaloneWasRunning = !mySqlSvcRunning && _serviceManager.IsMySqlProcessRunning(localMySqlDir);

                // 1-pre. Credentials preflight: check the login BEFORE taking anything down.
                // A standalone process can only be shut down cleanly with a working login, so abort now (nothing touched yet).
                // With a running service the SCM can stop it without a login, so a failed login is only a warning there
                // (innodb_fast_shutdown=0 will be skipped, the shutdown itself is still clean).
                if (_serviceManager.IsMySqlProcessRunning(localMySqlDir))
                {
                    var (loginOk, loginMessage) = await _serviceManager.TestMySqlLoginAsync(
                        localMySqlDir, _shutdownUser, _shutdownPassword, DetectMySqlPort());

                    if (!loginOk && mySqlStandaloneWasRunning)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Database Login Required",
                            Message = $"[ERROR] {loginMessage} MySQL is running as a standalone process, which can only be shut down " +
                                      "cleanly with a working admin login. Enter the root user/password on this step and click 'Test Login'. " +
                                      "No files or services were changed.",
                            Percentage = 100,
                            IsError = true
                        });
                        return false;
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = "Database Login Check",
                        Message = loginOk
                            ? $"{modeTag}{loginMessage}"
                            : $"[WARNING] {loginMessage} Continuing: the service will be stopped gracefully without a login " +
                              "(innodb_fast_shutdown=0 is skipped).",
                        Percentage = 4
                    });
                }

                // Phase-tagged progress: a leading "[TITLE]" in a message becomes the log step title, so each
                // command (start / login / flush / stop / verify) appears as its own labelled section.
                IProgress<string> PhaseProgress(int percentage) => new Progress<string>(msg =>
                {
                    string title = "Safe MySQL Shutdown";
                    string text = msg;
                    if (msg.StartsWith('['))
                    {
                        int end = msg.IndexOf(']');
                        if (end > 1)
                        {
                            title = msg.Substring(1, end - 1);
                            text = msg.Substring(end + 1).TrimStart();
                        }
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}{title}",
                        Message = text,
                        Percentage = percentage,
                        IsIndeterminate = true
                    });
                });

                // 1-start. MySQL is STOPPED. Its data folder may still hold a dirty redo log from an earlier unclean stop,
                // which the new version cannot replay ("Upgrade after a crash is not supported"). Start the CURRENT
                // (old) binaries once so InnoDB finishes its own recovery, then the safe shutdown below makes it clean.
                string dataDirForRecovery = Path.Combine(localMySqlDir, "data");
                if (StartForCleanShutdown &&
                    !_serviceManager.IsMySqlProcessRunning(localMySqlDir) &&
                    Directory.Exists(dataDirForRecovery))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Starting MySQL For Clean Shutdown",
                        Message = $"{modeTag}MySQL is stopped. Starting the current version once so InnoDB can finish recovery and shut down cleanly...",
                        Percentage = 3,
                        IsIndeterminate = true
                    });

                    var startLog = PhaseProgress(3);
                    startLog.Report($"[START 1/2 START] Running: start MySQL as {(mySqlServiceExists ? $"Windows service '{mySqlServiceName}'" : "standalone process")} using the currently installed binaries");
                    await _serviceManager.EnsureMySqlRunningAsync(
                        mySqlServiceName, localMySqlDir, mySqlIniPath,
                        new Progress<string>(m => startLog.Report($"[START 1/2 START] {m}")));

                    bool startedOk = _serviceManager.IsMySqlProcessRunning(localMySqlDir);
                    startLog.Report(startedOk
                        ? "[START 1/2 START] OK: MySQL process is running."
                        : "[START 1/2 START] FAILED: MySQL exited right after launch (see the error log).");

                    if (!startedOk)
                    {
                        string logHint = ReadStartupErrorHint(dataDirForRecovery);
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "MySQL Did Not Start",
                            Message = "[ERROR] The currently installed MySQL could not start on its data folder, so it cannot be shut down cleanly. " +
                                      (string.IsNullOrEmpty(logHint) ? string.Empty : $"Server log: {logHint} ") +
                                      "If the data was last used by an OLDER MariaDB (e.g. 10.4), run that version once on this data folder " +
                                      "and shut it down cleanly (binaries are in your mysql_backup_*.zip / mysql_old), then retry. " +
                                      "No files were replaced.",
                            Percentage = 100,
                            IsError = true
                        });
                        return false;
                    }

                    // Crash recovery can take a while: wait until the server accepts the admin login.
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Waiting For MySQL Recovery",
                        Message = $"{modeTag}Waiting for MySQL to finish recovery and accept connections...",
                        Percentage = 4,
                        IsIndeterminate = true
                    });

                    bool recovered = false;
                    string recoveryMessage = string.Empty;
                    var recoveryStart = DateTime.UtcNow;
                    while (DateTime.UtcNow - recoveryStart < TimeSpan.FromSeconds(180) &&
                           _serviceManager.IsMySqlProcessRunning(localMySqlDir))
                    {
                        var (ok, msg) = await _serviceManager.TestMySqlLoginAsync(
                            localMySqlDir, _shutdownUser, _shutdownPassword, DetectMySqlPort());
                        recoveryMessage = msg;
                        if (ok)
                        {
                            recovered = true;
                            startLog.Report("[START 2/2 READY] OK: server finished recovery and accepted the admin login.");
                            break;
                        }

                        // A refused login (wrong password) will not fix itself; only keep waiting on connection errors.
                        if (msg.Contains("Access denied", StringComparison.OrdinalIgnoreCase)) break;
                        await Task.Delay(2000, cancellationToken);
                    }

                    if (!recovered)
                    {
                        // We started it, so state clearly what is left running.
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Database Login Required",
                            Message = $"[ERROR] {recoveryMessage} MySQL was started to complete recovery and is still running. " +
                                      "Enter the correct admin user/password on this step, click 'Test Login', and run the update again; " +
                                      "it will then be shut down cleanly. No files were replaced.",
                            Percentage = 100,
                            IsError = true
                        });
                        return false;
                    }
                }

                // 1a. Stop Apache first (prevents MySQL connection errors during upgrade) - service and/or standalone httpd
                if (apacheServiceExists || apacheStandaloneWasRunning)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Stopping Apache Service",
                        Message = $"{modeTag}Stopping Apache service '{apacheServiceName}' to prevent database errors...",
                        Percentage = 10,
                        IsIndeterminate = true
                    });
                    await _serviceManager.StopApacheAnyAsync(apacheServiceName, apacheDir, progressAdapter);
                }

                // 1b. UPGRADE-SAFE MySQL shutdown (service and/or standalone process).
                // Sets innodb_fast_shutdown=0 (full flush) then stops gracefully and WAITS - it never kills.
                // A hard kill leaves the InnoDB redo log dirty, and MariaDB 10.5+ refuses to start on it:
                // "Upgrade after a crash is not supported. The redo log was created with MariaDB 10.4.x".
                if (_serviceManager.ServiceExists(mySqlServiceName) || _serviceManager.IsMySqlProcessRunning(localMySqlDir))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStop,
                        StepTitle = $"{modeTag}Safe MySQL Shutdown",
                        Message = $"{modeTag}Flushing InnoDB and shutting MySQL down cleanly before replacing any files...",
                        Percentage = 15,
                        IsIndeterminate = true
                    });

                    bool cleanStop = await _serviceManager.SafeShutdownMySqlAsync(
                        mySqlServiceName, localMySqlDir, _shutdownUser, _shutdownPassword, DetectMySqlPort(), PhaseProgress(16));

                    if (!cleanStop)
                    {
                        // Do NOT fall back to killing the process: the next version could not recover that log.
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Safe Shutdown Failed",
                            Message = "[ERROR] The safe shutdown did not complete: see the 'SHUTDOWN 1/4 ... 4/4' log lines above " +
                                      "for the command that failed. No files were changed. Fix that command (e.g. admin login) or stop MySQL " +
                                      "manually ('mysqladmin -u root -p shutdown'), then run the update again.",
                            Percentage = 100,
                            IsError = true
                        });
                        return false;
                    }
                }

                // 1c. Release file locks from any other database process (normally none are left after 1b)
                await EnsureNoLingeringDatabaseProcessesAsync(localMySqlDir, progress, cancellationToken);

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
                // STEP 3: Rename MySQL folder (mysql_old_{timestamp}, ...)
                // ==========================================
                // Ensure no background mysqld.exe / mariadbd.exe processes or open handles before renaming
                if (!isTestMode)
                {
                    await EnsureNoLingeringDatabaseProcessesAsync(localMySqlDir, progress, cancellationToken);
                }

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

                        // Retry policy: exponential backoff (1s, 2s, 4s, 8s, 15s cap...) with +/-20% jitter.
                        // Antivirus scanners / Explorer / search indexer handles often release after 10-60s,
                        // so a longer total window (~1.5 min) is far more forgiving than a short linear retry.
                        const int maxRetries = 8;
                        const int baseDelayMs = 1000;
                        const int maxDelayMs = 15000;

                        bool moved = false;
                        Exception? lastException = null;

                        for (int attempt = 1; attempt <= maxRetries; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            try
                            {
                                Directory.Move(localMySqlDir, targetRenamePath);
                                moved = true;
                                break;
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                            {
                                lastException = ex;

                                // A previous partial attempt may have already moved the folder.
                                if (!Directory.Exists(localMySqlDir) && Directory.Exists(targetRenamePath))
                                {
                                    moved = true;
                                    break;
                                }

                                if (attempt < maxRetries)
                                {
                                    int delayMs = (int)Math.Min(maxDelayMs, baseDelayMs * Math.Pow(2, attempt - 1));
                                    delayMs = (int)(delayMs * (0.8 + Random.Shared.NextDouble() * 0.4));

                                    progress?.Report(new UpdateProgressReport
                                    {
                                        Step = UpdateStep.FileReplacement,
                                        StepTitle = "Retrying Folder Move",
                                        Message = $"[RETRY {attempt}/{maxRetries}] Folder '{localMySqlDir}' is locked ({ex.Message}). Detecting lingering processes & retrying in {delayMs / 1000.0:0.0}s...",
                                        Percentage = 40
                                    });

                                    await EnsureNoLingeringDatabaseProcessesAsync(localMySqlDir, progress, cancellationToken);
                                    await Task.Delay(delayMs, cancellationToken);
                                }
                            }
                        }

                        if (!moved && lastException != null)
                        {
                            throw new InvalidOperationException(
                                $"Failed to move '{localMySqlDir}' to '{targetRenameName}' after {maxRetries} attempts. " +
                                $"Access was denied ({lastException.Message}). " +
                                $"Nothing was changed - your original MySQL folder is intact, so it is safe to click Retry. " +
                                $"Please ensure mysqld.exe / mariadbd.exe is stopped and no command prompts or Explorer windows are open inside '{localMySqlDir}'.",
                                lastException);
                        }

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

                if (!isTestMode)
                {
                    await MySqlDeployHelper.DeployNewFilesAsync(incomingMySqlRoot, localMySqlDir, progress, cancellationToken);
                }

                if (isTestMode)
                {
                    var allIncomingFiles = Directory.GetFiles(incomingMySqlRoot, "*", enumOptions);
                    int totalFiles = allIncomingFiles.Length;
                    int copiedCount = 0;

                    foreach (var srcFile in allIncomingFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string relPath = Path.GetRelativePath(incomingMySqlRoot, srcFile);

                        // Skip any file in data directory
                        if (MySqlDeployHelper.IsDataDirectory(relPath))
                        {
                            continue;
                        }

                        string destFile = Path.Combine(localMySqlDir, relPath);
                        copiedCount++;

                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Deploying New MySQL Files",
                            Message = $"[SIMULATE COPY] {srcFile} -> {destFile}",
                            Percentage = 50 + ((double)copiedCount / Math.Max(1, totalFiles) * 20.0)
                        });
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
                        await MySqlDeployHelper.ApplyConfigsAsync(new[] { diffItem }, localMySqlDir, progress, cancellationToken);
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
                        await MySqlDeployHelper.MigrateDataAsync(srcDataDir, destDataDir, progress, cancellationToken);
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Data Migration Complete",
                            Message = $"[DATA] Preserved database data directory '{destDataDir}'. Files were kept as-is; InnoDB consistency depends on MySQL having been shut down cleanly beforehand.",
                            Percentage = 90
                        });
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

                // 7a. Start MySQL first, in the mode it was running in (service, or standalone process)
                if (mySqlServiceExists && !mySqlStandaloneWasRunning)
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

                else if (mySqlStandaloneWasRunning)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting MySQL Process",
                        Message = $"{modeTag}MySQL was running as a process. Starting it again...",
                        Percentage = 93,
                        IsIndeterminate = true
                    });
                    bool mysqlProcStarted = await _serviceManager.StartMySqlProcessAsync(localMySqlDir, mySqlIniPath, progressAdapter);
                    if (!mysqlProcStarted)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting MySQL Process",
                            Message = "[WARNING] MySQL process did not start. Check the MySQL error log (.err) for details.",
                            Percentage = 95
                        });
                    }
                }

                // 7b. Start Apache in the mode it was running in (service, or standalone process)
                if (apacheServiceExists && !apacheStandaloneWasRunning)
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

                else if (apacheStandaloneWasRunning)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ServiceStart,
                        StepTitle = $"{modeTag}Starting Apache Process",
                        Message = $"{modeTag}Apache was running as a process. Starting httpd again...",
                        Percentage = 96,
                        IsIndeterminate = true
                    });
                    bool apacheProcStarted = await _serviceManager.StartApacheProcessAsync(apacheDir, progressAdapter);
                    if (!apacheProcStarted)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ServiceStart,
                            StepTitle = $"{modeTag}Starting Apache Process",
                            Message = "[WARNING] Apache process did not start. Check configuration or port conflicts.",
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

        public static string GetAvailableOldDirectoryPath(string currentDirectoryPath, string? timestamp = null)
        {
            string? parent = Path.GetDirectoryName(currentDirectoryPath);
            if (string.IsNullOrEmpty(parent)) parent = Directory.GetCurrentDirectory();

            string dirName = Path.GetFileName(currentDirectoryPath);
            string ts = !string.IsNullOrWhiteSpace(timestamp) ? timestamp : DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string candidate = Path.Combine(parent, $"{dirName}_old_{ts}");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            int index = 1;
            while (true)
            {
                string indexedCandidate = Path.Combine(parent, $"{dirName}_old_{ts}_{index}");
                if (!Directory.Exists(indexedCandidate))
                {
                    return indexedCandidate;
                }
                index++;
            }
        }

        private static async Task EnsureNoLingeringDatabaseProcessesAsync(
            string localMySqlDir,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var targetNames = new[] { "mysqld", "mariadbd", "mysqld-nt" };
            string normalizedLocalDir = Path.GetFullPath(localMySqlDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            foreach (var name in targetNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var proc in procs)
                {
                    using (proc)
                    {
                        bool isOurProcess = false;
                        string procPath = string.Empty;

                        try
                        {
                            procPath = proc.MainModule?.FileName ?? string.Empty;
                            isOurProcess = XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(procPath, localMySqlDir);
                        }
                        catch
                        {
                            // Image path unreadable: ownership cannot be proven, so this process is left alone.
                            isOurProcess = false;
                        }

                        if (isOurProcess)
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ServiceStop,
                                StepTitle = "Stopping Database Process",
                                Message = $"[PROCESS DETECTED] Active database engine '{name}.exe' (PID: {proc.Id}) detected in '{localMySqlDir}'. Closing process to release file locks...",
                                Percentage = 16,
                                IsIndeterminate = true
                            });

                            try
                            {
                                if (!proc.HasExited)
                                {
                                    proc.CloseMainWindow();
                                    await Task.Delay(1000, cancellationToken);
                                }

                                if (!proc.HasExited)
                                {
                                    proc.Kill(true);
                                    await Task.Run(() => proc.WaitForExit(5000), cancellationToken);
                                }

                                progress?.Report(new UpdateProgressReport
                                {
                                    Step = UpdateStep.ServiceStop,
                                    StepTitle = "Process Stopped",
                                    Message = $"[PROCESS TERMINATED] Database engine '{name}.exe' (PID: {proc.Id}) has exited.",
                                    Percentage = 18,
                                    IsIndeterminate = false
                                });
                            }
                            catch (Exception ex)
                            {
                                progress?.Report(new UpdateProgressReport
                                {
                                    Step = UpdateStep.ServiceStop,
                                    StepTitle = "Process Warning",
                                    Message = $"[PROCESS WARN] Could not terminate '{name}.exe' (PID: {proc.Id}): {ex.Message}",
                                    Percentage = 18,
                                    IsIndeterminate = false
                                });
                            }
                        }
                    }
                }
            }

            // Force GC and brief settle delay to allow Windows kernel and filesystem drivers to release handles
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(800, cancellationToken);
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
            // Works for both modes: service if installed, standalone process otherwise (never a second instance).
            {
                if (!_serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Starting MySQL",
                        Message = "[SERVICE] MySQL is not running. Starting it for the upgrade connection...",
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

                    await _serviceManager.EnsureMySqlRunningAsync(mySqlServiceName, settings.MySql.InstallationPath, settings.MySql.IniPath, serviceProgress);
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

        public string? DetectDumpToolPath()
        {
            var settings = _settingsService.CurrentSettings;
            string localMySqlDir = settings.MySql.InstallationPath;
            var candidates = new[]
            {
                Path.Combine(localMySqlDir, "bin", "mariadb-dump.exe"),
                Path.Combine(localMySqlDir, "bin", "mysqldump.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public string? DetectMySqlClientToolPath()
        {
            var settings = _settingsService.CurrentSettings;
            string localMySqlDir = settings.MySql.InstallationPath;
            var candidates = new[]
            {
                Path.Combine(localMySqlDir, "bin", "mariadb.exe"),
                Path.Combine(localMySqlDir, "bin", "mysql.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public string? DetectInstallDbToolPath()
        {
            var settings = _settingsService.CurrentSettings;
            string localMySqlDir = settings.MySql.InstallationPath;
            var candidates = new[]
            {
                Path.Combine(localMySqlDir, "bin", "mariadb-install-db.exe"),
                Path.Combine(localMySqlDir, "bin", "mysql_install_db.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private int DetectMySqlPort()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                string iniPath = settings.MySql.IniPath;
                if (!File.Exists(iniPath))
                {
                    iniPath = Path.Combine(settings.MySql.InstallationPath, "bin", "my.ini");
                }
                if (!File.Exists(iniPath))
                {
                    iniPath = Path.Combine(settings.MySql.InstallationPath, "my.ini");
                }
                if (File.Exists(iniPath))
                {
                    var lines = File.ReadAllLines(iniPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("port", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split('=');
                            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int p) && p > 0)
                            {
                                return p;
                            }
                        }
                    }
                }
            }
            catch { }
            return 3306;
        }

        public async Task<bool> DumpAllDatabasesAsync(
            string targetFilePath,
            string username,
            string password,
            string host = "127.0.0.1",
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string? dumpTool = DetectDumpToolPath();
            if (string.IsNullOrEmpty(dumpTool) || !File.Exists(dumpTool))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Dump Tool Not Found",
                    Message = "[ERROR] mysqldump / mariadb-dump executable not found in MySQL bin folder.",
                    Percentage = 100,
                    IsError = true
                });
                return false;
            }

            var settings = _settingsService.CurrentSettings;
            string serviceName = settings.MySql.ServiceName;
            // Works for both modes: service if installed, standalone process otherwise (never a second instance).
            {
                if (!_serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Starting MySQL",
                        Message = "[SERVICE] MySQL is not running. Starting it to perform the database dump...",
                        Percentage = 10,
                        IsIndeterminate = true
                    });
                    var svcProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Starting MySQL Service",
                            Message = $"[SERVICE] {msg}",
                            Percentage = 15,
                            IsIndeterminate = true
                        });
                    });
                    await _serviceManager.EnsureMySqlRunningAsync(serviceName, settings.MySql.InstallationPath, settings.MySql.IniPath, svcProgress);
                }
            }

            return await Task.Run(async () =>
            {
                try
                {
                    string? dir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    int port = DetectMySqlPort();
                    string user = string.IsNullOrWhiteSpace(username) ? "root" : username.Trim();
                    var argList = new List<string>
                    {
                        $"-u {user}",
                        $"-h {host.Trim()}",
                        $"-P {port}",
                        "--all-databases",
                        "--add-drop-table",
                        "--routines",
                        "--events",
                        "--triggers",
                        "--hex-blob"
                    };

                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        argList.Add($"--password=\"{password.Trim()}\"");
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = dumpTool,
                        Arguments = string.Join(" ", argList),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(dumpTool) ?? string.Empty
                    };

                    string loggedArgs = psi.Arguments;
                    if (!string.IsNullOrEmpty(password))
                    {
                        loggedArgs = loggedArgs.Replace($"\"{password.Trim()}\"", "\"******\"").Replace(password.Trim(), "******");
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Dumping Databases",
                        Message = $"[COMMAND] {Path.GetFileName(dumpTool)} {loggedArgs}",
                        Percentage = 25,
                        IsIndeterminate = true
                    });

                    using var process = new Process { StartInfo = psi };
                    var errBuilder = new StringBuilder();

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            lock (errBuilder)
                            {
                                errBuilder.AppendLine(e.Data);
                            }
                            string sanitized = !string.IsNullOrEmpty(password)
                                ? e.Data.Replace(password.Trim(), "******")
                                : e.Data;
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "Dump Info/Warning",
                                Message = $"[DUMP WARN] {sanitized}",
                                Percentage = 50,
                                IsIndeterminate = true
                            });
                        }
                    };

                    process.Start();
                    process.BeginErrorReadLine();

                    await using (var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                    {
                        await process.StandardOutput.BaseStream.CopyToAsync(fileStream, cancellationToken);
                        await fileStream.FlushAsync(cancellationToken);
                    }

                    while (!process.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(true); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    int exitCode = process.ExitCode;
                    if (exitCode == 0 && File.Exists(targetFilePath))
                    {
                        long size = new FileInfo(targetFilePath).Length;
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Complete,
                            StepTitle = "Database Dump Complete",
                            Message = $"[SUCCESS] All databases successfully dumped to: {targetFilePath} ({ArchiveService.FormatBytes(size)})",
                            Percentage = 100,
                            IsIndeterminate = false
                        });
                        return true;
                    }
                    else
                    {
                        string errText = errBuilder.ToString().Trim();
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Database Dump Failed",
                            Message = $"[ERROR] Dump tool exited with code {exitCode}. {errText}",
                            Percentage = 100,
                            IsError = true,
                            IsIndeterminate = false
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "Database Dump Failed",
                        Message = $"[ERROR] Dump failed: {ex.Message}",
                        Percentage = 100,
                        IsError = true,
                        IsIndeterminate = false
                    });
                    return false;
                }
            }, cancellationToken);
        }

        public async Task<bool> RestoreDatabaseFromSqlFileAsync(
            string sqlFilePath,
            string username,
            string password,
            string host = "127.0.0.1",
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sqlFilePath))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "File Not Found",
                    Message = $"[ERROR] SQL file not found: {sqlFilePath}",
                    Percentage = 100,
                    IsError = true
                });
                return false;
            }

            string? clientTool = DetectMySqlClientToolPath();
            if (string.IsNullOrEmpty(clientTool) || !File.Exists(clientTool))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Client Tool Not Found",
                    Message = "[ERROR] mysql / mariadb client executable not found in MySQL bin folder.",
                    Percentage = 100,
                    IsError = true
                });
                return false;
            }

            var settings = _settingsService.CurrentSettings;
            string serviceName = settings.MySql.ServiceName;
            // Works for both modes: service if installed, standalone process otherwise (never a second instance).
            {
                if (!_serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Starting MySQL",
                        Message = "[SERVICE] MySQL is not running. Starting it to restore the database...",
                        Percentage = 10,
                        IsIndeterminate = true
                    });
                    var svcProgress = new Progress<string>(msg =>
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.ConfigApplication,
                            StepTitle = "Starting MySQL Service",
                            Message = $"[SERVICE] {msg}",
                            Percentage = 15,
                            IsIndeterminate = true
                        });
                    });
                    await _serviceManager.EnsureMySqlRunningAsync(serviceName, settings.MySql.InstallationPath, settings.MySql.IniPath, svcProgress);
                }
            }

            return await Task.Run(async () =>
            {
                try
                {
                    int port = DetectMySqlPort();
                    string user = string.IsNullOrWhiteSpace(username) ? "root" : username.Trim();
                    var argList = new List<string>
                    {
                        $"-u {user}",
                        $"-h {host.Trim()}",
                        $"-P {port}",
                        "--default-character-set=utf8mb4"
                    };

                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        argList.Add($"--password=\"{password.Trim()}\"");
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = clientTool,
                        Arguments = string.Join(" ", argList),
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(clientTool) ?? string.Empty
                    };

                    string loggedArgs = psi.Arguments;
                    if (!string.IsNullOrEmpty(password))
                    {
                        loggedArgs = loggedArgs.Replace($"\"{password.Trim()}\"", "\"******\"").Replace(password.Trim(), "******");
                    }

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Restoring Database",
                        Message = $"[COMMAND] {Path.GetFileName(clientTool)} {loggedArgs} < \"{Path.GetFileName(sqlFilePath)}\"",
                        Percentage = 25,
                        IsIndeterminate = true
                    });

                    using var process = new Process { StartInfo = psi };
                    var errBuilder = new StringBuilder();

                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "Restore Output",
                                Message = e.Data,
                                Percentage = 50,
                                IsIndeterminate = true
                            });
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            lock (errBuilder)
                            {
                                errBuilder.AppendLine(e.Data);
                            }
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "Restore Stderr",
                                Message = $"[RESTORE WARN/ERROR] {e.Data}",
                                Percentage = 60,
                                IsIndeterminate = true
                            });
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await using (var fileStream = new FileStream(sqlFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
                    {
                        await fileStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
                        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
                        process.StandardInput.Close();
                    }

                    while (!process.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(true); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    int exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Complete,
                            StepTitle = "Database Restore Complete",
                            Message = $"[SUCCESS] Successfully restored database from: {Path.GetFileName(sqlFilePath)}",
                            Percentage = 100,
                            IsIndeterminate = false
                        });
                        return true;
                    }
                    else
                    {
                        string errText = errBuilder.ToString().Trim();
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "Database Restore Failed",
                            Message = $"[ERROR] Restore exited with code {exitCode}. {errText}",
                            Percentage = 100,
                            IsError = true,
                            IsIndeterminate = false
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "Database Restore Failed",
                        Message = $"[ERROR] Restore exception: {ex.Message}",
                        Percentage = 100,
                        IsError = true,
                        IsIndeterminate = false
                    });
                    return false;
                }
            }, cancellationToken);
        }

        public async Task<bool> RunMariaDbInstallDbAsync(
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string? installDbTool = DetectInstallDbToolPath();
            if (string.IsNullOrEmpty(installDbTool) || !File.Exists(installDbTool))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.Failed,
                    StepTitle = "Tool Not Found",
                    Message = "[ERROR] mariadb-install-db.exe or mysql_install_db.exe not found in MySQL bin directory.",
                    Percentage = 100,
                    IsError = true
                });
                return false;
            }

            var settings = _settingsService.CurrentSettings;
            string serviceName = settings.MySql.ServiceName;
            // Stop whichever mode is running (service and/or standalone process) before initialising the data dir.
            if (_serviceManager.ServiceExists(serviceName) || _serviceManager.IsMySqlProcessRunning(settings.MySql.InstallationPath))
            {
                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ConfigApplication,
                    StepTitle = "Stopping MySQL",
                    Message = "[SERVICE] Stopping MySQL prior to running mariadb-install-db...",
                    Percentage = 10,
                    IsIndeterminate = true
                });
                await _serviceManager.StopMySqlAnyAsync(serviceName, settings.MySql.InstallationPath);
                await Task.Delay(1000, cancellationToken);
            }

            string dataDir = Path.Combine(settings.MySql.InstallationPath, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installDbTool,
                        Arguments = $"--datadir=\"{dataDir.Replace('\\', '/')}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(installDbTool) ?? string.Empty
                    };

                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.ConfigApplication,
                        StepTitle = "Running mariadb-install-db",
                        Message = $"[COMMAND] {Path.GetFileName(installDbTool)} {psi.Arguments}",
                        Percentage = 25,
                        IsIndeterminate = true
                    });

                    using var process = new Process { StartInfo = psi };
                    var errBuilder = new StringBuilder();

                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "mariadb-install-db Output",
                                Message = e.Data,
                                Percentage = 50,
                                IsIndeterminate = true
                            });
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            lock (errBuilder)
                            {
                                errBuilder.AppendLine(e.Data);
                            }
                            progress?.Report(new UpdateProgressReport
                            {
                                Step = UpdateStep.ConfigApplication,
                                StepTitle = "mariadb-install-db Info/Warning",
                                Message = $"[INSTALL-DB WARN] {e.Data}",
                                Percentage = 60,
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
                            try { process.Kill(true); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    int exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Complete,
                            StepTitle = "mariadb-install-db Complete",
                            Message = "[SUCCESS] mariadb-install-db completed successfully. System tables created/updated.",
                            Percentage = 100,
                            IsIndeterminate = false
                        });
                        return true;
                    }
                    else
                    {
                        string errText = errBuilder.ToString().Trim();
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.Failed,
                            StepTitle = "mariadb-install-db Failed",
                            Message = $"[ERROR] mariadb-install-db exited with code {exitCode}. {errText}",
                            Percentage = 100,
                            IsError = true,
                            IsIndeterminate = false
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.Failed,
                        StepTitle = "mariadb-install-db Failed",
                        Message = $"[ERROR] Exception executing mariadb-install-db: {ex.Message}",
                        Percentage = 100,
                        IsError = true,
                        IsIndeterminate = false
                    });
                    return false;
                }
            }, cancellationToken);
        }

        internal static async Task<long> CopyFileChunkedAsync(
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
