using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class ConfigDiffService : IConfigDiffService
    {
        private readonly ISideBySideDiffBuilder _diffBuilder;

        public ConfigDiffService()
        {
            _diffBuilder = new SideBySideDiffBuilder(new Differ());
        }

        public async Task<List<ConfigDiffItem>> CompareConfigFilesAsync(string localApacheRoot, string incomingApacheRoot, IEnumerable<string> relativeConfigPaths)
        {
            var results = new List<ConfigDiffItem>();
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Process Required Files (Must always be shown)
            foreach (var relativePath in relativeConfigPaths)
            {
                string norm = NormalizeRelativePath(relativePath);
                if (processedPaths.Add(norm))
                {
                    string localPath = Path.Combine(localApacheRoot, norm.Replace('/', Path.DirectorySeparatorChar));
                    string incomingPath = Path.Combine(incomingApacheRoot, norm.Replace('/', Path.DirectorySeparatorChar));

                    var diffItem = await CompareSingleFileAsync(localPath, incomingPath, norm);
                    results.Add(diffItem);
                }
            }

            // 2. Auto-discover all other .conf files in local and incoming conf/ directories
            var discoveredConfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ScanConfDir(string rootDir)
            {
                string confFolder = Path.Combine(rootDir, "conf");
                if (Directory.Exists(confFolder))
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(confFolder, "*.conf", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(rootDir, file);
                            discoveredConfs.Add(NormalizeRelativePath(rel));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error scanning conf dir '{confFolder}': {ex.Message}");
                    }
                }
            }

            if (Directory.Exists(localApacheRoot))
            {
                ScanConfDir(localApacheRoot);
            }

            if (Directory.Exists(incomingApacheRoot))
            {
                ScanConfDir(incomingApacheRoot);
            }

            // 3. For any other discovered .conf file, compare and add to pulldown IF contents are different
            var otherConfs = discoveredConfs
                .Where(p => !processedPaths.Contains(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var otherPath in otherConfs)
            {
                string localPath = Path.Combine(localApacheRoot, otherPath.Replace('/', Path.DirectorySeparatorChar));
                string incomingPath = Path.Combine(incomingApacheRoot, otherPath.Replace('/', Path.DirectorySeparatorChar));

                var diffItem = await CompareSingleFileAsync(localPath, incomingPath, otherPath);

                if (diffItem.HasDifferences)
                {
                    results.Add(diffItem);
                }
            }

            return results;
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            return relativePath.Replace('\\', '/').TrimStart('/');
        }

        public async Task<ConfigDiffItem> CompareSingleFileAsync(string localFilePath, string incomingFilePath, string relativeFilePath)
        {
            return await Task.Run(async () =>
            {
                var item = new ConfigDiffItem
                {
                    RelativeFilePath = relativeFilePath,
                    LocalFilePath = localFilePath,
                    IncomingFilePath = incomingFilePath,
                    LocalExists = File.Exists(localFilePath),
                    IncomingExists = File.Exists(incomingFilePath),
                    Resolution = ConfigFileResolution.KeepCurrent // Default to keeping current working configs safe
                };

                string oldText = item.LocalExists ? await File.ReadAllTextAsync(localFilePath, Encoding.UTF8) : string.Empty;
                string newText = item.IncomingExists ? await File.ReadAllTextAsync(incomingFilePath, Encoding.UTF8) : string.Empty;

                item.OriginalLocalContent = oldText;
                item.IncomingContent = newText;
                item.MergedContent = oldText;

                if (!item.LocalExists && !item.IncomingExists)
                {
                    item.HasDifferences = false;
                    return item;
                }

                RebuildDiff(item, oldText, newText);
                return item;
            });
        }

        public void RebuildDiff(ConfigDiffItem item, string workingLocalText, string incomingText)
        {
            var diff = _diffBuilder.BuildDiffModel(workingLocalText, incomingText);

            int adds = 0;
            int dels = 0;
            int mods = 0;

            var localList = new List<DiffLineModel>();
            var incomingList = new List<DiffLineModel>();
            var rowList = new List<DiffRowModel>();

            int maxLines = Math.Max(diff.OldText.Lines.Count, diff.NewText.Lines.Count);

            for (int i = 0; i < maxLines; i++)
            {
                var oldLine = i < diff.OldText.Lines.Count ? diff.OldText.Lines[i] : null;
                var newLine = i < diff.NewText.Lines.Count ? diff.NewText.Lines[i] : null;

                var localModel = new DiffLineModel();
                if (oldLine != null)
                {
                    localModel.LineNumber = oldLine.Position;
                    localModel.Text = oldLine.Text ?? string.Empty;
                    localModel.Type = MapDiffType(oldLine.Type);

                    if (oldLine.Type == ChangeType.Deleted) dels++;
                    else if (oldLine.Type == ChangeType.Modified) mods++;
                }
                else
                {
                    localModel.Type = DiffLineType.EmptyPlaceholder;
                }
                localList.Add(localModel);

                var incomingModel = new DiffLineModel();
                if (newLine != null)
                {
                    incomingModel.LineNumber = newLine.Position;
                    incomingModel.Text = newLine.Text ?? string.Empty;
                    incomingModel.Type = MapDiffType(newLine.Type);

                    if (newLine.Type == ChangeType.Inserted) adds++;
                }
                else
                {
                    incomingModel.Type = DiffLineType.EmptyPlaceholder;
                }
                incomingList.Add(incomingModel);

                rowList.Add(new DiffRowModel
                {
                    Index = i,
                    Local = localModel,
                    Incoming = incomingModel
                });
            }

            item.LocalLines = localList;
            item.IncomingLines = incomingList;
            item.DiffRows = rowList;
            item.AdditionsCount = adds;
            item.DeletionsCount = dels;
            item.ModificationsCount = mods;
            item.HasDifferences = (adds > 0 || dels > 0 || mods > 0 || item.LocalExists != item.IncomingExists);
        }

        private static DiffLineType MapDiffType(ChangeType type)
        {
            return type switch
            {
                ChangeType.Unchanged => DiffLineType.Unchanged,
                ChangeType.Deleted => DiffLineType.Deleted,
                ChangeType.Inserted => DiffLineType.Inserted,
                ChangeType.Modified => DiffLineType.Modified,
                ChangeType.Imaginary => DiffLineType.EmptyPlaceholder,
                _ => DiffLineType.Unchanged
            };
        }
    }
}
