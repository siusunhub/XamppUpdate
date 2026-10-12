using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XamppUpdate.Services.Uat;

/// <summary>Pure text operations on my.ini / *.cnf content. No file or process access.</summary>
public static class UatIniRewriter
{
    // A root match must end at a segment boundary: end-of-string, a separator, a quote or whitespace.
    // "mysql" must not match "mysql_old", "mysql.bak", "mysql;C:\other" or "mysql+x".
    private const string Boundary = @"(?=$|[\\/""'\s])";

    private static readonly Regex HeaderRegex =
        new(@"^\s*\uFEFF?\s*\[([^\]]+)\]\s*(?:[#;].*)?$", RegexOptions.CultureInvariant);

    private static readonly string[] WritablePathKeys =
    {
        "datadir", "tmpdir", "log-error", "log_error", "general-log-file", "general_log_file",
        "slow-query-log-file", "slow_query_log_file", "log-bin", "log_bin", "relay-log", "relay_log",
        "innodb_data_home_dir", "innodb-data-home-dir", "innodb_log_group_home_dir", "innodb-log-group-home-dir",
        "innodb_undo_directory", "innodb-undo-directory", "innodb_tmpdir", "innodb-tmpdir",
        "pid-file", "pid_file", "secure-file-priv", "secure_file_priv"
    };

    private static Regex RootRegex(string root)
    {
        var segments = root.Trim().TrimEnd('\\', '/')
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        string joined = string.Join(@"(?:\\{1,2}|/)", segments.Select(Regex.Escape));
        return new Regex(joined + Boundary, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string StripBom(string content) =>
        content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;

    private static string BomOf(string content) =>
        content.Length > 0 && content[0] == '\uFEFF' ? "\uFEFF" : string.Empty;

    private static string[] SplitLines(string content) =>
        StripBom(content).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

    private static bool IsComment(string trimmed) => trimmed.StartsWith('#') || trimmed.StartsWith(';');

    // '-' and '_' are interchangeable in MySQL option names.
    private static bool SameKey(string a, string b) =>
        string.Equals(a.Trim().Replace('_', '-'), b.Trim().Replace('_', '-'), StringComparison.OrdinalIgnoreCase);

    public static string RewriteRoot(string content, string oldRoot, string newRoot)
    {
        string back = newRoot.Trim().TrimEnd('\\', '/').Replace('/', '\\');
        return RootRegex(oldRoot).Replace(content, m =>
        {
            if (m.Value.Contains(@"\\")) return back.Replace(@"\", @"\\");
            if (m.Value.Contains('/') && !m.Value.Contains('\\')) return back.Replace('\\', '/');
            return back;
        });
    }

    public static IReadOnlyList<string> FindRemainingRoot(string content, string oldRoot)
    {
        var rx = RootRegex(oldRoot);
        return SplitLines(content)
            .Where(l => !IsComment(l.TrimStart()) && rx.IsMatch(l))
            .ToList();
    }

    private static bool TryHeader(string line, out string name)
    {
        var m = HeaderRegex.Match(line);
        name = m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        return m.Success;
    }

    /// <summary>All blocks (header index, exclusive end index) of the named section; a section may repeat.</summary>
    private static List<(int Start, int End)> FindSections(IList<string> lines, string section)
    {
        var blocks = new List<(int Start, int End)>();
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!TryHeader(lines[i], out string name)) continue;
            if (start >= 0) { blocks.Add((start, i)); start = -1; }
            if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        if (start >= 0) blocks.Add((start, lines.Count));
        return blocks;
    }

    private static string KeyOf(string trimmedLine)
    {
        int eq = trimmedLine.IndexOf('=');
        return (eq >= 0 ? trimmedLine[..eq] : trimmedLine).Trim();
    }

    private static bool IsActiveKeyLine(string line, string key, out string trimmed)
    {
        trimmed = line.TrimStart();
        return !(IsComment(trimmed) || trimmed.Length == 0) && SameKey(KeyOf(trimmed), key);
    }

    public static string SetOption(string content, string section, string key, string value)
    {
        string bom = BomOf(content);
        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var blocks = FindSections(lines, section);
        string newLine = $"{key}={value}";

        if (blocks.Count == 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add(newLine);
            lines.Add(string.Empty);
            return bom + string.Join(nl, lines);
        }

        bool replaced = false;
        foreach (var (start, end) in blocks)
        {
            for (int i = start + 1; i < end; i++)
            {
                if (IsActiveKeyLine(lines[i], key, out _))
                {
                    lines[i] = newLine;
                    replaced = true;
                }
            }
        }
        if (!replaced)
        {
            var (start, end) = blocks[^1];
            int insertAt = end;
            while (insertAt > start + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
            lines.Insert(insertAt, newLine);
        }
        return bom + string.Join(nl, lines);
    }

    public static string? GetOption(string content, string section, string key)
    {
        var lines = SplitLines(content);
        string? found = null;
        foreach (var (start, end) in FindSections(lines, section))
        {
            for (int i = start + 1; i < end; i++)
            {
                if (!IsActiveKeyLine(lines[i], key, out string t)) continue;
                int eq = t.IndexOf('=');
                found = eq >= 0 ? t[(eq + 1)..].Trim().Trim('"') : string.Empty;
            }
        }
        return found;
    }

    public static string CommentOutOptions(string content, string section, params string[] keys)
    {
        string bom = BomOf(content);
        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var blocks = FindSections(lines, section);
        if (blocks.Count == 0) return content;

        foreach (var (start, end) in blocks)
        {
            for (int i = start + 1; i < end; i++)
            {
                if (keys.Any(k => IsActiveKeyLine(lines[i], k, out _)))
                {
                    lines[i] = "# [UAT disabled] " + lines[i];
                }
            }
        }
        return bom + string.Join(nl, lines);
    }

    public static bool ContainsInclude(string content) =>
        SplitLines(content).Any(l => l.TrimStart().StartsWith("!include", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> FindExternalPaths(string content, string allowedRoot)
    {
        var result = new List<string>();
        foreach (var line in SplitLines(content))
        {
            string t = line.TrimStart();
            if (IsComment(t) || t.Length == 0 || TryHeader(t, out _)) continue;

            string key = KeyOf(t);
            if (!WritablePathKeys.Any(k => SameKey(k, key))) continue;

            int eq = t.IndexOf('=');
            if (eq < 0) continue;
            string value = t[(eq + 1)..].Trim().Trim('"');
            if (value.Length == 0 || !Path.IsPathRooted(value)) continue;

            if (!PathSafety.IsSameOrUnder(value, allowedRoot))
            {
                result.Add($"{key}={value}");
            }
        }
        return result;
    }

    // Any key containing pass(word|wd) (loose-password, master-password, ssl-key-password...). Never crosses a line.
    public static string RedactSecrets(string content) =>
        Regex.Replace(content, @"^([ \t]*[\w.\-]*pass(?:word|wd)[\w.\-]*[ \t]*=[ \t]*)[^\r\n]+", "$1******",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
}
