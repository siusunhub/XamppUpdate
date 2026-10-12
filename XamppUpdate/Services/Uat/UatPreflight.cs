using System;
using System.Collections.Generic;
using System.IO;

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

        // ---- paths (validation never throws: invalid paths become issues) ----
        string? production = TryCanonical(o.ProductionRoot);
        string? incoming = TryCanonical(o.IncomingRoot);
        string? target = null;

        if (production == null)
            Add("ProductionPathInvalid", $"Production folder path is not valid: '{o.ProductionRoot}'.");
        if (incoming == null)
            Add("IncomingPathInvalid", $"Incoming package folder path is not valid: '{o.IncomingRoot}'.");

        if (string.IsNullOrWhiteSpace(o.TargetRoot))
        {
            Add("TargetEmpty", "Target folder is empty.");
        }
        else
        {
            target = TryCanonical(o.TargetRoot);
            if (target == null)
            {
                Add("TargetInvalid", $"Target folder path is not valid: '{o.TargetRoot}'.");
            }
            else if (!Path.IsPathFullyQualified(o.TargetRoot))
            {
                Add("TargetNotAbsolute", $"Target folder must be an absolute path: {o.TargetRoot}");
                target = null;
            }
        }

        if (target != null)
        {
            string? rootOfTarget = Path.GetPathRoot(target);

            if (rootOfTarget != null && target.Equals(PathSafety.Canonical(rootOfTarget), StringComparison.OrdinalIgnoreCase))
                Add("TargetIsRoot", "Target folder cannot be a drive root.");

            if (production != null && PathSafety.Overlaps(target, production))
                Add("TargetOverlapsProduction", $"Target folder overlaps the production folder: {target}");

            if (incoming != null && PathSafety.Overlaps(target, incoming))
                Add("TargetOverlapsIncoming", $"Target folder overlaps the incoming package folder: {target}");

            if (production != null)
            {
                foreach (var problem in UatTargetGuard.Check(target, production))
                    Add("TargetNotAllowed", $"Target folder refused: {problem}. Nothing was changed.");
            }

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
        if (production != null)
        {
            var reparse = fs.FindReparsePoints(o.ProductionRoot);
            if (reparse.Count > 0)
                Add("LinkedContent", $"Production folder contains links/junctions (e.g. {reparse[0]}); not supported.");
        }

        if (!string.IsNullOrWhiteSpace(o.ProductionIniPath) && fs.PathExists(o.ProductionIniPath))
        {
            if (UatIniRewriter.ContainsInclude(fs.ReadAllText(o.ProductionIniPath)))
                Add("IncludeUnsupported", "The production ini uses !include/!includedir, which is not supported in this version.");
        }

        // ---- disk ----
        if (target != null && production != null && incoming != null)
        {
            long need = RequiredBytes(fs.GetDirectorySize(o.ProductionRoot), fs.GetDirectorySize(o.IncomingRoot));
            long free = fs.GetFreeBytes(target);
            if (free < need)
                Add("DiskSpace", $"Not enough free disk space on the target volume: need about {need:N0} bytes, free {free:N0}.");
        }

        return issues;
    }

    private static string? TryCanonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = PathSafety.Canonical(path);
            // NTFS limits a single path component to 255 characters; GetFullPath does not enforce it.
            foreach (var part in full.Split('\\', '/'))
                if (part.Length > 255) return null;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
