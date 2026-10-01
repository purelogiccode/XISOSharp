namespace XISOSharp.BattleTests;

/// <summary>Outcome of a single battle op.</summary>
internal enum BattleStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Result of one op (list/extract/rewrite) for one ISO. <see cref="Seconds"/>
/// is the combined op wall time; <see cref="CliSeconds"/>/<see cref="OracleSeconds"/>
/// are the per-executable times measured by the harness.</summary>
internal sealed record SubResult(
    string Op,
    BattleStatus Status,
    string Detail,
    double Seconds,
    double CliSeconds,
    double OracleSeconds);

/// <summary>Per-ISO battle result across all requested ops.</summary>
internal sealed class IsoResult
{
    internal required string FilePath { get; init; }

    internal required string FileName { get; init; }

    internal required long FileSize { get; init; }

    internal double Seconds { get; set; }

    internal List<SubResult> Subs { get; } = [];

    internal bool HasFailures => Subs.Any(static s => s.Status == BattleStatus.Failed);

    internal bool AllPassed => Subs.Count > 0 && Subs.All(static s => s.Status == BattleStatus.Passed);
}

/// <summary>Aggregated session state for reporting.</summary>
internal sealed class BattleSession
{
    internal int Seed { get; init; }

    internal string CliPath { get; set; } = string.Empty;

    internal string OraclePath { get; set; } = string.Empty;

    internal string CliVersion { get; set; } = string.Empty;

    internal string OracleVersion { get; set; } = string.Empty;

    internal string XdvdfsPath { get; set; } = string.Empty;

    internal string XdvdfsVersion { get; set; } = "not found";

    internal string XboxkitPath { get; set; } = string.Empty;

    internal string XboxkitVersion { get; set; } = "not found";

    internal string WorkRoot { get; set; } = string.Empty;

    internal List<string> Ops { get; init; } = [];

    internal TimeSpan Elapsed { get; set; }

    internal List<IsoResult> IsoResults { get; } = [];

    internal int TotalIsos => IsoResults.Count;

    internal int FailedIsos => IsoResults.Count(static r => r.HasFailures);

    internal int TotalSubs => IsoResults.Sum(static r => r.Subs.Count);

    internal int PassedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Passed));

    internal int FailedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Failed));

    internal int SkippedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Skipped));
}
