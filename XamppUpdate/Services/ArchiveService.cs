using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace XamppUpdate.Services
{
    public class ArchiveService : IArchiveService
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        public async Task DownloadFileAsync(string url, string destinationFilePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            string? dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    double percentage = (double)totalRead / totalBytes * 100.0;
                    progress.Report(percentage);
                }
            }
        }

        public async Task ExtractArchiveAsync(string archiveFilePath, string destinationDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(archiveFilePath))
            {
                throw new FileNotFoundException($"Archive file not found: {archiveFilePath}");
            }

            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await Task.Run(() =>
            {
                progress?.Report($"Opening archive {Path.GetFileName(archiveFilePath)}...");

                using var archive = ArchiveFactory.Open(archiveFilePath);
                int totalEntries = 0;
                foreach (var _ in archive.Entries) totalEntries++;

                int currentEntry = 0;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!entry.IsDirectory)
                    {
                        currentEntry++;
                        if (currentEntry % 25 == 0 || currentEntry == totalEntries)
                        {
                            progress?.Report($"Extracting ({currentEntry}/{totalEntries}): {entry.Key}");
                        }

                        entry.WriteToDirectory(destinationDirectory, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }

                progress?.Report("Extraction completed.");
            }, cancellationToken);
        }

        public async Task CreateZipBackupAsync(string sourceDirectory, string destinationZipFilePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory for backup not found: {sourceDirectory}");
            }

            string? targetDir = Path.GetDirectoryName(destinationZipFilePath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            await Task.Run(() =>
            {
                progress?.Report($"Creating backup zip: {Path.GetFileName(destinationZipFilePath)}...");
                if (File.Exists(destinationZipFilePath))
                {
                    File.Delete(destinationZipFilePath);
                }

                ZipFile.CreateFromDirectory(sourceDirectory, destinationZipFilePath, CompressionLevel.Optimal, false);
                progress?.Report("Backup zip created successfully.");
            }, cancellationToken);
        }

        public string FindApacheRoot(string extractedDirectory)
        {
            if (!Directory.Exists(extractedDirectory))
            {
                return extractedDirectory;
            }

            // Case 1: Direct root contains bin/httpd.exe
            if (File.Exists(Path.Combine(extractedDirectory, "bin", "httpd.exe")))
            {
                return extractedDirectory;
            }

            // Case 2: Subfolder contains bin/httpd.exe (e.g. Apache24, apache, httpd-2.4.x)
            var subDirs = Directory.GetDirectories(extractedDirectory);
            foreach (var dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "bin", "httpd.exe")))
                {
                    return dir;
                }
            }

            // Case 3: Check 2 levels deep
            foreach (var dir in subDirs)
            {
                var secondLevel = Directory.GetDirectories(dir);
                foreach (var sub2 in secondLevel)
                {
                    if (File.Exists(Path.Combine(sub2, "bin", "httpd.exe")))
                    {
                        return sub2;
                    }
                }
            }

            return extractedDirectory;
        }
    }
}
