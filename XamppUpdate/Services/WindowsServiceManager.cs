using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public class WindowsServiceManager : IWindowsServiceManager
    {
        public bool ServiceExists(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return false;

            try
            {
                // GetServices() returns one handle-owning ServiceController per service: dispose them all
                // (this runs on a 2 s status poll, so leaking them adds up quickly).
                var services = ServiceController.GetServices();
                try
                {
                    return services.Any(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var s in services) s.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        private ServiceController? FindService(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return null;

            try
            {
                var services = ServiceController.GetServices();
                var match = services.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                foreach (var s in services)
                {
                    if (!ReferenceEquals(s, match)) s.Dispose();
                }
                return match;
            }
            catch
            {
                return null;
            }
        }

        public async Task<ServiceControllerStatus?> GetServiceStatusAsync(string serviceName)
        {
            return await Task.Run<ServiceControllerStatus?>(() =>
            {
                using var sc = FindService(serviceName);
                if (sc == null) return null;

                try
                {
                    sc.Refresh();
                    return sc.Status;
                }
                catch
                {
                    return null;
                }
            });
        }

        public async Task<bool> StopServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null)
                {
                    progress?.Report($"Service '{serviceName}' not found. Treating as stopped.");
                    return true;
                }

                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    progress?.Report($"Service '{serviceName}' is already stopped.");
                    return true;
                }

                try
                {
                    progress?.Report($"Sending stop request to service '{sc.ServiceName}'...");
                    if (sc.CanStop)
                    {
                        sc.Stop();
                    }
                    else
                    {
                        progress?.Report($"Warning: Service '{sc.ServiceName}' reports it cannot be stopped directly.");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to request stop for '{serviceName}': {ex.Message}");
                }

                return await WaitForStatusInternalAsync(sc, ServiceControllerStatus.Stopped, timeout, progress);
            });
        }

        public async Task<bool> StartServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null)
                {
                    progress?.Report($"Service '{serviceName}' not found. Cannot start.");
                    return false;
                }

                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    progress?.Report($"Service '{serviceName}' is already running.");
                    return true;
                }

                try
                {
                    progress?.Report($"Sending start request to service '{sc.ServiceName}'...");
                    sc.Start();
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to start service '{serviceName}': {ex.Message}");
                    return false;
                }

                return await WaitForStatusInternalAsync(sc, ServiceControllerStatus.Running, timeout, progress);
            });
        }

        public async Task<bool> WaitForStatusAsync(string serviceName, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null) return false;
                return await WaitForStatusInternalAsync(sc, desiredStatus, timeout, progress);
            });
        }

        private async Task<bool> WaitForStatusInternalAsync(ServiceController sc, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress)
        {
            var startTime = DateTime.UtcNow;
            var delayMs = 500;

            while (DateTime.UtcNow - startTime < timeout)
            {
                try
                {
                    sc.Refresh();
                    if (sc.Status == desiredStatus)
                    {
                        progress?.Report($"Service '{sc.ServiceName}' reached status: {desiredStatus}.");
                        return true;
                    }
                    progress?.Report($"Waiting for '{sc.ServiceName}' (Current: {sc.Status}, Target: {desiredStatus})...");
                }
                catch (Exception ex)
                {
                    progress?.Report($"Error checking service status: {ex.Message}");
                }

                await Task.Delay(delayMs);
            }

            progress?.Report($"Timed out waiting for service '{sc.ServiceName}' to reach status {desiredStatus}.");
            return false;
        }

        // ==========================================
        // PROCESS STATUS CHECKS
        // ==========================================

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static string? GetProcessMainModuleFileName(Process p)
        {
            try
            {
                return p.MainModule?.FileName;
            }
            catch
            {
                try
                {
                    // PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
                    IntPtr hProc = OpenProcess(0x1000, false, p.Id);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(1024);
                            int size = sb.Capacity;
                            if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                            {
                                return sb.ToString();
                            }
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                }
                catch
                {
                    // Ignore inspection errors
                }
                return null;
            }
        }

        public bool IsApacheProcessRunning(string? apachePath = null)
        {
            try
            {
                var procs = Process.GetProcessesByName("httpd");
                if (procs.Length == 0) return false;
                if (string.IsNullOrWhiteSpace(apachePath)) return true;

                foreach (var p in procs)
                {
                    try
                    {
                        string? fn = GetProcessMainModuleFileName(p);
                        if (XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(fn, apachePath))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore process inspection errors, do not falsely return true
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool IsMySqlProcessRunning(string? mySqlPath = null)
            => GetRunningMySqlProcessInfo(mySqlPath) != null;

        /// <summary>
        /// Returns "name (PID n) path" for the first mysqld/mariadbd under <paramref name="mySqlPath"/>,
        /// or null if none. With no path given, any mysqld/mariadbd on the machine matches.
        /// </summary>
        public string? GetRunningMySqlProcessInfo(string? mySqlPath = null)
        {
            try
            {
                var procs = Process.GetProcessesByName("mysqld")
                    .Concat(Process.GetProcessesByName("mariadbd"))
                    .ToArray();

                string? normRoot = string.IsNullOrWhiteSpace(mySqlPath)
                    ? null
                    : Path.GetFullPath(mySqlPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

                string? found = null;
                foreach (var p in procs)
                {
                    try
                    {
                        if (found != null) continue;

                        string? fn = GetProcessMainModuleFileName(p);
                        if (normRoot == null)
                        {
                            found = $"{p.ProcessName} (PID {p.Id}) {fn}".TrimEnd();
                        }
                        else if (!string.IsNullOrEmpty(fn) &&
                                 Path.GetFullPath(fn).StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            // Trailing separator in normRoot: C:\xampp\mysql must not match C:\xampp\mysql_old\...
                            found = $"{p.ProcessName} (PID {p.Id}) {fn}";
                        }
                    }
                    catch
                    {
                        // Ignore process inspection errors, do not falsely report running
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return found;
            }
            catch
            {
                return null;
            }
        }

        // ==========================================
        // APACHE SERVICE & PROCESS MANAGEMENT
        // ==========================================

        public async Task<bool> InstallApacheServiceAsync(string apachePath, string serviceName, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report($"Installing Apache service '{serviceName}'...");
                string httpdExe = Path.Combine(apachePath, "bin", "httpd.exe");
                if (!File.Exists(httpdExe))
                {
                    httpdExe = Path.Combine(apachePath, "httpd.exe");
                }

                if (!File.Exists(httpdExe))
                {
                    progress?.Report($"Error: Apache executable not found at '{httpdExe}'.");
                    return false;
                }

                string binDir = Path.GetDirectoryName(httpdExe) ?? apachePath;
                var (code1, out1) = await RunProcessAsync(httpdExe, $"-k install -n \"{serviceName}\"", binDir);
                if (!string.IsNullOrWhiteSpace(out1)) progress?.Report(out1.Trim());
                await Task.Delay(500);

                if (ServiceExists(serviceName))
                {
                    progress?.Report($"Service '{serviceName}' successfully registered via httpd.");
                    return true;
                }

                progress?.Report($"Attempting fallback registration via sc.exe...");
                string binPath = $"\\\"{httpdExe}\\\" -k runservice";
                var (code2, out2) = await RunProcessAsync("sc.exe", $"create \"{serviceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{serviceName}\"");
                if (!string.IsNullOrWhiteSpace(out2)) progress?.Report(out2.Trim());
                await Task.Delay(500);

                bool success = ServiceExists(serviceName);
                if (success)
                {
                    progress?.Report($"Service '{serviceName}' successfully registered via sc.exe.");
                }
                else
                {
                    progress?.Report($"Failed to register service '{serviceName}'.");
                }
                return success;
            });
        }

        public async Task<bool> RemoveApacheServiceAsync(string apachePath, string serviceName, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report($"Removing Apache service '{serviceName}'...");
                if (ServiceExists(serviceName))
                {
                    await StopServiceAsync(serviceName, TimeSpan.FromSeconds(15), progress);
                }

                string httpdExe = Path.Combine(apachePath, "bin", "httpd.exe");
                if (!File.Exists(httpdExe))
                {
                    httpdExe = Path.Combine(apachePath, "httpd.exe");
                }

                if (File.Exists(httpdExe))
                {
                    string binDir = Path.GetDirectoryName(httpdExe) ?? apachePath;
                    var (code1, out1) = await RunProcessAsync(httpdExe, $"-k uninstall -n \"{serviceName}\"", binDir);
                    if (!string.IsNullOrWhiteSpace(out1)) progress?.Report(out1.Trim());
                    await Task.Delay(500);
                }

                if (ServiceExists(serviceName))
                {
                    progress?.Report($"Attempting fallback removal via sc.exe delete...");
                    var (code2, out2) = await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
                    if (!string.IsNullOrWhiteSpace(out2)) progress?.Report(out2.Trim());
                    await Task.Delay(500);
                }

                bool removed = !ServiceExists(serviceName);
                if (removed)
                {
                    progress?.Report($"Service '{serviceName}' removed successfully.");
                }
                else
                {
                    progress?.Report($"Warning: Service '{serviceName}' may still be marked for deletion.");
                }
                return removed;
            });
        }

        public async Task<bool> StartApacheProcessAsync(string apachePath, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                if (IsApacheProcessRunning(apachePath))
                {
                    progress?.Report("Apache process is already running.");
                    return true;
                }

                string httpdExe = Path.Combine(apachePath, "bin", "httpd.exe");
                if (!File.Exists(httpdExe))
                {
                    httpdExe = Path.Combine(apachePath, "httpd.exe");
                }

                if (!File.Exists(httpdExe))
                {
                    progress?.Report($"Error: Apache executable not found at '{httpdExe}'.");
                    return false;
                }

                progress?.Report($"Starting Apache in background: {httpdExe}...");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = httpdExe,
                        WorkingDirectory = Path.GetDirectoryName(httpdExe) ?? apachePath,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                    await Task.Delay(1000);

                    bool running = IsApacheProcessRunning(apachePath);
                    if (running)
                    {
                        progress?.Report("Apache background process started successfully.");
                        return true;
                    }
                    else
                    {
                        progress?.Report("Apache process exited shortly after launch. Check configuration or port conflicts.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to launch Apache: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> StopApacheProcessAsync(string apachePath, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report("Stopping Apache background process...");

                string httpdExe = Path.Combine(apachePath, "bin", "httpd.exe");
                if (!File.Exists(httpdExe))
                {
                    httpdExe = Path.Combine(apachePath, "httpd.exe");
                }

                if (File.Exists(httpdExe))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = httpdExe,
                            Arguments = "-k stop",
                            WorkingDirectory = Path.GetDirectoryName(httpdExe) ?? apachePath,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(3000);
                    }
                    catch { }
                }

                await Task.Delay(500);

                TerminateProcessesByName("httpd", apachePath, progress);

                await Task.Delay(500);
                bool stopped = !IsApacheProcessRunning(apachePath);
                if (stopped)
                {
                    progress?.Report("Apache process stopped.");
                }
                else
                {
                    progress?.Report("Warning: Could not confirm all Apache processes have stopped.");
                }
                return stopped;
            });
        }

        // ==========================================
        // MYSQL / MARIADB SERVICE & PROCESS MANAGEMENT
        // ==========================================

        public async Task<bool> InstallMySqlServiceAsync(string mySqlPath, string serviceName, string? iniPath = null, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report($"Installing MySQL service '{serviceName}'...");
                string? daemonExe = FindMySqlDaemon(mySqlPath);
                if (string.IsNullOrEmpty(daemonExe))
                {
                    progress?.Report($"Error: Neither mysqld.exe nor mariadbd.exe found in '{mySqlPath}'.");
                    return false;
                }

                string resolvedIni = ResolveMySqlIni(mySqlPath, iniPath);
                string args = !string.IsNullOrEmpty(resolvedIni)
                    ? $"--install \"{serviceName}\" --defaults-file=\"{resolvedIni}\""
                    : $"--install \"{serviceName}\"";

                progress?.Report($"Executing: {daemonExe} {args}");
                var (code1, out1) = await RunProcessAsync(daemonExe, args, mySqlPath);
                if (!string.IsNullOrWhiteSpace(out1)) progress?.Report(out1.Trim());
                await Task.Delay(500);

                if (ServiceExists(serviceName))
                {
                    progress?.Report($"Service '{serviceName}' successfully registered via MySQL daemon.");
                    return true;
                }

                progress?.Report($"Attempting fallback registration via sc.exe...");
                string binPath = !string.IsNullOrEmpty(resolvedIni)
                    ? $"\\\"{daemonExe}\\\" --defaults-file=\\\"{resolvedIni}\\\" {serviceName}"
                    : $"\\\"{daemonExe}\\\" {serviceName}";

                var (code2, out2) = await RunProcessAsync("sc.exe", $"create \"{serviceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{serviceName}\"");
                if (!string.IsNullOrWhiteSpace(out2)) progress?.Report(out2.Trim());
                await Task.Delay(500);

                bool success = ServiceExists(serviceName);
                if (success)
                {
                    progress?.Report($"Service '{serviceName}' successfully registered via sc.exe.");
                }
                else
                {
                    progress?.Report($"Failed to register service '{serviceName}'.");
                }
                return success;
            });
        }

        public async Task<bool> RemoveMySqlServiceAsync(string mySqlPath, string serviceName, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report($"Removing MySQL service '{serviceName}'...");
                if (ServiceExists(serviceName))
                {
                    await StopServiceAsync(serviceName, TimeSpan.FromSeconds(15), progress);
                }

                string? daemonExe = FindMySqlDaemon(mySqlPath);
                if (!string.IsNullOrEmpty(daemonExe))
                {
                    var (code1, out1) = await RunProcessAsync(daemonExe, $"--remove \"{serviceName}\"", mySqlPath);
                    if (!string.IsNullOrWhiteSpace(out1)) progress?.Report(out1.Trim());
                    await Task.Delay(500);
                }

                if (ServiceExists(serviceName))
                {
                    progress?.Report($"Attempting fallback removal via sc.exe delete...");
                    var (code2, out2) = await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
                    if (!string.IsNullOrWhiteSpace(out2)) progress?.Report(out2.Trim());
                    await Task.Delay(500);
                }

                bool removed = !ServiceExists(serviceName);
                if (removed)
                {
                    progress?.Report($"Service '{serviceName}' removed successfully.");
                }
                else
                {
                    progress?.Report($"Warning: Service '{serviceName}' may still be marked for deletion.");
                }
                return removed;
            });
        }

        public async Task<bool> StartMySqlProcessAsync(string mySqlPath, string? iniPath = null, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                if (IsMySqlProcessRunning(mySqlPath))
                {
                    progress?.Report("MySQL / MariaDB process is already running.");
                    return true;
                }

                string? daemonExe = FindMySqlDaemon(mySqlPath);
                if (string.IsNullOrEmpty(daemonExe))
                {
                    progress?.Report($"Error: MySQL / MariaDB daemon executable not found in '{mySqlPath}'.");
                    return false;
                }

                string resolvedIni = ResolveMySqlIni(mySqlPath, iniPath);
                string args = !string.IsNullOrEmpty(resolvedIni)
                    ? $"--defaults-file=\"{resolvedIni}\" --standalone"
                    : "--standalone";

                progress?.Report($"Starting MySQL in background: {daemonExe} {args}...");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = daemonExe,
                        Arguments = args,
                        WorkingDirectory = mySqlPath,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                    await Task.Delay(1500);

                    bool running = IsMySqlProcessRunning(mySqlPath);
                    if (running)
                    {
                        progress?.Report("MySQL background process started successfully.");
                        return true;
                    }
                    else
                    {
                        progress?.Report("MySQL process exited shortly after launch. Check error log (.err) for details.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to launch MySQL process: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> StopMySqlProcessAsync(string mySqlPath, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                progress?.Report("Stopping MySQL / MariaDB background process...");

                string mysqladmin = Path.Combine(mySqlPath, "bin", "mysqladmin.exe");
                if (File.Exists(mysqladmin))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = mysqladmin,
                            Arguments = "-u root shutdown",
                            WorkingDirectory = Path.Combine(mySqlPath, "bin"),
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(4000);
                    }
                    catch { }
                }

                await Task.Delay(800);

                TerminateProcessesByName("mysqld", mySqlPath, progress);
                TerminateProcessesByName("mariadbd", mySqlPath, progress);

                await Task.Delay(500);
                bool stopped = !IsMySqlProcessRunning(mySqlPath);
                if (stopped)
                {
                    progress?.Report("MySQL process stopped.");
                }
                else
                {
                    progress?.Report("Warning: Could not confirm MySQL process has stopped.");
                }
                return stopped;
            });
        }

        // ==========================================
        // MODE-AWARE HELPERS (service and/or standalone process)
        // ==========================================

        public async Task<bool> StopMySqlAnyAsync(string serviceName, string mySqlPath, IProgress<string>? progress = null)
        {
            bool ok = true;

            if (ServiceExists(serviceName))
            {
                ok &= await StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
            }

            // Anything left (standalone process, or a process the service did not take down)
            if (IsMySqlProcessRunning(mySqlPath))
            {
                ok &= await StopMySqlProcessAsync(mySqlPath, progress);
            }

            return ok && !IsMySqlProcessRunning(mySqlPath);
        }

        public async Task<bool> SafeShutdownMySqlAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null)
        {
            var report = await SafeShutdownMySqlDetailedAsync(serviceName, mySqlPath, user, password, port, progress);
            // Production semantics unchanged: true when nothing was aborted and no process remains.
            return !report.Aborted && (!report.WasRunning || report.ProcessExited);
        }

        public async Task<MySqlShutdownReport> SafeShutdownMySqlDetailedAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null)
        {
            void Log(string phase, string message) => progress?.Report($"[{phase}] {message}");
            const string pLogin = "SHUTDOWN 1/4 LOGIN";
            const string pFlush = "SHUTDOWN 2/4 FLUSH";
            const string pStop = "SHUTDOWN 3/4 STOP";
            const string pVerify = "SHUTDOWN 4/4 VERIFY";

            bool serviceExists = ServiceExists(serviceName);
            if (!serviceExists && !IsMySqlProcessRunning(mySqlPath))
            {
                Log(pStop, "MySQL is not running. Nothing to shut down. OK");
                return new MySqlShutdownReport { WasRunning = false };
            }

            string effectiveUser = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();
            string? client = FindMySqlTool(mySqlPath, "mariadb.exe", "mysql.exe");
            string? admin = FindMySqlTool(mySqlPath, "mariadb-admin.exe", "mysqladmin.exe");

            bool running = IsMySqlProcessRunning(mySqlPath);
            bool loggedIn = false;
            string flushState = "SKIPPED";
            var logMark = XamppUpdate.Services.Uat.ErrorLogEvidence.Capture(mySqlPath);
            var logSizes = XamppUpdate.Services.Uat.ErrorLogEvidence.SnapshotAll(mySqlPath);
            if (!string.IsNullOrEmpty(logMark.Path))
            {
                Log(pLogin, $"Detected active error log: {Path.GetFileName(logMark.Path)} (initial length: {logMark.Length})");
            }
            string? originalFlush = null;
            bool flushChanged = false;
            bool stopRequested = false;
            bool aborted = false;

            // ---- 1/4 LOGIN (also reads the current innodb_fast_shutdown value) ----
            if (running)
            {
                if (client == null)
                {
                    Log(pLogin, "FAILED: mysql / mariadb client not found in bin\\. Cannot flush; continuing with a plain graceful stop.");
                }
                else
                {
                    string cmd = $"{Path.GetFileName(client)} --no-defaults -u {effectiveUser} -h 127.0.0.1 -P {port} -N -B -e \"SELECT @@GLOBAL.innodb_fast_shutdown\"";
                    Log(pLogin, $"Running: {cmd}");
                    var (code, output) = await RunToolWithPasswordAsync(
                        client,
                        $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -N -B -e \"SELECT @@GLOBAL.innodb_fast_shutdown\"",
                        password, Path.GetDirectoryName(client));
                    loggedIn = code == 0;
                    if (loggedIn)
                    {
                        string scalar = ExtractScalar(output);
                        if (scalar.Length > 0 && scalar.All(char.IsDigit)) originalFlush = scalar;
                    }
                    Log(pLogin, loggedIn
                        ? $"OK (exit 0). Current innodb_fast_shutdown = {ExtractScalar(output)}"
                        : $"FAILED (exit {code}): {output.Trim()}");
                }
            }
            else
            {
                Log(pLogin, "Process not running (service only). A login is not needed to stop a service. SKIPPED");
            }

            // ---- 2/4 FLUSH: SET GLOBAL innodb_fast_shutdown=0 and read it back ----
            if (loggedIn && client != null)
            {
                string setCmd = $"{Path.GetFileName(client)} --no-defaults -u {effectiveUser} -h 127.0.0.1 -P {port} -e \"SET GLOBAL innodb_fast_shutdown=0\"";
                Log(pFlush, $"Running: {setCmd}");
                var (setCode, setOut) = await RunToolWithPasswordAsync(
                    client,
                    $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -e \"SET GLOBAL innodb_fast_shutdown=0\"",
                    password, Path.GetDirectoryName(client));

                string readBack = string.Empty;
                if (setCode == 0)
                {
                    var (getCode, getOut) = await RunToolWithPasswordAsync(
                        client,
                        $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -N -B -e \"SELECT @@GLOBAL.innodb_fast_shutdown\"",
                        password, Path.GetDirectoryName(client));
                    // stdout and stderr are merged: client warnings (e.g. the passwordless ssl-verify notice) must not
                    // be mistaken for the value, so take the numeric line only.
                    readBack = getCode == 0 ? ExtractScalar(getOut) : $"(read-back failed: {getOut.Trim()})";
                }

                if (setCode == 0 && readBack == "0")
                {
                    flushState = "OK";
                    flushChanged = true;
                    Log(pFlush, "OK (exit 0). Read-back innodb_fast_shutdown = 0: InnoDB will fully flush on shutdown.");
                }
                else
                {
                    // Logged in but the command did not take effect: stop here, MySQL is still running untouched.
                    Log(pFlush, $"FAILED (exit {setCode}, read-back \"{readBack}\"): {setOut.Trim()}. MySQL was NOT stopped.");
                    aborted = true;
                    // SET may have succeeded even though the read-back did not confirm it: put the original value back.
                    if (setCode == 0 && !string.IsNullOrEmpty(originalFlush))
                    {
                        var (rCode, rOut) = await RestoreFlushAsync(client, effectiveUser, port, password, originalFlush);
                        Log(pFlush, rCode == 0
                            ? $"OK: restored innodb_fast_shutdown={originalFlush}."
                            : $"FAILED (exit {rCode}): could not restore innodb_fast_shutdown={originalFlush}: {rOut.Trim()}");
                    }
                    return new MySqlShutdownReport { WasRunning = running, LoggedIn = loggedIn, FlushVerified = false, Aborted = true };
                }
            }
            else
            {
                Log(pFlush, running
                    ? "SKIPPED: no admin login. The shutdown below is still clean, only without the extra full flush."
                    : "SKIPPED: not applicable.");
            }

            // ---- 3/4 STOP: service via SCM, process via mysqladmin; never kill ----
            bool stopFailed = false;
            if (serviceExists)
            {
                Log(pStop, $"Running: stop service '{serviceName}' (graceful, up to 120s)");
                bool svcStopped = await StopServiceAsync(serviceName, TimeSpan.FromSeconds(120), progress == null ? null : new Progress<string>(m => Log(pStop, m)));
                Log(pStop, svcStopped ? $"OK: service '{serviceName}' stopped." : $"FAILED: service '{serviceName}' did not reach Stopped.");
                if (svcStopped) stopRequested = true;
                stopFailed |= !svcStopped;
            }

            if (IsMySqlProcessRunning(mySqlPath))
            {
                bool shutdownRequested = false;
                if (admin != null)
                {
                    Log(pStop, $"Running: {Path.GetFileName(admin)} --no-defaults -u {effectiveUser} -h 127.0.0.1 -P {port} shutdown");
                    var (code, output) = await RunToolWithPasswordAsync(
                        admin,
                        $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 shutdown",
                        password, Path.GetDirectoryName(admin));
                    shutdownRequested = code == 0;
                    if (shutdownRequested) stopRequested = true;
                    Log(pStop, shutdownRequested
                        ? "OK: shutdown requested (exit 0). Waiting for the process to exit..."
                        : $"FAILED: shutdown request refused (exit {code}): {output.Trim()}. If root has a password, enter it and retry.");
                }
                else
                {
                    Log(pStop, "FAILED: mysqladmin / mariadb-admin not found; cannot request a graceful shutdown.");
                }

                // Long window only if the server was actually told to shut down.
                var waitLimit = shutdownRequested ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(3);
                var waitStart = DateTime.UtcNow;
                while (IsMySqlProcessRunning(mySqlPath) && DateTime.UtcNow - waitStart < waitLimit)
                {
                    await Task.Delay(500);
                }
            }

            if (IsMySqlProcessRunning(mySqlPath))
            {
                Log(pStop, "FAILED: MySQL is still running after the graceful shutdown timeout. Nothing was killed.");
                // Production stayed up: put the runtime flush setting back the way we found it.
                if (flushChanged && client != null && !string.IsNullOrEmpty(originalFlush))
                {
                    var (rCode, rOut) = await RestoreFlushAsync(client, effectiveUser, port, password, originalFlush);
                    Log(pStop, rCode == 0
                        ? $"OK: restored innodb_fast_shutdown={originalFlush}."
                        : $"FAILED (exit {rCode}): could not restore innodb_fast_shutdown={originalFlush}: {rOut.Trim()}");
                }
                return new MySqlShutdownReport { WasRunning = true, LoggedIn = loggedIn, FlushVerified = flushState == "OK", StopRequested = stopRequested };
            }
            Log(pStop, stopFailed
                ? "OK: no mysqld process remains (the service reported a stop issue, see above)."
                : "OK: no MySQL process remains.");

            // ---- 4/4 VERIFY: the server's own log should say the shutdown completed ----
            var (fresh, logDetail) = await XamppUpdate.Services.Uat.ErrorLogEvidence.CheckGrownLogsAsync(logSizes, mySqlPath);
            Log(pVerify, fresh
                ? $"OK: error log reports shutdown completion ({logDetail})."
                : $"NOT SEEN: no shutdown completion line in the error log during this shutdown ({logDetail}). The copy's restart is checked for crash recovery instead. Active log: '{logMark.Path ?? "none"}', initial offset: {logMark.Length}.");

            Log("SHUTDOWN SUMMARY", $"login={(loggedIn ? "OK" : "-")}  flush(innodb_fast_shutdown=0)={flushState}  stop=OK  log={(fresh ? "OK" : "not seen")}");
            return new MySqlShutdownReport
            {
                WasRunning = true, LoggedIn = loggedIn, FlushVerified = flushState == "OK",
                StopRequested = stopRequested, ProcessExited = true, FreshShutdownEvidence = fresh, Aborted = aborted
            };
        }

        public async Task<(bool Success, string Message)> TestMySqlLoginAsync(string mySqlPath, string user, string password, int port = 3306)
        {
            string effectiveUser = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();

            if (!IsMySqlProcessRunning(mySqlPath))
            {
                return (false, "MySQL is not running, so the login cannot be tested. (Not needed: nothing has to be shut down.)");
            }

            string? client = FindMySqlTool(mySqlPath, "mariadb.exe", "mysql.exe");
            if (client == null)
            {
                return (false, "mysql / mariadb client not found in the MySQL bin folder.");
            }

            var (code, output) = await RunToolWithPasswordAsync(
                client,
                $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -N -B -e \"SHOW GRANTS FOR CURRENT_USER();\"",
                password,
                Path.GetDirectoryName(client));

            if (code != 0)
            {
                return (false, $"Login failed for '{effectiveUser}' on 127.0.0.1:{port}: {output}");
            }

            // Needs SUPER/SYSTEM_VARIABLES_ADMIN to SET GLOBAL and SHUTDOWN to stop; ALL PRIVILEGES covers both.
            bool canSetVariables = output.Contains("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase) ||
                                   output.Contains("SUPER", StringComparison.OrdinalIgnoreCase) ||
                                   output.Contains("SYSTEM_VARIABLES_ADMIN", StringComparison.OrdinalIgnoreCase);
            bool canShutdown = output.Contains("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase) ||
                               output.Contains("SHUTDOWN", StringComparison.OrdinalIgnoreCase);

            if (canSetVariables && canShutdown)
            {
                return (true, $"Login OK as '{effectiveUser}' (port {port}). The account can set variables and shut the server down.");
            }

            return (true, $"Login OK as '{effectiveUser}' (port {port}), but the account may lack " +
                          $"{(canSetVariables ? string.Empty : "SUPER ")}{(canShutdown ? string.Empty : "SHUTDOWN ")}privileges. " +
                          "Use root or another admin account for a clean shutdown.");
        }

        private static Task<(int ExitCode, string Output)> RestoreFlushAsync(string client, string effectiveUser, int port, string password, string originalFlush)
        {
            return RunToolWithPasswordAsync(client,
                $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -e \"SET GLOBAL innodb_fast_shutdown={originalFlush}\"",
                password, Path.GetDirectoryName(client));
        }

        /// <summary>Last line made only of digits (the scalar from `-N -B -e "SELECT ..."`), ignoring warning lines.</summary>
        private static string ExtractScalar(string output)
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length > 0 && l.All(char.IsDigit)) return l;
            }
            return output.Trim();
        }

        private static bool VerifyShutdownInErrorLog(string mySqlPath)
        {
            try
            {
                string dataDir = Path.Combine(mySqlPath, "data");
                if (!Directory.Exists(dataDir)) return false;

                var logFile = new DirectoryInfo(dataDir)
                    .EnumerateFiles("*.err")
                    .Concat(new DirectoryInfo(dataDir).EnumerateFiles("mysql_error.log"))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (logFile == null) return false;

                using var fs = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var tail = new System.Collections.Generic.Queue<string>();
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    tail.Enqueue(line);
                    if (tail.Count > 80) tail.Dequeue();
                }
                return tail.Any(l => l.Contains("Shutdown complete", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string? FindMySqlTool(string mySqlPath, params string[] names)
        {
            foreach (var name in names)
            {
                string p = Path.Combine(mySqlPath, "bin", name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>Runs a client tool passing the password via MYSQL_PWD so it never shows up in a process listing.</summary>
        private static async Task<(int ExitCode, string Output)> RunToolWithPasswordAsync(string exe, string args, string password, string? workingDir)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        WorkingDirectory = workingDir ?? string.Empty,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    if (!string.IsNullOrEmpty(password))
                    {
                        psi.Environment["MYSQL_PWD"] = password;
                    }

                    using var proc = Process.Start(psi);
                    if (proc == null) return (-1, "Failed to start process");
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(30000))
                    {
                        try { proc.Kill(); } catch { }
                        return (-1, "Timed out");
                    }
                    return (proc.ExitCode, (stdout + Environment.NewLine + stderr).Trim());
                }
                catch (Exception ex)
                {
                    return (-1, ex.Message);
                }
            });
        }

        public async Task<bool> EnsureMySqlRunningAsync(string serviceName, string mySqlPath, string? iniPath = null, IProgress<string>? progress = null)
        {
            // Already up (as service-spawned or standalone): never start a second instance on the same port/datadir.
            if (IsMySqlProcessRunning(mySqlPath))
            {
                return true;
            }

            if (ServiceExists(serviceName))
            {
                var status = await GetServiceStatusAsync(serviceName);
                if (status == ServiceControllerStatus.StartPending || status == ServiceControllerStatus.ContinuePending)
                {
                    return await WaitForStatusAsync(serviceName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(30), progress);
                }

                return await StartServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
            }

            return await StartMySqlProcessAsync(mySqlPath, iniPath, progress);
        }

        public async Task<bool> StopApacheAnyAsync(string serviceName, string apachePath, IProgress<string>? progress = null)
        {
            bool ok = true;

            if (ServiceExists(serviceName))
            {
                ok &= await StopServiceAsync(serviceName, TimeSpan.FromSeconds(30), progress);
            }

            if (IsApacheProcessRunning(apachePath))
            {
                ok &= await StopApacheProcessAsync(apachePath, progress);
            }

            return ok && !IsApacheProcessRunning(apachePath);
        }

        public string? GetServiceImagePath(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("ImagePath") as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True/false when the registry answer is reliable; null when it could not be read.</summary>
        private static bool? ServiceRegistryKeyExists(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key != null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> InstallOwnedMySqlServiceManualAsync(string mySqlPath, string serviceName, string iniPath, IProgress<string>? progress = null)
        {
            // Establish reliably that the name is unused: both SCM enumeration and the registry must say so.
            bool? registryExists = ServiceRegistryKeyExists(serviceName);
            if (registryExists == null)
            {
                progress?.Report($"FAILED: could not verify that service '{serviceName}' is unused (registry unreadable); nothing was installed.");
                return false;
            }
            if (registryExists == true || ServiceExists(serviceName))
            {
                progress?.Report($"FAILED: a service named '{serviceName}' already exists; it will not be reused or replaced.");
                return false;
            }
            bool? existedBefore = false;

            string? daemon = FindMySqlDaemon(mySqlPath);
            if (daemon == null)
            {
                progress?.Report($"FAILED: mysqld/mariadbd not found under '{mySqlPath}'.");
                return false;
            }

            progress?.Report($"Running: {Path.GetFileName(daemon)} --install-manual {serviceName} --defaults-file=<copy ini>");
            var (code, output) = await RunProcessAsync(daemon, $"--install-manual \"{serviceName}\" --defaults-file=\"{iniPath}\"", mySqlPath);
            await Task.Delay(500);

            string? image = GetServiceImagePath(serviceName);
            bool owned = image != null &&
                         XamppUpdate.Services.Uat.PathSafety.IsSameOrUnder(XamppUpdate.Services.Uat.ServiceImagePath.ExtractExecutable(image), mySqlPath);
            if (owned)
            {
                progress?.Report($"OK: service '{serviceName}' registered for manual start.");
                return true;
            }

            progress?.Report($"FAILED: service registration did not produce an executable under the copy (exit {code}: {output.Trim()}).");
            bool existsNow = ServiceRegistryKeyExists(serviceName) == true || ServiceExists(serviceName);
            if (!existsNow)
            {
                return false;
            }

            if (image == null)
            {
                progress?.Report($"WARNING: service '{serviceName}' is half-registered (its ImagePath could not be read).");
            }

            if (XamppUpdate.Services.Uat.ServiceInstallGuard.MayDeleteAfterFailedInstall(existedBefore, existsNow))
            {
                var (delCode, delOut) = await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
                await Task.Delay(500);
                bool gone = ServiceRegistryKeyExists(serviceName) != true && !ServiceExists(serviceName);
                progress?.Report(gone
                    ? $"OK: half-registered service '{serviceName}' removed."
                    : $"FAILED to remove half-registered service '{serviceName}' (exit {delCode}: {delOut.Trim()}). Run: sc.exe delete \"{serviceName}\"");
            }
            else
            {
                progress?.Report($"Leftover service '{serviceName}' needs manual cleanup: sc.exe delete \"{serviceName}\"");
            }
            return false;
        }

        public async Task<bool> RemoveServiceIfOwnedAsync(string serviceName, string expectedRoot, IProgress<string>? progress = null)
        {
            if (!ServiceExists(serviceName)) return true;

            string? image = GetServiceImagePath(serviceName);
            if (image == null || !XamppUpdate.Services.Uat.PathSafety.IsSameOrUnder(XamppUpdate.Services.Uat.ServiceImagePath.ExtractExecutable(image), expectedRoot))
            {
                progress?.Report($"refused: not registered to {expectedRoot}; if you are certain it is a leftover, remove it manually with: sc.exe delete \"{serviceName}\"");
                return false;
            }

            bool stopped = await StopServiceAsync(serviceName, TimeSpan.FromSeconds(60), progress);
            if (!stopped)
            {
                progress?.Report($"WARNING: service '{serviceName}' did not report a clean stop before removal.");
            }
            var (code, output) = await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
            await Task.Delay(500);

            bool removed = !ServiceExists(serviceName);
            progress?.Report(removed
                ? $"OK: service '{serviceName}' removed."
                : $"FAILED to remove '{serviceName}'{(stopped ? string.Empty : " (stop failed)")} (exit {code}: {output.Trim()}). Run: sc.exe delete \"{serviceName}\"");
            return removed;
        }

        // ==========================================
        // PRIVATE HELPERS
        // ==========================================

        private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, string? workingDir = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = workingDir ?? string.Empty,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return (-1, "Failed to start process");
                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                    return (proc.ExitCode, output + Environment.NewLine + error);
                }
                catch (Exception ex)
                {
                    return (-1, ex.Message);
                }
            });
        }

        private static string? FindMySqlDaemon(string mySqlPath)
        {
            var candidates = new[]
            {
                Path.Combine(mySqlPath, "bin", "mysqld.exe"),
                Path.Combine(mySqlPath, "bin", "mariadbd.exe"),
                Path.Combine(mySqlPath, "mysqld.exe"),
                Path.Combine(mySqlPath, "mariadbd.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string ResolveMySqlIni(string mySqlPath, string? candidateIni)
        {
            if (!string.IsNullOrEmpty(candidateIni) && File.Exists(candidateIni))
                return candidateIni;

            var candidates = new[]
            {
                Path.Combine(mySqlPath, "bin", "my.ini"),
                Path.Combine(mySqlPath, "my.ini")
            };
            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static void TerminateProcessesByName(string processName, string targetPath, IProgress<string>? progress)
        {
            try
            {
                var procs = Process.GetProcessesByName(processName);
                string normTarget = !string.IsNullOrEmpty(targetPath) ? Path.GetFullPath(targetPath).TrimEnd('\\', '/') : string.Empty;

                foreach (var proc in procs)
                {
                    try
                    {
                        bool shouldKill = false;
                        if (string.IsNullOrEmpty(normTarget))
                        {
                            shouldKill = true;
                        }
                        else
                        {
                            // Only kill processes verifiably under our install path. If the image path can't be
                            // read, leave it alone rather than risk killing another install's httpd/mysqld.
                            string? fn = GetProcessMainModuleFileName(proc);
                            if (XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(fn, targetPath))
                            {
                                shouldKill = true;
                            }
                        }

                        if (shouldKill && !proc.HasExited)
                        {
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"Could not terminate process {processName} (PID: {proc.Id}): {ex.Message}");
                    }
                }
            }
            catch { }
        }
    }
}
