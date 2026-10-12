using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XamppUpdate.Services.Uat;

/// <summary>
/// Decides whether a folder may be used as the UAT copy (and later deleted). The rule is strict on purpose:
/// a wrong target means deleting real XAMPP data, so anything that is not clearly a UAT copy is refused.
/// </summary>
public static class UatTargetGuard
{
    /// <summary>Folders XAMPP ships next to mysql. None of them may ever be a UAT target or be deleted as one.</summary>
    public static readonly IReadOnlyCollection<string> KnownXamppFolders = new[]
    {
        "apache", "php", "perl", "htdocs", "phpMyAdmin", "tmp", "data", "security", "webdav", "cgi-bin", "contrib",
        "img", "lib", "licenses", "FileZillaFTP", "MercuryMail", "sendmail", "webalizer", "anonymous", "mysql"
    };

    /// <summary>
    /// Returns the reasons a target is not allowed (empty = allowed). The target must:
    /// sit directly next to production (same parent folder), be named '&lt;production name&gt;_...'
    /// (e.g. mysql_copy_upgrade), and never be a known XAMPP component folder.
    /// </summary>
    public static IReadOnlyList<string> Check(string target, string productionRoot)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(productionRoot))
        {
            problems.Add("target and production folders must both be set");
            return problems;
        }

        string t = PathSafety.Canonical(target).TrimEnd('\\');
        string p = PathSafety.Canonical(productionRoot).TrimEnd('\\');
        string? tParent = Path.GetDirectoryName(t);
        string? pParent = Path.GetDirectoryName(p);
        string tName = Path.GetFileName(t);
        string pName = Path.GetFileName(p);

        if (string.IsNullOrEmpty(tParent) || string.IsNullOrEmpty(pParent) ||
            !string.Equals(tParent.TrimEnd('\\'), pParent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the copy must be placed next to production in '{pParent}', not in '{tParent}'");
        }

        if (!tName.StartsWith(pName + "_", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the copy folder name must start with '{pName}_' (for example '{pName}_copy_upgrade'), not '{tName}'");
        }

        if (KnownXamppFolders.Any(k => k.Equals(tName, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"'{tName}' is a XAMPP component folder");
        }

        return problems;
    }
}
