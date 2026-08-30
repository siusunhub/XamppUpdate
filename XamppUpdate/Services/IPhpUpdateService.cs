using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IPhpUpdateService
    {
        Task<string> PrepareIncomingSourceAsync(
            string sourceUrlOrPath,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingPhpRoot);

        Task<bool> ExecuteUpdatePipelineAsync(
            string incomingPhpRoot,
            List<ConfigDiffItem> resolvedConfigs,
            bool isTestMode = false,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);

        void CleanupTempDirectory(string tempDirectoryPath);
    }
}
