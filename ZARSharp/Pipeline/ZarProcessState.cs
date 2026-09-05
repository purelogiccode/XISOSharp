namespace ZARSharp.Pipeline;

/// <summary>
/// Overall batch state. Ports <c>ProcessState</c> (<c>models/process.py</c>,
/// ZarManager 1.2.0).
/// </summary>
public enum ZarProcessState
{
    /// <summary>No work started.</summary>
    Idle = 0,

    /// <summary>Work in flight.</summary>
    Running = 1,

    /// <summary>Paused via <see cref="PauseTokenSource"/>; resume to continue.</summary>
    Paused = 2,

    /// <summary>Cancellation requested, workers still unwinding.</summary>
    Cancelling = 3,

    /// <summary>Every item completed.</summary>
    Completed = 4,

    /// <summary>Some items completed, the rest skipped or failed.</summary>
    Partial = 5,

    /// <summary>At least one item failed.</summary>
    Failed = 6,

    /// <summary>Cancelled before every item finished; nothing failed.</summary>
    Cancelled = 7,
}
