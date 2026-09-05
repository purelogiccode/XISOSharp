namespace ZARSharp.Pipeline;

/// <summary>
/// Cooperative pause gate, porting <c>core.py</c>'s <c>pause_flag</c>
/// (<c>set</c> = running): workers block in
/// <see cref="PauseToken.WaitIfPaused"/> while paused and keep honoring
/// cancellation. <see cref="PauseToken"/> is a snapshot struct; the default
/// value never pauses.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly ManualResetEventSlim _running = new(initialState: true);

    /// <summary>True while paused.</summary>
    public bool IsPaused => !_running.IsSet;

    /// <summary>Token observing this source.</summary>
    public PauseToken Token => new(_running);

    /// <summary>Pauses workers at their next gate check.</summary>
    public void Pause() => _running.Reset();

    /// <summary>Resumes paused workers.</summary>
    public void Resume() => _running.Set();
}
