using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IMySqlUpdateService
    {
        Task<string> PrepareIncomingSourceAsync(
            string sourceUrlOrPath,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingMySqlRoot);

        Task<bool> ExecuteUpdatePipelineAsync(
            string incomingMySqlRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            bool skipBackup = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>Admin credentials used by the pre-upgrade safe shutdown. Empty password = default XAMPP root.</summary>
        void SetShutdownCredentials(string user, string password);

        /// <summary>
        /// When MySQL is stopped, start the CURRENT (old) version once, so InnoDB can finish crash recovery and shut down
        /// cleanly before the binaries are replaced. Default true.
        /// </summary>
        bool StartForCleanShutdown { get; set; }

        /// <summary>Tests the credentials set by <see cref="SetShutdownCredentials"/> against the running server.</summary>
        Task<(bool Success, string Message)> TestShutdownLoginAsync();

        string DetectMySqlUpgradeToolPath();

        Task<bool> RunMySqlUpgradeAsync(
            string? scriptOrExecutablePath,
            string username,
            string password,
            string additionalArguments,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<bool> DumpAllDatabasesAsync(
            string targetFilePath,
            string username,
            string password,
            string host = "127.0.0.1",
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<bool> RestoreDatabaseFromSqlFileAsync(
            string sqlFilePath,
            string username,
            string password,
            string host = "127.0.0.1",
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<bool> RunMariaDbInstallDbAsync(
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        void CleanupTempDirectory(string tempDirectoryPath);
    }
}
