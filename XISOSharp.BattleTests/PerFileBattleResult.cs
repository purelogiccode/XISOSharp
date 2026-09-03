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