using System;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public interface IWindowsServiceManager
    {
        bool ServiceExists(string serviceName);
        Task<ServiceControllerStatus?> GetServiceStatusAsync(string serviceName);
        Task<bool> StopServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null);
        Task<bool> StartServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null);
        Task<bool> WaitForStatusAsync(string serviceName, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress = null);

        // Process status checks
        bool IsApacheProcessRunning(string? apachePath = null);
        bool IsMySqlProcessRunning(string? mySqlPath = null);
        string? GetRunningMySqlProcessInfo(string? mySqlPath = null);

        // Apache Service & Process management
        Task<bool> InstallApacheServiceAsync(string apachePath, string serviceName, IProgress<string>? progress = null);
        Task<bool> RemoveApacheServiceAsync(string apachePath, string serviceName, IProgress<string>? progress = null);
        Task<bool> StartApacheProcessAsync(string apachePath, IProgress<string>? progress = null);
        Task<bool> StopApacheProcessAsync(string apachePath, IProgress<string>? progress = null);

        // MySQL / MariaDB Service & Process management
        Task<bool> InstallMySqlServiceAsync(string mySqlPath, string serviceName, string? iniPath = null, IProgress<string>? progress = null);
        Task<bool> RemoveMySqlServiceAsync(string mySqlPath, string serviceName, IProgress<string>? progress = null);
        Task<bool> StartMySqlProcessAsync(string mySqlPath, string? iniPath = null, IProgress<string>? progress = null);
        Task<bool> StopMySqlProcessAsync(string mySqlPath, IProgress<string>? progress = null);

        // Mode-aware helpers: act on the Windows service and/or a standalone process, whichever is actually present.
        /// <summary>Stops the MySQL service (if installed and running) and any standalone mysqld/mariadbd. True when nothing is left running.</summary>
        Task<bool> StopMySqlAnyAsync(string serviceName, string mySqlPath, IProgress<string>? progress = null);
        /// <summary>
        /// Upgrade-safe shutdown: sets innodb_fast_shutdown=0 (full flush) while running, then stops the service
        /// and/or process gracefully, never killing. Returns true only when MySQL is fully stopped without a kill.
        /// </summary>
        Task<bool> SafeShutdownMySqlAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null);
        /// <summary>Checks that the given account can log in to the running server and reports whether it looks privileged enough to set variables and shut down.</summary>
        Task<(bool Success, string Message)> TestMySqlLoginAsync(string mySqlPath, string user, string password, int port = 3306);
        /// <summary>Makes sure MySQL is running: no-op if already running, else starts the service if installed, otherwise a standalone process.</summary>
        Task<bool> EnsureMySqlRunningAsync(string serviceName, string mySqlPath, string? iniPath = null, IProgress<string>? progress = null);
        /// <summary>Stops the Apache service (if installed and running) and any standalone httpd. True when nothing is left running.</summary>
        Task<bool> StopApacheAnyAsync(string serviceName, string apachePath, IProgress<string>? progress = null);
    }
}
