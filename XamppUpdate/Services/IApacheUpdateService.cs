using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IApacheUpdateService
    {
        Task<string> PrepareIncomingSourceAsync(string sourceUrlOrPath, IProgress<UpdateProgressReport>? progress = null, CancellationToken cancellationToken = default);
        Task<List<ConfigDiffItem>> InspectConfigurationsAsync(string incomingApacheRoot);
        Task<bool> ExecuteUpdatePipelineAsync(
            string incomingApacheRoot,
            List<ConfigDiffItem> resolvedConfigs,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default);
        void CleanupTempDirectory(string tempDirectoryPath);
    }
}
