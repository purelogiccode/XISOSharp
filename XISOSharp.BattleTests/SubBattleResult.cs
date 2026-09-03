namespace XISOSharp.BattleTests;

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