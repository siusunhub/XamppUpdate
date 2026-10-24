using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        public async Task<List<ConfigDiffItem>> ComparePhpConfigFilesAsync(string localPhpRoot, string incomingPhpRoot)
        {
            var results = new List<ConfigDiffItem>();
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Primary Required File: php.ini
            string localPhpIni = Path.Combine(localPhpRoot, "php.ini");
            string incomingPhpIni = Path.Combine(incomingPhpRoot, "php.ini");

            // If incoming has no php.ini, check php.ini-production or php.ini-development
            if (!File.Exists(incomingPhpIni))
            {
                string prodIni = Path.Combine(incomingPhpRoot, "php.ini-production");
                string devIni = Path.Combine(incomingPhpRoot, "php.ini-development");
                if (File.Exists(prodIni))
                {
                    incomingPhpIni = prodIni;
                }
                else if (File.Exists(devIni))
                {
                    incomingPhpIni = devIni;
                }
            }

            var primaryDiff = await CompareSingleFileAsync(localPhpIni, incomingPhpIni, "php.ini");
            results.Add(primaryDiff);
            processedPaths.Add("php.ini");
            processedPaths.Add(Path.GetFileName(incomingPhpIni));

            // 2. Discover any other .ini files in local or incoming
            var discoveredInis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ScanIniDir(string rootDir)
            {
                if (Directory.Exists(rootDir))
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(rootDir, "*.ini", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(rootDir, file);
                            discoveredInis.Add(NormalizeRelativePath(rel));
                        }
                    }
                    catch { }
                }
            }

            ScanIniDir(localPhpRoot);
            ScanIniDir(incomingPhpRoot);

            var otherInis = discoveredInis
                .Where(p => !processedPaths.Contains(p) &&
                            !p.Equals("php.ini-development", StringComparison.OrdinalIgnoreCase) &&
                            !p.Equals("php.ini-production", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var otherPath in otherInis)
            {
                string localPath = Path.Combine(localPhpRoot, otherPath.Replace('/', Path.DirectorySeparatorChar));
                string incomingPath = Path.Combine(incomingPhpRoot, otherPath.Replace('/', Path.DirectorySeparatorChar));

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

                string oldText = item.LocalExists ? await FileEncodingHelper.ReadAllTextAsync(localFilePath) : string.Empty;
                string newText = item.IncomingExists ? await FileEncodingHelper.ReadAllTextAsync(incomingFilePath) : string.Empty;

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

            int count = Math.Max(diff.OldText.Lines.Count, diff.NewText.Lines.Count);
            int idx = 0;

            while (idx < count)
            {
                var oldLine = idx < diff.OldText.Lines.Count ? diff.OldText.Lines[idx] : null;
                var newLine = idx < diff.NewText.Lines.Count ? diff.NewText.Lines[idx] : null;

                bool isOldUnchanged = oldLine != null && oldLine.Type == ChangeType.Unchanged;
                bool isNewUnchanged = newLine != null && newLine.Type == ChangeType.Unchanged;

                if (isOldUnchanged && isNewUnchanged)
                {
                    // Unchanged anchor row: both sides identical
                    AddRow(rowList.Count, oldLine, newLine, DiffLineType.Unchanged, DiffLineType.Unchanged,
                           localList, incomingList, rowList);
                    idx++;
                }
                else
                {
                    // Collect all lines in this contiguous changed block
                    var oldBlockLines = new List<DiffPiece>();
                    var newBlockLines = new List<DiffPiece>();

                    while (idx < count)
                    {
                        var curOld = idx < diff.OldText.Lines.Count ? diff.OldText.Lines[idx] : null;
                        var curNew = idx < diff.NewText.Lines.Count ? diff.NewText.Lines[idx] : null;

                        bool curOldUnchanged = curOld != null && curOld.Type == ChangeType.Unchanged;
                        bool curNewUnchanged = curNew != null && curNew.Type == ChangeType.Unchanged;

                        if (curOldUnchanged && curNewUnchanged)
                        {
                            break; // Reached next anchor
                        }

                        if (curOld != null && curOld.Type != ChangeType.Imaginary && curOld.Position.HasValue)
                        {
                            oldBlockLines.Add(curOld);
                        }

                        if (curNew != null && curNew.Type != ChangeType.Imaginary && curNew.Position.HasValue)
                        {
                            newBlockLines.Add(curNew);
                        }

                        idx++;
                    }

                    // Smart block alignment (Key-aware & similarity-aware)
                    var alignedRows = AlignBlock(oldBlockLines, newBlockLines, item.RelativeFilePath);
                    foreach (var aligned in alignedRows)
                    {
                        var o = aligned.Old;
                        var n = aligned.New;

                        DiffLineType localType;
                        DiffLineType incomingType;

                        if (o != null && n != null)
                        {
                            if (string.Equals(o.Text, n.Text, StringComparison.Ordinal))
                            {
                                localType = DiffLineType.Unchanged;
                                incomingType = DiffLineType.Unchanged;
                            }
                            else
                            {
                                localType = DiffLineType.Modified;
                                incomingType = DiffLineType.Modified;
                                mods++;
                            }
                        }
                        else if (o != null)
                        {
                            localType = DiffLineType.Deleted;
                            incomingType = DiffLineType.EmptyPlaceholder;
                            dels++;
                        }
                        else
                        {
                            localType = DiffLineType.EmptyPlaceholder;
                            incomingType = DiffLineType.Inserted;
                            adds++;
                        }

                        AddRow(rowList.Count, o, n, localType, incomingType, localList, incomingList, rowList);
                    }
                }
            }

            AnnotateDifferenceBlocks(rowList);

            item.LocalLines = localList;
            item.IncomingLines = incomingList;
            item.DiffRows = rowList;
            item.AdditionsCount = adds;
            item.DeletionsCount = dels;
            item.ModificationsCount = mods;
            item.HasDifferences = (adds > 0 || dels > 0 || mods > 0 || item.LocalExists != item.IncomingExists);
        }

        public static void AnnotateDifferenceBlocks(List<DiffRowModel> rowList)
        {
            if (rowList == null || rowList.Count == 0) return;

            int i = 0;
            while (i < rowList.Count)
            {
                var row = rowList[i];
                if (!IsRowDifferent(row))
                {
                    row.Local.CanRemoveBlock = false;
                    row.Local.BlockButtonText = string.Empty;
                    row.Local.BlockButtonToolTip = string.Empty;
                    row.Local.BlockLineCount = 0;

                    row.Incoming.CanInsertBlock = false;
                    row.Incoming.BlockButtonText = string.Empty;
                    row.Incoming.BlockButtonToolTip = string.Empty;
                    row.Incoming.BlockLineCount = 0;
                    i++;
                    continue;
                }

                int start = i;
                while (i < rowList.Count && IsRowDifferent(rowList[i]))
                {
                    i++;
                }
                int end = i - 1;

                int insertableCount = 0;
                int removableCount = 0;
                int localLinesCount = 0;

                for (int j = start; j <= end; j++)
                {
                    if (rowList[j].Incoming.CanInsert) insertableCount++;
                    if (rowList[j].Local.CanRemove) removableCount++;
                    if (rowList[j].Local.LineNumber.HasValue && rowList[j].Local.Type != DiffLineType.EmptyPlaceholder)
                        localLinesCount++;
                }

                bool canInsertBlock = insertableCount > 1;
                bool canRemoveBlock = removableCount > 1;

                string insertText = $"⬅️ Block ({insertableCount})";
                string insertToolTip = localLinesCount > 0
                    ? $"Replace local block ({localLinesCount} lines) with incoming block ({insertableCount} lines)"
                    : $"Insert incoming block ({insertableCount} lines) into local merged config";

                string removeText = $"🗑 Block ({removableCount})";
                string removeToolTip = $"Remove entire deleted block ({removableCount} lines) from local merged config";

                for (int j = start; j <= end; j++)
                {
                    var r = rowList[j];
                    if (canInsertBlock && r.Incoming.CanInsert)
                    {
                        r.Incoming.CanInsertBlock = true;
                        r.Incoming.BlockLineCount = insertableCount;
                        r.Incoming.BlockButtonText = insertText;
                        r.Incoming.BlockButtonToolTip = insertToolTip;
                    }
                    else
                    {
                        r.Incoming.CanInsertBlock = false;
                        r.Incoming.BlockLineCount = 0;
                        r.Incoming.BlockButtonText = string.Empty;
                        r.Incoming.BlockButtonToolTip = string.Empty;
                    }

                    if (canRemoveBlock && r.Local.CanRemove)
                    {
                        r.Local.CanRemoveBlock = true;
                        r.Local.BlockLineCount = removableCount;
                        r.Local.BlockButtonText = removeText;
                        r.Local.BlockButtonToolTip = removeToolTip;
                    }
                    else
                    {
                        r.Local.CanRemoveBlock = false;
                        r.Local.BlockLineCount = 0;
                        r.Local.BlockButtonText = string.Empty;
                        r.Local.BlockButtonToolTip = string.Empty;
                    }
                }
            }
        }

        private static bool IsRowDifferent(DiffRowModel row)
        {
            return row.Local.Type is DiffLineType.Inserted or DiffLineType.Deleted or DiffLineType.Modified
                || row.Incoming.Type is DiffLineType.Inserted or DiffLineType.Deleted or DiffLineType.Modified;
        }

        private static void AddRow(
            int rowIndex,
            DiffPiece? oldPiece,
            DiffPiece? newPiece,
            DiffLineType localType,
            DiffLineType incomingType,
            List<DiffLineModel> localList,
            List<DiffLineModel> incomingList,
            List<DiffRowModel> rowList)
        {
            var localModel = new DiffLineModel
            {
                LineNumber = (oldPiece != null && localType != DiffLineType.EmptyPlaceholder) ? oldPiece.Position : null,
                Text = (oldPiece != null && localType != DiffLineType.EmptyPlaceholder) ? oldPiece.Text ?? string.Empty : string.Empty,
                Type = localType
            };
            localList.Add(localModel);

            var incomingModel = new DiffLineModel
            {
                LineNumber = (newPiece != null && incomingType != DiffLineType.EmptyPlaceholder) ? newPiece.Position : null,
                Text = (newPiece != null && incomingType != DiffLineType.EmptyPlaceholder) ? newPiece.Text ?? string.Empty : string.Empty,
                Type = incomingType
            };
            incomingList.Add(incomingModel);

            rowList.Add(new DiffRowModel
            {
                Index = rowIndex,
                Local = localModel,
                Incoming = incomingModel
            });
        }

        private readonly struct AlignedRow
        {
            public DiffPiece? Old { get; }
            public DiffPiece? New { get; }

            public AlignedRow(DiffPiece? oldPiece, DiffPiece? newPiece)
            {
                Old = oldPiece;
                New = newPiece;
            }
        }

        private static List<AlignedRow> AlignBlock(List<DiffPiece> oldLines, List<DiffPiece> newLines, string relativePath)
        {
            int m = oldLines.Count;
            int n = newLines.Count;

            if (m == 0 && n == 0) return new List<AlignedRow>();

            if (m == 0)
            {
                var result = new List<AlignedRow>(n);
                foreach (var nl in newLines) result.Add(new AlignedRow(null, nl));
                return result;
            }

            if (n == 0)
            {
                var result = new List<AlignedRow>(m);
                foreach (var ol in oldLines) result.Add(new AlignedRow(ol, null));
                return result;
            }

            // Fallback to simple sequential alignment if block is excessively large
            if (m > 200 || n > 200)
            {
                var result = new List<AlignedRow>(Math.Max(m, n));
                int max = Math.Max(m, n);
                for (int i = 0; i < max; i++)
                {
                    result.Add(new AlignedRow(i < m ? oldLines[i] : null, i < n ? newLines[i] : null));
                }
                return result;
            }

            // Needleman-Wunsch Sequence Alignment with Directive Match Bonus
            const int GapPenalty = -2;
            int[,] dp = new int[m + 1, n + 1];
            int[,] dir = new int[m + 1, n + 1]; // 1: Diag (Pair), 2: Left (Insert New), 3: Up (Delete Old)

            for (int i = 1; i <= m; i++)
            {
                dp[i, 0] = i * GapPenalty;
                dir[i, 0] = 3;
            }

            for (int j = 1; j <= n; j++)
            {
                dp[0, j] = j * GapPenalty;
                dir[0, j] = 2;
            }

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    int matchScore = CalculateMatchScore(oldLines[i - 1], newLines[j - 1], relativePath);
                    int scoreDiag = dp[i - 1, j - 1] + matchScore;
                    int scoreLeft = dp[i, j - 1] + GapPenalty;
                    int scoreUp = dp[i - 1, j] + GapPenalty;

                    int bestScore = scoreDiag;
                    int bestDir = 1;

                    if (scoreLeft > bestScore)
                    {
                        bestScore = scoreLeft;
                        bestDir = 2;
                    }

                    if (scoreUp > bestScore)
                    {
                        bestScore = scoreUp;
                        bestDir = 3;
                    }

                    dp[i, j] = bestScore;
                    dir[i, j] = bestDir;
                }
            }

            var reversedRows = new List<AlignedRow>();
            int currI = m;
            int currJ = n;

            while (currI > 0 || currJ > 0)
            {
                int d = dir[currI, currJ];
                if (d == 1)
                {
                    reversedRows.Add(new AlignedRow(oldLines[currI - 1], newLines[currJ - 1]));
                    currI--;
                    currJ--;
                }
                else if (d == 2)
                {
                    reversedRows.Add(new AlignedRow(null, newLines[currJ - 1]));
                    currJ--;
                }
                else
                {
                    reversedRows.Add(new AlignedRow(oldLines[currI - 1], null));
                    currI--;
                }
            }

            reversedRows.Reverse();
            return reversedRows;
        }

        private static int CalculateMatchScore(DiffPiece oldPiece, DiffPiece newPiece, string relativePath)
        {
            bool oldBlank = string.IsNullOrWhiteSpace(oldPiece.Text);
            bool newBlank = string.IsNullOrWhiteSpace(newPiece.Text);

            if (oldBlank && newBlank) return 10;
            if (oldBlank || newBlank) return -50;

            string? oldKey = ExtractDirectiveKey(oldPiece.Text, relativePath);
            string? newKey = ExtractDirectiveKey(newPiece.Text, relativePath);

            if (oldKey != null && newKey != null)
            {
                if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(oldPiece.Text.Trim(), newPiece.Text.Trim(), StringComparison.Ordinal) ? 120 : 100;
                }
                else
                {
                    return -100; // Both are directives, but different keys
                }
            }

            if (oldKey != null || newKey != null)
            {
                return -50; // One is a directive, one is a comment/documentation
            }

            // Both are comments or general text lines
            double sim = ComputeSimilarity(oldPiece.Text, newPiece.Text);
            if (sim >= 0.6)
            {
                return (int)(sim * 30);
            }

            return 0;
        }

        private static string? ExtractDirectiveKey(string line, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            string trimmed = line.Trim();

            // Section Header: [Section]
            var secMatch = Regex.Match(trimmed, @"^\[(?<sec>[a-zA-Z0-9_\.\-]+)\]$");
            if (secMatch.Success)
            {
                return $"section:{secMatch.Groups["sec"].Value.ToLowerInvariant()}";
            }

            // INI style directive: key = value or ;key = value
            var iniMatch = Regex.Match(trimmed, @"^(?:[;#]\s*)?(?<key>[a-zA-Z0-9_\-]+(?:\.[a-zA-Z0-9_\-]+)*)\s*=(?<val>.*)$");
            if (iniMatch.Success)
            {
                string key = iniMatch.Groups["key"].Value.ToLowerInvariant();
                string val = iniMatch.Groups["val"].Value.Trim().Trim('"', '\'');

                if (key == "extension" || key == "zend_extension")
                {
                    return $"{key}:{val.ToLowerInvariant()}";
                }

                return $"key:{key}";
            }

            // Apache / Conf style directive: Directive Argument (e.g. LoadModule php_module ... or DocumentRoot "...")
            if (relativePath.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            {
                var confMatch = Regex.Match(trimmed, @"^(?:#\s*)?(?<dir>[A-Za-z][A-Za-z0-9_-]+)\s+(?<arg>\S+)(?:.*)$");
                if (confMatch.Success)
                {
                    string dir = confMatch.Groups["dir"].Value.ToLowerInvariant();
                    string arg = confMatch.Groups["arg"].Value.Trim('"', '\'').ToLowerInvariant();

                    if (dir == "loadmodule")
                    {
                        return $"loadmodule:{arg}";
                    }

                    return $"dir:{dir}";
                }
            }

            return null;
        }

        private static double ComputeSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;

            s1 = s1.Trim();
            s2 = s2.Trim();
            if (string.Equals(s1, s2, StringComparison.Ordinal)) return 1.0;

            int maxLen = Math.Max(s1.Length, s2.Length);
            if (maxLen == 0) return 1.0;
            if (maxLen > 200)
            {
                s1 = s1.Substring(0, Math.Min(s1.Length, 200));
                s2 = s2.Substring(0, Math.Min(s2.Length, 200));
                maxLen = 200;
            }

            int dist = LevenshteinDistance(s1, s2);
            return 1.0 - ((double)dist / maxLen);
        }

        private static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                char sc = s[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    int cost = (sc == t[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                Array.Copy(curr, prev, m + 1);
            }

            return prev[m];
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
