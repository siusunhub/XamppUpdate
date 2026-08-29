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

            foreach (var relativePath in relativeConfigPaths)
            {
                string localPath = Path.Combine(localApacheRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string incomingPath = Path.Combine(incomingApacheRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                var diffItem = await CompareSingleFileAsync(localPath, incomingPath, relativePath);
                results.Add(diffItem);
            }

            return results;
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

                if (!item.LocalExists && !item.IncomingExists)
                {
                    item.HasDifferences = false;
                    return item;
                }

                var diff = _diffBuilder.BuildDiffModel(oldText, newText);

                int adds = 0;
                int dels = 0;
                int mods = 0;

                var localList = new List<DiffLineModel>();
                var incomingList = new List<DiffLineModel>();

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
                }

                item.LocalLines = localList;
                item.IncomingLines = incomingList;
                item.AdditionsCount = adds;
                item.DeletionsCount = dels;
                item.ModificationsCount = mods;
                item.HasDifferences = (adds > 0 || dels > 0 || mods > 0 || item.LocalExists != item.IncomingExists);

                return item;
            });
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
