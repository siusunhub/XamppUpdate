using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XamppUpdate.Services.Uat;

public static class ErrorLogEvidence
{
    private static readonly Regex ShutdownComplete = new(
        @"(?:Shutdown\s+complete[d]?|Normal\s+shutdown|Server\s+shutdown\s+complete[d]?|Stopping\s+server)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ContainsShutdownComplete(string text) => ShutdownComplete.IsMatch(text);

    public static List<string> GetLogCandidates(string mySqlPath)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(mySqlPath)) return candidates;

        // 1. Inspect my.ini (bin\my.ini or root my.ini) for configured log-error / log_error
        string[] iniFiles = { Path.Combine(mySqlPath, "bin", "my.ini"), Path.Combine(mySqlPath, "my.ini") };
        foreach (var iniFile in iniFiles)
        {
            if (!File.Exists(iniFile)) continue;
            try
            {
                string iniContent = File.ReadAllText(iniFile, Encoding.UTF8);
                string? opt = null;
                foreach (var section in new[] { "mysqld", "server", "mariadb", "mysqld-8.0" })
                {
                    opt = UatIniRewriter.GetOption(iniContent, section, "log-error")
                       ?? UatIniRewriter.GetOption(iniContent, section, "log_error");
                    if (!string.IsNullOrWhiteSpace(opt)) break;
                }

                if (!string.IsNullOrWhiteSpace(opt))
                {
                    string clean = opt.Trim('"', '\'', ' ', '\t');
                    if (Path.IsPathRooted(clean))
                    {
                        candidates.Add(clean);
                    }
                    else
                    {
                        candidates.Add(Path.Combine(mySqlPath, "data", clean));
                        candidates.Add(Path.Combine(mySqlPath, clean));
                        candidates.Add(Path.Combine(mySqlPath, "bin", clean));
                    }
                }
            }
            catch { }
        }

        // 2. Data directory candidates
        string dataDir = Path.Combine(mySqlPath, "data");
        if (Directory.Exists(dataDir))
        {
            candidates.Add(Path.Combine(dataDir, "mysql_error.log"));
            candidates.Add(Path.Combine(dataDir, $"{Environment.MachineName}.err"));
            candidates.Add(Path.Combine(dataDir, "mariadb.err"));
            candidates.Add(Path.Combine(dataDir, "mysql.err"));
            candidates.Add(Path.Combine(dataDir, "mysqld.err"));
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(dataDir, "*.err"));
                candidates.AddRange(Directory.EnumerateFiles(dataDir, "*error*.log"));
                candidates.AddRange(Directory.EnumerateFiles(dataDir, "*.log"));
            }
            catch { }
        }

        // 3. MySQL root directory candidates
        candidates.Add(Path.Combine(mySqlPath, "mysql_error.log"));
        candidates.Add(Path.Combine(mySqlPath, $"{Environment.MachineName}.err"));
        candidates.Add(Path.Combine(mySqlPath, "mariadb.err"));
        candidates.Add(Path.Combine(mySqlPath, "mysql.err"));
        candidates.Add(Path.Combine(mySqlPath, "mysqld.err"));
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(mySqlPath, "*.err"));
            candidates.AddRange(Directory.EnumerateFiles(mySqlPath, "*error*.log"));
        }
        catch { }

        // Filter to existing files and sort descending by LastWriteTimeUtc
        return candidates
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc)
            .ToList();
    }

    public static string? FindActiveLog(string mySqlPath)
    {
        var candidates = GetLogCandidates(mySqlPath);
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Remember where the log ends right now, so later evidence can be proven fresh.</summary>
    public static (string? Path, long Length) Capture(string mySqlPath)
    {
        try
        {
            var logPath = FindActiveLog(mySqlPath);
            if (logPath == null) return (null, 0);
            var fi = new FileInfo(logPath);
            return (fi.FullName, fi.Length);
        }
        catch
        {
            return (null, 0);
        }
    }

    /// <summary>Length of every candidate log right now. Lets a shutdown be checked against whichever file actually grew.</summary>
    public static Dictionary<string, long> SnapshotAll(string mySqlPath)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in GetLogCandidates(mySqlPath))
        {
            try { sizes[path] = new FileInfo(path).Length; }
            catch { }
        }
        return sizes;
    }

    /// <summary>
    /// Looks for a shutdown-complete line in every candidate log that gained text after the snapshot.
    /// Detail names the files that grew and their last line, so a failed check shows where mysqld actually wrote.
    /// </summary>
    public static async Task<(bool Matched, string Detail)> CheckGrownLogsAsync(
        IReadOnlyDictionary<string, long> before,
        string mySqlPath,
        int maxRetries = 6,
        int delayMs = 500)
    {
        string detail = "no log file grew during the shutdown";
        for (int i = 0; i <= maxRetries; i++)
        {
            var grown = new List<string>();
            foreach (var path in GetLogCandidates(mySqlPath))
            {
                long offset = before.TryGetValue(path, out var seen) ? seen : 0;
                if (!TryReadAppended(path, offset, out string appended) || appended.Length == 0) continue;

                if (ContainsShutdownComplete(appended))
                {
                    return (true, $"shutdown line found in {Path.GetFileName(path)}");
                }

                string lastLine = appended.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .LastOrDefault() ?? string.Empty;
                if (lastLine.Length > 160) lastLine = lastLine[..160] + "...";
                grown.Add($"{Path.GetFileName(path)} (+{appended.Length} chars, last line: \"{lastLine}\")");
            }

            if (grown.Count > 0) detail = "files that grew: " + string.Join("; ", grown);

            if (i < maxRetries) await Task.Delay(delayMs);
        }

        return (false, detail);
    }

    private static readonly Regex CrashRecoveryMarkers = new(
        @"Database\s+was\s+not\s+shutdown\s+normally|Starting\s+crash\s+recovery|Starting\s+recovery\s+for\s+crash|Upgrade\s+after\s+a\s+crash",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StartupFinished = new(
        @"Server\s+socket\s+created|ready\s+for\s+connections",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public enum StartupVerdict { CleanStart, CrashRecovery, NotSeen }

    /// <summary>
    /// Reads the log text written after <paramref name="before"/> until the server reports it is ready (or the wait ends).
    /// A clean stop leaves no redo to replay, so the restart logs no crash recovery. That is the evidence used when the
    /// server does not write its own "Shutdown complete" line.
    /// </summary>
    public static async Task<(StartupVerdict Verdict, string Detail)> CheckStartupAsync(
        IReadOnlyDictionary<string, long> before,
        string mySqlPath,
        TimeSpan maxWait,
        int delayMs = 500)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (true)
        {
            var sb = new StringBuilder();
            foreach (var path in GetLogCandidates(mySqlPath))
            {
                long offset = before.TryGetValue(path, out var seen) ? seen : 0;
                if (TryReadAppended(path, offset, out string appended)) sb.Append(appended);
            }

            string text = sb.ToString();
            var crash = CrashRecoveryMarkers.Match(text);
            if (crash.Success)
                return (StartupVerdict.CrashRecovery, $"crash recovery was logged after the restart ('{crash.Value}')");

            if (StartupFinished.IsMatch(text))
                return (StartupVerdict.CleanStart, "the server started after the restart with no crash recovery in its log");

            if (DateTime.UtcNow >= deadline)
                return (StartupVerdict.NotSeen, "the restart log never reported readiness, so crash recovery could not be ruled out");

            await Task.Delay(delayMs);
        }
    }

    private static bool TryReadAppended(string path, long offset, out string text)
    {
        text = string.Empty;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(fs.Length < offset ? 0 : offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            text = sr.ReadToEnd();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCheckLogFile(string logPath, long offset, out bool matched)
    {
        matched = false;
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return false;
        try
        {
            var fi = new FileInfo(logPath);
            fi.Refresh();
            long seekPos = (fi.Length < offset) ? 0 : offset;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= seekPos && seekPos > 0)
            {
                return false;
            }

            fs.Seek(seekPos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string content = sr.ReadToEnd();
            matched = ContainsShutdownComplete(content);
            return true;
        }
        catch (IOException)
        {
            // Transient sharing/lock issue while process terminates
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ShutdownCompleteAfterAsync(
        string? logPath,
        long offset,
        string? mySqlPath = null,
        int maxRetries = 6,
        int delayMs = 500)
    {
        for (int i = 0; i <= maxRetries; i++)
        {
            if (!string.IsNullOrEmpty(logPath) && TryCheckLogFile(logPath, offset, out bool matched))
            {
                if (matched) return true;
            }

            if (!string.IsNullOrEmpty(mySqlPath))
            {
                string? active = FindActiveLog(mySqlPath);
                if (!string.IsNullOrEmpty(active) && !string.Equals(active, logPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryCheckLogFile(active, 0, out bool altMatched) && altMatched)
                    {
                        return true;
                    }
                }
            }

            if (i < maxRetries)
            {
                await Task.Delay(delayMs);
            }
        }

        return false;
    }

    public static bool ShutdownCompleteAfter(string? logPath, long offset, string? mySqlPath = null)
    {
        if (!string.IsNullOrEmpty(logPath) && TryCheckLogFile(logPath, offset, out bool matched) && matched)
            return true;

        if (!string.IsNullOrEmpty(mySqlPath))
        {
            string? active = FindActiveLog(mySqlPath);
            if (!string.IsNullOrEmpty(active) && !string.Equals(active, logPath, StringComparison.OrdinalIgnoreCase))
            {
                if (TryCheckLogFile(active, 0, out bool altMatched) && altMatched)
                    return true;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            System.Threading.Thread.Sleep(300);
            if (!string.IsNullOrEmpty(logPath) && TryCheckLogFile(logPath, offset, out bool m) && m)
                return true;

            if (!string.IsNullOrEmpty(mySqlPath))
            {
                string? active = FindActiveLog(mySqlPath);
                if (!string.IsNullOrEmpty(active) && TryCheckLogFile(active, string.Equals(active, logPath, StringComparison.OrdinalIgnoreCase) ? offset : 0, out bool altM) && altM)
                    return true;
            }
        }

        return false;
    }
}

