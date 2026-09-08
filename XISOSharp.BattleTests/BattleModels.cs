namespace XISOSharp.BattleTests;

/// <summary>Outcome of a single battle op.</summary>
internal enum BattleStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Result of one op (list/extract/rewrite) for one ISO.</summary>
internal sealed record SubResult(string Op, BattleStatus Status, string Detail, double Seconds);

/// <summary>Per-ISO battle result across all requested ops.</summary>
internal sealed class IsoResult
{
    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }

    public double Seconds { get; set; }

    public List<SubResult> Subs { get; } = [];

    public bool HasFailures => Subs.Any(static s => s.Status == BattleStatus.Failed);

    public bool AllPassed => Subs.Count > 0 && Subs.All(static s => s.Status == BattleStatus.Passed);
}

/// <summary>Aggregated session state for reporting.</summary>
internal sealed class BattleSession
{
    public int Seed { get; init; }

    public string CliPath { get; set; } = string.Empty;

    public string OraclePath { get; set; } = string.Empty;

    public string CliVersion { get; set; } = string.Empty;

    public string OracleVersion { get; set; } = string.Empty;

    public string WorkRoot { get; set; } = string.Empty;

    public List<string> Ops { get; init; } = [];

    public TimeSpan Elapsed { get; set; }

    public List<IsoResult> IsoResults { get; } = [];

    public int TotalIsos => IsoResults.Count;

    public int FailedIsos => IsoResults.Count(static r => r.HasFailures);

    public int TotalSubs => IsoResults.Sum(static r => r.Subs.Count);

    public int PassedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Passed));

    public int FailedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Failed));

    public int SkippedSubs => IsoResults.Sum(static r => r.Subs.Count(static s => s.Status == BattleStatus.Skipped));
}
