using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services.Uat;

public static class MySqlToolArgs
{
    public static string Query(string user, int port, string sql) =>
        $"--no-defaults -u {QuoteArg(ValidateUser(user))} -h 127.0.0.1 -P {port} --connect-timeout=10 -N -B -e {QuoteArg(sql)}";

    public static string Upgrade(string user, int port) =>
        $"--no-defaults -u {QuoteArg(ValidateUser(user))} -h 127.0.0.1 -P {port} --force --verbose";

    private static string ValidateUser(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("User name must not be empty.", nameof(user));
        if (user.Contains('"') || user.Any(char.IsControl))
            throw new ArgumentException("User name must not contain quotes or control characters.", nameof(user));
        return user;
    }

    /// <summary>Standard Windows (CommandLineToArgvW) quoting: one argument, no breakout possible.</summary>
    public static string QuoteArg(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}

public sealed record UatIdentity(string Version, int Port, string BaseDir, string DataDir)
{
    public const string Sql = "SELECT @@version, @@port, @@basedir, @@datadir";

    public static UatIdentity? Parse(string stdout)
    {
        foreach (var raw in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = raw.Split('\t');
            if (cols.Length >= 4 && int.TryParse(cols[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            {
                return new UatIdentity(cols[0].Trim(), port, cols[2].Trim(), cols[3].Trim());
            }
        }
        return null;
    }

    public bool Matches(int expectedPort, string expectedRoot) =>
        Port == expectedPort &&
        PathSafety.IsSameOrUnder(BaseDir.Replace('/', '\\'), expectedRoot) &&
        PathSafety.IsSameOrUnder(DataDir.Replace('/', '\\'), expectedRoot);
}

public static class ServiceImagePath
{
    public static string ExtractExecutable(string imagePath)
    {
        string s = imagePath.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 0 ? s[1..end] : s.Trim('"');
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s;
    }
}

public interface IMySqlToolRunner
{
    Task<ToolResult> QueryAsync(MySqlTarget target, string user, string password, string sql, CancellationToken ct);
    Task<ToolResult> UpgradeAsync(MySqlTarget target, string user, string password, CancellationToken ct);
}

public sealed class MySqlToolRunner : IMySqlToolRunner
{
    private static string? Find(string root, params string[] names) =>
        names.Select(n => Path.Combine(root, "bin", n)).FirstOrDefault(File.Exists);

    public Task<ToolResult> QueryAsync(MySqlTarget target, string user, string password, string sql, CancellationToken ct)
    {
        string? exe = Find(target.Root, "mariadb.exe", "mysql.exe");
        if (exe == null) return Task.FromResult(new ToolResult(-1, string.Empty, "mariadb/mysql client not found in " + target.Root));
        return RunAsync(exe, MySqlToolArgs.Query(user, target.Port, sql), password, TimeSpan.FromSeconds(60), ct);
    }

    public Task<ToolResult> UpgradeAsync(MySqlTarget target, string user, string password, CancellationToken ct)
    {
        string? exe = Find(target.Root, "mariadb-upgrade.exe", "mysql_upgrade.exe");
        if (exe == null) return Task.FromResult(new ToolResult(-1, string.Empty, "mariadb-upgrade not found in " + target.Root));
        return RunAsync(exe, MySqlToolArgs.Upgrade(user, target.Port), password, TimeSpan.FromMinutes(20), ct);
    }

    private static async Task<ToolResult> RunAsync(string exe, string args, string password, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // The password travels in the environment: it is never on the command line, in logs or in process listings.
        // Ambient MYSQL_PWD / MYSQL_HOST / MYSQL_TCP_PORT must never leak into a target-explicit call.
        psi.Environment.Remove("MYSQL_PWD");
        psi.Environment.Remove("MYSQL_HOST");
        psi.Environment.Remove("MYSQL_TCP_PORT");
        if (!string.IsNullOrEmpty(password)) psi.Environment["MYSQL_PWD"] = password;

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ToolResult(-1, string.Empty, ex.Message);
        }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
            string why = ct.IsCancellationRequested ? "cancelled" : "timed out";
            string so = await ReadBoundedAsync(stdout, TimeSpan.FromSeconds(5));
            string se = await ReadBoundedAsync(stderr, TimeSpan.FromSeconds(5));
            return new ToolResult(-1, so, $"{Path.GetFileName(exe)} {why}. {se}".Trim());
        }

        return new ToolResult(proc.ExitCode, await ReadBoundedAsync(stdout, TimeSpan.FromSeconds(10)), await ReadBoundedAsync(stderr, TimeSpan.FromSeconds(10)));
    }

    public const string OutputNotAvailable = "(output not available)";

    /// <summary>Returns the stream text, or a marker if it does not complete within <paramref name="bound"/>.</summary>
    public static async Task<string> ReadBoundedAsync(Task<string> streamTask, TimeSpan bound)
    {
        var done = await Task.WhenAny(streamTask, Task.Delay(bound));
        if (done != streamTask) return OutputNotAvailable;
        try { return await streamTask; } catch { return OutputNotAvailable; }
    }
}

/// <summary>Pure decision logic for the install-failure cleanup: never delete a service this call did not create.</summary>
public static class ServiceInstallGuard
{
    /// <param name="existedBeforeReliably">null = could not be established (never delete); false = reliably absent before; true = existed.</param>
    public static bool MayDeleteAfterFailedInstall(bool? existedBeforeReliably, bool existsNow) =>
        existedBeforeReliably == false && existsNow;
}
