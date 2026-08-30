using System;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public interface IArchiveService
    {
        Task DownloadFileAsync(string url, string destinationFilePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
        Task ExtractArchiveAsync(string archiveFilePath, string destinationDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
        Task CreateZipBackupAsync(string sourceDirectory, string destinationZipFilePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
        string FindApacheRoot(string extractedDirectory);
        string FindPhpRoot(string extractedDirectory);
    }
}
