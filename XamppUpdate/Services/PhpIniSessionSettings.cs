using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    /// <summary>
    /// Pure text (and optional file) operations for the PHP <c>session.upload_progress.enabled</c> directive.
    /// An active directive is updated in place; when only a commented directive exists (for example
    /// <c>;session.upload_progress.enabled = Off</c>) that line is uncommented and reused instead of
    /// appending a duplicate. A new directive is only appended when neither form exists.
    /// </summary>
    public static class PhpIniSessionSettings
    {
        public const string UploadProgressEnabledKey = "session.upload_progress.enabled";

        private static readonly Regex UploadProgressLineRegex = new(
            @"^(?<indent>[ \t]*)(?<comment>[#;]+[ \t]*)?(?<key>session\.upload_progress\.enabled)(?<sep>[ \t]*=[ \t]*)(?<value>[^;#\r\n]*?)(?<suffix>[ \t]*(?:[#;][^\r\n]*)?\r?)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads the effective (active) value of the directive, falling back to the first commented
        /// directive when no active one exists. Returns <c>null</c> when the directive is absent or
        /// its value cannot be interpreted as a boolean.
        /// </summary>
        public static bool? TryGetUploadProgressEnabled(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            var matches = UploadProgressLineRegex.Matches(content).Cast<Match>().ToList();

            var active = matches.LastOrDefault(m => m.Groups["comment"].Length == 0);
            if (active != null) return ParseBool(active.Groups["value"].Value);

            var commented = matches.FirstOrDefault(m => m.Groups["comment"].Length > 0);
            return commented != null ? ParseBool(commented.Groups["value"].Value) : null;
        }

        /// <summary>
        /// Sets <c>session.upload_progress.enabled</c> to <c>1</c> (enable) or <c>0</c> (disable),
        /// preserving indentation, trailing comments, BOM and line endings.
        /// </summary>
        /// <param name="uncommentedCommentedLine">True when a commented directive was uncommented and reused.</param>
        /// <param name="appendedNewLine">True when no directive existed and a new one was appended.</param>
        public static string SetUploadProgressEnabled(
            string content,
            bool enabled,
            out bool uncommentedCommentedLine,
            out bool appendedNewLine)
        {
            uncommentedCommentedLine = false;
            appendedNewLine = false;

            content ??= string.Empty;
            string value = enabled ? "1" : "0";

            var matches = UploadProgressLineRegex.Matches(content).Cast<Match>().ToList();
            var activeMatches = matches.Where(m => m.Groups["comment"].Length == 0).ToList();

            if (activeMatches.Count > 0)
            {
                // Update every active occurrence (PHP uses the last one, but keep the file consistent).
                var builder = new System.Text.StringBuilder(content);
                for (int i = activeMatches.Count - 1; i >= 0; i--)
                {
                    var match = activeMatches[i];
                    builder.Remove(match.Index, match.Length);
                    builder.Insert(match.Index, BuildDirectiveLine(match, value, uncomment: false));
                }
                return builder.ToString();
            }

            var commentedMatch = matches.FirstOrDefault(m => m.Groups["comment"].Length > 0);
            if (commentedMatch != null)
            {
                // Reuse the commented directive: strip the leading #/; and set the requested value.
                uncommentedCommentedLine = true;
                var builder = new System.Text.StringBuilder(content);
                builder.Remove(commentedMatch.Index, commentedMatch.Length);
                builder.Insert(commentedMatch.Index, BuildDirectiveLine(commentedMatch, value, uncomment: true));
                return builder.ToString();
            }

            appendedNewLine = true;
            return AppendDirective(content, $"{UploadProgressEnabledKey} = {value}");
        }

        /// <summary>
        /// Reads <paramref name="iniPath"/>, toggles the directive and writes the file back using its
        /// original encoding and line endings. When the desired value is already present the file is
        /// left untouched (no rewrite).
        /// </summary>
        public static async Task<UploadProgressToggleResult> ApplyAsync(
            string iniPath,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            string original = await FileEncodingHelper.ReadAllTextAsync(iniPath, cancellationToken);
            if (original.Length > 0 && original[0] == '\uFEFF')
            {
                original = original[1..];
            }

            bool? previous = TryGetUploadProgressEnabled(original);
            string updated = SetUploadProgressEnabled(original, enabled, out bool uncommented, out bool appended);

            bool changed = !string.Equals(updated, original, StringComparison.Ordinal);
            if (changed)
            {
                await FileEncodingHelper.WriteAllTextPreservingEncodingAsync(iniPath, updated, cancellationToken);
            }

            return new UploadProgressToggleResult(previous, enabled, changed, uncommented, appended);
        }

        private static string BuildDirectiveLine(Match match, string value, bool uncomment)
        {
            var groups = match.Groups;
            string comment = uncomment ? string.Empty : groups["comment"].Value;
            return groups["indent"].Value
                   + comment
                   + groups["key"].Value
                   + groups["sep"].Value
                   + value
                   + groups["suffix"].Value;
        }

        private static bool? ParseBool(string? raw)
        {
            string value = (raw ?? string.Empty).Trim().Trim('"', '\'').ToLowerInvariant();
            return value switch
            {
                "1" or "on" or "true" or "yes" => true,
                "0" or "off" or "false" or "no" or "" => false,
                _ => null
            };
        }

        /// <summary>
        /// Appends the directive at the end of the [Session] section when that section exists,
        /// otherwise at the end of the file.
        /// </summary>
        private static string AppendDirective(string content, string directive)
        {
            string nl = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = new List<string>(normalized.Split('\n'));

            // Drop the empty element produced by a trailing newline; it is re-added when joining.
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            int sessionStart = -1;
            int sessionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("[", StringComparison.Ordinal)) continue;
                int close = trimmed.IndexOf(']');
                if (close < 0) continue;

                if (sessionStart >= 0)
                {
                    sessionEnd = i;
                    break;
                }

                string sectionName = trimmed[1..close].Trim();
                if (string.Equals(sectionName, "Session", StringComparison.OrdinalIgnoreCase))
                {
                    sessionStart = i;
                }
            }

            if (sessionStart >= 0)
            {
                int insertAt = sessionEnd;
                while (insertAt > sessionStart + 1 && lines[insertAt - 1].Trim().Length == 0)
                {
                    insertAt--;
                }
                lines.Insert(insertAt, directive);
            }
            else
            {
                // Never leave the directive under an unrelated trailing section (e.g. [HOST=...]),
                // where it would only apply to that host/path.
                while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add("[Session]");
                lines.Add(directive);
            }

            return string.Join(nl, lines) + nl;
        }
    }

    /// <summary>Describes the outcome of <see cref="PhpIniSessionSettings.ApplyAsync"/>.</summary>
    public sealed record UploadProgressToggleResult(
        bool? PreviousValue,
        bool NewValue,
        bool Changed,
        bool DirectiveWasCommented,
        bool DirectiveWasAppended);
}
