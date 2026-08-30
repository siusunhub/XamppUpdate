using System;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public interface IComposerService
    {
        Task<string> DetectVersionAsync(string composerExecutable);
        Task<bool> RunSelfUpdateAsync(string composerExecutable, IProgress<string> progress, CancellationToken cancellationToken = default);
        Task<bool> RunCheckOutdatedAsync(string composerExecutable, string workingDirectory, IProgress<string> progress, CancellationToken cancellationToken = default);
        Task<bool> RunUpdatePackagesAsync(string composerExecutable, string workingDirectory, string? backupDestinationDirectory, bool isTestMode, IProgress<string> progress, CancellationToken cancellationToken = default);
        Task<string?> BackupVendorFolderAsync(string workingDirectory, string? destinationBackupDirectory, IProgress<string> progress, CancellationToken cancellationToken = default);
    }
}
