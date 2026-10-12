using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    /// <summary>Real-mode file deployment shared by the production pipeline and the UAT run.</summary>
    public static class MySqlDeployHelper
    {
        private static readonly EnumerationOptions EnumOptions = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        public static bool IsDataDirectory(string relativePath)
        {
            string norm = relativePath.Replace('\\', '/').TrimStart('/');
            return norm.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                   norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<int> DeployNewFilesAsync(
            string incomingRoot, string destRoot, IProgress<UpdateProgressReport>? progress, CancellationToken ct)
        {
            var files = Directory.GetFiles(incomingRoot, "*", EnumOptions);
            int total = files.Length;
            int copied = 0;

            foreach (var src in files)
            {
                ct.ThrowIfCancellationRequested();

                string rel = Path.GetRelativePath(incomingRoot, src);
                if (IsDataDirectory(rel)) continue;

                string dest = Path.Combine(destRoot, rel);
                string? folder = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

                File.Copy(src, dest, true);
                copied++;

                if (copied % 25 == 0 || copied == total)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Deploying New MySQL Files",
                        Message = $"[DEPLOY] Copied {copied}/{total} files: {rel}",
                        Percentage = 50 + ((double)copied / Math.Max(1, total) * 20.0)
                    });
                }
            }

            await Task.CompletedTask;
            return copied;
        }

        public static async Task ApplyConfigsAsync(
            IEnumerable<ConfigDiffItem> configs, string destRoot, IProgress<UpdateProgressReport>? progress, CancellationToken ct)
        {
            foreach (var item in configs)
            {
                ct.ThrowIfCancellationRequested();

                string destIni = Path.Combine(destRoot, item.RelativeFilePath);
                string content = item.Resolution switch
                {
                    ConfigFileResolution.KeepCurrent => item.OriginalLocalContent,
                    ConfigFileResolution.OverwriteWithNew => item.IncomingContent,
                    ConfigFileResolution.UseMerged => item.MergedContent,
                    _ => item.OriginalLocalContent
                };

                string? folder = Path.GetDirectoryName(destIni);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

                await FileEncodingHelper.WriteAllTextPreservingEncodingAsync(destIni, content, ct);

                progress?.Report(new UpdateProgressReport
                {
                    Step = UpdateStep.ConfigApplication,
                    StepTitle = "Applying Configuration",
                    Message = $"[CONFIG] Applied {item.Resolution} resolution to '{item.RelativeFilePath}'.",
                    Percentage = 80
                });
            }
        }

        /// <summary>Moves (same volume) or stream-copies the data directory. False when the source does not exist.</summary>
        public static async Task<bool> MigrateDataAsync(
            string srcDataDir, string destDataDir, IProgress<UpdateProgressReport>? progress, CancellationToken ct)
        {
            if (!Directory.Exists(srcDataDir)) return false;

            // Primary approach: atomic directory move (instant, zero risk of InnoDB page corruption).
            if (!Directory.Exists(destDataDir))
            {
                try
                {
                    Directory.Move(srcDataDir, destDataDir);
                    return true;
                }
                catch (Exception ex)
                {
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Data Migration",
                        Message = $"[DATA WARNING] Direct move not available ({ex.Message}). Falling back to stream migration...",
                        Percentage = 83
                    });
                }
            }

            // Fallback approach: chunked stream migration (move failed or destination exists).
            var dataFiles = Directory.GetFiles(srcDataDir, "*", EnumOptions);
            int totalDataFiles = dataFiles.Length;
            long totalDataBytes = 0;
            foreach (var f in dataFiles)
            {
                try { totalDataBytes += new FileInfo(f).Length; } catch { }
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.FileReplacement,
                StepTitle = "Migrating Database Data",
                Message = $"Migrating {totalDataFiles} database files ({ArchiveService.FormatBytes(totalDataBytes)})...",
                Percentage = 83
            });

            int dataCopied = 0;
            long totalDataBytesCopied = 0;

            foreach (var srcDataFile in dataFiles)
            {
                ct.ThrowIfCancellationRequested();
                dataCopied++;

                string relDataPath = Path.GetRelativePath(srcDataDir, srcDataFile);
                string destDataFile = Path.Combine(destDataDir, relDataPath);

                long bytesCopied = await MySqlUpdateService.CopyFileChunkedAsync(
                    srcDataFile, destDataFile, relDataPath, totalDataBytes, totalDataBytesCopied,
                    progress, string.Empty, ct);
                totalDataBytesCopied += bytesCopied;

                if (dataCopied % 50 == 0 || dataCopied == totalDataFiles)
                {
                    double overallPercent = totalDataBytes > 0 ? (double)totalDataBytesCopied / totalDataBytes * 100.0 : 100.0;
                    progress?.Report(new UpdateProgressReport
                    {
                        Step = UpdateStep.FileReplacement,
                        StepTitle = "Migrating Database Data",
                        Message = $"[DATA] Copied {dataCopied}/{totalDataFiles} database files ({ArchiveService.FormatBytes(totalDataBytesCopied)} / {ArchiveService.FormatBytes(totalDataBytes)} - {overallPercent:F0}%)...",
                        Percentage = 83 + (overallPercent * 0.07)
                    });
                }
            }

            progress?.Report(new UpdateProgressReport
            {
                Step = UpdateStep.FileReplacement,
                StepTitle = "Data Migration Complete",
                Message = $"[DATA] Successfully migrated {totalDataFiles} database file(s) ({ArchiveService.FormatBytes(totalDataBytesCopied)}) to '{destDataDir}'.",
                Percentage = 90
            });

            return true;
        }
    }
}
