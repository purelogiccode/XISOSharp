namespace XISOSharp.BattleTests.Models;

/// <summary>Single battle check status.</summary>
internal enum BattleStatus
{
    /// <summary>The check compared equal or completed successfully.</summary>
    Passed,

    /// <summary>The check compared unequal or raised an unexpected error.</summary>
    Failed,

    /// <summary>The check was not run (e.g. large file, missing native tool, empty image).</summary>
    Skipped,

    /// <summary>The check hit an infrastructure error rather than a comparison mismatch.</summary>
    Error
}