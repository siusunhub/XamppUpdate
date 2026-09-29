using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IPhpMyAdminUpdateService
    {
        Task<string> PrepareIncomingSourceAsync(
            string sourceUrlOrPath,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingPhpMyAdminRoot);

        Task<bool> ExecuteUpdatePipelineAsync(
            string incomingPhpMyAdminRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            bool skipBackup = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        void CleanupTempDirectory(string tempDirectoryPath);
    }
}
