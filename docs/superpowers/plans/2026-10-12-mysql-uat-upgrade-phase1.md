# MySQL "Real Test Upgrade" (UAT copy) - Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a step-1 "Real Test Upgrade" button that makes a cold copy of the production MariaDB install, isolates it on another port, upgrades it with the incoming version, and reports each stage with evidence, without ever modifying production files.

**Architecture:** Pure, unit-tested helpers (`PathSafety`, `UatIniRewriter`, `UatPortProbe`, `UatPreflight`, `ErrorLogEvidence`, `MySqlToolArgs`) feed a thin orchestrator (`UatUpgradeService`) that talks to production/copy only through `IWindowsServiceManager`, `IMySqlToolRunner` and `IUatFileSystem`, so ordering and the "always restore production" guarantee are testable with fakes. The real pipeline's file-deploy code is extracted into `MySqlDeployHelper` and shared with stage 5.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-11-mysql-uat-upgrade-design.md` (read section 10a "Delivery phases": this plan is **Phase 1 only**).

## Global Constraints

- Target framework `net10.0-windows`; nullable enabled; match existing code style (4-space indent, file-scoped namespaces are NOT used in this repo's older files, new files under `Services/Uat` use file-scoped namespaces).
- Defaults: target folder `<parent of MySql.InstallationPath>\mysql_copy_upgrade`, port `13306`, service name `mysql_uat`, "Stop production MySQL before copy" ON, "Test as Windows service" ON.
- Phase 1 supports **cold copy only** (production stopped for the copy). Live copy is Phase 2: the option is not shown and `UatOptions.StopProductionFirst == false` is a preflight failure.
- MariaDB-to-MariaDB only. Any `!include` / `!includedir` in the production ini is rejected in Phase 1.
- Production files/config are never replaced, rewritten, renamed or deleted by this feature. Production is only stopped and restarted.
- Passwords are never put on a command line or in logs/reports: client tools get them via the `MYSQL_PWD` environment variable; every client/admin/upgrade call uses `--no-defaults` first and explicit `-h 127.0.0.1 -P <port>`.
- Process ownership is decided with `PathSafety.IsProcessUnderRoot`; an unreadable process image path is never treated as owned and never killed.
- Do not commit unless the user has asked for commits (steps marked "Commit" are skipped otherwise).
- Never change global app settings to redirect an operation.

## Review Focus

Failure modes the spec implies but the stage tests do not name; each has a test in the owning task:

1. Admin password containing spaces, quotes, `%`, `^`, `&`: must reach the server unchanged (Task 6, `MySqlToolArgs` + env-var runner).
2. Paths containing spaces or non-ASCII characters (`C:\xampp test\mysql copy`): rewriting and process matching must still work (Task 2 and Task 3).
3. `my.ini` with a UTF-8 BOM, mixed `\r\n`/`\n` endings, or no `[client]` section (Task 3).
4. Production originally **stopped**: the run must not start it afterwards (Task 9).
5. Cancel during the file copy: production restored, the partial target is recorded as owned so it can be deleted, report says "Cancelled" (Task 9).
6. User types the target folder with a trailing `\` or a different case than the existing folder (Task 2 / Task 4).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `XamppUpdate.Tests/XamppUpdate.Tests.csproj` | Create | xUnit project the `.sln` already references |
| `XamppUpdate/Services/Uat/PathSafety.cs` | Create | Canonical, segment-boundary-safe path comparisons |
| `XamppUpdate/Services/Uat/UatIniRewriter.cs` | Create | Pure ini text operations (root rewrite, set/get option, includes, redaction, external paths) |
| `XamppUpdate/Services/Uat/UatPortProbe.cs` | Create | Port validity and exclusive-bind availability |
| `XamppUpdate/Services/Uat/UatModels.cs` | Create | `UatOptions`, `UatStageStatus`, `UatStageResult`, `UatReport`, `UatEvent`, `MySqlTarget`, `ToolResult`, `UatPreflightIssue` |
| `XamppUpdate/Services/Uat/IUatFileSystem.cs` | Create | Filesystem seam (faked in tests) |
| `XamppUpdate/Services/Uat/UatFileSystem.cs` | Create | Real implementation of the seam |
| `XamppUpdate/Services/Uat/UatPreflight.cs` | Create | Stage-1 validation |
| `XamppUpdate/Services/Uat/ErrorLogEvidence.cs` | Create | Fresh "Shutdown complete" evidence using a log offset |
| `XamppUpdate/Services/Uat/MySqlToolRunner.cs` | Create | `IMySqlToolRunner`, `MySqlToolArgs`, `UatIdentity` |
| `XamppUpdate/Services/Uat/UatReportWriter.cs` | Create | Redacted report persisted outside the data tree |
| `XamppUpdate/Services/Uat/UatUpgradeService.cs` | Create | `IUatUpgradeService` + stages 1-8 + cleanup |
| `XamppUpdate/Services/MySqlDeployHelper.cs` | Create | Deploy / apply-config / migrate-data extracted from the pipeline |
| `XamppUpdate/Services/IWindowsServiceManager.cs` | Modify | Detailed shutdown, owned-service ops |
| `XamppUpdate/Services/WindowsServiceManager.cs` | Modify | Path-safe matching, detailed shutdown, owned service, `--no-defaults` |
| `XamppUpdate/Services/MySqlUpdateService.cs` | Modify | Use `PathSafety` and `MySqlDeployHelper` |
| `XamppUpdate/ViewModels/UatUpgradeViewModel.cs` | Create | Options, stage list, log, commands |
| `XamppUpdate/Views/UatUpgradeWindow.xaml(.cs)` | Create | UAT dialog |
| `XamppUpdate/ViewModels/MySqlWizardViewModel.cs` | Modify | "Real Test Upgrade" command + open-dialog event |
| `XamppUpdate/Views/MySqlUpdateWizardWindow.xaml(.cs)` | Modify | Button + dialog hosting |
| `XamppUpdate.Tests/*` | Create | Tests per task |

---

### Task 1: Test project scaffold

**Files:**
- Create: `XamppUpdate.Tests/XamppUpdate.Tests.csproj`
- Create: `XamppUpdate.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: a test project that references `XamppUpdate.csproj`; every later task adds files under `XamppUpdate.Tests/`.

- [ ] **Step 1: Create the project file** `XamppUpdate.Tests/XamppUpdate.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\XamppUpdate\XamppUpdate.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write a smoke test** `XamppUpdate.Tests/SmokeTests.cs`

```csharp
using XamppUpdate.Models;
using Xunit;

namespace XamppUpdate.Tests;

public class SmokeTests
{
    [Fact]
    public void AppAssemblyIsReferenced()
    {
        Assert.Equal("Failed", UpdateStep.Failed.ToString());
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test XamppUpdate.Tests/XamppUpdate.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 4: Verify the solution builds**

Run: `dotnet build XamppUpdate.sln`
Expected: `Build succeeded.` (both projects). If referencing the WinExe project causes an `app.manifest`/apphost error, add `<SelfContained>false</SelfContained>` to the test csproj `PropertyGroup` and re-run.

- [ ] **Step 5: Commit** (only if the user asked for commits)

```bash
git add XamppUpdate.Tests XamppUpdate.sln
git commit -m "test: add XamppUpdate.Tests project referenced by the solution"
```

---

### Task 2: PathSafety and process-matching fixes

**Files:**
- Create: `XamppUpdate/Services/Uat/PathSafety.cs`
- Test: `XamppUpdate.Tests/PathSafetyTests.cs`
- Modify: `XamppUpdate/Services/WindowsServiceManager.cs` (`TerminateProcessesByName`, `IsApacheProcessRunning`)
- Modify: `XamppUpdate/Services/MySqlUpdateService.cs` (`EnsureNoLingeringDatabaseProcessesAsync`)

**Interfaces:**
- Produces: `PathSafety.Canonical(string) -> string`, `PathSafety.IsSameOrUnder(string path, string root) -> bool`, `PathSafety.Overlaps(string a, string b) -> bool`, `PathSafety.IsProcessUnderRoot(string? imagePath, string root) -> bool`.

- [ ] **Step 1: Write the failing tests** `XamppUpdate.Tests/PathSafetyTests.cs`

```csharp
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData(@"C:\xampp\mysql\bin\mysqld.exe", @"C:\xampp\mysql", true)]
    [InlineData(@"C:\xampp\mysql", @"C:\xampp\mysql", true)]
    [InlineData(@"C:\xampp\mysql\", @"C:\xampp\mysql", true)]
    [InlineData(@"c:\XAMPP\MySQL\bin\mysqld.exe", @"C:\xampp\mysql\", true)]
    [InlineData(@"C:\xampp\mysql_copy_upgrade\bin\mysqld.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp\mysql_old\bin\mysqld.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp\mysql\..\mysql_old\x.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp test\mysql copy\bin\mysqld.exe", @"C:\xampp test\mysql copy", true)]
    public void IsSameOrUnder_RespectsSegmentBoundaries(string path, string root, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsSameOrUnder(path, root));
    }

    [Fact]
    public void Overlaps_IsSymmetric()
    {
        Assert.True(PathSafety.Overlaps(@"C:\xampp\mysql", @"C:\xampp\mysql\uat"));
        Assert.True(PathSafety.Overlaps(@"C:\xampp\mysql\uat", @"C:\xampp\mysql"));
        Assert.False(PathSafety.Overlaps(@"C:\xampp\mysql", @"C:\xampp\mysql_copy_upgrade"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProcessUnderRoot_UnreadablePathIsNeverOwned(string? image)
    {
        Assert.False(PathSafety.IsProcessUnderRoot(image, @"C:\xampp\mysql"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter PathSafetyTests`
Expected: FAIL, build error `The type or namespace name 'Uat' does not exist`.

- [ ] **Step 3: Implement** `XamppUpdate/Services/Uat/PathSafety.cs`

```csharp
namespace XamppUpdate.Services.Uat;

/// <summary>Canonical, path-segment-boundary-safe path comparisons. Plain StartsWith lets "mysql" match "mysql_old".</summary>
public static class PathSafety
{
    public static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        // Keep "C:\" intact; trim a trailing separator everywhere else.
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        string p = Canonical(path);
        string r = Canonical(root);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;

        string rWithSep = r.EndsWith('\\') ? r : r + '\\';
        return p.StartsWith(rWithSep, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string a, string b) => IsSameOrUnder(a, b) || IsSameOrUnder(b, a);

    /// <summary>False for null/blank (unreadable image path): ownership must be proven, never assumed.</summary>
    public static bool IsProcessUnderRoot(string? imagePath, string root)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(root)) return false;
        return IsSameOrUnder(imagePath, root);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter PathSafetyTests`
Expected: PASS (all cases).

- [ ] **Step 5: Use it in `WindowsServiceManager.TerminateProcessesByName`**

Replace this block:

```csharp
                            string? fn = GetProcessMainModuleFileName(proc);
                            if (!string.IsNullOrEmpty(fn) && Path.GetFullPath(fn).StartsWith(normTarget, StringComparison.OrdinalIgnoreCase))
                            {
                                shouldKill = true;
                            }
```

with:

```csharp
                            string? fn = GetProcessMainModuleFileName(proc);
                            if (XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(fn, targetPath))
                            {
                                shouldKill = true;
                            }
```

The call is fully qualified, so no `using` is needed. Remove the now-unused `normTarget` local only if the compiler reports it unused.

- [ ] **Step 6: Use it in `IsApacheProcessRunning`**

Replace `string normApache = Path.GetFullPath(apachePath).TrimEnd('\\', '/');` and the condition `Path.GetFullPath(fn).StartsWith(normApache, StringComparison.OrdinalIgnoreCase)` so the loop reads:

```csharp
                foreach (var p in procs)
                {
                    try
                    {
                        string? fn = GetProcessMainModuleFileName(p);
                        if (XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(fn, apachePath))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore process inspection errors, do not falsely return true
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return false;
```

- [ ] **Step 7: Remove the "unreadable means ours" fallback in `EnsureNoLingeringDatabaseProcessesAsync`**

In `MySqlUpdateService.cs` replace

```csharp
                            procPath = proc.MainModule?.FileName ?? string.Empty;
                            if (!string.IsNullOrEmpty(procPath) && procPath.StartsWith(normalizedLocalDir, StringComparison.OrdinalIgnoreCase))
                            {
                                isOurProcess = true;
                            }
                        }
                        catch
                        {
                            // If access to MainModule is restricted, match process name
                            isOurProcess = true;
                        }
```

with:

```csharp
                            procPath = proc.MainModule?.FileName ?? string.Empty;
                            isOurProcess = XamppUpdate.Services.Uat.PathSafety.IsProcessUnderRoot(procPath, localMySqlDir);
                        }
                        catch
                        {
                            // Image path unreadable: ownership cannot be proven, so this process is left alone.
                            isOurProcess = false;
                        }
```

- [ ] **Step 8: Build and run all tests**

Run: `dotnet build XamppUpdate/XamppUpdate.csproj -v q` then `dotnet test XamppUpdate.Tests`
Expected: `Build succeeded.`, tests pass.

- [ ] **Step 9: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "fix: path-segment-safe process matching, never kill unreadable processes"
```

---

### Task 3: UatIniRewriter (pure ini operations)

**Files:**
- Create: `XamppUpdate/Services/Uat/UatIniRewriter.cs`
- Test: `XamppUpdate.Tests/UatIniRewriterTests.cs`

**Interfaces:**
- Consumes: `PathSafety.IsSameOrUnder` (Task 2).
- Produces:
  - `UatIniRewriter.RewriteRoot(string content, string oldRoot, string newRoot) -> string`
  - `UatIniRewriter.FindRemainingRoot(string content, string oldRoot) -> IReadOnlyList<string>` (non-comment lines)
  - `UatIniRewriter.SetOption(string content, string section, string key, string value) -> string`
  - `UatIniRewriter.GetOption(string content, string section, string key) -> string?`
  - `UatIniRewriter.CommentOutOptions(string content, string section, params string[] keys) -> string`
  - `UatIniRewriter.ContainsInclude(string content) -> bool`
  - `UatIniRewriter.FindExternalPaths(string content, string allowedRoot) -> IReadOnlyList<string>`
  - `UatIniRewriter.RedactSecrets(string content) -> string`

- [ ] **Step 1: Write the failing tests** `XamppUpdate.Tests/UatIniRewriterTests.cs`

```csharp
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class UatIniRewriterTests
{
    private const string Old = @"C:\xampp\mysql";
    private const string New = @"C:\xampp\mysql_copy_upgrade";

    [Theory]
    [InlineData(@"basedir=C:\xampp\mysql", @"basedir=C:\xampp\mysql_copy_upgrade")]
    [InlineData(@"datadir=C:/xampp/mysql/data", @"datadir=C:/xampp/mysql_copy_upgrade/data")]
    [InlineData(@"datadir=""C:\\xampp\\mysql\\data""", @"datadir=""C:\\xampp\\mysql_copy_upgrade\\data""")]
    [InlineData(@"log-error=c:\XAMPP\MYSQL\data\err.log", @"log-error=C:\xampp\mysql_copy_upgrade\data\err.log")]
    public void RewriteRoot_ReplacesAllForms(string input, string expected)
    {
        Assert.Equal(expected, UatIniRewriter.RewriteRoot(input, Old, New));
    }

    [Theory]
    [InlineData(@"backup=C:\xampp\mysql_old\data")]
    [InlineData(@"x=C:\xampp\mysqlbackup")]
    [InlineData(@"x=C:\xampp\mysql.bak\data")]
    public void RewriteRoot_DoesNotTouchSiblingNames(string input)
    {
        Assert.Equal(input, UatIniRewriter.RewriteRoot(input, Old, New));
    }

    [Fact]
    public void RewriteRoot_IsIdempotent()
    {
        string once = UatIniRewriter.RewriteRoot(@"basedir=C:\xampp\mysql", Old, New);
        Assert.Equal(once, UatIniRewriter.RewriteRoot(once, Old, New));
    }

    [Fact]
    public void RewriteRoot_HandlesRootsWithSpaces()
    {
        string result = UatIniRewriter.RewriteRoot(@"basedir=C:\xampp test\mysql", @"C:\xampp test\mysql", @"C:\xampp test\mysql copy");
        Assert.Equal(@"basedir=C:\xampp test\mysql copy", result);
    }

    [Fact]
    public void FindRemainingRoot_IgnoresCommentsAndSiblings()
    {
        string ini = "# basedir=C:\\xampp\\mysql\r\n; old C:\\xampp\\mysql\r\nx=C:\\xampp\\mysql_old\r\ndatadir=C:\\xampp\\mysql\\data\r\n";
        var found = UatIniRewriter.FindRemainingRoot(ini, Old);
        Assert.Single(found);
        Assert.Contains("datadir", found[0]);
    }

    [Fact]
    public void SetOption_ReplacesExistingActiveKeyInSection()
    {
        string ini = "[client]\nport=3306\n[mysqld]\nport=3306\nsocket=x\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.Equal("[client]\nport=3306\n[mysqld]\nport=13306\nsocket=x\n", result);
    }

    [Fact]
    public void SetOption_AddsKeyWhenMissingAndCreatesSectionWhenAbsent()
    {
        string withSection = UatIniRewriter.SetOption("[mysqld]\nport=3306\n", "mysqld", "bind-address", "127.0.0.1");
        Assert.Equal("127.0.0.1", UatIniRewriter.GetOption(withSection, "mysqld", "bind-address"));

        string noClient = UatIniRewriter.SetOption("[mysqld]\nport=3306\n", "client", "port", "13306");
        Assert.Equal("13306", UatIniRewriter.GetOption(noClient, "client", "port"));
        Assert.Equal("3306", UatIniRewriter.GetOption(noClient, "mysqld", "port"));
    }

    [Fact]
    public void SetOption_PreservesCrLfAndBom()
    {
        string ini = "\uFEFF[mysqld]\r\nport=3306\r\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.StartsWith("\uFEFF[mysqld]", result);
        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\r\n\r\n", result);
        Assert.Equal("13306", UatIniRewriter.GetOption(result, "mysqld", "port"));
    }

    [Fact]
    public void SetOption_IgnoresCommentedKeys()
    {
        string ini = "[mysqld]\n# port=3306\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.Equal("13306", UatIniRewriter.GetOption(result, "mysqld", "port"));
        Assert.Contains("# port=3306", result);
    }

    [Fact]
    public void CommentOutOptions_DisablesNamedPipeAndSocket()
    {
        string ini = "[mysqld]\nsocket=\"C:/x/mysql.sock\"\nenable-named-pipe\nport=1\n";
        string result = UatIniRewriter.CommentOutOptions(ini, "mysqld", "socket", "enable-named-pipe");
        Assert.Null(UatIniRewriter.GetOption(result, "mysqld", "socket"));
        Assert.Equal("1", UatIniRewriter.GetOption(result, "mysqld", "port"));
    }

    [Theory]
    [InlineData("!include C:/x.cnf", true)]
    [InlineData("  !includedir C:/conf.d", true)]
    [InlineData("# !include C:/x.cnf", false)]
    [InlineData("[mysqld]\nport=3306", false)]
    public void ContainsInclude_DetectsActiveIncludes(string ini, bool expected)
    {
        Assert.Equal(expected, UatIniRewriter.ContainsInclude(ini));
    }

    [Fact]
    public void FindExternalPaths_FlagsWritablePathsOutsideAllowedRoot()
    {
        string ini = "[mysqld]\ndatadir=C:/xampp/mysql_copy_upgrade/data\ntmpdir=D:/tmp\nlog-error=C:/xampp/mysql/data/err.log\nport=1\n";
        var external = UatIniRewriter.FindExternalPaths(ini, @"C:\xampp\mysql_copy_upgrade");
        Assert.Equal(2, external.Count);
        Assert.Contains(external, e => e.Contains("tmpdir"));
        Assert.Contains(external, e => e.Contains("log-error"));
    }

    [Fact]
    public void RedactSecrets_MasksPasswords()
    {
        string result = UatIniRewriter.RedactSecrets("[client]\npassword = s3cr3t!\nuser=root\n");
        Assert.DoesNotContain("s3cr3t", result);
        Assert.Contains("user=root", result);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter UatIniRewriterTests`
Expected: FAIL (`UatIniRewriter` not defined).

- [ ] **Step 3: Implement** `XamppUpdate/Services/Uat/UatIniRewriter.cs`

```csharp
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter UatIniRewriterTests`
Expected: PASS. If `RewriteRoot_ReplacesAllForms` fails on the double-backslash case, confirm the regex separator group is `(?:\\{1,2}|/)` (matches one or two backslashes, or a slash).

- [ ] **Step 5: Commit** (only if asked)

```bash
git add XamppUpdate/Services/Uat/UatIniRewriter.cs XamppUpdate.Tests/UatIniRewriterTests.cs
git commit -m "feat: UatIniRewriter pure ini operations"
```

---

### Task 4: Port probe, UAT models, filesystem seam interface, preflight

**Files:**
- Create: `XamppUpdate/Services/Uat/UatPortProbe.cs`
- Create: `XamppUpdate/Services/Uat/UatModels.cs`
- Create: `XamppUpdate/Services/Uat/IUatFileSystem.cs`
- Create: `XamppUpdate/Services/Uat/UatPreflight.cs`
- Test: `XamppUpdate.Tests/UatPreflightTests.cs`

**Interfaces:**
- Consumes: `PathSafety`, `UatIniRewriter.ContainsInclude`.
- Produces: all types named in the File Structure for `UatModels.cs`; `IUatFileSystem` (below); `UatPreflight.Validate(UatOptions o, IUatFileSystem fs, Func<int,bool> isPortFree) -> IReadOnlyList<UatPreflightIssue>`; `UatPreflight.RequiredBytes(long productionBytes, long incomingBytes) -> long`.

- [ ] **Step 1: Create the models** `XamppUpdate/Services/Uat/UatModels.cs`

```csharp
namespace XamppUpdate.Services.Uat;

public sealed record UatOptions
{
    public required string ProductionRoot { get; init; }
    public required string ProductionIniPath { get; init; }
    public required string ProductionServiceName { get; init; }
    public int ProductionPort { get; init; } = 3306;
    public required string IncomingRoot { get; init; }
    public string IncomingVersion { get; init; } = string.Empty;
    public required string TargetRoot { get; init; }
    public int Port { get; init; } = 13306;
    public bool StopProductionFirst { get; init; } = true;
    public bool TestAsService { get; init; } = true;
    public string ServiceName { get; init; } = "mysql_uat";
    public string AdminUser { get; init; } = "root";
    public string AdminPassword { get; init; } = string.Empty;
}

public sealed record UatPreflightIssue(string Code, string Message);

public enum UatStageStatus { Pending, Running, Ok, Failed, Cancelled, Skipped }

public sealed class UatStageResult
{
    public int Number { get; init; }
    public string Name { get; init; } = string.Empty;
    public UatStageStatus Status { get; set; } = UatStageStatus.Pending;
    public string Message { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

public sealed record UatEvent(int StageNumber, string StageName, UatStageStatus Status, string Message);

public sealed class UatReport
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public List<UatStageResult> Stages { get; } = new();
    public bool Success { get; set; }
    public bool ProductionRestored { get; set; } = true;
    public string ProductionState { get; set; } = string.Empty;
    public string RecoveryInstructions { get; set; } = string.Empty;
    public List<string> OwnedPaths { get; } = new();
    public string OwnedServiceName { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public List<string> Notes { get; } = new();
}

public sealed record MySqlTarget(string Root, string IniPath, int Port);

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Combined => (StdOut + Environment.NewLine + StdErr).Trim();
}
```

- [ ] **Step 2: Create the filesystem seam** `XamppUpdate/Services/Uat/IUatFileSystem.cs`

```csharp
namespace XamppUpdate.Services.Uat;

/// <summary>All filesystem access the UAT run needs, so orchestration can be tested with a fake.</summary>
public interface IUatFileSystem
{
    bool PathExists(string path);
    long GetDirectorySize(string path);
    long GetFreeBytes(string path);
    IReadOnlyList<string> FindReparsePoints(string root);
    IReadOnlyList<string> FindConfigFiles(string root);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    void CreateDirectory(string path);
    void MoveDirectory(string source, string destination);
    void DeleteTree(string path);
    Task CopyTreeAsync(string source, string destination, IProgress<string>? progress, CancellationToken ct);
    /// <summary>Relative path (lower-cased, '/' separators) to file length.</summary>
    Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string root, CancellationToken ct);
    /// <summary>Deploys the incoming package (excluding data\) into <paramref name="destRoot"/>; returns files copied.</summary>
    Task<int> DeployNewFilesAsync(string incomingRoot, string destRoot, CancellationToken ct);
    /// <summary>Moves/copies the data folder; false when the source does not exist.</summary>
    Task<bool> MigrateDataAsync(string srcDataDir, string destDataDir, CancellationToken ct);
}
```

- [ ] **Step 3: Write the failing tests** `XamppUpdate.Tests/UatPreflightTests.cs`

```csharp
using System.Net;
using System.Net.Sockets;
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class UatPreflightTests
{
    private sealed class FakeFs : IUatFileSystem
    {
        public HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase);
        public long DirSize = 100;
        public long Free = 10_000;
        public List<string> Reparse = new();
        public string ProductionIni = "[mysqld]\nport=3306\n";

        public bool PathExists(string path) => Existing.Contains(path.TrimEnd('\\'));
        public long GetDirectorySize(string path) => DirSize;
        public long GetFreeBytes(string path) => Free;
        public IReadOnlyList<string> FindReparsePoints(string root) => Reparse;
        public IReadOnlyList<string> FindConfigFiles(string root) => Array.Empty<string>();
        public string ReadAllText(string path) => ProductionIni;
        public void WriteAllText(string path, string content) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void MoveDirectory(string source, string destination) => throw new NotSupportedException();
        public void DeleteTree(string path) => throw new NotSupportedException();
        public Task CopyTreeAsync(string s, string d, IProgress<string>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string root, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> DeployNewFilesAsync(string i, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> MigrateDataAsync(string s, string d, CancellationToken ct) => throw new NotSupportedException();
    }

    private static UatOptions Opts(string target = @"C:\xampp\mysql_copy_upgrade", int port = 13306) => new()
    {
        ProductionRoot = @"C:\xampp\mysql",
        ProductionIniPath = @"C:\xampp\mysql\bin\my.ini",
        ProductionServiceName = "mysql",
        ProductionPort = 3306,
        IncomingRoot = @"C:\temp\incoming",
        TargetRoot = target,
        Port = port,
    };

    [Fact]
    public void ValidOptions_HaveNoIssues()
    {
        var issues = UatPreflight.Validate(Opts(), new FakeFs(), _ => true);
        Assert.Empty(issues);
    }

    [Fact]
    public void ExistingTarget_StopsWithClearMessage()
    {
        var fs = new FakeFs();
        fs.Existing.Add(@"C:\xampp\mysql_copy_upgrade");
        var issues = UatPreflight.Validate(Opts(), fs, _ => true);
        var issue = Assert.Single(issues, i => i.Code == "TargetExists");
        Assert.Contains("Target folder already exists", issue.Message);
        Assert.Contains("Nothing was changed", issue.Message);
    }

    [Fact]
    public void ExistingTarget_IsDetectedWithTrailingSeparatorAndDifferentCase()
    {
        var fs = new FakeFs();
        fs.Existing.Add(@"C:\xampp\mysql_copy_upgrade");
        var issues = UatPreflight.Validate(Opts(@"C:\XAMPP\MYSQL_COPY_UPGRADE\"), fs, _ => true);
        Assert.Contains(issues, i => i.Code == "TargetExists");
    }

    [Theory]
    [InlineData(@"C:\xampp\mysql")]
    [InlineData(@"C:\xampp\mysql\uat")]
    [InlineData(@"C:\xampp")]
    [InlineData(@"C:\")]
    public void TargetOverlappingProduction_IsRejected(string target)
    {
        var issues = UatPreflight.Validate(Opts(target), new FakeFs(), _ => true);
        Assert.Contains(issues, i => i.Code is "TargetOverlapsProduction" or "TargetIsRoot");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(3306)]
    public void BadPorts_AreRejected(int port)
    {
        var issues = UatPreflight.Validate(Opts(port: port), new FakeFs(), _ => true);
        Assert.Contains(issues, i => i.Code is "PortInvalid" or "PortSameAsProduction");
    }

    [Fact]
    public void BusyPort_IsRejected()
    {
        var issues = UatPreflight.Validate(Opts(), new FakeFs(), _ => false);
        Assert.Contains(issues, i => i.Code == "PortBusy");
    }

    [Fact]
    public void InsufficientDisk_IsRejected()
    {
        var fs = new FakeFs { DirSize = 1000, Free = 1000 };
        var issues = UatPreflight.Validate(Opts(), fs, _ => true);
        Assert.Contains(issues, i => i.Code == "DiskSpace");
    }

    [Fact]
    public void RequiredBytes_IsProductionPlusMarginPlusIncoming()
    {
        Assert.Equal(1200 + 50, UatPreflight.RequiredBytes(1000, 50));
    }

    [Fact]
    public void LinkedSourceContent_IsRejected()
    {
        var fs = new FakeFs();
        fs.Reparse.Add(@"C:\xampp\mysql\data\link");
        Assert.Contains(UatPreflight.Validate(Opts(), fs, _ => true), i => i.Code == "LinkedContent");
    }

    [Fact]
    public void IncludeInProductionIni_IsRejectedInPhase1()
    {
        var fs = new FakeFs { ProductionIni = "!include C:/extra.cnf\n[mysqld]\n" };
        Assert.Contains(UatPreflight.Validate(Opts(), fs, _ => true), i => i.Code == "IncludeUnsupported");
    }

    [Fact]
    public void LiveCopy_IsNotSupportedInPhase1()
    {
        var o = Opts() with { StopProductionFirst = false };
        Assert.Contains(UatPreflight.Validate(o, new FakeFs(), _ => true), i => i.Code == "LiveCopyNotSupported");
    }

    [Fact]
    public void ServiceNameEqualToProduction_IsRejected()
    {
        var o = Opts() with { ServiceName = "mysql" };
        Assert.Contains(UatPreflight.Validate(o, new FakeFs(), _ => true), i => i.Code == "ServiceNameCollision");
    }

    [Fact]
    public void PortProbe_DetectsOccupiedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.False(UatPortProbe.IsPortFree(port));
        }
        finally
        {
            listener.Stop();
        }
        Assert.True(UatPortProbe.IsPortFree(port));
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter UatPreflightTests`
Expected: FAIL (`UatPreflight`/`UatPortProbe` not defined).

- [ ] **Step 5: Implement the port probe** `XamppUpdate/Services/Uat/UatPortProbe.cs`

```csharp
using System.Net;
using System.Net.Sockets;

namespace XamppUpdate.Services.Uat;

public static class UatPortProbe
{
    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>Exclusive bind on IPv4 loopback and any-address; the probe sockets are released before returning.</summary>
    public static bool IsPortFree(int port)
    {
        if (!IsValidPort(port)) return false;

        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any })
        {
            try
            {
                var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
                listener.Start();
                listener.Stop();
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }
}
```

- [ ] **Step 6: Implement preflight** `XamppUpdate/Services/Uat/UatPreflight.cs`

```csharp
namespace XamppUpdate.Services.Uat;

public static class UatPreflight
{
    public static long RequiredBytes(long productionBytes, long incomingBytes) =>
        (long)(productionBytes * 1.2) + incomingBytes;

    public static IReadOnlyList<UatPreflightIssue> Validate(UatOptions o, IUatFileSystem fs, Func<int, bool> isPortFree)
    {
        var issues = new List<UatPreflightIssue>();
        void Add(string code, string message) => issues.Add(new UatPreflightIssue(code, message));

        if (!o.StopProductionFirst)
            Add("LiveCopyNotSupported", "Copying while production runs is not supported yet. Enable \"Stop production MySQL before copy\".");

        // ---- paths ----
        if (string.IsNullOrWhiteSpace(o.TargetRoot))
        {
            Add("TargetEmpty", "Target folder is empty.");
        }
        else
        {
            string target = PathSafety.Canonical(o.TargetRoot);
            string? rootOfTarget = Path.GetPathRoot(target);

            if (rootOfTarget != null && target.Equals(PathSafety.Canonical(rootOfTarget), StringComparison.OrdinalIgnoreCase))
                Add("TargetIsRoot", "Target folder cannot be a drive root.");

            if (PathSafety.Overlaps(target, o.ProductionRoot))
                Add("TargetOverlapsProduction", $"Target folder overlaps the production folder: {target}");

            if (PathSafety.Overlaps(target, o.IncomingRoot))
                Add("TargetOverlapsIncoming", $"Target folder overlaps the incoming package folder: {target}");

            if (fs.PathExists(target))
                Add("TargetExists", $"Target folder already exists: {target}. Nothing was changed.");
        }

        // ---- port ----
        if (!UatPortProbe.IsValidPort(o.Port))
            Add("PortInvalid", $"Port {o.Port} is not valid (1-65535).");
        else if (o.Port == o.ProductionPort)
            Add("PortSameAsProduction", $"Port {o.Port} is production's port. Choose a different port.");
        else if (!isPortFree(o.Port))
            Add("PortBusy", $"Port {o.Port} is already in use. Choose a different port.");

        // ---- service ----
        if (o.TestAsService)
        {
            if (string.IsNullOrWhiteSpace(o.ServiceName))
                Add("ServiceNameEmpty", "Service name is empty.");
            else if (string.Equals(o.ServiceName, o.ProductionServiceName, StringComparison.OrdinalIgnoreCase))
                Add("ServiceNameCollision", $"Service name '{o.ServiceName}' is the production service. Choose another name.");
        }

        // ---- production source ----
        var reparse = fs.FindReparsePoints(o.ProductionRoot);
        if (reparse.Count > 0)
            Add("LinkedContent", $"Production folder contains links/junctions (e.g. {reparse[0]}); not supported.");

        if (!string.IsNullOrWhiteSpace(o.ProductionIniPath) && fs.PathExists(o.ProductionIniPath))
        {
            if (UatIniRewriter.ContainsInclude(fs.ReadAllText(o.ProductionIniPath)))
                Add("IncludeUnsupported", "The production ini uses !include/!includedir, which is not supported in this version.");
        }

        // ---- disk ----
        if (!string.IsNullOrWhiteSpace(o.TargetRoot))
        {
            long need = RequiredBytes(fs.GetDirectorySize(o.ProductionRoot), fs.GetDirectorySize(o.IncomingRoot));
            long free = fs.GetFreeBytes(o.TargetRoot);
            if (free < need)
                Add("DiskSpace", $"Not enough free disk space on the target volume: need about {need:N0} bytes, free {free:N0}.");
        }

        return issues;
    }
}
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter UatPreflightTests`
Expected: PASS.

- [ ] **Step 8: Commit** (only if asked)

```bash
git add XamppUpdate/Services/Uat XamppUpdate.Tests/UatPreflightTests.cs
git commit -m "feat: UAT models, port probe and stage-1 preflight"
```

---

### Task 5: Strict shutdown report with fresh log evidence

**Files:**
- Create: `XamppUpdate/Services/Uat/ErrorLogEvidence.cs`
- Modify: `XamppUpdate/Services/IWindowsServiceManager.cs`
- Modify: `XamppUpdate/Services/WindowsServiceManager.cs` (`SafeShutdownMySqlAsync`)
- Test: `XamppUpdate.Tests/ErrorLogEvidenceTests.cs`

**Interfaces:**
- Produces:
  - `ErrorLogEvidence.Capture(string mySqlPath) -> (string? Path, long Length)`
  - `ErrorLogEvidence.ShutdownCompleteAfter(string? logPath, long offset) -> bool`
  - `ErrorLogEvidence.ContainsShutdownComplete(string text) -> bool`
  - `class MySqlShutdownReport` with `WasRunning, LoggedIn, FlushVerified, StopRequested, ProcessExited, FreshShutdownEvidence, Aborted, StrictSuccess`
  - `IWindowsServiceManager.SafeShutdownMySqlDetailedAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null) -> Task<MySqlShutdownReport>`
  - `SafeShutdownMySqlAsync` keeps its signature and production meaning (`true` = process gone and nothing aborted).

- [ ] **Step 1: Write the failing tests** `XamppUpdate.Tests/ErrorLogEvidenceTests.cs`

```csharp
using XamppUpdate.Services;
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class ErrorLogEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uat_log_" + Guid.NewGuid().ToString("N"));

    public ErrorLogEvidenceTests() => Directory.CreateDirectory(Path.Combine(_root, "data"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("2026-10-12 [Note] InnoDB: Shutdown completed; log sequence number 1", true)]
    [InlineData("mysqld.exe: Shutdown complete", true)]
    [InlineData("[ERROR] InnoDB: Upgrade after a crash is not supported", false)]
    public void ContainsShutdownComplete_MatchesVariants(string text, bool expected)
    {
        Assert.Equal(expected, ErrorLogEvidence.ContainsShutdownComplete(text));
    }

    [Fact]
    public void ShutdownCompleteAfter_IgnoresLinesBeforeTheOffset()
    {
        string log = Path.Combine(_root, "data", "host.err");
        File.WriteAllText(log, "old run\nmysqld.exe: Shutdown complete\n");

        var (path, length) = ErrorLogEvidence.Capture(_root);
        Assert.Equal(log, path);
        Assert.False(ErrorLogEvidence.ShutdownCompleteAfter(path, length));

        File.AppendAllText(log, "new run\nmysqld.exe: Shutdown complete\n");
        Assert.True(ErrorLogEvidence.ShutdownCompleteAfter(path, length));
    }

    [Fact]
    public void StrictSuccess_RequiresEveryPhase()
    {
        var ok = new MySqlShutdownReport
        {
            WasRunning = true, LoggedIn = true, FlushVerified = true,
            StopRequested = true, ProcessExited = true, FreshShutdownEvidence = true
        };
        Assert.True(ok.StrictSuccess);

        Assert.False(new MySqlShutdownReport
        {
            WasRunning = true, LoggedIn = true, FlushVerified = false,
            StopRequested = true, ProcessExited = true, FreshShutdownEvidence = true
        }.StrictSuccess);

        Assert.False(new MySqlShutdownReport
        {
            WasRunning = true, LoggedIn = true, FlushVerified = true,
            StopRequested = true, ProcessExited = true, FreshShutdownEvidence = false
        }.StrictSuccess);

        Assert.True(new MySqlShutdownReport { WasRunning = false }.StrictSuccess);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter ErrorLogEvidenceTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement** `XamppUpdate/Services/Uat/ErrorLogEvidence.cs`

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace XamppUpdate.Services.Uat;

public static class ErrorLogEvidence
{
    private static readonly Regex ShutdownComplete =
        new(@"Shutdown complete", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsShutdownComplete(string text) => ShutdownComplete.IsMatch(text);

    private static FileInfo? NewestLog(string mySqlPath)
    {
        string dataDir = Path.Combine(mySqlPath, "data");
        if (!Directory.Exists(dataDir)) return null;

        var dir = new DirectoryInfo(dataDir);
        return dir.EnumerateFiles("*.err")
            .Concat(dir.EnumerateFiles("mysql_error.log"))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Remember where the log ends right now, so later evidence can be proven fresh.</summary>
    public static (string? Path, long Length) Capture(string mySqlPath)
    {
        try
        {
            var log = NewestLog(mySqlPath);
            return log == null ? (null, 0) : (log.FullName, log.Length);
        }
        catch
        {
            return (null, 0);
        }
    }

    public static bool ShutdownCompleteAfter(string? logPath, long offset)
    {
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return false;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= offset) return false;

            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return ContainsShutdownComplete(sr.ReadToEnd());
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Add the report type and interface method** in `IWindowsServiceManager.cs`

Add inside `namespace XamppUpdate.Services` (above the interface):

```csharp
    public sealed class MySqlShutdownReport
    {
        public bool WasRunning { get; init; }
        public bool LoggedIn { get; init; }
        /// <summary>SET GLOBAL innodb_fast_shutdown=0 succeeded and the read-back value is 0.</summary>
        public bool FlushVerified { get; init; }
        public bool StopRequested { get; init; }
        public bool ProcessExited { get; init; }
        /// <summary>"Shutdown complete" appeared in the error log AFTER this shutdown began.</summary>
        public bool FreshShutdownEvidence { get; init; }
        /// <summary>Flush could not be applied; MySQL was deliberately left running.</summary>
        public bool Aborted { get; init; }

        public bool StrictSuccess =>
            !WasRunning ||
            (LoggedIn && FlushVerified && StopRequested && ProcessExited && FreshShutdownEvidence && !Aborted);
    }
```

and in the interface, next to `SafeShutdownMySqlAsync`:

```csharp
        Task<MySqlShutdownReport> SafeShutdownMySqlDetailedAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null);
```

- [ ] **Step 5: Refactor the implementation** in `WindowsServiceManager.cs`

Rename the existing `SafeShutdownMySqlAsync` method to `SafeShutdownMySqlDetailedAsync`, change its return type to `Task<MySqlShutdownReport>`, and apply these edits inside it:

1. Before phase 1 add: `var logMark = XamppUpdate.Services.Uat.ErrorLogEvidence.Capture(mySqlPath); string? originalFlush = null; bool flushChanged = false; bool stopRequested = false; bool aborted = false;`
2. When the login succeeds set `originalFlush = ExtractScalar(output);`
3. After `flushState = "OK";` add `flushChanged = true;`
4. Replace `return false;` in the flush-failure branch with:
```csharp
                    aborted = true;
                    return new MySqlShutdownReport { WasRunning = running, LoggedIn = loggedIn, FlushVerified = false, Aborted = true };
```
5. Where the service stop succeeds or `shutdownRequested` is true set `stopRequested = true;`.
6. Replace the final still-running branch:
```csharp
            if (IsMySqlProcessRunning(mySqlPath))
            {
                Log(pStop, "FAILED: MySQL is still running after the graceful shutdown timeout. Nothing was killed.");
                // Production stayed up: put the runtime flush setting back the way we found it.
                if (flushChanged && client != null && !string.IsNullOrEmpty(originalFlush))
                {
                    await RunToolWithPasswordAsync(client,
                        $"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -e \"SET GLOBAL innodb_fast_shutdown={originalFlush}\"",
                        password, Path.GetDirectoryName(client));
                }
                return new MySqlShutdownReport { WasRunning = running || serviceExists, LoggedIn = loggedIn, FlushVerified = flushState == "OK", StopRequested = stopRequested };
            }
```
7. Replace the verify block and final `return true;` with:
```csharp
            bool fresh = XamppUpdate.Services.Uat.ErrorLogEvidence.ShutdownCompleteAfter(logMark.Path, logMark.Length);
            Log(pVerify, fresh
                ? "OK: error log reports 'Shutdown complete' for this shutdown."
                : "UNVERIFIED: no fresh 'Shutdown complete' line was written to the error log during this shutdown.");

            Log("SHUTDOWN SUMMARY", $"login={(loggedIn ? "OK" : "-")}  flush(innodb_fast_shutdown=0)={flushState}  stop=OK  log={(fresh ? "OK" : "unverified")}");
            return new MySqlShutdownReport
            {
                WasRunning = true, LoggedIn = loggedIn, FlushVerified = flushState == "OK",
                StopRequested = stopRequested, ProcessExited = true, FreshShutdownEvidence = fresh, Aborted = aborted
            };
```
8. In the early "not running" return use `return new MySqlShutdownReport { WasRunning = false };`.
9. Prefix every tool argument string in this method with `--no-defaults ` (the first option), e.g. `$"--no-defaults -u \"{effectiveUser}\" -h 127.0.0.1 -P {port} ..."`.

Then add the compatibility wrapper next to it:

```csharp
        public async Task<bool> SafeShutdownMySqlAsync(string serviceName, string mySqlPath, string user, string password, int port = 3306, IProgress<string>? progress = null)
        {
            var report = await SafeShutdownMySqlDetailedAsync(serviceName, mySqlPath, user, password, port, progress);
            // Production semantics unchanged: true when nothing was aborted and no process remains.
            return !report.Aborted && (!report.WasRunning || report.ProcessExited);
        }
```

- [ ] **Step 6: Build and run tests**

Run: `dotnet build XamppUpdate/XamppUpdate.csproj -v q` then `dotnet test XamppUpdate.Tests --filter ErrorLogEvidenceTests`
Expected: `Build succeeded.`, PASS.

- [ ] **Step 7: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "feat: structured shutdown report with fresh log evidence"
```

---

### Task 6: Target-explicit tool runner and owned-service operations

**Files:**
- Create: `XamppUpdate/Services/Uat/MySqlToolRunner.cs`
- Modify: `XamppUpdate/Services/IWindowsServiceManager.cs`
- Modify: `XamppUpdate/Services/WindowsServiceManager.cs`
- Test: `XamppUpdate.Tests/MySqlToolRunnerTests.cs`

**Interfaces:**
- Consumes: `MySqlTarget`, `ToolResult` (Task 4), `PathSafety` (Task 2).
- Produces:
  - `MySqlToolArgs.Query(string user, int port, string sql) -> string`
  - `MySqlToolArgs.Upgrade(string user, int port) -> string`
  - `UatIdentity.Parse(string stdout) -> UatIdentity?` with `Version`, `Port`, `BaseDir`, `DataDir`; `UatIdentity.Matches(int expectedPort, string expectedRoot) -> bool`
  - `interface IMySqlToolRunner { Task<ToolResult> QueryAsync(MySqlTarget t, string user, string password, string sql, CancellationToken ct); Task<ToolResult> UpgradeAsync(MySqlTarget t, string user, string password, CancellationToken ct); }`
  - `ServiceImagePath.ExtractExecutable(string imagePath) -> string`
  - `IWindowsServiceManager.GetServiceImagePath(string) -> string?`
  - `IWindowsServiceManager.InstallOwnedMySqlServiceManualAsync(string mySqlPath, string serviceName, string iniPath, IProgress<string>? progress = null) -> Task<bool>`
  - `IWindowsServiceManager.RemoveServiceIfOwnedAsync(string serviceName, string expectedRoot, IProgress<string>? progress = null) -> Task<bool>`

- [ ] **Step 1: Write the failing tests** `XamppUpdate.Tests/MySqlToolRunnerTests.cs`

```csharp
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class MySqlToolRunnerTests
{
    [Fact]
    public void QueryArgs_StartWithNoDefaultsAndTargetExplicitPort()
    {
        string args = MySqlToolArgs.Query("root", 13306, "SELECT 1");
        Assert.StartsWith("--no-defaults ", args);
        Assert.Contains("-h 127.0.0.1", args);
        Assert.Contains("-P 13306", args);
        Assert.Contains("-e \"SELECT 1\"", args);
    }

    [Fact]
    public void QueryArgs_NeverContainThePassword()
    {
        // The password is delivered through MYSQL_PWD, so no argument builder even accepts it.
        string args = MySqlToolArgs.Query("root", 13306, "SELECT 1");
        Assert.DoesNotContain("--password", args);
        Assert.DoesNotContain(" -p", args);   // lower-case -p is the password flag; -P (port) is upper-case
    }

    [Fact]
    public void QueryArgs_EscapeDoubleQuotesInSql()
    {
        string args = MySqlToolArgs.Query("root", 1, "SELECT \"a\"");
        Assert.Contains("-e \"SELECT \\\"a\\\"\"", args);
    }

    [Fact]
    public void UpgradeArgs_ForceAndExplicitEndpoint()
    {
        string args = MySqlToolArgs.Upgrade("root", 13306);
        Assert.StartsWith("--no-defaults ", args);
        Assert.Contains("--force", args);
        Assert.Contains("-P 13306", args);
    }

    [Fact]
    public void Identity_ParsesTabSeparatedRow()
    {
        string stdout = "10.6.28-MariaDB\t13306\tC:\\xampp\\mysql_copy_upgrade\\\tC:\\xampp\\mysql_copy_upgrade\\data\\\r\n";
        var id = UatIdentity.Parse(stdout);
        Assert.NotNull(id);
        Assert.Equal("10.6.28-MariaDB", id!.Version);
        Assert.Equal(13306, id.Port);
        Assert.True(id.Matches(13306, @"C:\xampp\mysql_copy_upgrade"));
        Assert.False(id.Matches(3306, @"C:\xampp\mysql_copy_upgrade"));
        Assert.False(id.Matches(13306, @"C:\xampp\mysql"));
    }

    [Fact]
    public void Identity_IgnoresWarningLinesAroundTheRow()
    {
        string stdout = "WARNING: option --ssl-verify-server-cert is disabled\r\n10.11.19-MariaDB\t13306\tD:/m/\tD:/m/data/\r\n";
        var id = UatIdentity.Parse(stdout);
        Assert.NotNull(id);
        Assert.Equal("10.11.19-MariaDB", id!.Version);
        Assert.True(id.Matches(13306, @"D:\m"));
    }

    [Theory]
    [InlineData("\"C:\\xampp\\mysql_copy_upgrade\\bin\\mysqld.exe\" --defaults-file=\"C:\\xampp\\mysql_copy_upgrade\\bin\\my.ini\" mysql_uat",
                "C:\\xampp\\mysql_copy_upgrade\\bin\\mysqld.exe")]
    [InlineData("C:\\xampp\\mysql\\bin\\mysqld.exe --defaults-file=x mysql", "C:\\xampp\\mysql\\bin\\mysqld.exe")]
    public void ServiceImagePath_ExtractsExecutable(string imagePath, string expected)
    {
        Assert.Equal(expected, ServiceImagePath.ExtractExecutable(imagePath));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter MySqlToolRunnerTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement** `XamppUpdate/Services/Uat/MySqlToolRunner.cs`

```csharp
using System.Diagnostics;
using System.Globalization;

namespace XamppUpdate.Services.Uat;

public static class MySqlToolArgs
{
    public static string Query(string user, int port, string sql) =>
        $"--no-defaults -u \"{user}\" -h 127.0.0.1 -P {port} --connect-timeout=10 -N -B -e \"{sql.Replace("\"", "\\\"")}\"";

    public static string Upgrade(string user, int port) =>
        $"--no-defaults -u \"{user}\" -h 127.0.0.1 -P {port} --force --verbose";
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
            string why = ct.IsCancellationRequested ? "cancelled" : "timed out";
            return new ToolResult(-1, await stdout, $"{Path.GetFileName(exe)} {why}. {await stderr}".Trim());
        }

        return new ToolResult(proc.ExitCode, await stdout, await stderr);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter MySqlToolRunnerTests`
Expected: PASS.

- [ ] **Step 5: Add owned-service operations** to `IWindowsServiceManager.cs`

```csharp
        string? GetServiceImagePath(string serviceName);
        /// <summary>Installs a manual-start MySQL service only if the name is unused; verifies the registered executable.</summary>
        Task<bool> InstallOwnedMySqlServiceManualAsync(string mySqlPath, string serviceName, string iniPath, IProgress<string>? progress = null);
        /// <summary>Stops and removes the service only if its registered executable lives under <paramref name="expectedRoot"/>.</summary>
        Task<bool> RemoveServiceIfOwnedAsync(string serviceName, string expectedRoot, IProgress<string>? progress = null);
```

- [ ] **Step 6: Implement them** in `WindowsServiceManager.cs` (next to the other mode-aware helpers)

```csharp
        public string? GetServiceImagePath(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("ImagePath") as string;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> InstallOwnedMySqlServiceManualAsync(string mySqlPath, string serviceName, string iniPath, IProgress<string>? progress = null)
        {
            if (ServiceExists(serviceName))
            {
                progress?.Report($"FAILED: a service named '{serviceName}' already exists; it will not be reused or replaced.");
                return false;
            }

            string? daemon = FindMySqlDaemon(mySqlPath);
            if (daemon == null)
            {
                progress?.Report($"FAILED: mysqld/mariadbd not found under '{mySqlPath}'.");
                return false;
            }

            progress?.Report($"Running: {Path.GetFileName(daemon)} --install-manual {serviceName} --defaults-file=<copy ini>");
            var (code, output) = await RunProcessAsync(daemon, $"--install-manual \"{serviceName}\" --defaults-file=\"{iniPath}\"", mySqlPath);
            await Task.Delay(500);

            string? image = GetServiceImagePath(serviceName);
            bool owned = image != null &&
                         XamppUpdate.Services.Uat.PathSafety.IsSameOrUnder(XamppUpdate.Services.Uat.ServiceImagePath.ExtractExecutable(image), mySqlPath);
            if (!owned)
            {
                progress?.Report($"FAILED: service registration did not produce an executable under the copy (exit {code}: {output.Trim()}).");
                // Never leave a half-registered service behind.
                if (ServiceExists(serviceName) && image != null) await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
                return false;
            }

            progress?.Report($"OK: service '{serviceName}' registered for manual start.");
            return true;
        }

        public async Task<bool> RemoveServiceIfOwnedAsync(string serviceName, string expectedRoot, IProgress<string>? progress = null)
        {
            if (!ServiceExists(serviceName)) return true;

            string? image = GetServiceImagePath(serviceName);
            if (image == null || !XamppUpdate.Services.Uat.PathSafety.IsSameOrUnder(XamppUpdate.Services.Uat.ServiceImagePath.ExtractExecutable(image), expectedRoot))
            {
                progress?.Report($"REFUSED: service '{serviceName}' is not registered to '{expectedRoot}'. Remove it manually with: sc.exe delete \"{serviceName}\"");
                return false;
            }

            await StopServiceAsync(serviceName, TimeSpan.FromSeconds(60), progress);
            var (code, output) = await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"");
            await Task.Delay(500);

            bool removed = !ServiceExists(serviceName);
            progress?.Report(removed
                ? $"OK: service '{serviceName}' removed."
                : $"FAILED to remove '{serviceName}' (exit {code}: {output.Trim()}). Run: sc.exe delete \"{serviceName}\"");
            return removed;
        }
```

- [ ] **Step 7: Build and run all tests**

Run: `dotnet build XamppUpdate/XamppUpdate.csproj -v q` then `dotnet test XamppUpdate.Tests`
Expected: `Build succeeded.`, all pass.

- [ ] **Step 8: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "feat: target-explicit tool runner and owned service operations"
```

---

### Task 7: Extract the shared deploy helper

**Files:**
- Create: `XamppUpdate/Services/MySqlDeployHelper.cs`
- Modify: `XamppUpdate/Services/MySqlUpdateService.cs` (pipeline steps 4, 5, 6 real-mode branches)
- Test: `XamppUpdate.Tests/MySqlDeployHelperTests.cs`

**Interfaces:**
- Consumes: `ConfigDiffItem`, `UpdateProgressReport`, `ArchiveService.FormatBytes`.
- Produces (all `static`, real-mode only; simulation stays in the pipeline):
  - `MySqlDeployHelper.IsDataDirectory(string relativePath) -> bool`
  - `Task<int> MySqlDeployHelper.DeployNewFilesAsync(string incomingRoot, string destRoot, IProgress<UpdateProgressReport>? progress, CancellationToken ct)` returns files copied
  - `Task MySqlDeployHelper.ApplyConfigsAsync(IEnumerable<ConfigDiffItem> configs, string destRoot, IProgress<UpdateProgressReport>? progress, CancellationToken ct)`
  - `Task<bool> MySqlDeployHelper.MigrateDataAsync(string srcDataDir, string destDataDir, IProgress<UpdateProgressReport>? progress, CancellationToken ct)` returns true when data existed and was migrated

- [ ] **Step 1: Write the failing parity tests** `XamppUpdate.Tests/MySqlDeployHelperTests.cs`

```csharp
using XamppUpdate.Services;
using Xunit;

namespace XamppUpdate.Tests;

public class MySqlDeployHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uat_deploy_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Make(string rel, string content = "x")
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Theory]
    [InlineData("data", true)]
    [InlineData("data/ibdata1", true)]
    [InlineData(@"data\mysql\user.MAD", true)]
    [InlineData("bin/mysqld.exe", false)]
    [InlineData("database/x", false)]
    public void IsDataDirectory_MatchesProductionRule(string rel, bool expected)
    {
        Assert.Equal(expected, MySqlDeployHelper.IsDataDirectory(rel));
    }

    [Fact]
    public async Task Deploy_CopiesEverythingExceptDataAndLeavesNoStaleFiles()
    {
        Make(@"incoming\bin\mysqld.exe", "new");
        Make(@"incoming\share\x.txt");
        Make(@"incoming\data\ibdata1", "incoming-data-must-not-copy");
        string dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);

        int copied = await MySqlDeployHelper.DeployNewFilesAsync(Path.Combine(_root, "incoming"), dest, null, CancellationToken.None);

        Assert.Equal(2, copied);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "bin", "mysqld.exe")));
        Assert.False(File.Exists(Path.Combine(dest, "data", "ibdata1")));
        Assert.False(File.Exists(Path.Combine(dest, "bin", "stale.dll")));
    }

    [Fact]
    public async Task MigrateData_MovesDirectoryAndPreservesContent()
    {
        Make(@"old\data\ibdata1", "payload");
        string src = Path.Combine(_root, "old", "data");
        string dst = Path.Combine(_root, "new", "data");
        Directory.CreateDirectory(Path.Combine(_root, "new"));

        bool migrated = await MySqlDeployHelper.MigrateDataAsync(src, dst, null, CancellationToken.None);

        Assert.True(migrated);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(dst, "ibdata1")));
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public async Task MigrateData_MissingSourceReturnsFalse()
    {
        bool migrated = await MySqlDeployHelper.MigrateDataAsync(
            Path.Combine(_root, "nope"), Path.Combine(_root, "dst"), null, CancellationToken.None);
        Assert.False(migrated);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter MySqlDeployHelperTests`
Expected: FAIL (`MySqlDeployHelper` not defined).

- [ ] **Step 3: Implement** `XamppUpdate/Services/MySqlDeployHelper.cs`

This is the production code moved verbatim from `ExecuteUpdatePipelineAsync` steps 4-6 (real-mode branches). The only behavioural addition is the reparse-point exclusion already present in `enumOptions`.

```csharp
using System.IO;
using System.Text;
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
            var files = Directory.GetFiles(incomingRoot, "*.*", EnumOptions);
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

                await File.WriteAllTextAsync(destIni, content, new UTF8Encoding(false), ct);

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
                        StepTitle = "Migrating Database Data",
                        Message = $"[DATA WARNING] Direct move not available ({ex.Message}). Falling back to stream copy...",
                        Percentage = 83
                    });
                }
            }

            foreach (var file in Directory.GetFiles(srcDataDir, "*.*", EnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(srcDataDir, file);
                string dest = Path.Combine(destDataDir, rel);
                string? folder = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                File.Copy(file, dest, true);
            }

            await Task.CompletedTask;
            return true;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter MySqlDeployHelperTests`
Expected: PASS.

- [ ] **Step 5: Make the pipeline call the helper (real mode only)**

In `MySqlUpdateService.ExecuteUpdatePipelineAsync`:

1. STEP 4: keep the `isTestMode` simulate loop exactly as is. In the `else` (real mode) replace the whole `foreach` body's real branch with a single call placed before the loop when `!isTestMode`:
```csharp
                if (!isTestMode)
                {
                    await MySqlDeployHelper.DeployNewFilesAsync(incomingMySqlRoot, localMySqlDir, progress, cancellationToken);
                }
```
   and make the existing `foreach (var srcFile in allIncomingFiles)` loop run only when `isTestMode` (wrap it in `if (isTestMode)` and delete its `else { ... File.Copy ... }` branch).
2. STEP 5: in the `else` branch replace the body that writes `contentToWrite` with `await MySqlDeployHelper.ApplyConfigsAsync(new[] { diffItem }, localMySqlDir, progress, cancellationToken);`.
3. STEP 6: in the non-test branch replace the whole "Primary Approach ... Fallback Approach" region with:
```csharp
                        bool migrated = await MySqlDeployHelper.MigrateDataAsync(srcDataDir, destDataDir, progress, cancellationToken);
                        progress?.Report(new UpdateProgressReport
                        {
                            Step = UpdateStep.FileReplacement,
                            StepTitle = "Data Migration Complete",
                            Message = $"[DATA] Preserved database data directory '{destDataDir}'. Files were kept as-is; InnoDB consistency depends on MySQL having been shut down cleanly beforehand.",
                            Percentage = 90
                        });
```
4. Delete the now-unused private `IsDataDirectory` in `MySqlUpdateService` and change its remaining callers to `MySqlDeployHelper.IsDataDirectory`.

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build XamppUpdate/XamppUpdate.csproj -v q` then `dotnet test XamppUpdate.Tests`
Expected: `Build succeeded.`, all tests pass.

- [ ] **Step 7: Manual regression of the real pipeline** (cannot be unit tested)

On a disposable XAMPP copy run the normal wizard, step 3 in **test mode** then a real run. Expected: identical log lines for steps 4-6 as before this change.

Known, accepted difference: the data-migration **fallback** (used only when `Directory.Move` is not possible, e.g. across volumes) now logs less per-file progress detail than the old chunked copy. Behaviour and result are the same; if per-file progress matters, make `MySqlUpdateService.CopyFileChunkedAsync` `internal static` and call it from `MySqlDeployHelper.MigrateDataAsync` in place of `File.Copy`.

- [ ] **Step 8: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "refactor: extract MySqlDeployHelper shared by pipeline and UAT"
```

---

### Task 8: Real UAT filesystem

**Files:**
- Create: `XamppUpdate/Services/Uat/UatFileSystem.cs`
- Test: `XamppUpdate.Tests/UatFileSystemTests.cs`

**Interfaces:**
- Consumes: `IUatFileSystem` (Task 4).
- Produces: `class UatFileSystem : IUatFileSystem`.

- [ ] **Step 1: Write the failing tests** `XamppUpdate.Tests/UatFileSystemTests.cs`

```csharp
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class UatFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uat_fs_" + Guid.NewGuid().ToString("N"));
    private readonly UatFileSystem _fs = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Make(string rel, string content)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public async Task CopyTree_CopiesAllFilesAndManifestsMatch()
    {
        Make(@"src\bin\mysqld.exe", "binary");
        Make(@"src\data\ibdata1", "0123456789");
        Make(@"src\my.ini", "[mysqld]");

        await _fs.CopyTreeAsync(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), null, CancellationToken.None);

        var a = await _fs.BuildManifestAsync(Path.Combine(_root, "src"), CancellationToken.None);
        var b = await _fs.BuildManifestAsync(Path.Combine(_root, "dst"), CancellationToken.None);
        Assert.Equal(3, a.Count);
        Assert.Equal(a.OrderBy(k => k.Key), b.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task CopyTree_RefusesToOverwriteAnExistingTarget()
    {
        Make(@"src\a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        await Assert.ThrowsAsync<IOException>(() =>
            _fs.CopyTreeAsync(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), null, CancellationToken.None));
    }

    [Fact]
    public async Task CopyTree_CancellationStopsAndThrows()
    {
        for (int i = 0; i < 20; i++) Make($@"src\f{i}.bin", new string('x', 1000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fs.CopyTreeAsync(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), null, cts.Token));
    }

    [Fact]
    public void FindConfigFiles_ReturnsIniAndCnfRecursively()
    {
        Make(@"c\bin\my.ini", "x");
        Make(@"c\my.ini", "x");
        Make(@"c\data\extra.cnf", "x");
        Make(@"c\data\ibdata1", "x");
        var found = _fs.FindConfigFiles(Path.Combine(_root, "c"));
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void GetDirectorySize_SumsFileLengths()
    {
        Make(@"s\a.bin", "12345");
        Make(@"s\sub\b.bin", "123");
        Assert.Equal(8, _fs.GetDirectorySize(Path.Combine(_root, "s")));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter UatFileSystemTests`
Expected: FAIL (`UatFileSystem` not defined).

- [ ] **Step 3: Implement** `XamppUpdate/Services/Uat/UatFileSystem.cs`

```csharp
namespace XamppUpdate.Services.Uat;

public sealed class UatFileSystem : IUatFileSystem
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,        // an unreadable file must fail the copy, not be skipped silently
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    public long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", Walk)) total += new FileInfo(f).Length;
        return total;
    }

    public long GetFreeBytes(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("No volume for " + path);
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public IReadOnlyList<string> FindReparsePoints(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", Walk))
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) result.Add(e.FullName);
        }
        return result;
    }

    public IReadOnlyList<string> FindConfigFiles(string root) =>
        Directory.EnumerateFiles(root, "*", Walk)
            .Where(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".cnf", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string content) =>
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteTree(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }

    public async Task CopyTreeAsync(string source, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        if (PathExists(destination))
            throw new IOException($"Target folder already exists: {destination}");

        // Race-safe reservation: CreateDirectory on an existing folder is silent, so re-check right after.
        Directory.CreateDirectory(destination);

        var files = Directory.EnumerateFiles(source, "*", Walk).ToList();
        int done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(source, file);
            string dest = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            await using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            await src.CopyToAsync(dst, 1 << 20, ct);

            done++;
            if (done % 50 == 0 || done == files.Count)
                progress?.Report($"Copied {done}/{files.Count} files");
        }

        // Empty directories are part of the tree (e.g. data\test).
        foreach (var dir in Directory.EnumerateDirectories(source, "*", Walk))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
    }

    public Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string root, CancellationToken ct)
    {
        return Task.Run<IReadOnlyDictionary<string, long>>(() =>
        {
            var map = new Dictionary<string, long>();
            foreach (var f in Directory.EnumerateFiles(root, "*", Walk))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/').ToLowerInvariant();
                map[rel] = new FileInfo(f).Length;
            }
            return map;
        }, ct);
    }

    // Same code production uses (Task 7), so the UAT exercises the real deploy rules.
    public Task<int> DeployNewFilesAsync(string incomingRoot, string destRoot, CancellationToken ct) =>
        XamppUpdate.Services.MySqlDeployHelper.DeployNewFilesAsync(incomingRoot, destRoot, null, ct);

    public Task<bool> MigrateDataAsync(string srcDataDir, string destDataDir, CancellationToken ct) =>
        XamppUpdate.Services.MySqlDeployHelper.MigrateDataAsync(srcDataDir, destDataDir, null, ct);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter UatFileSystemTests`
Expected: PASS.

- [ ] **Step 5: Commit** (only if asked)

```bash
git add XamppUpdate/Services/Uat/UatFileSystem.cs XamppUpdate.Tests/UatFileSystemTests.cs
git commit -m "feat: UatFileSystem copy, manifest and link detection"
```

---

### Task 9: UatUpgradeService orchestration and report

**Files:**
- Create: `XamppUpdate/Services/Uat/UatReportWriter.cs`
- Create: `XamppUpdate/Services/Uat/UatUpgradeService.cs`
- Test: `XamppUpdate.Tests/UatUpgradeServiceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-8, `IWindowsServiceManager` (`ServiceExists`, `GetServiceStatusAsync`, `IsMySqlProcessRunning`, `StartServiceAsync`, `StartMySqlProcessAsync`, `StopServiceAsync`, `TestMySqlLoginAsync`, `SafeShutdownMySqlDetailedAsync`, `InstallOwnedMySqlServiceManualAsync`, `RemoveServiceIfOwnedAsync`), `IUatFileSystem.DeployNewFilesAsync/MigrateDataAsync` (Tasks 4 and 8, backed by `MySqlDeployHelper` from Task 7).
- Produces:
  - `interface IUatUpgradeService { Task<UatReport> RunAsync(UatOptions options, IProgress<UatEvent> progress, CancellationToken ct); Task<bool> DeleteCopyAsync(UatReport report, IProgress<string>? progress); }`
  - `class UatUpgradeService(IWindowsServiceManager services, IMySqlToolRunner tools, IUatFileSystem fs, Func<int, bool>? isPortFree = null, Func<string, string>? reportDirectory = null)`
  - `UatReportWriter.Write(UatReport report, string directory, string adminPassword) -> string path`

**Stage names (exact strings used by UI and tests):** `1 Preflight`, `2 Copy`, `3 Rewrite config`, `4 Old binaries`, `5 Deploy new binaries`, `6 Start new version`, `7 Full upgrade`, `8 Cleanup`.

- [ ] **Step 1: Write the failing orchestration tests** `XamppUpdate.Tests/UatUpgradeServiceTests.cs`

```csharp
using System.ServiceProcess;
using XamppUpdate.Services;
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class UatUpgradeServiceTests
{
    // ---- fakes ----
    private sealed class FakeServices : IWindowsServiceManager
    {
        public bool ProductionRunning = true;
        public bool ProductionServiceInstalled = true;
        public bool ShutdownSucceeds = true;
        public bool StartProductionSucceeds = true;
        public List<string> Calls = new();
        public HashSet<string> RunningRoots = new(StringComparer.OrdinalIgnoreCase);
        public bool UatServiceInstalled;

        private const string Prod = @"C:\xampp\mysql";

        public bool ServiceExists(string name) =>
            name == "mysql" ? ProductionServiceInstalled : name == "mysql_uat" && UatServiceInstalled;
        public Task<ServiceControllerStatus?> GetServiceStatusAsync(string name) =>
            Task.FromResult<ServiceControllerStatus?>(name == "mysql" && ProductionRunning ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped);
        public bool IsMySqlProcessRunning(string? path = null) =>
            path != null && (path.Equals(Prod, StringComparison.OrdinalIgnoreCase) ? ProductionRunning : RunningRoots.Contains(path));
        public string? GetRunningMySqlProcessInfo(string? p = null) => IsMySqlProcessRunning(p) ? "mysqld (PID 1)" : null;

        public Task<bool> StartServiceAsync(string name, TimeSpan t, IProgress<string>? p = null)
        {
            Calls.Add("StartService:" + name);
            if (name == "mysql") { if (!StartProductionSucceeds) return Task.FromResult(false); ProductionRunning = true; }
            else RunningRoots.Add(@"C:\xampp\mysql_copy_upgrade");
            return Task.FromResult(true);
        }
        public Task<bool> StartMySqlProcessAsync(string path, string? ini = null, IProgress<string>? p = null)
        {
            Calls.Add("StartProcess:" + path);
            if (path.Equals(Prod, StringComparison.OrdinalIgnoreCase)) ProductionRunning = StartProductionSucceeds;
            else RunningRoots.Add(path);
            return Task.FromResult(true);
        }
        public Task<MySqlShutdownReport> SafeShutdownMySqlDetailedAsync(string svc, string path, string user, string pw, int port = 3306, IProgress<string>? p = null)
        {
            Calls.Add("Shutdown:" + path);
            bool prod = path.Equals(Prod, StringComparison.OrdinalIgnoreCase);
            if (prod && !ShutdownSucceeds) return Task.FromResult(new MySqlShutdownReport { WasRunning = true, LoggedIn = true, Aborted = true });
            if (prod) ProductionRunning = false; else RunningRoots.Remove(path);
            return Task.FromResult(new MySqlShutdownReport
            {
                WasRunning = true, LoggedIn = true, FlushVerified = true, StopRequested = true, ProcessExited = true, FreshShutdownEvidence = true
            });
        }
        public Task<(bool Success, string Message)> TestMySqlLoginAsync(string path, string user, string pw, int port = 3306) =>
            Task.FromResult((true, "Login OK"));
        public Task<bool> InstallOwnedMySqlServiceManualAsync(string path, string name, string ini, IProgress<string>? p = null)
        { Calls.Add("InstallService:" + name); UatServiceInstalled = true; return Task.FromResult(true); }
        public Task<bool> RemoveServiceIfOwnedAsync(string name, string root, IProgress<string>? p = null)
        { Calls.Add("RemoveService:" + name); UatServiceInstalled = false; return Task.FromResult(true); }
        public Task<bool> StopServiceAsync(string name, TimeSpan t, IProgress<string>? p = null)
        { Calls.Add("StopService:" + name); RunningRoots.Remove(@"C:\xampp\mysql_copy_upgrade"); return Task.FromResult(true); }

        // --- members the orchestrator does not use ---
        public Task<bool> WaitForStatusAsync(string s, ServiceControllerStatus d, TimeSpan t, IProgress<string>? p = null) => Task.FromResult(true);
        public bool IsApacheProcessRunning(string? a = null) => false;
        public Task<bool> InstallApacheServiceAsync(string a, string s, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> RemoveApacheServiceAsync(string a, string s, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> StartApacheProcessAsync(string a, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> StopApacheProcessAsync(string a, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> InstallMySqlServiceAsync(string m, string s, string? i = null, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> RemoveMySqlServiceAsync(string m, string s, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> StopMySqlProcessAsync(string m, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> StopMySqlAnyAsync(string s, string m, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> EnsureMySqlRunningAsync(string s, string m, string? i = null, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> StopApacheAnyAsync(string s, string a, IProgress<string>? p = null) => Task.FromResult(true);
        public Task<bool> SafeShutdownMySqlAsync(string s, string m, string u, string pw, int port = 3306, IProgress<string>? p = null) => Task.FromResult(true);
        public string? GetServiceImagePath(string name) => null;
    }

    private sealed class FakeTools : IMySqlToolRunner
    {
        public bool UpgradeSucceeds = true;
        public string CopyRoot = @"C:\xampp\mysql_copy_upgrade";
        public int Port = 13306;
        public string Version = "10.11.19-MariaDB";
        public List<string> Queries = new();

        public Task<ToolResult> QueryAsync(MySqlTarget t, string u, string pw, string sql, CancellationToken ct)
        {
            Queries.Add(t.Port + ":" + sql);
            if (sql == UatIdentity.Sql)
                return Task.FromResult(new ToolResult(0, $"{Version}\t{t.Port}\t{t.Root}\\\t{t.Root}\\data\\\r\n", string.Empty));
            if (sql.Contains("ENGINES", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ToolResult(0, "InnoDB\tDEFAULT\r\n", string.Empty));
            return Task.FromResult(new ToolResult(0, "varchar(384)\r\n", string.Empty));
        }
        public Task<ToolResult> UpgradeAsync(MySqlTarget t, string u, string pw, CancellationToken ct) =>
            Task.FromResult(UpgradeSucceeds ? new ToolResult(0, "OK", string.Empty) : new ToolResult(1, string.Empty, "[ERROR] upgrade failed"));
    }

    private sealed class FakeFs : IUatFileSystem
    {
        public HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase) { @"C:\xampp\mysql" };
        public bool CopyThrows;
        public bool CopyCancelsViaToken;
        public List<string> Deleted = new();
        public Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\xampp\mysql_copy_upgrade\bin\my.ini"] = "[mysqld]\nbasedir=C:/xampp/mysql\ndatadir=C:/xampp/mysql/data\nport=3306\n"
        };

        public bool PathExists(string p) => Existing.Contains(p.TrimEnd('\\'));
        public long GetDirectorySize(string p) => 100;
        public long GetFreeBytes(string p) => 1_000_000;
        public IReadOnlyList<string> FindReparsePoints(string r) => Array.Empty<string>();
        public IReadOnlyList<string> FindConfigFiles(string r) => Files.Keys.ToList();
        public string ReadAllText(string p) => Files.TryGetValue(p, out var c) ? c : "[mysqld]\nport=3306\n";
        public void WriteAllText(string p, string c) => Files[p] = c;
        public void CreateDirectory(string p) => Existing.Add(p.TrimEnd('\\'));
        public void MoveDirectory(string s, string d) { Existing.Remove(s); Existing.Add(d); }
        public void DeleteTree(string p) { Deleted.Add(p); Existing.Remove(p); }
        public Task CopyTreeAsync(string s, string d, IProgress<string>? p, CancellationToken ct)
        {
            if (CopyCancelsViaToken) throw new OperationCanceledException();
            if (CopyThrows) throw new IOException("disk full");
            Existing.Add(d.TrimEnd('\\'));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string r, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["bin/mysqld.exe"] = 1 });
        public Task<int> DeployNewFilesAsync(string incoming, string dest, CancellationToken ct) => Task.FromResult(5);
        public Task<bool> MigrateDataAsync(string src, string dst, CancellationToken ct) => Task.FromResult(true);
    }

    private static UatOptions Opts() => new()
    {
        ProductionRoot = @"C:\xampp\mysql",
        ProductionIniPath = @"C:\xampp\mysql\bin\my.ini",
        ProductionServiceName = "mysql",
        ProductionPort = 3306,
        IncomingRoot = @"C:\temp\incoming",
        IncomingVersion = "10.11.19",
        TargetRoot = @"C:\xampp\mysql_copy_upgrade",
        Port = 13306,
        TestAsService = true,
    };

    private static UatUpgradeService Make(FakeServices s, FakeTools t, FakeFs f, string reports) =>
        new(s, t, f, _ => true, _ => reports);

    private static string TempReports() => Path.Combine(Path.GetTempPath(), "uat_rep_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HappyPath_RunsAllStagesAndRestoresProduction()
    {
        var svc = new FakeServices(); var tools = new FakeTools(); var fs = new FakeFs();
        var report = await Make(svc, tools, fs, TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.True(report.Success);
        Assert.All(report.Stages, s => Assert.Equal(UatStageStatus.Ok, s.Status));
        Assert.True(svc.ProductionRunning);
        Assert.True(report.ProductionRestored);
        Assert.Contains("RemoveService:mysql_uat", svc.Calls);
    }

    [Fact]
    public async Task ExistingTarget_StopsBeforeProductionIsTouched()
    {
        var svc = new FakeServices(); var fs = new FakeFs();
        fs.Existing.Add(@"C:\xampp\mysql_copy_upgrade");
        var report = await Make(svc, new FakeTools(), fs, TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.False(report.Success);
        Assert.Equal(UatStageStatus.Failed, report.Stages[0].Status);
        Assert.Contains("already exists", report.Stages[0].Message);
        Assert.DoesNotContain(svc.Calls, c => c.StartsWith("Shutdown:"));
        // Stages 2-7 are skipped; stage 8 (cleanup) always runs.
        Assert.All(report.Stages.Skip(1).Take(6), s => Assert.Equal(UatStageStatus.Skipped, s.Status));
        Assert.Equal(UatStageStatus.Ok, report.Stages[7].Status);
    }

    [Fact]
    public async Task CopyFailure_StillRestartsProductionInItsPriorMode()
    {
        var svc = new FakeServices(); var fs = new FakeFs { CopyThrows = true };
        var report = await Make(svc, new FakeTools(), fs, TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.False(report.Success);
        Assert.True(svc.ProductionRunning);
        Assert.Contains("StartService:mysql", svc.Calls);
        Assert.True(report.ProductionRestored);
    }

    [Fact]
    public async Task CancelDuringCopy_RestoresProductionAndRecordsPartialTargetAsOwned()
    {
        var svc = new FakeServices(); var fs = new FakeFs { CopyCancelsViaToken = true };
        var report = await Make(svc, new FakeTools(), fs, TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.Equal(UatStageStatus.Cancelled, report.Stages[1].Status);
        Assert.True(svc.ProductionRunning);
        Assert.Contains(@"C:\xampp\mysql_copy_upgrade", report.OwnedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionOriginallyStopped_IsNotStartedAfterwards()
    {
        var svc = new FakeServices { ProductionRunning = false };
        var report = await Make(svc, new FakeTools(), new FakeFs(), TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.True(report.Success);
        Assert.False(svc.ProductionRunning);
        Assert.DoesNotContain("StartService:mysql", svc.Calls);
        Assert.DoesNotContain(svc.Calls, c => c == @"StartProcess:C:\xampp\mysql");
    }

    [Fact]
    public async Task ProductionShutdownRefused_AbortsAndLeavesProductionRunning()
    {
        var svc = new FakeServices { ShutdownSucceeds = false };
        var report = await Make(svc, new FakeTools(), new FakeFs(), TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.False(report.Success);
        Assert.Equal(UatStageStatus.Failed, report.Stages[1].Status);
        Assert.True(svc.ProductionRunning);
    }

    [Fact]
    public async Task UpgradeFailure_FailsStage7_ButStillCleansUp()
    {
        var svc = new FakeServices(); var tools = new FakeTools { UpgradeSucceeds = false };
        var report = await Make(svc, tools, new FakeFs(), TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.False(report.Success);
        Assert.Equal(UatStageStatus.Failed, report.Stages[6].Status);
        Assert.Equal(UatStageStatus.Ok, report.Stages[7].Status);
        Assert.Contains("RemoveService:mysql_uat", svc.Calls);
        Assert.True(svc.ProductionRunning);
    }

    [Fact]
    public async Task WrongServerIdentity_FailsStage6()
    {
        var svc = new FakeServices(); var tools = new FakeTools { Version = "10.4.8-MariaDB" };
        var report = await Make(svc, tools, new FakeFs(), TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        Assert.False(report.Success);
        Assert.Equal(UatStageStatus.Failed, report.Stages[5].Status);
        Assert.Contains("version", report.Stages[5].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigRewrite_MakesPathsAndPortCopyLocal()
    {
        var svc = new FakeServices(); var fs = new FakeFs();
        await Make(svc, new FakeTools(), fs, TempReports()).RunAsync(Opts(), new Progress<UatEvent>(), CancellationToken.None);

        string ini = fs.Files[@"C:\xampp\mysql_copy_upgrade\bin\my.ini"];
        Assert.Contains("basedir=C:/xampp/mysql_copy_upgrade", ini);
        Assert.Contains("port=13306", ini);
        Assert.Contains("bind-address=127.0.0.1", ini);
        Assert.DoesNotContain("C:/xampp/mysql/", ini);
    }

    [Fact]
    public async Task Report_IsWrittenOutsideTheDataTreeWithoutThePassword()
    {
        string dir = TempReports();
        var o = Opts() with { AdminPassword = "Sup3r S3cret!" };
        var report = await Make(new FakeServices(), new FakeTools(), new FakeFs(), dir).RunAsync(o, new Progress<UatEvent>(), CancellationToken.None);

        var files = Directory.GetFiles(dir, "uat_*.txt");
        Assert.Single(files);
        string text = File.ReadAllText(files[0]);
        Assert.DoesNotContain("Sup3r S3cret!", text);
        Assert.Contains("1 Preflight", text);
        Directory.Delete(dir, true);
    }
}
```

Note: `FakeServices` must implement every member of `IWindowsServiceManager`. If the compiler reports a missing member, add it as a trivial `Task.FromResult(...)` stub; the list above reflects the interface as of this plan.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter UatUpgradeServiceTests`
Expected: FAIL (`UatUpgradeService`/`UatReportWriter` not defined).

- [ ] **Step 3: Implement the report writer** `XamppUpdate/Services/Uat/UatReportWriter.cs`

```csharp
using System.Text;

namespace XamppUpdate.Services.Uat;

public static class UatReportWriter
{
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XamppUpdate", "uat-reports");

    /// <summary>Writes a redacted report outside any MySQL data tree. Returns the file path.</summary>
    public static string Write(UatReport report, string directory, string adminPassword)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"uat_{report.StartedUtc:yyyyMMdd_HHmmss}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"UAT upgrade report - {(report.Success ? "SUCCESS" : "FAILED")}");
        sb.AppendLine($"Started (UTC): {report.StartedUtc:O}");
        sb.AppendLine($"Finished (UTC): {report.FinishedUtc:O}");
        sb.AppendLine($"Source version: {report.SourceVersion}");
        sb.AppendLine($"Target version: {report.TargetVersion}");
        sb.AppendLine($"Production state: {report.ProductionState}; restored: {report.ProductionRestored}");
        sb.AppendLine();
        foreach (var s in report.Stages)
            sb.AppendLine($"[{s.Status,-9}] {s.Name} ({s.Duration.TotalSeconds:F1}s) {s.Message}");
        if (report.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in report.Notes) sb.AppendLine("- " + n);
        }
        if (!string.IsNullOrWhiteSpace(report.RecoveryInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("RECOVERY: " + report.RecoveryInstructions);
        }

        string text = sb.ToString();
        if (!string.IsNullOrEmpty(adminPassword)) text = text.Replace(adminPassword, "******");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
```

- [ ] **Step 4: Implement the orchestrator** `XamppUpdate/Services/Uat/UatUpgradeService.cs`

```csharp
using XamppUpdate.Services;

namespace XamppUpdate.Services.Uat;

public interface IUatUpgradeService
{
    Task<UatReport> RunAsync(UatOptions options, IProgress<UatEvent> progress, CancellationToken ct);
    /// <summary>Deletes only the trees recorded in <paramref name="report"/>, after re-verifying ownership and that nothing runs there.</summary>
    Task<bool> DeleteCopyAsync(UatReport report, IProgress<string>? progress);
}

public sealed class UatUpgradeService : IUatUpgradeService
{
    private static readonly string[] StageNames =
    {
        "1 Preflight", "2 Copy", "3 Rewrite config", "4 Old binaries",
        "5 Deploy new binaries", "6 Start new version", "7 Full upgrade", "8 Cleanup"
    };

    private sealed record ProdState(bool Running, bool ViaService, string ServiceName, string Root, string Ini, int Port);

    private readonly IWindowsServiceManager _services;
    private readonly IMySqlToolRunner _tools;
    private readonly IUatFileSystem _fs;
    private readonly Func<int, bool> _isPortFree;
    private readonly Func<string, string> _reportDirectory;

    public UatUpgradeService(
        IWindowsServiceManager services, IMySqlToolRunner tools, IUatFileSystem fs,
        Func<int, bool>? isPortFree = null, Func<string, string>? reportDirectory = null)
    {
        _services = services;
        _tools = tools;
        _fs = fs;
        _isPortFree = isPortFree ?? UatPortProbe.IsPortFree;
        _reportDirectory = reportDirectory ?? (_ => UatReportWriter.DefaultDirectory());
    }

    public async Task<UatReport> RunAsync(UatOptions o, IProgress<UatEvent> progress, CancellationToken ct)
    {
        var report = new UatReport { SourceVersion = string.Empty, TargetVersion = o.IncomingVersion };
        foreach (var (name, i) in StageNames.Select((n, i) => (n, i + 1)))
            report.Stages.Add(new UatStageResult { Number = i, Name = name });

        string target = PathSafety.Canonical(o.TargetRoot);
        string oldTree = target + "_old";
        var targetInfo = new MySqlTarget(target, Path.Combine(target, "bin", "my.ini"), o.Port);

        var prod = CaptureProduction(o);
        report.ProductionState = prod.Running ? (prod.ViaService ? "running (service)" : "running (process)") : "stopped";

        bool stoppedByUs = false;
        bool serviceCreated = false;
        bool abort = false;

        async Task<bool> Stage(int n, Func<Task<string>> body)
        {
            var st = report.Stages[n - 1];
            if (abort) { st.Status = UatStageStatus.Skipped; Raise(progress, st); return false; }

            st.Status = UatStageStatus.Running; Raise(progress, st);
            var started = DateTime.UtcNow;
            try
            {
                st.Message = await body();
                st.Status = UatStageStatus.Ok;
            }
            catch (OperationCanceledException)
            {
                st.Status = UatStageStatus.Cancelled; st.Message = "Cancelled."; abort = true;
            }
            catch (Exception ex)
            {
                st.Status = UatStageStatus.Failed; st.Message = ex.Message; abort = true;
            }
            st.Duration = DateTime.UtcNow - started;
            Raise(progress, st);
            return st.Status == UatStageStatus.Ok;
        }

        try
        {
            // ---- 1 Preflight ----
            await Stage(1, () =>
            {
                var issues = UatPreflight.Validate(o, _fs, _isPortFree).Select(i => i.Message).ToList();

                // An existing service of that name is never reused, stopped or removed.
                if (o.TestAsService && !string.IsNullOrWhiteSpace(o.ServiceName) && _services.ServiceExists(o.ServiceName))
                    issues.Add($"A Windows service named '{o.ServiceName}' already exists. Choose another name or remove it manually.");

                if (issues.Count > 0) throw new InvalidOperationException(string.Join(" | ", issues));
                return Task.FromResult("All checks passed.");
            });

            // ---- 2 Copy (cold) ----
            await Stage(2, async () =>
            {
                report.OwnedPaths.Add(target);   // recorded before anything is created, so a partial copy can be deleted

                if (prod.Running)
                {
                    stoppedByUs = true;          // restoration responsibility recorded BEFORE shutdown is requested
                    var sd = await _services.SafeShutdownMySqlDetailedAsync(
                        prod.ServiceName, prod.Root, o.AdminUser, o.AdminPassword, prod.Port, null);
                    if (!sd.StrictSuccess)
                    {
                        if (sd.Aborted || !sd.ProcessExited) stoppedByUs = false; // production still up
                        throw new InvalidOperationException(
                            "Production did not shut down cleanly (login/flush/exit/log evidence required). Nothing was copied.");
                    }
                }

                await _fs.CopyTreeAsync(prod.Root, target, null, ct);

                var src = await _fs.BuildManifestAsync(prod.Root, ct);
                var dst = await _fs.BuildManifestAsync(target, ct);
                if (src.Count != dst.Count || src.Any(kv => !dst.TryGetValue(kv.Key, out var len) || len != kv.Value))
                    throw new InvalidOperationException("Copy verification failed: manifests differ.");

                // Downtime ends here: bring production back before doing anything else.
                if (stoppedByUs)
                {
                    await RestoreProductionAsync(prod, o);
                    stoppedByUs = false;
                }
                return $"Copied {dst.Count} files; production restored.";
            });

            // ---- 3 Rewrite config ----
            await Stage(3, () =>
            {
                int changed = 0;
                foreach (var file in _fs.FindConfigFiles(target))
                {
                    string before = _fs.ReadAllText(file);
                    string after = UatIniRewriter.RewriteRoot(before, prod.Root, target);
                    after = UatIniRewriter.SetOption(after, "mysqld", "port", o.Port.ToString());
                    after = UatIniRewriter.SetOption(after, "client", "port", o.Port.ToString());
                    after = UatIniRewriter.SetOption(after, "mysqld", "bind-address", "127.0.0.1");
                    after = UatIniRewriter.SetOption(after, "mysqld", "event_scheduler", "OFF");
                    after = UatIniRewriter.CommentOutOptions(after, "mysqld", "socket", "enable-named-pipe", "named-pipe", "shared-memory");
                    after = UatIniRewriter.CommentOutOptions(after, "client", "socket");

                    var remaining = UatIniRewriter.FindRemainingRoot(after, prod.Root);
                    if (remaining.Count > 0)
                        throw new InvalidOperationException($"{file}: production path still present: {remaining[0].Trim()}");

                    var external = UatIniRewriter.FindExternalPaths(after, target);
                    if (external.Count > 0)
                        throw new InvalidOperationException($"{file}: writable path outside the copy: {external[0]}");

                    if (after != before) { _fs.WriteAllText(file, after); changed++; }
                }
                return Task.FromResult($"Rewrote {changed} config file(s); port {o.Port}; bound to 127.0.0.1.");
            });

            // ---- 4 Old binaries on the copy: recover + flush + clean stop ----
            await Stage(4, async () =>
            {
                bool started = await _services.StartMySqlProcessAsync(target, targetInfo.IniPath, null);
                if (!started || !_services.IsMySqlProcessRunning(target))
                    throw new InvalidOperationException("The copy did not start with its original binaries (see its error log).");

                await VerifyIdentityAsync(targetInfo, o, expectedVersionPrefix: null);

                var sd = await _services.SafeShutdownMySqlDetailedAsync(string.Empty, target, o.AdminUser, o.AdminPassword, o.Port, null);
                if (!sd.StrictSuccess)
                    throw new InvalidOperationException("Safe shutdown of the copy was not fully verified (login/flush/exit/log evidence).");
                return "Recovered, flushed (innodb_fast_shutdown=0) and stopped cleanly.";
            });

            // ---- 5 Deploy new binaries into a fresh tree ----
            await Stage(5, async () =>
            {
                if (_fs.PathExists(oldTree))
                    throw new InvalidOperationException($"Folder already exists: {oldTree}");

                _fs.MoveDirectory(target, oldTree);
                report.OwnedPaths.Add(oldTree);
                _fs.CreateDirectory(target);

                int files = await _fs.DeployNewFilesAsync(o.IncomingRoot, target, ct);

                // Retained, already-rewritten configs come from the old UAT tree.
                foreach (var rel in new[] { @"bin\my.ini", "my.ini" })
                {
                    string from = Path.Combine(oldTree, rel);
                    if (!_fs.PathExists(from)) continue;

                    string to = Path.Combine(target, rel);
                    _fs.CreateDirectory(Path.GetDirectoryName(to)!);
                    _fs.WriteAllText(to, _fs.ReadAllText(from));
                }

                bool migrated = await _fs.MigrateDataAsync(Path.Combine(oldTree, "data"), Path.Combine(target, "data"), ct);
                if (!migrated) throw new InvalidOperationException("Copied data folder was not found to migrate.");

                return $"Deployed {files} files; data migrated; old tree kept as {Path.GetFileName(oldTree)}.";
            });

            // ---- 6 Start the new version ----
            await Stage(6, async () =>
            {
                if (o.TestAsService)
                {
                    serviceCreated = await _services.InstallOwnedMySqlServiceManualAsync(target, o.ServiceName, targetInfo.IniPath, null);
                    if (!serviceCreated) throw new InvalidOperationException($"Could not register service '{o.ServiceName}'.");
                    report.OwnedServiceName = o.ServiceName;   // ownership is recorded only once registration succeeded
                    if (!await _services.StartServiceAsync(o.ServiceName, TimeSpan.FromSeconds(60), null))
                        throw new InvalidOperationException($"Service '{o.ServiceName}' did not start.");
                }
                else if (!await _services.StartMySqlProcessAsync(target, targetInfo.IniPath, null))
                {
                    throw new InvalidOperationException("The new version did not start (see the copy's error log).");
                }

                var id = await VerifyIdentityAsync(targetInfo, o, o.IncomingVersion);
                report.TargetVersion = id.Version;
                return $"Running {id.Version} on port {o.Port}.";
            });

            // ---- 7 Full upgrade ----
            await Stage(7, async () =>
            {
                var up = await _tools.UpgradeAsync(targetInfo, o.AdminUser, o.AdminPassword, ct);
                if (!up.Ok || up.Combined.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                    up.Combined.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"mariadb-upgrade failed (exit {up.ExitCode}): {Truncate(up.Combined)}");

                var engines = await _tools.QueryAsync(targetInfo, o.AdminUser, o.AdminPassword,
                    "SELECT ENGINE, SUPPORT FROM information_schema.ENGINES WHERE ENGINE='InnoDB'", ct);
                if (!engines.Ok || !engines.StdOut.Contains("InnoDB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("InnoDB is not available after the upgrade.");

                var definer = await _tools.QueryAsync(targetInfo, o.AdminUser, o.AdminPassword,
                    "SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='mysql' AND TABLE_NAME='event' AND COLUMN_NAME='definer'", ct);
                report.Notes.Add($"mysql.event.definer type after upgrade: {definer.StdOut.Trim()} (informational)");
                return "mariadb-upgrade completed; InnoDB available.";
            });
        }
        finally
        {
            // ---- 8 Cleanup: always runs, with its own budget unaffected by the cancelled token ----
            await RunCleanupAsync(report, o, prod, targetInfo, serviceCreated, stoppedByUs, abort, progress);
        }

        report.Success = report.Stages.All(s => s.Status == UatStageStatus.Ok) && report.ProductionRestored;
        report.FinishedUtc = DateTime.UtcNow;
        try { UatReportWriter.Write(report, _reportDirectory(o.TargetRoot), o.AdminPassword); } catch { /* reporting must not mask the result */ }
        return report;
    }

    private async Task RunCleanupAsync(
        UatReport report, UatOptions o, ProdState prod, MySqlTarget target,
        bool serviceCreated, bool stoppedByUs, bool aborted, IProgress<UatEvent> progress)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var st = report.Stages[7];
        st.Status = UatStageStatus.Running; Raise(progress, st);
        var started = DateTime.UtcNow;
        var problems = new List<string>();

        // Independent obligations: each one is attempted even if another fails.
        try
        {
            if (_services.IsMySqlProcessRunning(target.Root))
            {
                var sd = await _services.SafeShutdownMySqlDetailedAsync(
                    serviceCreated ? o.ServiceName : string.Empty, target.Root, o.AdminUser, o.AdminPassword, o.Port, null);
                if (!sd.ProcessExited) problems.Add($"The copy is still running on port {o.Port}; stop it manually.");
            }
        }
        catch (Exception ex) { problems.Add("Stopping the copy failed: " + ex.Message); }

        try
        {
            if (serviceCreated)
            {
                if (!await _services.RemoveServiceIfOwnedAsync(o.ServiceName, target.Root, null))
                    problems.Add($"Service '{o.ServiceName}' was not removed. Run: sc.exe delete \"{o.ServiceName}\"");
            }
        }
        catch (Exception ex) { problems.Add("Removing the service failed: " + ex.Message); }

        try
        {
            if (stoppedByUs)
            {
                await RestoreProductionAsync(prod, o);
            }
            report.ProductionRestored = true;
        }
        catch (Exception ex)
        {
            report.ProductionRestored = false;
            string how = prod.ViaService ? $"net start {prod.ServiceName}" : $"start mysqld from {prod.Root}\\bin";
            report.RecoveryInstructions = $"Production could NOT be restarted automatically. Start it manually: {how}. ({ex.Message})";
            problems.Add(report.RecoveryInstructions);
        }

        st.Duration = DateTime.UtcNow - started;
        if (problems.Count == 0)
        {
            st.Status = UatStageStatus.Ok;
            st.Message = "Copy stopped, temporary service removed, production state verified.";
        }
        else
        {
            st.Status = UatStageStatus.Failed;
            st.Message = string.Join(" | ", problems);
        }
        Raise(progress, st);
    }

    private ProdState CaptureProduction(UatOptions o)
    {
        bool svcInstalled = _services.ServiceExists(o.ProductionServiceName);
        bool svcRunning = false;
        if (svcInstalled)
        {
            var status = _services.GetServiceStatusAsync(o.ProductionServiceName).GetAwaiter().GetResult();
            svcRunning = status == System.ServiceProcess.ServiceControllerStatus.Running;
        }

        bool procRunning = _services.IsMySqlProcessRunning(o.ProductionRoot);
        bool viaService = svcRunning;
        bool running = svcRunning || procRunning;
        return new ProdState(running, viaService, o.ProductionServiceName, o.ProductionRoot, o.ProductionIniPath, o.ProductionPort);
    }

    /// <summary>Restart production in exactly the mode it was in, then verify process + login.</summary>
    private async Task RestoreProductionAsync(ProdState prod, UatOptions o)
    {
        bool ok = prod.ViaService
            ? await _services.StartServiceAsync(prod.ServiceName, TimeSpan.FromSeconds(120), null)
            : await _services.StartMySqlProcessAsync(prod.Root, prod.Ini, null);

        if (!ok || !_services.IsMySqlProcessRunning(prod.Root))
            throw new InvalidOperationException("production process is not running after restart.");

        var login = await _services.TestMySqlLoginAsync(prod.Root, o.AdminUser, o.AdminPassword, prod.Port);
        if (!login.Success)
            throw new InvalidOperationException("production restarted but the admin login check failed: " + login.Message);
    }

    private async Task<UatIdentity> VerifyIdentityAsync(MySqlTarget t, UatOptions o, string? expectedVersionPrefix)
    {
        // Poll while the server finishes recovery / startup.
        ToolResult last = new(-1, string.Empty, "no attempt");
        for (int i = 0; i < 90; i++)
        {
            last = await _tools.QueryAsync(t, o.AdminUser, o.AdminPassword, UatIdentity.Sql, CancellationToken.None);
            if (last.Ok && UatIdentity.Parse(last.StdOut) != null) break;
            await Task.Delay(i == 0 ? 0 : 2000);
        }

        var id = UatIdentity.Parse(last.StdOut)
                 ?? throw new InvalidOperationException($"Could not read the server identity: {Truncate(last.Combined)}");

        if (!id.Matches(t.Port, t.Root))
            throw new InvalidOperationException(
                $"Connected server is not the UAT copy (port {id.Port}, basedir {id.BaseDir}, datadir {id.DataDir}).");

        if (!string.IsNullOrEmpty(expectedVersionPrefix) &&
            !id.Version.StartsWith(expectedVersionPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Server version {id.Version} does not match the incoming version {expectedVersionPrefix}.");

        return id;
    }

    public async Task<bool> DeleteCopyAsync(UatReport report, IProgress<string>? progress)
    {
        foreach (var path in report.OwnedPaths)
        {
            if (_services.IsMySqlProcessRunning(path))
            {
                progress?.Report($"REFUSED: a database process is still running under {path}.");
                return false;
            }
        }
        if (!string.IsNullOrEmpty(report.OwnedServiceName) && _services.ServiceExists(report.OwnedServiceName))
        {
            progress?.Report($"REFUSED: service '{report.OwnedServiceName}' still exists. Remove it first.");
            return false;
        }

        foreach (var path in report.OwnedPaths)
        {
            if (_fs.FindReparsePoints(path).Count > 0)
            {
                progress?.Report($"REFUSED: {path} contains links/junctions.");
                return false;
            }
        }

        await Task.Run(() =>
        {
            foreach (var path in report.OwnedPaths)
            {
                _fs.DeleteTree(path);
                progress?.Report($"Deleted {path}");
            }
        });
        return true;
    }

    private static void Raise(IProgress<UatEvent> progress, UatStageResult s) =>
        progress.Report(new UatEvent(s.Number, s.Name, s.Status, s.Message));

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "...";
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test XamppUpdate.Tests --filter UatUpgradeServiceTests`
Expected: PASS (10 tests). `FakeFs` implements `DeployNewFilesAsync`/`MigrateDataAsync` itself, so stage 5 never touches the real disk in these tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test XamppUpdate.Tests`
Expected: all green.

- [ ] **Step 7: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "feat: UatUpgradeService orchestration, cleanup and report"
```

---

### Task 10: UI - view model, window and step-1 button

**Files:**
- Create: `XamppUpdate/ViewModels/UatUpgradeViewModel.cs`
- Create: `XamppUpdate/Views/UatUpgradeWindow.xaml`
- Create: `XamppUpdate/Views/UatUpgradeWindow.xaml.cs`
- Modify: `XamppUpdate/ViewModels/MySqlWizardViewModel.cs`
- Modify: `XamppUpdate/Views/MySqlUpdateWizardWindow.xaml`
- Modify: `XamppUpdate/Views/MySqlUpdateWizardWindow.xaml.cs`
- Test: `XamppUpdate.Tests/UatUpgradeViewModelTests.cs`

**Interfaces:**
- Consumes: `IUatUpgradeService`, `UatOptions`, `UatEvent`, `UatReport` (Task 9).
- Produces:
  - `UatUpgradeViewModel(IUatUpgradeService service, UatOptions defaults)`; observable `Port`, `TargetRoot`, `TestAsService`, `AdminUser`, `AdminPassword` (set by the window), `IsRunning`, `Stages`, `LogLines`, `LastReport`; commands `RunCommand`, `CancelCommand`, `DeleteCopyCommand`, `OpenFolderCommand`.
  - `MySqlWizardViewModel.RealTestUpgradeCommand` and event `RequestOpenUatUpgrade(UatLaunchInfo)`.

- [ ] **Step 1: Write the failing view-model tests** `XamppUpdate.Tests/UatUpgradeViewModelTests.cs`

```csharp
using XamppUpdate.Services.Uat;
using XamppUpdate.ViewModels;
using Xunit;

namespace XamppUpdate.Tests;

public class UatUpgradeViewModelTests
{
    private sealed class FakeService : IUatUpgradeService
    {
        public UatOptions? Seen;
        public TaskCompletionSource<bool> Gate = new();
        public Task<UatReport> RunAsync(UatOptions o, IProgress<UatEvent> progress, CancellationToken ct)
        {
            Seen = o;
            progress.Report(new UatEvent(1, "1 Preflight", UatStageStatus.Ok, "ok"));
            var r = new UatReport { Success = true };
            r.OwnedPaths.Add(o.TargetRoot);
            return Task.FromResult(r);
        }
        public Task<bool> DeleteCopyAsync(UatReport r, IProgress<string>? p) => Task.FromResult(true);
    }

    private static UatOptions Defaults() => new()
    {
        ProductionRoot = @"C:\xampp\mysql", ProductionIniPath = @"C:\xampp\mysql\bin\my.ini",
        ProductionServiceName = "mysql", IncomingRoot = @"C:\in", TargetRoot = @"C:\xampp\mysql_copy_upgrade",
    };

    [Fact]
    public async Task Run_PassesEditedOptionsAndPopulatesStages()
    {
        var svc = new FakeService();
        var vm = new UatUpgradeViewModel(svc, Defaults()) { Port = 14000, AdminPassword = "p w" };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(14000, svc.Seen!.Port);
        Assert.Equal("p w", svc.Seen.AdminPassword);
        Assert.True(svc.Seen.StopProductionFirst);
        Assert.NotEmpty(vm.Stages);
        Assert.False(vm.IsRunning);
        Assert.NotNull(vm.LastReport);
    }

    [Fact]
    public void DefaultsMatchTheSpec()
    {
        var vm = new UatUpgradeViewModel(new FakeService(), Defaults());
        Assert.Equal(13306, vm.Port);
        Assert.True(vm.TestAsService);
        Assert.Equal("mysql_uat", vm.ServiceName);
        Assert.Equal(@"C:\xampp\mysql_copy_upgrade", vm.TargetRoot);
    }

    [Fact]
    public async Task DeleteCopy_IsOnlyAvailableForAReportThatOwnsPaths()
    {
        var vm = new UatUpgradeViewModel(new FakeService(), Defaults());
        Assert.False(vm.DeleteCopyCommand.CanExecute(null));
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.DeleteCopyCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test XamppUpdate.Tests --filter UatUpgradeViewModelTests`
Expected: FAIL (`UatUpgradeViewModel` not defined).

- [ ] **Step 3: Implement the view model** `XamppUpdate/ViewModels/UatUpgradeViewModel.cs`

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XamppUpdate.Services.Uat;

namespace XamppUpdate.ViewModels
{
    public sealed class UatStageRow : ObservableObject
    {
        private UatStageStatus _status = UatStageStatus.Pending;
        private string _message = string.Empty;

        public int Number { get; init; }
        public string Name { get; init; } = string.Empty;
        public UatStageStatus Status { get => _status; set => SetProperty(ref _status, value); }
        public string Message { get => _message; set => SetProperty(ref _message, value); }
    }

    public partial class UatUpgradeViewModel : ObservableObject
    {
        private readonly IUatUpgradeService _service;
        private readonly UatOptions _defaults;
        private CancellationTokenSource? _cts;

        public UatUpgradeViewModel(IUatUpgradeService service, UatOptions defaults)
        {
            _service = service;
            _defaults = defaults;
            TargetRoot = defaults.TargetRoot;
            Port = defaults.Port;
            TestAsService = defaults.TestAsService;
            ServiceName = defaults.ServiceName;
            AdminUser = defaults.AdminUser;
            AdminPassword = defaults.AdminPassword;
        }

        public ObservableCollection<UatStageRow> Stages { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        [ObservableProperty] private string _targetRoot = string.Empty;
        [ObservableProperty] private int _port = 13306;
        [ObservableProperty] private bool _testAsService = true;
        [ObservableProperty] private string _serviceName = "mysql_uat";
        [ObservableProperty] private string _adminUser = "root";
        [ObservableProperty] private string _adminPassword = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(DeleteCopyCommand))]
        private bool _isRunning;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeleteCopyCommand))]
        private UatReport? _lastReport;

        [ObservableProperty] private string _summary = string.Empty;

        private bool CanRun() => !IsRunning;
        private bool CanCancel() => IsRunning;
        private bool CanDelete() => !IsRunning && LastReport is { OwnedPaths.Count: > 0 };

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task RunAsync()
        {
            IsRunning = true;
            LastReport = null;
            Stages.Clear();
            LogLines.Clear();
            Summary = string.Empty;

            var options = _defaults with
            {
                TargetRoot = TargetRoot.Trim(),
                Port = Port,
                StopProductionFirst = true,       // Phase 1: cold copy only
                TestAsService = TestAsService,
                ServiceName = ServiceName.Trim(),
                AdminUser = AdminUser,
                AdminPassword = AdminPassword
            };

            _cts = new CancellationTokenSource();
            var progress = new Progress<UatEvent>(e =>
            {
                var row = Stages.FirstOrDefault(s => s.Number == e.StageNumber);
                if (row == null)
                {
                    row = new UatStageRow { Number = e.StageNumber, Name = e.StageName };
                    Stages.Add(row);
                }
                row.Status = e.Status;
                row.Message = e.Message;
                LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {e.StageName}: {e.Status} {e.Message}".TrimEnd());
            });

            try
            {
                LastReport = await _service.RunAsync(options, progress, _cts.Token);
                Summary = LastReport.Success
                    ? "UAT upgrade SUCCEEDED. Production was restored to its original state."
                    : "UAT upgrade FAILED. See the failing stage; production state: " +
                      (LastReport.ProductionRestored ? "restored." : "NOT restored - " + LastReport.RecoveryInstructions);
            }
            catch (Exception ex)
            {
                Summary = "UAT run aborted: " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand(CanExecute = nameof(CanDelete))]
        private async Task DeleteCopyAsync()
        {
            if (LastReport == null) return;
            var progress = new Progress<string>(m => LogLines.Add(m));
            if (await _service.DeleteCopyAsync(LastReport, progress)) LastReport.OwnedPaths.Clear();
            DeleteCopyCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void OpenFolder()
        {
            if (Directory.Exists(TargetRoot))
                System.Diagnostics.Process.Start("explorer.exe", TargetRoot);
        }
    }
}
```

- [ ] **Step 4: Create the window** `XamppUpdate/Views/UatUpgradeWindow.xaml`

```xml
<Window x:Class="XamppUpdate.Views.UatUpgradeWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Real Test Upgrade (UAT copy)"
        Height="640" Width="900" MinHeight="520" MinWidth="760"
        WindowStartupLocation="CenterOwner"
        Background="{StaticResource WindowBgBrush}">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Rehearse the upgrade on a full copy, in parallel with production"
                   FontSize="16" FontWeight="Bold" Foreground="{StaticResource TextPrimaryBrush}" Margin="0,0,0,10" />

        <!-- Options -->
        <Border Grid.Row="1" Style="{StaticResource CardBorderStyle}" Padding="14" Margin="0,0,0,12">
            <Grid IsEnabled="{Binding IsRunning, Converter={StaticResource InverseBoolConverter}}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" /><ColumnDefinition Width="110" />
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Grid.Column="0" Text="Target folder:" VerticalAlignment="Center" Margin="0,0,8,6" />
                <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding TargetRoot, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,12,6" />
                <TextBlock Grid.Row="0" Grid.Column="2" Text="Port:" VerticalAlignment="Center" Margin="0,0,8,6" />
                <TextBox Grid.Row="0" Grid.Column="3" Text="{Binding Port, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,6" />

                <TextBlock Grid.Row="1" Grid.Column="0" Text="Admin user:" VerticalAlignment="Center" Margin="0,0,8,6" />
                <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding AdminUser, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,12,6" />
                <TextBlock Grid.Row="1" Grid.Column="2" Text="Password:" VerticalAlignment="Center" Margin="0,0,8,6" />
                <PasswordBox Grid.Row="1" Grid.Column="3" x:Name="PasswordBox" PasswordChanged="PasswordBox_PasswordChanged" Margin="0,0,0,6" />

                <StackPanel Grid.Row="2" Grid.ColumnSpan="4" Orientation="Horizontal">
                    <CheckBox Content="Test as Windows service (temporary 'mysql_uat', removed afterwards)"
                              IsChecked="{Binding TestAsService}" Margin="0,0,20,0" />
                    <TextBlock Text="Production is stopped briefly for the copy, then restarted in its previous mode."
                               Foreground="{StaticResource TextMutedBrush}" FontSize="11.5" VerticalAlignment="Center" />
                </StackPanel>
            </Grid>
        </Border>

        <!-- Stages + log -->
        <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="320" /><ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <ListBox Grid.Column="0" ItemsSource="{Binding Stages}" Margin="0,0,10,0">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="2">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="{Binding Status}" Width="80" FontWeight="SemiBold" />
                                <TextBlock Text="{Binding Name}" />
                            </StackPanel>
                            <TextBlock Text="{Binding Message}" TextWrapping="Wrap" FontSize="11"
                                       Foreground="{StaticResource TextMutedBrush}" />
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <ListBox Grid.Column="1" ItemsSource="{Binding LogLines}" FontFamily="Consolas" FontSize="12" />
        </Grid>

        <!-- Footer -->
        <Grid Grid.Row="3" Margin="0,12,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="{Binding Summary}" TextWrapping="Wrap" FontWeight="SemiBold" VerticalAlignment="Center" />
            <StackPanel Grid.Column="1" Orientation="Horizontal">
                <Button Content="Run" Command="{Binding RunCommand}" Padding="16,6" Margin="0,0,8,0" />
                <Button Content="Cancel" Command="{Binding CancelCommand}" Padding="16,6" Margin="0,0,8,0" />
                <Button Content="Open folder" Command="{Binding OpenFolderCommand}" Padding="12,6" Margin="0,0,8,0" />
                <Button Content="Delete UAT copy" IsEnabled="{Binding CanDeleteCopy}" Click="DeleteButton_Click" Padding="12,6" Margin="0,0,8,0" />
                <Button Content="Close" Click="CloseButton_Click" Padding="12,6" />
            </StackPanel>
        </Grid>
    </Grid>
</Window>
```

The window needs `InverseBoolConverter` in its resources. Add inside `<Window ...>` before the `<Grid>`:

```xml
    <Window.Resources>
        <conv:InverseBooleanConverter x:Key="InverseBoolConverter" xmlns:conv="clr-namespace:XamppUpdate.Converters" />
    </Window.Resources>
```

- [ ] **Step 5: Window code-behind** `XamppUpdate/Views/UatUpgradeWindow.xaml.cs`

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class UatUpgradeWindow : Window
    {
        private readonly UatUpgradeViewModel _vm;

        public UatUpgradeWindow(UatUpgradeViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb) _vm.AdminPassword = pb.Password;
        }

        // The confirmation must come BEFORE the delete, so the handler (not a Command binding) owns the call.
        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.LastReport == null || _vm.LastReport.OwnedPaths.Count == 0) return;

            string paths = string.Join(Environment.NewLine, _vm.LastReport.OwnedPaths);
            var answer = MessageBox.Show(
                $"Permanently delete these folders?\n\n{paths}",
                "Delete UAT copy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            await _vm.DeleteCopyCommand.ExecuteAsync(null);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // Closing requests cancellation and waits for the run to finish its cleanup instead of abandoning it.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm.IsRunning)
            {
                _vm.CancelCommand.Execute(null);
                MessageBox.Show("A run is in progress. It was asked to cancel; the window can be closed once cleanup finishes.",
                    "UAT", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
```

- [ ] **Step 6: Expose `CanDeleteCopy` on the view model** (the delete button binds to a plain property because `IRelayCommand` has no bindable `CanExecute` state)

Add to `UatUpgradeViewModel`:

```csharp
        public bool CanDeleteCopy => !IsRunning && LastReport is { OwnedPaths.Count: > 0 };

        partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanDeleteCopy));
        partial void OnLastReportChanged(UatReport? value) => OnPropertyChanged(nameof(CanDeleteCopy));
```

and in `DeleteCopyAsync`, after `LastReport.OwnedPaths.Clear();` add `OnPropertyChanged(nameof(CanDeleteCopy));`.

- [ ] **Step 7: Run the view-model tests**

Run: `dotnet test XamppUpdate.Tests --filter UatUpgradeViewModelTests`
Expected: PASS.

- [ ] **Step 8: Wire the step-1 button** in `MySqlUpdateWizardWindow.xaml`

Replace the lone `Next` button (`Grid.Column="2"`) with a panel holding the new button to its left:

```xml
                                    <StackPanel Grid.Column="2" Orientation="Horizontal">
                                        <Button Style="{StaticResource SecondaryButtonStyle}"
                                                Content="🧪 Real Test Upgrade"
                                                Command="{Binding RealTestUpgradeCommand}"
                                                IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}"
                                                Padding="16,10" FontSize="13" Margin="0,0,10,0"
                                                ToolTip="Copy MySQL to a new folder, run it on another port and rehearse the upgrade there. Production is not modified." />

                                        <Button Style="{StaticResource PrimaryButtonStyle}"
                                                Content="Next"
                                                Command="{Binding ProcessStep1Command}"
                                                IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}"
                                                Padding="24,10" FontSize="13.5" />
                                    </StackPanel>
```

- [ ] **Step 9: Add the command and event** to `MySqlWizardViewModel.cs`

Add near the other events:

```csharp
        public sealed record UatLaunchInfo(string IncomingRoot, string IncomingVersion);
        public event Action<UatLaunchInfo>? RequestOpenUatUpgrade;

        // Exposed so the window can build the UAT service without touching global settings.
        public ISettingsService SettingsServiceForUat => _settingsService;
        public IWindowsServiceManager ServiceManagerForUat => _serviceManager;
```

and the command (next to `ProcessStep1Async`):

```csharp
        [RelayCommand]
        public async Task RealTestUpgradeAsync()
        {
            if (string.IsNullOrWhiteSpace(SourcePathOrUrl))
            {
                ErrorMessage = "Please enter a download URL or select a local archive file.";
                return;
            }

            ErrorMessage = string.Empty;
            IsBusy = true;
            IsIndeterminateProgress = true;
            StatusMessage = "Preparing the incoming package for the UAT run...";
            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<UpdateProgressReport>(r => StatusMessage = r.Message);
                PreparedIncomingRoot = await _updateService.PrepareIncomingSourceAsync(SourcePathOrUrl, progress, _cts.Token);
                IncomingDetectedVersion = await _versionDetectionService.DetectMySqlVersionAsync(PreparedIncomingRoot);

                StatusMessage = "Source ready. Opening the UAT window...";
                RequestOpenUatUpgrade?.Invoke(new UatLaunchInfo(PreparedIncomingRoot, IncomingDetectedVersion));
                StatusMessage = "Ready.";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to prepare source: {ex.Message}";
                StatusMessage = "Preparation failed.";
            }
            finally
            {
                IsBusy = false;
                IsIndeterminateProgress = false;
            }
        }
```

- [ ] **Step 10: Host the dialog** in `MySqlUpdateWizardWindow.xaml.cs` constructor, after `viewModel.RequestClose += ...`

```csharp
            viewModel.RequestOpenUatUpgrade += info =>
            {
                var settings = viewModel.SettingsServiceForUat.CurrentSettings;
                string prodRoot = settings.MySql.InstallationPath;
                string iniPath = settings.MySql.IniPath;
                string? parent = System.IO.Path.GetDirectoryName(prodRoot.TrimEnd('\\', '/'));
                int prodPort = 3306;
                try
                {
                    if (System.IO.File.Exists(iniPath))
                    {
                        string? p = XamppUpdate.Services.Uat.UatIniRewriter.GetOption(
                            System.IO.File.ReadAllText(iniPath), "mysqld", "port");
                        if (int.TryParse(p, out int parsed) && parsed > 0) prodPort = parsed;
                    }
                }
                catch { /* keep default */ }

                var defaults = new XamppUpdate.Services.Uat.UatOptions
                {
                    ProductionRoot = prodRoot,
                    ProductionIniPath = iniPath,
                    ProductionServiceName = settings.MySql.ServiceName,
                    ProductionPort = prodPort,
                    IncomingRoot = info.IncomingRoot,
                    IncomingVersion = info.IncomingVersion,
                    TargetRoot = System.IO.Path.Combine(parent ?? prodRoot, "mysql_copy_upgrade"),
                    AdminUser = viewModel.MySqlUpgradeUser,
                    AdminPassword = viewModel.MySqlUpgradePassword
                };

                var service = new XamppUpdate.Services.Uat.UatUpgradeService(
                    viewModel.ServiceManagerForUat,
                    new XamppUpdate.Services.Uat.MySqlToolRunner(),
                    new XamppUpdate.Services.Uat.UatFileSystem());

                var uatWindow = new UatUpgradeWindow(new UatUpgradeViewModel(service, defaults)) { Owner = this };
                uatWindow.ShowDialog();
            };
```

- [ ] **Step 11: Build and run all tests**

Run: `dotnet build XamppUpdate.sln -v q` then `dotnet test XamppUpdate.Tests`
Expected: `Build succeeded.`, all tests pass.

- [ ] **Step 12: Commit** (only if asked)

```bash
git add XamppUpdate XamppUpdate.Tests
git commit -m "feat: Real Test Upgrade window and step-1 button"
```

---

### Task 11: Manual UAT checklist and final verification

**Files:**
- Create: `docs/superpowers/plans/2026-10-12-mysql-uat-manual-checklist.md`

- [ ] **Step 1: Write the checklist** (verbatim)

```markdown
# UAT manual verification - run on a disposable XAMPP copy (never first on production)

Setup: production MariaDB on 3306 with data; incoming package of the next version.

- [ ] Cold copy, production as a Windows service: production stops, folder copied, production service running again, copy on 13306 upgraded; `net start` shows no leftover `mysql_uat`.
- [ ] Same, production as a standalone process: restarted as a process (not as the service).
- [ ] Production initially stopped: stays stopped afterwards.
- [ ] Target folder already exists: stage 1 fails with "Target folder already exists"; production never stopped.
- [ ] Port busy (start a listener on 13306): stage 1 fails naming the port.
- [ ] Root password set: login fields used; wrong password fails stage 1/2 with production still running.
- [ ] Cancel during the copy: production back up; partial `mysql_copy_upgrade` listed for deletion; report says Cancelled.
- [ ] Service option off: no service created; copy runs as a process; stopped at the end.
- [ ] Failed upgrade (use an incompatible version): stage 7 FAILED, cleanup still OK, production untouched.
- [ ] While the copy runs, production queries still work on 3306; the copy answers on 13306 with the new version.
- [ ] Delete UAT copy: confirmation lists `mysql_copy_upgrade` and `mysql_copy_upgrade_old`; both removed; refuses while a copy process runs.
- [ ] Report file exists under `%LOCALAPPDATA%\XamppUpdate\uat-reports`, contains no password.
- [ ] After all runs: `Get-Process mysqld,mariadbd` shows only production; `Get-Service mysql_uat` finds nothing.
```

- [ ] **Step 2: Final automated verification**

Run: `dotnet build XamppUpdate.sln -v q` and `dotnet test XamppUpdate.Tests`
Expected: both succeed with zero warnings introduced by this work.

- [ ] **Step 3: Confirm production code paths are unchanged in behaviour**

Run the normal (non-UAT) wizard in **test mode** once against the disposable install and compare the step 3 log with the earlier run. Expected: same stages and messages for deploy/config/data.

- [ ] **Step 4: Commit** (only if asked)

```bash
git add docs/superpowers/plans/2026-10-12-mysql-uat-manual-checklist.md
git commit -m "docs: UAT manual verification checklist"
```

---

## Self-Review

**Spec coverage (Phase 1 items from section 10a):**
- Prerequisite fixes: path-segment matching, unreadable-process fallback (Task 2); strict shutdown result with fresh evidence (Task 5); target-explicit runner so production cannot be touched (Task 6; `RunMySqlUpgradeAsync` is deliberately left unchanged and the UAT uses `IMySqlToolRunner` instead); owned-service verification and manual-start registration (Task 6).
- Test project the `.sln` references (Task 1).
- Stages 1-8 cold copy only (Tasks 4, 8, 9); `!include` rejected (Tasks 3, 4); fresh deploy via shared helper (Task 7, used in Task 9 stage 5); ownership-safe cleanup and redacted report (Task 9); length-manifest verification (Tasks 8, 9); IPv4 exclusive-bind probe (Task 4).
- UI: step-1 button left of Next, options, stage list, log, Cancel/Open/Delete with confirmation (Task 10).
- Section 5 guarantees: restoration recorded before shutdown and in `finally` with its own budget (Task 9 `RunCleanupAsync`); never delete automatically and re-verify at delete time (Task 9 `DeleteCopyAsync`); service removed only if owned (Task 6); fresh log evidence (Task 5).
- Not in this plan by design (Phase 2): recovery journal, content-hash manifest, include resolution, live copy mode, IPv6 probe.

**Placeholder scan:** no TBD/TODO; every code step shows code. Two steps describe edits to existing methods by exact before/after text (Tasks 2, 5, 7) because the surrounding method bodies are long; the replaced text is quoted in full.

**Type consistency:** `MySqlShutdownReport` (Task 5) is used by the Task 9 fake and orchestrator with the same member names; `MySqlTarget`/`ToolResult`/`UatIdentity` (Tasks 4, 6) match their uses in Task 9; `IUatFileSystem` (Task 4, including `DeployNewFilesAsync`/`MigrateDataAsync`) matches `UatFileSystem` (Task 8, which delegates to `MySqlDeployHelper` from Task 7) and both fakes (Tasks 4, 9). Task 8 therefore depends on Task 7; the task order already respects that.

**Review-focus coverage:** (1) password with special characters: Task 6 args/env tests; (2) spaces/non-ASCII paths: Task 2 and Task 3 theories; (3) BOM/CRLF/no `[client]`: Task 3; (4) production originally stopped: Task 9; (5) cancel during copy: Task 9; (6) trailing separator/case on the target: Task 4.

**Known risk to confirm during execution:** `mysqld --install-manual` and the `event_scheduler=OFF` option must be accepted by both the old (10.4) and new binaries; stage 4/6 will fail loudly with the server's error text if an option is rejected, which is the intended signal.
