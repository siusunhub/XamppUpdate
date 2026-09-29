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

        string DetectMySqlUpgradeToolPath();

        Task<bool> RunMySqlUpgradeAsync(
            string? scriptOrExecutablePath,
            string username,
            string password,
            string additionalArguments,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        void CleanupTempDirectory(string tempDirectoryPath);
    }
}
