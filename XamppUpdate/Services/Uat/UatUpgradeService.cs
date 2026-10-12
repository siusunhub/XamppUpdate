using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamppUpdate.Services;

namespace XamppUpdate.Services.Uat;

public interface IUatUpgradeService
{
    Task<UatReport> RunAsync(UatOptions options, IProgress<UatEvent> progress, CancellationToken ct);
    /// <summary>Deletes only the trees recorded in <paramref name="report"/>, after re-verifying ownership and that nothing runs there.</summary>
    Task<bool> DeleteCopyAsync(UatReport report, IProgress<string>? progress);
}

/// <summary>Outcome of classifying production. <see cref="Ambiguity"/> non-null means a hard stop: nothing is touched.</summary>
public sealed record UatProductionDecision(bool Running, bool ViaService, string? Ambiguity);

public static class UatProductionState
{
    /// <summary>
    /// Pure classification of production from observed facts. Ownership is proven from the service's registered
    /// executable and a process under the production root, never inferred from a service name.
    /// </summary>
    public static UatProductionDecision Decide(
        bool serviceExists, string? serviceImagePath, System.ServiceProcess.ServiceControllerStatus? serviceStatus,
        bool processUnderRoot, string productionRoot, string serviceName)
    {
        if (!serviceExists)
            return new UatProductionDecision(processUnderRoot, false, null);

        if (string.IsNullOrWhiteSpace(serviceImagePath))
            return new UatProductionDecision(false, false,
                $"Service '{serviceName}' exists but its registered executable could not be read. Nothing was changed.");

        string exe = ServiceImagePath.ExtractExecutable(serviceImagePath);
        if (!PathSafety.IsSameOrUnder(exe, productionRoot))
            return new UatProductionDecision(false, false,
                $"Service '{serviceName}' is registered to another installation ({exe}). Nothing was changed.");

        if (serviceStatus is not (System.ServiceProcess.ServiceControllerStatus.Running or System.ServiceProcess.ServiceControllerStatus.Stopped))
            return new UatProductionDecision(false, false,
                $"Service '{serviceName}' is in a transitional or unknown state; retry when it is Running or Stopped.");

        if (serviceStatus == System.ServiceProcess.ServiceControllerStatus.Running)
        {
            return processUnderRoot
                ? new UatProductionDecision(true, true, null)
                : new UatProductionDecision(false, false,
                    $"Service '{serviceName}' is Running but no database process was found under {productionRoot}; ownership is ambiguous. Nothing was changed.");
        }

        // Stopped service: a process under the root is a standalone daemon, otherwise production is stopped.
        return new UatProductionDecision(processUnderRoot, false, null);
    }
}

public sealed class UatUpgradeService : IUatUpgradeService
{
    /// <summary>Pause between admin-login attempts while production finishes starting (tests shorten it).</summary>
    public TimeSpan LoginRetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Poll interval while waiting for an in-flight production stop to finish (tests shorten it).</summary>
    public TimeSpan PendingStopPoll { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Upper bound for waiting on a stop that was requested but not confirmed.</summary>
    public TimeSpan PendingStopMaxWait { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Log sizes before production is restarted (tests replace the file-system reads).</summary>
    public Func<string, IReadOnlyDictionary<string, long>> LogSnapshot { get; init; } = ErrorLogEvidence.SnapshotAll;
    /// <summary>Reads the restart log for crash recovery (tests replace it).</summary>
    public Func<IReadOnlyDictionary<string, long>, string, Task<(ErrorLogEvidence.StartupVerdict Verdict, string Detail)>> StartupCheck { get; init; } =
        (before, root) => ErrorLogEvidence.CheckStartupAsync(before, root, TimeSpan.FromSeconds(60));

    private sealed class ProductionNotRunningException(string message) : InvalidOperationException(message);
    private sealed class ProductionLoginUnverifiedException(string message) : InvalidOperationException(message);
    /// <summary>A stop was requested by this run and the process is still alive (or the service is not Running).</summary>
    private sealed class ProductionShutdownInProgressException(string message) : InvalidOperationException(message);
    /// <summary>Shared between restore attempts: true once THIS run started production's process during restoration.</summary>
    private sealed class RestoreState { public bool StartedByUs; }

    /// <summary>Synchronous progress sink: service-manager guidance (exact sc.exe commands, exit codes) reaches the report.</summary>
    private sealed class NoteSink(UatReport report, IProgress<UatEvent> progress) : IProgress<string>
    {
        public int Stage = 1;
        public void Report(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (report.Notes) report.Notes.Add($"[{Stage}] {value}");
            var st = report.Stages[Stage - 1];
            progress.Report(new UatEvent(st.Number, st.Name, st.Status, value));
        }
    }

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

    public async Task<UatReport> RunAsync(UatOptions options, IProgress<UatEvent> progress, CancellationToken ct)
    {
        // The tool runner rejects a blank user: normalise once and use the same account for every call.
        var o = string.IsNullOrWhiteSpace(options.AdminUser) ? options with { AdminUser = "root" } : options;

        var report = new UatReport { SourceVersion = string.Empty, TargetVersion = o.IncomingVersion };
        foreach (var (name, i) in StageNames.Select((n, i) => (n, i + 1)))
            report.Stages.Add(new UatStageResult { Number = i, Name = name });

        // An invalid path must become a failed preflight, not an exception: fall back to the raw text (preflight rejects it).
        string target;
        try { target = PathSafety.Canonical(o.TargetRoot); } catch (Exception) { target = o.TargetRoot; }
        report.TargetRoot = target;
        report.ProductionRoot = o.ProductionRoot;
        string oldTree = target + "_old";
        var targetInfo = new MySqlTarget(target, Path.Combine(target, "bin", "my.ini"), o.Port);

        // Placeholder until stage 1 has proven production's state; nothing is touched before then.
        var prod = new ProdState(false, false, o.ProductionServiceName, o.ProductionRoot, o.ProductionIniPath, o.ProductionPort);
        var sink = new NoteSink(report, progress);
        var restoreState = new RestoreState();
        report.ProductionState = "unknown (not determined)";

        bool stoppedByUs = false;
        bool serviceCreated = false;
        bool installAttempted = false;
        bool abort = false;

        async Task<bool> Stage(int n, Func<Task<string>> body)
        {
            var st = report.Stages[n - 1];
            sink.Stage = n;
            if (abort) { st.Status = UatStageStatus.Skipped; Raise(progress, st); return false; }

            st.Status = UatStageStatus.Running; Raise(progress, st);
            var started = DateTime.UtcNow;
            try
            {
                // Never begin a stage (in particular never stop production) once cancellation was requested.
                ct.ThrowIfCancellationRequested();
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
            await Stage(1, async () =>
            {
                var issues = new List<string>();

                // Production state is proven, not inferred (a service name alone proves nothing).
                UatProductionDecision capture;
                try
                {
                    capture = await CaptureProductionAsync(o);
                }
                catch (Exception ex)
                {
                    report.ProductionState = $"unknown ({ex.Message})";
                    throw;
                }
                if (capture.Ambiguity != null)
                {
                    issues.Add(capture.Ambiguity);
                    report.ProductionState = $"unknown ({capture.Ambiguity})";
                }
                else
                {
                    prod = prod with { Running = capture.Running, ViaService = capture.ViaService };
                    report.ProductionState = prod.Running ? (prod.ViaService ? "running (service)" : "running (process)") : "stopped";
                }

                issues.AddRange(UatPreflight.Validate(o, _fs, _isPortFree).Select(i => i.Message));

                // An existing service of that name is never reused, stopped or removed.
                if (o.TestAsService && !string.IsNullOrWhiteSpace(o.ServiceName) && _services.ServiceExists(o.ServiceName))
                    issues.Add($"A Windows service named '{o.ServiceName}' already exists. Choose another name or remove it manually.");

                if (issues.Count > 0) throw new InvalidOperationException(string.Join(" | ", issues));

                // Production is about to be stopped: the admin login must already work (otherwise there is no verified flush).
                if (prod.Running)
                {
                    var login = await _services.TestMySqlLoginAsync(prod.Root, o.AdminUser, o.AdminPassword, prod.Port);
                    if (!login.Success)
                        throw new InvalidOperationException(
                            "Admin login to production failed, so a verified flush and shutdown is not possible. Production was not touched. " + login.Message);
                }
                return "All checks passed.";
            });

            // ---- 2 Copy (cold) ----
            await Stage(2, async () =>
            {
                if (prod.Running)
                {
                    stoppedByUs = true;          // restoration responsibility recorded BEFORE shutdown is requested
                    var sd = await _services.SafeShutdownMySqlDetailedAsync(
                        prod.ServiceName, prod.Root, o.AdminUser, o.AdminPassword, prod.Port, sink);
                    if (!sd.StrictSuccess)
                    {
                        // Only a deliberate abort (flush not applied, server left running) means there is nothing to restore.
                        if (sd.Aborted) stoppedByUs = false;
                        throw new InvalidOperationException(
                            "Production did not shut down cleanly (login, flush, graceful exit required). Nothing was copied.");
                    }
                }

                // Validate the source BEFORE the target is recorded as owned: a source failure happens before any
                // destination exists, so a folder someone else created at the target must never become ours.
                if (!_fs.PathExists(prod.Root))
                    throw new InvalidOperationException($"Production folder not found: {prod.Root}. Nothing was copied.");
                var links = _fs.FindReparsePoints(prod.Root);
                if (links.Count > 0)
                    throw new InvalidOperationException($"Production folder contains links/junctions (e.g. {links[0]}); not supported. Nothing was copied.");

                // Recorded before the copy starts, so a partial or cancelled copy can be inspected and deleted.
                report.OwnedPaths.Add(target);
                try
                {
                    await _fs.CopyTreeAsync(prod.Root, target, null, ct);
                }
                catch (UatTargetReservationException)
                {
                    // The folder was created by someone else: it never becomes ours (not deletable, not stopped).
                    report.OwnedPaths.RemoveAll(p => p.Equals(target, StringComparison.OrdinalIgnoreCase));
                    throw;
                }

                var src = await _fs.BuildManifestAsync(prod.Root, ct);
                var dst = await _fs.BuildManifestAsync(target, ct);
                if (src.Count != dst.Count || src.Any(kv => !dst.TryGetValue(kv.Key, out var len) || len != kv.Value))
                    throw new InvalidOperationException("Copy verification failed: manifests differ.");

                // Downtime ends here: bring production back before doing anything else.
                if (stoppedByUs)
                {
                    var restartMark = LogSnapshot(prod.Root);
                    await RestoreProductionAsync(prod, o, waitForPendingStop: true, restoreState, sink);
                    stoppedByUs = false;

                    // The copy is only a clean rehearsal source if the restart needed no crash recovery.
                    var (verdict, startDetail) = await StartupCheck(restartMark, prod.Root);
                    if (verdict != ErrorLogEvidence.StartupVerdict.CleanStart)
                        throw new InvalidOperationException(
                            $"Production was restarted, but its log does not confirm a clean shutdown: {startDetail}. " +
                            "The copy is not a valid rehearsal source. Production is running; nothing was upgraded.");
                    return $"Copied {dst.Count} files; production restored; restart log shows no crash recovery.";
                }
                return $"Copied {dst.Count} files.";
            });

            // ---- 3 Rewrite config ----
            await Stage(3, () =>
            {
                int changed = 0;
                // The production tmpdir (e.g. C:\xampp\tmp) is shared with production, so the copy gets its own temp folder.
                // Written with forward slashes: in an option file a backslash starts an escape, so "\tmp" would become a TAB.
                EnsureCopyTmp(target);
                string copyTmpIni = Path.Combine(target, "tmp").Replace('\\', '/');

                foreach (var file in _fs.FindConfigFiles(target))
                {
                    string before = _fs.ReadAllText(file);
                    string after = UatIniRewriter.RewriteRoot(before, prod.Root, target);
                    after = UatIniRewriter.SetOption(after, "mysqld", "tmpdir", copyTmpIni);
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
                bool started = await _services.StartMySqlProcessAsync(target, targetInfo.IniPath, sink);
                if (!started || !_services.IsMySqlProcessRunning(target))
                    throw new InvalidOperationException("The copy did not start with its original binaries (see its error log).");

                await VerifyIdentityAsync(targetInfo, o, expectedVersionPrefix: null, ct, sink);

                var sd = await _services.SafeShutdownMySqlDetailedAsync(string.Empty, target, o.AdminUser, o.AdminPassword, o.Port, sink);
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

                // The fresh tree has no temp folder yet; the retained config points tmpdir at it.
                EnsureCopyTmp(target);

                bool migrated = await _fs.MigrateDataAsync(Path.Combine(oldTree, "data"), Path.Combine(target, "data"), ct);
                if (!migrated) throw new InvalidOperationException("Copied data folder was not found to migrate.");

                return $"Deployed {files} files; data migrated; old tree kept as {Path.GetFileName(oldTree)}.";
            });

            // ---- 6 Start the new version ----
            await Stage(6, async () =>
            {
                EnsureCopyTmp(target);
                if (o.TestAsService)
                {
                    // Only a successful registration by THIS run makes the service ours to remove.
                    installAttempted = true;
                    serviceCreated = await _services.InstallOwnedMySqlServiceManualAsync(target, o.ServiceName, targetInfo.IniPath, sink);
                    if (!serviceCreated) throw new InvalidOperationException($"Could not register service '{o.ServiceName}'.");
                    report.OwnedServiceName = o.ServiceName;
                    if (!await _services.StartServiceAsync(o.ServiceName, TimeSpan.FromSeconds(60), sink))
                        throw new InvalidOperationException($"Service '{o.ServiceName}' did not start.");
                }
                else if (!await _services.StartMySqlProcessAsync(target, targetInfo.IniPath, sink))
                {
                    throw new InvalidOperationException("The new version did not start (see the copy's error log).");
                }

                var id = await VerifyIdentityAsync(targetInfo, o, o.IncomingVersion, ct, sink);
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
                lock (report.Notes) report.Notes.Add($"mysql.event.definer type after upgrade: {definer.StdOut.Trim()} (informational)");
                return "mariadb-upgrade completed; InnoDB available.";
            });
        }
        finally
        {
            // ---- 8 Cleanup: always runs, with its own budget unaffected by the cancelled token ----
            await RunCleanupAsync(report, o, prod, targetInfo, serviceCreated, installAttempted, stoppedByUs, progress, sink, restoreState);
        }

        report.Success = report.Stages.All(s => s.Status == UatStageStatus.Ok) && report.ProductionRestored;
        report.FinishedUtc = DateTime.UtcNow;
        try { UatReportWriter.Write(report, _reportDirectory(o.TargetRoot), o.AdminPassword); } catch { /* reporting must not mask the result */ }
        return report;
    }

    private async Task RunCleanupAsync(
        UatReport report, UatOptions o, ProdState prod, MySqlTarget target,
        bool serviceCreated, bool installAttempted, bool stoppedByUs, IProgress<UatEvent> progress, NoteSink sink, RestoreState restoreState)
    {
        var st = report.Stages[7];
        sink.Stage = 8;
        st.Status = UatStageStatus.Running; Raise(progress, st);
        var started = DateTime.UtcNow;
        var problems = new List<string>();

        // Independent obligations: each one is attempted even if another fails.
        // 1. Production first: it is the most important and must not wait behind the copy's shutdown.
        try
        {
            if (stoppedByUs)
                await RestoreProductionAsync(prod, o, waitForPendingStop: true, restoreState, sink);

            // Verify, do not assume: production that was running when we started must be running now.
            if (prod.Running && !_services.IsMySqlProcessRunning(prod.Root))
                throw new ProductionNotRunningException("production process is not running.");
            report.ProductionRestored = true;
        }
        catch (ProductionShutdownInProgressException ex)
        {
            // Not restored: mysqld may still exit after this run ends, leaving production down.
            report.ProductionRestored = false;
            report.RecoveryInstructions =
                $"mysqld is still shutting down after the stop request. Wait until the process has exited, then run: {ManualStartCommand(prod)}. ({ex.Message})";
            problems.Add(report.RecoveryInstructions);
        }
        catch (ProductionLoginUnverifiedException ex)
        {
            // The process is up in its original mode: nothing to start manually, but the login must be checked by hand.
            report.ProductionRestored = true;
            problems.Add(ex.Message);
        }
        catch (Exception ex)
        {
            report.ProductionRestored = false;
            report.RecoveryInstructions =
                $"Production could NOT be restarted automatically. Start it manually: {ManualStartCommand(prod)}. ({ex.Message})";
            problems.Add(report.RecoveryInstructions);
        }

        // 2. Stop the copy, but only if this run created that tree: a pre-existing folder is never touched.
        bool ownsTarget = report.OwnedPaths.Any(p => p.Equals(target.Root, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (ownsTarget && _services.IsMySqlProcessRunning(target.Root))
            {
                var sd = await _services.SafeShutdownMySqlDetailedAsync(
                    serviceCreated ? o.ServiceName : string.Empty, target.Root, o.AdminUser, o.AdminPassword, o.Port, sink);
                if (!sd.ProcessExited) problems.Add($"The copy is still running on port {o.Port}; stop it manually.");
            }
        }
        catch (Exception ex) { problems.Add("Stopping the copy failed: " + ex.Message); }

        // 3. Remove the temporary service, only when this run registered it.
        try
        {
            if (serviceCreated)
            {
                if (!await _services.RemoveServiceIfOwnedAsync(o.ServiceName, target.Root, sink))
                    problems.Add($"Service '{o.ServiceName}' was not removed. Run: sc.exe delete \"{o.ServiceName}\"");
            }
            else if (o.TestAsService && installAttempted && _services.ServiceExists(o.ServiceName))
            {
                // Registration was reported as failed yet the name exists: ownership is unproven, so it is NOT deleted here.
                problems.Add($"Service '{o.ServiceName}' exists after a failed registration and was not removed (ownership unproven). " +
                             $"If it is a leftover of this run, run: sc.exe delete \"{o.ServiceName}\"");
            }
        }
        catch (Exception ex) { problems.Add("Removing the service failed: " + ex.Message); }

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

    private string ManualStartCommand(ProdState prod)
    {
        if (prod.ViaService)
            return $"net start \"{prod.ServiceName}\" (or: sc.exe start \"{prod.ServiceName}\")";

        string bin = Path.Combine(prod.Root, "bin");
        string exe = _fs.PathExists(Path.Combine(bin, "mariadbd.exe")) ? "mariadbd.exe" : "mysqld.exe";
        return $"\"{Path.Combine(bin, exe)}\" --defaults-file=\"{prod.Ini}\" --standalone";
    }

    private async Task<UatProductionDecision> CaptureProductionAsync(UatOptions o)
    {
        bool exists = _services.ServiceExists(o.ProductionServiceName);
        string? image = null;
        System.ServiceProcess.ServiceControllerStatus? status = null;
        if (exists)
        {
            image = _services.GetServiceImagePath(o.ProductionServiceName);
            status = await _services.GetServiceStatusAsync(o.ProductionServiceName);
        }
        bool processUnderRoot = _services.IsMySqlProcessRunning(o.ProductionRoot);
        return UatProductionState.Decide(exists, image, status, processUnderRoot, o.ProductionRoot, o.ProductionServiceName);
    }

    /// <summary>
    /// Brings production back in exactly the mode it was in, then verifies process + login. Uses its own bounded
    /// budget, independent of the run's (possibly cancelled) token. A process that is already running is never restarted.
    /// </summary>
    private async Task RestoreProductionAsync(
        ProdState prod, UatOptions o, bool waitForPendingStop, RestoreState state, IProgress<string>? sink)
    {
        using var budget = new CancellationTokenSource(PendingStopMaxWait + TimeSpan.FromMinutes(5));
        var token = budget.Token;

        // A stop that was requested but not confirmed may still be in flight: let it finish before deciding.
        if (waitForPendingStop && !state.StartedByUs)
        {
            var deadline = DateTime.UtcNow + PendingStopMaxWait;
            while ((_services.IsMySqlProcessRunning(prod.Root) ||
                    (prod.ViaService && (await _services.GetServiceStatusAsync(prod.ServiceName)) == System.ServiceProcess.ServiceControllerStatus.StopPending))
                   && DateTime.UtcNow < deadline)
                await Task.Delay(PendingStopPoll, token);
        }

        bool serviceStillStopping = prod.ViaService &&
            (await _services.GetServiceStatusAsync(prod.ServiceName)) == System.ServiceProcess.ServiceControllerStatus.StopPending;

        if (!_services.IsMySqlProcessRunning(prod.Root) && !serviceStillStopping)
        {
            bool ok = prod.ViaService
                ? await _services.StartServiceAsync(prod.ServiceName, TimeSpan.FromSeconds(120), sink)
                : await _services.StartMySqlProcessAsync(prod.Root, prod.Ini, sink);

            if (!ok || !_services.IsMySqlProcessRunning(prod.Root))
                throw new ProductionNotRunningException("production process is not running after restart.");
            state.StartedByUs = true;
        }
        else if (!state.StartedByUs)
        {
            // This run asked production to stop and the process or service is still there after the bounded wait:
            // it is (still) shutting down, so it must not be reported as restored.
            throw new ProductionShutdownInProgressException(
                "the production process or service was still running after the bounded wait following the stop request");
        }

        // The server may need a few seconds after start before it accepts the admin login.
        (bool Success, string Message) login = (false, string.Empty);
        for (int i = 0; i < 10; i++)
        {
            login = await _services.TestMySqlLoginAsync(prod.Root, o.AdminUser, o.AdminPassword, prod.Port);
            if (login.Success) break;
            if (!_services.IsMySqlProcessRunning(prod.Root))
                throw new ProductionNotRunningException("production process stopped again after restart.");
            await Task.Delay(LoginRetryDelay, token);
        }

        // In service mode "restored" also requires the service itself to be Running (not StopPending etc.).
        if (prod.ViaService)
        {
            var status = await _services.GetServiceStatusAsync(prod.ServiceName);
            if (status != System.ServiceProcess.ServiceControllerStatus.Running)
                throw new ProductionShutdownInProgressException($"service '{prod.ServiceName}' status is {status?.ToString() ?? "unknown"}, not Running");
        }

        if (!login.Success)
            throw new ProductionLoginUnverifiedException(
                "production process is running but the admin login could not be verified: " + login.Message);
    }

    private async Task<UatIdentity> VerifyIdentityAsync(MySqlTarget t, UatOptions o, string? expectedVersionPrefix, CancellationToken ct, IProgress<string>? sink = null)
    {
        // Poll while the server finishes recovery / startup. Every few attempts the reason is reported, so a wait is visible.
        const int attempts = 90;
        ToolResult last = new(-1, string.Empty, "no attempt");
        bool processExited = false;
        for (int i = 0; i < attempts; i++)
        {
            last = await _tools.QueryAsync(t, o.AdminUser, o.AdminPassword, UatIdentity.Sql, ct);
            if (last.Ok && UatIdentity.Parse(last.StdOut) != null) break;

            // A server that exited will never answer: fail now instead of waiting out the whole budget.
            if (!_services.IsMySqlProcessRunning(t.Root))
            {
                processExited = true;
                break;
            }

            if (i % 5 == 0)
                sink?.Report($"Waiting for the server on port {t.Port} (attempt {i + 1}/{attempts}): {Truncate(last.Combined)}");
            await Task.Delay(i == 0 ? 0 : 2000, ct);
        }

        var id = UatIdentity.Parse(last.StdOut)
                 ?? throw new InvalidOperationException(
                     (processExited
                         ? $"The copy's mysqld exited before it accepted connections on port {t.Port}"
                         : $"Could not read the server identity on port {t.Port} after {attempts} attempts") +
                     $": {Truncate(last.Combined)}. Last lines of the copy's error log: {ErrorLogTail(t.Root)}");

        if (!id.Matches(t.Port, t.Root))
            throw new InvalidOperationException(
                $"Connected server is not the UAT copy (port {id.Port}, basedir {id.BaseDir}, datadir {id.DataDir}).");

        if (!string.IsNullOrEmpty(expectedVersionPrefix) && !VersionMatches(id.Version, expectedVersionPrefix))
            throw new InvalidOperationException($"Server version {id.Version} does not match the incoming version {expectedVersionPrefix}.");

        return id;
    }

    /// <summary>
    /// "13.1.1-MariaDB" matches "13.1.1.0" and "13.1" matches "13.1.1". Build suffixes and a trailing ".0" are ignored.
    /// </summary>
    public static bool VersionMatches(string serverVersion, string expected)
    {
        if (serverVersion.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return true;
        return VersionKey(serverVersion) == VersionKey(expected);
    }

    private static string VersionKey(string version)
    {
        string core = version.Trim().Split('-')[0];
        return Version.TryParse(core, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : core;
    }

    /// <summary>
    /// The copy's own temp folder (tmpdir points here). Created whenever a tree is (re)built, because a missing
    /// tmpdir stops InnoDB from starting with "Unable to create temporary file".
    /// </summary>
    private void EnsureCopyTmp(string target)
    {
        string tmp = Path.Combine(target, "tmp");
        if (!_fs.PathExists(tmp)) _fs.CreateDirectory(tmp);
    }

    /// <summary>Last few lines of the copy's error log, read without locking it out, so a failed start explains itself.</summary>
    private static string ErrorLogTail(string root)
    {
        try
        {
            var log = ErrorLogEvidence.FindActiveLog(root);
            if (log == null) return "(no error log found)";

            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? "(error log is empty)" : string.Join(" | ", lines.TakeLast(12));
        }
        catch (Exception ex)
        {
            return $"(could not read the error log: {ex.Message})";
        }
    }

    public async Task<bool> DeleteCopyAsync(UatReport report, IProgress<string>? progress)
    {
        // Last line of defence: never delete a folder that is not a UAT copy next to production (e.g. php, apache, mysql).
        foreach (var path in report.OwnedPaths)
        {
            var problems = UatTargetGuard.Check(path, report.ProductionRoot);
            if (problems.Count > 0)
            {
                progress?.Report($"REFUSED: {path} is not a UAT copy: {string.Join("; ", problems)}.");
                return false;
            }
        }

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
            // Only the service this run registered, and only if its program lives in this run's copy folder.
            if (string.IsNullOrEmpty(report.TargetRoot))
            {
                progress?.Report($"REFUSED: service '{report.OwnedServiceName}' exists but the copy folder is unknown. Remove it manually.");
                return false;
            }
            progress?.Report($"Removing test service '{report.OwnedServiceName}'...");
            if (!await _services.RemoveServiceIfOwnedAsync(report.OwnedServiceName, report.TargetRoot, progress))
            {
                progress?.Report($"REFUSED: could not remove service '{report.OwnedServiceName}'. Run: sc.exe delete \"{report.OwnedServiceName}\"");
                return false;
            }
        }

        try
        {
            foreach (var path in report.OwnedPaths)
            {
                if (_fs.PathExists(path) && _fs.FindReparsePoints(path).Count > 0)
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
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            progress?.Report($"FAILED: {ex.Message}");
            return false;
        }
    }

    private static void Raise(IProgress<UatEvent> progress, UatStageResult s) =>
        progress.Report(new UatEvent(s.Number, s.Name, s.Status, s.Message));

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "...";
}
