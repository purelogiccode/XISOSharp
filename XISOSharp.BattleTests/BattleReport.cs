namespace XISOSharp.BattleTests;

/// <summary>Per-file battle result.</summary>
internal sealed class PerFileBattleResult
{
    /// <summary>File path.</summary>
    public required string FilePath { get; init; }
    /// <summary>File name.</summary>
    public required string FileName { get; init; }
    /// <summary>File size.</summary>
    public long FileSize { get; init; }
    /// <summary>Elapsed seconds.</summary>
    public double ElapsedSeconds { get; set; }
    /// <summary>Sub-tests.</summary>
    public List<SubBattleResult> SubTests { get; } = [];
    /// <summary>True if all sub-tests passed or skipped (no failures).</summary>
    public bool AllPassed => SubTests.All(s => s.Status != BattleStatus.Failed);
    /// <summary>True if any failed.</summary>
    public bool HasFailures => SubTests.Any(s => s.Status == BattleStatus.Failed);
}

/// <summary>Single battle check status.</summary>
internal enum BattleStatus { Passed, Failed, Skipped, Error }

/// <summary>Single battle sub-test result.</summary>
internal sealed class SubBattleResult
{
    /// <summary>Test name.</summary>
    public required string TestName { get; init; }
    /// <summary>Status.</summary>
    public required BattleStatus Status { get; init; }
    /// <summary>Human-readable detail.</summary>
    public required string Detail { get; init; }
    /// <summary>Elapsed seconds.</summary>
    public double ElapsedSeconds { get; init; }
}

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
    public int PassedFiles => FileResults.Count(r => r.AllPassed && !r.SubTests.All(s => s.Status == BattleStatus.Skipped));
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
