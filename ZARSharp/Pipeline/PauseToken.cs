namespace ZARSharp.Pipeline;

/// <summary>Pause gate snapshot; see <see cref="PauseTokenSource"/>.</summary>
public readonly struct PauseToken(ManualResetEventSlim? running)
{
    /// <summary>True while the source is paused.</summary>
    public bool IsPaused => running?.IsSet == false;

    /// <summary>
    /// Blocks while paused. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> fires first (mirroring
    /// <c>request_cancel</c>, which unblocks paused workers to unwind).
    /// </summary>
    public void WaitIfPaused(CancellationToken cancellationToken = default) =>
        running?.Wait(cancellationToken);
}
