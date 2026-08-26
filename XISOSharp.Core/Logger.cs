namespace XISOSharp;

/// <summary>
/// Centralised logging for the XISO tool. Writes informational messages to
/// <see cref="Out"/> and error messages to <see cref="Error"/>,
/// with optional quiet/silent modes. By default <see cref="Out"/> points to
/// <see cref="Console.Out"/> and <see cref="Error"/> points to
/// <see cref="Console.Error"/>, but both can be redirected for use in
/// non-console applications.
/// </summary>
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

    /// <summary>Cumulative bytes written across the current operation.</summary>
    public static long TotalBytes { get; set; }

    /// <summary>Cumulative files processed in the current operation.</summary>
    public static int TotalFiles { get; set; }

    /// <summary>Cumulative bytes across all processed ISO images.</summary>
    public static long TotalBytesAllIsos { get; set; }

    /// <summary>Cumulative file count across all processed ISO images.</summary>
    public static int TotalFilesAllIsos { get; set; }

    /// <summary>When <c>true</c>, files in a <c>$SystemUpdate</c> folder are skipped.</summary>
    public static bool RemoveSystemUpdate { get; set; }

    /// <summary>
    /// When <c>true</c> (the default), <c>.xbe</c> files are automatically patched
    /// for media-enable during creation/rewrite.
    /// </summary>
    public static bool MediaEnable { get; set; } = true;

    /// <summary>Disc lseek offset detected during verification, used in rewrite mode.</summary>
    public static long XboxDiscLseek { get; set; }

    /// <summary>
    /// Writes a formatted message to <see cref="Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void Log(string message, params object?[] args)
    {
        if (!Quiet) Out.Write(message, args);
    }

    /// <summary>
    /// Writes a line to <see cref="Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">The line of text to write (no format arguments).</param>
    public static void LogLine(string message)
    {
        if (!Quiet) Out.WriteLine(message);
    }

    /// <summary>
    /// Flushes <see cref="Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    public static void Flush()
    {
        if (!Quiet) Out.Flush();
    }

    /// <summary>
    /// Writes a formatted error message to <see cref="Error"/>
    /// unless <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void LogErr(string message, params object?[] args)
    {
        if (!RealQuiet) Error.Write(message, args);
    }
}