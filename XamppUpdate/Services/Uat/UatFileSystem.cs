using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services.Uat;

/// <summary>
/// Real filesystem for the UAT run. Linked content (junctions/symlinks) is rejected, never followed or skipped.
/// <see cref="CopyTreeAsync"/> leaves a partial tree plus the <c>.uat-copy-in-progress</c> marker at the destination
/// on cancellation or failure; the caller must record the destination as owned BEFORE calling it.
/// </summary>
public sealed class UatFileSystem : IUatFileSystem
{
    internal const string CopyMarkerName = ".uat-copy-in-progress";

    /// <summary>
    /// Depth-first walk yielding every entry (pattern "*", so extensionless files are included). Reparse-point
    /// directories are yielded but never descended into. Unreadable entries throw (nothing is swallowed).
    /// </summary>
    private static IEnumerable<FileSystemInfo> WalkEntries(string root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var e in dir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                yield return e;
                if (e is DirectoryInfo sub && !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    stack.Push(sub);
            }
        }
    }

    private static bool IsLink(FileSystemInfo e) => e.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static IOException LinkedContent(string path) =>
        new IOException($"Linked content is not supported: {path}");

    /// <summary>Entries of the tree; throws IOException when a reparse point is met.</summary>
    private static IEnumerable<FileSystemInfo> WalkRejectingLinks(string root)
    {
        foreach (var e in WalkEntries(root))
        {
            if (IsLink(e)) throw LinkedContent(e.FullName);
            yield return e;
        }
    }

    public bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    public long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var e in WalkRejectingLinks(path))
            if (e is FileInfo f) total += f.Length;
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
        foreach (var e in WalkEntries(root))
            if (IsLink(e)) result.Add(e.FullName);
        return result;
    }

    public IReadOnlyList<string> FindConfigFiles(string root) =>
        WalkEntries(root)
            .Where(e => e is FileInfo && !IsLink(e) &&
                        (e.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                         e.Name.EndsWith(".cnf", StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.FullName)
            .ToList();

    public string ReadAllText(string path) => FileEncodingHelper.ReadAllText(path);
    public void WriteAllText(string path, string content) =>
        FileEncodingHelper.WriteAllTextPreservingEncoding(path, content);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public void DeleteTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Refusing to delete: path is empty.", nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"Refusing to delete a path that is not fully qualified: {path}", nameof(path));
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root != null && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Refusing to delete a drive or share root: {path}");

        if (!Directory.Exists(full)) return;

        if (IsLink(new DirectoryInfo(full)))
            throw new IOException($"Refusing to delete a linked folder: {path}");
        foreach (var e in WalkEntries(full))
            if (IsLink(e))
                throw new IOException($"Refusing to delete a tree containing linked content: {e.FullName}");

        Directory.Delete(full, true);
    }

    public async Task CopyTreeAsync(string source, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        // Validate the source BEFORE creating anything at the destination.
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source folder not found: {source}");
        if (IsLink(new DirectoryInfo(source)))
            throw LinkedContent(source);
        var entries = WalkRejectingLinks(source).ToList();

        if (PathExists(destination))
            throw new UatTargetReservationException($"Target folder already exists: {destination}");

        Directory.CreateDirectory(destination);
        // CreateDirectory is silent when the folder already exists (e.g. created by a racing caller), so require it empty.
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new UatTargetReservationException($"Target folder is not empty: {destination}");

        // Exclusive marker: a second copier hits an IOException here; on failure/cancel it stays and flags a partial copy.
        string markerPath = Path.Combine(destination, CopyMarkerName);
        FileStream marker;
        try
        {
            marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new UatTargetReservationException($"Could not reserve the target folder (marker already present or locked): {destination}", ex);
        }
        bool success = false;
        try
        {
            var files = entries.OfType<FileInfo>().ToList();
            int done = 0;
            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(source, fi.FullName);
                string dest = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                await using var src = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
                await src.CopyToAsync(dst, 1 << 20, ct);

                done++;
                if (done % 50 == 0 || done == files.Count)
                    progress?.Report($"Copied {done}/{files.Count} files");
            }

            // Empty directories are part of the tree (e.g. data\test).
            foreach (var dir in entries.OfType<DirectoryInfo>())
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir.FullName)));
            success = true;
        }
        finally
        {
            await marker.DisposeAsync();
            if (success) File.Delete(markerPath);
        }
    }

    public Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string root, CancellationToken ct)
    {
        return Task.Run<IReadOnlyDictionary<string, long>>(() =>
        {
            var map = new Dictionary<string, long>();
            foreach (var e in WalkRejectingLinks(root))
            {
                ct.ThrowIfCancellationRequested();
                if (e is not FileInfo f) continue;
                string rel = Path.GetRelativePath(root, f.FullName).Replace('\\', '/').ToLowerInvariant();
                map[rel] = f.Length;
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
