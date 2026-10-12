using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    /// <summary>Copies the tree to a new, empty destination; rejects linked content and an existing destination.
    /// On cancellation or failure a partial tree (and the <c>.uat-copy-in-progress</c> marker) remains at the destination;
    /// the CALLER must record the destination as owned BEFORE calling so it can be inspected or deleted.</summary>
    Task CopyTreeAsync(string source, string destination, IProgress<string>? progress, CancellationToken ct);
    /// <summary>Relative path (lower-cased, '/' separators) to file length.</summary>
    Task<IReadOnlyDictionary<string, long>> BuildManifestAsync(string root, CancellationToken ct);
    /// <summary>Deploys the incoming package (excluding data\) into <paramref name="destRoot"/>; returns files copied.</summary>
    Task<int> DeployNewFilesAsync(string incomingRoot, string destRoot, CancellationToken ct);
    /// <summary>Moves/copies the data folder; false when the source does not exist.</summary>
    Task<bool> MigrateDataAsync(string srcDataDir, string destDataDir, CancellationToken ct);
}

/// <summary>Thrown by CopyTreeAsync ONLY when the destination could not be reserved (it already existed, was not empty, or
/// the in-progress marker collided): the folder belongs to someone else and must never be treated as owned.</summary>
public sealed class UatTargetReservationException : IOException
{
    public UatTargetReservationException(string message) : base(message) { }
    public UatTargetReservationException(string message, Exception inner) : base(message, inner) { }
}
