using System;
using System.IO;

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
