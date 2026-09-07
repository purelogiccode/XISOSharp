namespace XISOSharp;

/// <summary>
/// Centralised logging for the XISO tool. Writes informational messages to
/// <see cref="Out"/> and error messages to <see cref="Error"/>,
/// with optional quiet/silent modes. By default <see cref="Out"/> points to
/// <see cref="Console.Out"/> and <see cref="Error"/> points to
/// <see cref="Console.Error"/>, but both can be redirected for use in
/// non-console applications.
/// </summary>
/// <remarks>
/// Thread safety: the progress counters (<see cref="TotalBytes"/>,
/// <see cref="TotalFiles"/>, <see cref="TotalBytesAllIsos"/>,
/// <see cref="TotalFilesAllIsos"/>) are updated with
/// <see cref="Interlocked"/> and safe to bump from concurrent operations.
/// The sinks (<see cref="Out"/>, <see cref="Error"/>) and the mode flags
/// (<see cref="Quiet"/>, <see cref="RealQuiet"/>, <see cref="Warned"/>,
/// <see cref="RemoveSystemUpdate"/>, <see cref="MediaEnable"/>,
/// <see cref="XboxDiscLseek"/>) are set-at-startup configuration and are not
/// synchronized; configure them before starting work. Per-operation offsets
/// such as the disc lseek travel as explicit method parameters (see
/// <c>XisoWriter.CreateXiso</c>), not through
/// <see cref="XboxDiscLseek"/>, so concurrent rewrites cannot cross-read them.
/// </remarks>
public static class Logger
{
    /// <summary>
    /// The <see cref="TextWriter"/> used for normal output.
    /// Defaults to <see cref="Console.Out"/>. Set to <c>null</c> or
    /// <see cref="TextWriter.Null"/> to discard output.
    /// </summary>
    public static TextWriter Out { get; set; } = Console.Out;

    /// <summary>
    /// The <see cref="TextWriter"/> used for error output.
    /// Defaults to <see cref="Console.Error"/>. Set to <c>null</c> or
    /// <see cref="TextWriter.Null"/> to discard error output.
    /// </summary>
    public static TextWriter Error { get; set; } = Console.Error;

    /// <summary>When <c>true</c>, suppresses all non-error output.</summary>
    public static bool Quiet { get; set; }

    /// <summary>When <c>true</c>, suppresses all output including errors.</summary>
    public static bool RealQuiet { get; set; }

    /// <summary>Set to <c>true</c> when a warning is issued during processing.</summary>
    public static bool Warned { get; set; }

    /// <summary>Cumulative bytes written across the current operation (thread-safe).</summary>
    private static long _totalBytes;

    /// <summary>Cumulative bytes written across the current operation.</summary>
    public static long TotalBytes
    {
        get => Interlocked.Read(ref _totalBytes);
        set => Interlocked.Exchange(ref _totalBytes, value);
    }

    /// <summary>Cumulative files processed in the current operation (thread-safe).</summary>
    private static int _totalFiles;

    /// <summary>Cumulative files processed in the current operation.</summary>
    public static int TotalFiles
    {
        get => Volatile.Read(ref _totalFiles);
        set => Volatile.Write(ref _totalFiles, value);
    }

    /// <summary>Cumulative bytes across all processed ISO images (thread-safe).</summary>
    private static long _totalBytesAllIsos;

    /// <summary>Cumulative bytes across all processed ISO images.</summary>
    public static long TotalBytesAllIsos
    {
        get => Interlocked.Read(ref _totalBytesAllIsos);
        set => Interlocked.Exchange(ref _totalBytesAllIsos, value);
    }

    /// <summary>Cumulative file count across all processed ISO images (thread-safe).</summary>
    private static int _totalFilesAllIsos;

    /// <summary>Cumulative file count across all processed ISO images.</summary>
    public static int TotalFilesAllIsos
    {
        get => Volatile.Read(ref _totalFilesAllIsos);
        set => Volatile.Write(ref _totalFilesAllIsos, value);
    }

    /// <summary>
    /// Records one processed file of <paramref name="byteCount"/> bytes in the
    /// current-operation totals. Thread-safe (BUG-LIB-013).
    /// </summary>
    public static void RecordFileWritten(long byteCount)
    {
        Interlocked.Increment(ref _totalFiles);
        Interlocked.Add(ref _totalBytes, byteCount);
    }

    /// <summary>
    /// Records one processed file of <paramref name="byteCount"/> bytes in both the
    /// current-operation and the all-images totals. Thread-safe (BUG-LIB-013).
    /// </summary>
    public static void RecordIsoFileWritten(long byteCount)
    {
        RecordFileWritten(byteCount);
        Interlocked.Increment(ref _totalFilesAllIsos);
        Interlocked.Add(ref _totalBytesAllIsos, byteCount);
    }

    /// <summary>When <c>true</c>, files in a <c>$SystemUpdate</c> folder are skipped.</summary>
    public static bool RemoveSystemUpdate { get; set; }

    /// <summary>
    /// When <c>true</c> (the default), <c>.xbe</c> files are automatically patched
    /// for media-enable during creation/rewrite.
    /// </summary>
    public static bool MediaEnable { get; set; } = true;

    /// <summary>
    /// Legacy mirror of the disc lseek offset detected during verification.
    /// Kept for compatibility and diagnostics; operational code passes the
    /// offset as an explicit parameter (see <c>XisoWriter.CreateXiso</c>) and
    /// never reads this back, so concurrent rewrites cannot race on it
    /// (BUG-LIB-013). Not synchronized: set before starting work.
    /// </summary>
    public static long XboxDiscLseek { get; set; }

    /// <summary>
    /// Optional Serilog bridge. Host apps (CLI/GUI/Tester) set these in their
    /// logging bootstrap so every <see cref="Log"/>/<see cref="LogErr"/> write is
    /// also routed through Serilog (file sinks + Warning+ bug-report forwarding).
    /// Invocation is best-effort and never throws.
    /// </summary>
    public static Action<string>? ForwardInfo { get; set; }

    /// <summary>
    /// Optional Serilog bridge for error output. See <see cref="ForwardInfo"/>.
    /// </summary>
    public static Action<string>? ForwardError { get; set; }

    /// <summary>
    /// Writes a formatted message to <see cref="Out"/> unless <see cref="Quiet"/>
    /// or <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void Log(string message, params object?[] args)
    {
        if (!Quiet && !RealQuiet)
        {
            // Call sites pass pre-interpolated text (file names, tool output) that may
            // itself contain braces; only run composite formatting when args exist.
            if (args.Length == 0)
                Out.Write(message);
            else
                Out.Write(message, args);
        }

        Forward(ForwardInfo, message, args);
    }

    /// <summary>
    /// Writes a line to <see cref="Out"/> unless <see cref="Quiet"/> or
    /// <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">The line of text to write (no format arguments).</param>
    public static void LogLine(string message)
    {
        if (!Quiet && !RealQuiet) Out.WriteLine(message);
    }

    /// <summary>
    /// Flushes <see cref="Out"/> unless <see cref="Quiet"/> or
    /// <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    public static void Flush()
    {
        if (!Quiet && !RealQuiet) Out.Flush();
    }

    /// <summary>
    /// Writes a formatted error message to <see cref="Error"/>
    /// unless <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void LogErr(string message, params object?[] args)
    {
        if (!RealQuiet)
        {
            // See Log: never composite-format caller-interpolated text without args.
            if (args.Length == 0)
                Error.Write(message);
            else
                Error.Write(message, args);
        }

        Forward(ForwardError, message, args);
    }

    private static void Forward(Action<string>? target, string message, object?[] args)
    {
        if (target is null)
            return;

        try
        {
            var text = args.Length == 0 ? message : string.Format(message, args);
            if (!string.IsNullOrEmpty(text))
                target(text);
        }
        catch
        {
            // The Serilog bridge must never break library output.
        }
    }
}
