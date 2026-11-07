namespace XISOSharp.Logging;

/// <summary>
/// Per-operation log sink. Messages arrive already formatted and retain the
/// newlines the static <see cref="XISOSharp.Logger"/> call sites emit.
/// Implementations must be thread-safe when used by concurrent operations.
/// </summary>
public interface IXisoLogger
{
    /// <summary>Writes an informational message.</summary>
    /// <param name="message">Formatted message.</param>
    void Log(string message);

    /// <summary>Writes a warning message.</summary>
    /// <param name="message">Formatted message.</param>
    void LogWarning(string message);

    /// <summary>Writes an error message.</summary>
    /// <param name="message">Formatted message.</param>
    void LogError(string message);

    /// <summary>Writes an internal diagnostic message.</summary>
    /// <param name="message">Formatted message.</param>
    void LogDebug(string message);

    /// <summary>Flushes buffered output, when the sink buffers.</summary>
    void Flush()
    {
        // Sinks are not required to buffer.
    }
}

/// <summary>
/// Options for routing one operation's log output to an
/// <see cref="IXisoLogger"/> instead of the process-wide static
/// <see cref="XISOSharp.Logger"/> writers (LIB-02).
/// </summary>
public sealed record XisoLogOptions
{
    /// <summary>Gets the per-operation sink; <c>null</c> keeps static logging.</summary>
    public IXisoLogger? Sink { get; init; }

    /// <summary>
    /// Gets whether static <see cref="XISOSharp.Logger.Out"/> /
    /// <see cref="XISOSharp.Logger.Error"/> writes are suppressed while the
    /// scope is active. Defaults to <c>true</c>, so concurrent operations do not
    /// interleave in the process-wide writers.
    /// </summary>
    public bool SuppressStaticOutput { get; init; } = true;
}

/// <summary>
/// Ambient per-operation log scope. <see cref="Begin"/> routes every static
/// <see cref="XISOSharp.Logger"/> call made in the current async/thread flow to
/// the supplied sink until the returned scope is disposed. The static writers
/// remain the compatibility path when no scope is active.
/// </summary>
/// <remarks>
/// Scopes use <see cref="AsyncLocal{T}"/>, so two concurrent operations (on
/// separate tasks/threads) can log to separate sinks without sharing state.
/// They nest: an inner scope restores the outer sink on dispose.
/// </remarks>
public static class XisoLogScope
{
    internal sealed class ScopeState(IXisoLogger sink, bool suppressStaticOutput, ScopeState? parent)
    {
        internal IXisoLogger Sink { get; } = sink;
        internal bool SuppressStaticOutput { get; } = suppressStaticOutput;
        internal ScopeState? Parent { get; } = parent;
    }

    private sealed class NoopScope : IDisposable
    {
        internal static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class RestoreScope(ScopeState? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CurrentState.Value = previous;
        }
    }

    private static readonly AsyncLocal<ScopeState?> CurrentState = new();

    /// <summary>Gets the active scope, or <c>null</c> when static logging is in effect.</summary>
    internal static ScopeState? Current => CurrentState.Value;

    /// <summary>
    /// Starts a logging scope. A <c>null</c> or sink-less options object is a
    /// no-op, so callers can pass an optional value unconditionally.
    /// </summary>
    /// <param name="options">Per-call sink options.</param>
    /// <returns>A disposable that restores the previous scope.</returns>
    public static IDisposable Begin(XisoLogOptions? options)
    {
        if (options?.Sink is null)
        {
            return NoopScope.Instance;
        }

        ScopeState? previous = CurrentState.Value;
        CurrentState.Value = new ScopeState(options.Sink, options.SuppressStaticOutput, previous);
        return new RestoreScope(previous);
    }

    /// <summary>
    /// Starts a logging scope for an operation and disposes it when the
    /// operation completes.
    /// </summary>
    /// <typeparam name="T">Operation result type.</typeparam>
    /// <param name="options">Per-call sink options.</param>
    /// <param name="operation">Operation to run inside the scope.</param>
    /// <returns>The operation result.</returns>
    public static T Run<T>(XisoLogOptions? options, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using IDisposable scope = Begin(options);
        return operation();
    }
}
