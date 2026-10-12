using System;
using System.IO;
using System.Linq;
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
        string[] notes;
        lock (report.Notes) notes = report.Notes.ToArray();
        if (notes.Length > 0)
        {
            sb.AppendLine();
            foreach (var n in notes) sb.AppendLine("- " + n);
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
