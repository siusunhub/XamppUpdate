using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XamppUpdate.Services.Uat;

/// <summary>Pure text operations on my.ini / *.cnf content. No file or process access.</summary>
public static class UatIniRewriter
{
    // A root match must end at a segment boundary: "mysql" must not match "mysql_old" or "mysql.bak".
    private const string Boundary = @"(?![\w.\-])";

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

    private static string[] SplitLines(string content) => content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

    private static bool IsComment(string trimmed) => trimmed.StartsWith('#') || trimmed.StartsWith(';');

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

    private static (int Start, int End) FindSection(IList<string> lines, string section)
    {
        int start = -1;
        int end = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim().TrimStart('\uFEFF');
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (start >= 0) { end = i; break; }
                if (string.Equals(t.Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase)) start = i;
            }
        }
        return (start, end);
    }

    private static string KeyOf(string trimmedLine)
    {
        int eq = trimmedLine.IndexOf('=');
        return (eq >= 0 ? trimmedLine[..eq] : trimmedLine).Trim();
    }

    public static string SetOption(string content, string section, string key, string value)
    {
        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var (start, end) = FindSection(lines, section);
        string newLine = $"{key}={value}";

        if (start < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add(newLine);
            lines.Add(string.Empty);
            return string.Join(nl, lines);
        }

        bool replaced = false;
        for (int i = start + 1; i < end; i++)
        {
            string t = lines[i].TrimStart();
            if (IsComment(t) || t.Length == 0) continue;
            if (string.Equals(KeyOf(t), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = newLine;
                replaced = true;
            }
        }
        if (!replaced) lines.Insert(start + 1, newLine);
        return string.Join(nl, lines);
    }

    public static string? GetOption(string content, string section, string key)
    {
        var lines = SplitLines(content);
        var (start, end) = FindSection(lines, section);
        if (start < 0) return null;

        string? found = null;
        for (int i = start + 1; i < end; i++)
        {
            string t = lines[i].TrimStart();
            if (IsComment(t) || t.Length == 0) continue;
            if (string.Equals(KeyOf(t), key, StringComparison.OrdinalIgnoreCase))
            {
                int eq = t.IndexOf('=');
                found = eq >= 0 ? t[(eq + 1)..].Trim().Trim('"') : string.Empty;
            }
        }
        return found;
    }

    public static string CommentOutOptions(string content, string section, params string[] keys)
    {
        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(content).ToList();
        var (start, end) = FindSection(lines, section);
        if (start < 0) return content;

        for (int i = start + 1; i < end; i++)
        {
            string t = lines[i].TrimStart();
            if (IsComment(t) || t.Length == 0) continue;
            if (keys.Any(k => string.Equals(KeyOf(t), k, StringComparison.OrdinalIgnoreCase)))
            {
                lines[i] = "# [UAT disabled] " + lines[i];
            }
        }
        return string.Join(nl, lines);
    }

    public static bool ContainsInclude(string content) =>
        SplitLines(content).Any(l => l.TrimStart().StartsWith("!include", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> FindExternalPaths(string content, string allowedRoot)
    {
        var result = new List<string>();
        foreach (var line in SplitLines(content))
        {
            string t = line.TrimStart();
            if (IsComment(t) || t.Length == 0 || t.StartsWith('[')) continue;

            string key = KeyOf(t);
            if (!WritablePathKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;

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

    public static string RedactSecrets(string content) =>
        Regex.Replace(content, @"^(\s*(?:password|passwd)\s*=\s*).+$", "$1******",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
}
