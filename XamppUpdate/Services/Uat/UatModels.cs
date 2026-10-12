using System;
using System.Collections.Generic;

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
    /// <summary>The copy folder of this run. The service is only removed when its registered program lives under it.</summary>
    public string TargetRoot { get; set; } = string.Empty;
    /// <summary>Production folder of this run. Every folder the delete button would remove must pass <see cref="UatTargetGuard"/> against it.</summary>
    public string ProductionRoot { get; set; } = string.Empty;
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
