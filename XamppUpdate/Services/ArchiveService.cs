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
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    // Allow SSL/TLS connections for mirror sites and CDN endpoints that might have custom/untrusted cert chains
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                }
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(15)
            };

            // Set browser User-Agent to prevent CDNs / mirror sites from rejecting or dropping the connection
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "*/*");

            return client;
        }

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

        public Task CreateZipBackupAsync(string sourceDirectory, string destinationZipFilePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            return CreateZipBackupAsync(sourceDirectory, destinationZipFilePath, null, progress, cancellationToken);
        }

        public async Task CreateZipBackupAsync(string sourceDirectory, string destinationZipFilePath, System.Collections.Generic.IEnumerable<string>? excludedDirectoryNames, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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
                if (File.Exists(destinationZipFilePath))
                {
                    File.Delete(destinationZipFilePath);
                }

                var dirInfo = new DirectoryInfo(sourceDirectory);
                var allFiles = dirInfo.GetFiles("*", SearchOption.AllDirectories);

                var candidateFiles = new System.Collections.Generic.List<FileInfo>();
                int skippedLogs = 0;
                int skippedExcluded = 0;
                long totalBytes = 0;

                foreach (var file in allFiles)
                {
                    string relativePath = Path.GetRelativePath(sourceDirectory, file.FullName);

                    if (IsLogFile(relativePath))
                    {
                        skippedLogs++;
                        continue;
                    }

                    if (IsInExcludedDirectory(relativePath, excludedDirectoryNames))
                    {
                        skippedExcluded++;
                        continue;
                    }

                    candidateFiles.Add(file);
                    totalBytes += file.Length;
                }

                progress?.Report($"[BACKUP] Starting backup: {candidateFiles.Count} files ({FormatBytes(totalBytes)}) to {Path.GetFileName(destinationZipFilePath)}...");

                // 2MB buffer for high throughput
                byte[] buffer = new byte[2 * 1024 * 1024];
                long totalArchivedBytes = 0;
                int added = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                using (var zipStream = new FileStream(destinationZipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.SequentialScan))
                using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var file in candidateFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string relativePath = Path.GetRelativePath(sourceDirectory, file.FullName).Replace('\\', '/');

                        try
                        {
                            // For large files (>20MB) or database files, use Fastest / NoCompression to avoid CPU lockup
                            var compressionLevel = file.Length > 50 * 1024 * 1024 ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                            var entry = zipArchive.CreateEntry(relativePath, compressionLevel);
                            entry.LastWriteTime = file.LastWriteTime;

                            using var entryStream = entry.Open();
                            using var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 2 * 1024 * 1024, FileOptions.SequentialScan);

                            long fileBytesRead = 0;
                            long fileLength = file.Length;
                            int bytesRead;
                            long lastReportTicks = stopwatch.ElapsedMilliseconds;

                            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                entryStream.Write(buffer, 0, bytesRead);
                                fileBytesRead += bytesRead;
                                totalArchivedBytes += bytesRead;

                                // For files > 10MB, report periodic sub-file progress every 500ms or on completion
                                if (fileLength > 10 * 1024 * 1024)
                                {
                                    long currentTicks = stopwatch.ElapsedMilliseconds;
                                    if (currentTicks - lastReportTicks > 500 || fileBytesRead == fileLength)
                                    {
                                        double filePercent = (double)fileBytesRead / fileLength * 100.0;
                                        double overallPercent = totalBytes > 0 ? (double)totalArchivedBytes / totalBytes * 100.0 : 0;
                                        progress?.Report($"[BACKUP] {relativePath} ({FormatBytes(fileBytesRead)} / {FormatBytes(fileLength)} - {filePercent:F0}%) | Total: {overallPercent:F0}%");
                                        lastReportTicks = currentTicks;
                                    }
                                }
                            }

                            added++;
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"[BACKUP WARNING] Skipped file '{relativePath}': {ex.Message}");
                        }

                        if (added % 50 == 0 || added == candidateFiles.Count)
                        {
                            double overallPercent = totalBytes > 0 ? (double)totalArchivedBytes / totalBytes * 100.0 : 100.0;
                            progress?.Report($"[BACKUP] Archived {added}/{candidateFiles.Count} files ({FormatBytes(totalArchivedBytes)} / {FormatBytes(totalBytes)} - {overallPercent:F0}%)...");
                        }
                    }

                    if (skippedLogs > 0)
                    {
                        progress?.Report($"[BACKUP] Excluded {skippedLogs} log file(s) from backup to prevent lock conflicts.");
                    }
                }

                progress?.Report($"[BACKUP] Backup zip created successfully: {Path.GetFileName(destinationZipFilePath)} ({FormatBytes(totalArchivedBytes)} in {stopwatch.Elapsed.TotalSeconds:F1}s).");
            }, cancellationToken);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1024L * 1024 * 1024)
                return $"{(double)bytes / (1024 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024)
                return $"{(double)bytes / (1024 * 1024):F1} MB";
            if (bytes >= 1024L)
                return $"{(double)bytes / 1024:F0} KB";
            return $"{bytes} B";
        }

        private static bool IsLogFile(string relativePath)
        {
            string norm = relativePath.Replace('\\', '/');
            string fileName = Path.GetFileName(norm);

            // 1. Files in any logs directory
            if (norm.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                norm.Contains("/logs/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Any file ending with .log or starting with common log prefixes or having .log. in name
            if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".log.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("access.log", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("error.log", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("ssl_request.log", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsInExcludedDirectory(string relativePath, System.Collections.Generic.IEnumerable<string>? excludedDirectories)
        {
            if (excludedDirectories == null) return false;
            string norm = relativePath.Replace('\\', '/').TrimStart('/');
            foreach (var dir in excludedDirectories)
            {
                string d = dir.Replace('\\', '/').Trim('/');
                if (norm.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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

        public string FindPhpRoot(string extractedDirectory)
        {
            if (!Directory.Exists(extractedDirectory))
            {
                return extractedDirectory;
            }

            // Case 1: Direct root contains php.exe
            if (File.Exists(Path.Combine(extractedDirectory, "php.exe")))
            {
                return extractedDirectory;
            }

            // Case 2: Subfolder contains php.exe (e.g. php-8.x.x, php, etc.)
            var subDirs = Directory.GetDirectories(extractedDirectory);
            foreach (var dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "php.exe")))
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
                    if (File.Exists(Path.Combine(sub2, "php.exe")))
                    {
                        return sub2;
                    }
                }
            }

            return extractedDirectory;
        }

        public string FindMySqlRoot(string extractedDirectory)
        {
            if (!Directory.Exists(extractedDirectory))
            {
                return extractedDirectory;
            }

            // Case 1: Direct root contains bin/mysqld.exe or bin/mariadbd.exe
            if (File.Exists(Path.Combine(extractedDirectory, "bin", "mysqld.exe")) ||
                File.Exists(Path.Combine(extractedDirectory, "bin", "mariadbd.exe")))
            {
                return extractedDirectory;
            }

            // Case 2: Direct root contains mysqld.exe or mariadbd.exe
            if (File.Exists(Path.Combine(extractedDirectory, "mysqld.exe")) ||
                File.Exists(Path.Combine(extractedDirectory, "mariadbd.exe")))
            {
                return extractedDirectory;
            }

            // Case 3: Subfolder contains bin/mysqld.exe or bin/mariadbd.exe
            var subDirs = Directory.GetDirectories(extractedDirectory);
            foreach (var dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "bin", "mysqld.exe")) ||
                    File.Exists(Path.Combine(dir, "bin", "mariadbd.exe")) ||
                    File.Exists(Path.Combine(dir, "mysqld.exe")) ||
                    File.Exists(Path.Combine(dir, "mariadbd.exe")))
                {
                    return dir;
                }
            }

            // Case 4: Check 2 levels deep
            foreach (var dir in subDirs)
            {
                var secondLevel = Directory.GetDirectories(dir);
                foreach (var sub2 in secondLevel)
                {
                    if (File.Exists(Path.Combine(sub2, "bin", "mysqld.exe")) ||
                        File.Exists(Path.Combine(sub2, "bin", "mariadbd.exe")) ||
                        File.Exists(Path.Combine(sub2, "mysqld.exe")) ||
                        File.Exists(Path.Combine(sub2, "mariadbd.exe")))
                    {
                        return sub2;
                    }
                }
            }

            return extractedDirectory;
        }

        public string FindPhpMyAdminRoot(string extractedDirectory)
        {
            if (!Directory.Exists(extractedDirectory))
            {
                return extractedDirectory;
            }

            // Case 1: Direct root contains index.php and (config.sample.inc.php or libraries/ or RELEASE-DATE-*)
            if (File.Exists(Path.Combine(extractedDirectory, "index.php")) &&
                (File.Exists(Path.Combine(extractedDirectory, "config.sample.inc.php")) ||
                 Directory.Exists(Path.Combine(extractedDirectory, "libraries")) ||
                 Directory.GetFiles(extractedDirectory, "RELEASE-DATE-*").Length > 0))
            {
                return extractedDirectory;
            }

            // Case 2: Subfolder (e.g. phpMyAdmin-5.2.2-all-languages/)
            var subDirs = Directory.GetDirectories(extractedDirectory);
            foreach (var dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "index.php")) &&
                    (File.Exists(Path.Combine(dir, "config.sample.inc.php")) ||
                     Directory.Exists(Path.Combine(dir, "libraries")) ||
                     Directory.GetFiles(dir, "RELEASE-DATE-*").Length > 0))
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
                    if (File.Exists(Path.Combine(sub2, "index.php")) &&
                        (File.Exists(Path.Combine(sub2, "config.sample.inc.php")) ||
                         Directory.Exists(Path.Combine(sub2, "libraries")) ||
                         Directory.GetFiles(sub2, "RELEASE-DATE-*").Length > 0))
                    {
                        return sub2;
                    }
                }
            }

            return extractedDirectory;
        }
    }
}
