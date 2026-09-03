namespace XISOSharp.BattleTests.Models;

/// <summary>Session aggregate.</summary>
internal sealed class BattleSessionResult
{
    /// <summary>Per-file results.</summary>
    public List<PerFileBattleResult> FileResults { get; } = [];

    /// <summary>Session elapsed.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Version strings.</summary>
    public string? NativeVersion { get; set; }

    /// <summary>Counts.</summary>
    public int TotalFiles => FileResults.Count;

    /// <summary>Passed files (no failures).</summary>
    public int PassedFiles =>
        FileResults.Count(r => r.AllPassed && r.SubTests.Any(s => s.Status != BattleStatus.Skipped));

    /// <summary>Failed files.</summary>
    public int FailedFiles => FileResults.Count(r => r.HasFailures);

    /// <summary>Skipped files.</summary>
    public int SkippedFiles => TotalFiles - PassedFiles - FailedFiles;

    /// <summary>Total sub-tests.</summary>
    public int TotalSubTests => FileResults.Sum(r => r.SubTests.Count);

    /// <summary>Passed sub-tests.</summary>
    public int PassedSubTests => FileResults.Sum(r => r.SubTests.Count(s => s.Status == BattleStatus.Passed));

    /// <summary>Failed sub-tests.</summary>
    public int FailedSubTests => FileResults.Sum(r => r.SubTests.Count(s => s.Status == BattleStatus.Failed));

    /// <summary>Skipped sub-tests.</summary>
    public int SkippedSubTests => FileResults.Sum(r => r.SubTests.Count(s => s.Status == BattleStatus.Skipped));
}