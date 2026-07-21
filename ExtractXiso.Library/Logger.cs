namespace ExtractXiso;

/// <summary>
/// Centralised logging for the XISO tool. Writes informational messages to
/// <see cref="Console.Out"/> and error messages to <see cref="Console.Error"/>,
/// with optional quiet/silent modes.
/// </summary>
public static class Logger
{
    /// <summary>When <c>true</c>, suppresses all non-error output.</summary>
    public static bool Quiet;

    /// <summary>When <c>true</c>, suppresses all output including errors.</summary>
    public static bool RealQuiet;

    /// <summary>Set to <c>true</c> when a warning is issued during processing.</summary>
    public static bool Warned;

    /// <summary>Cumulative bytes written across the current operation.</summary>
    public static long TotalBytes;

    /// <summary>Cumulative files processed in the current operation.</summary>
    public static int TotalFiles;

    /// <summary>Cumulative bytes across all processed ISO images.</summary>
    public static long TotalBytesAllIsos;

    /// <summary>Cumulative file count across all processed ISO images.</summary>
    public static int TotalFilesAllIsos;

    /// <summary>When <c>true</c>, files in a <c>$SystemUpdate</c> folder are skipped.</summary>
    public static bool RemoveSystemUpdate;

    /// <summary>
    /// When <c>true</c> (the default), <c>.xbe</c> files are automatically patched
    /// for media-enable during creation/rewrite.
    /// </summary>
    public static bool MediaEnable = true;

    /// <summary>Disc lseek offset detected during verification, used in rewrite mode.</summary>
    public static long XboxDiscLseek;

    /// <summary>
    /// Writes a formatted message to <see cref="Console.Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void Log(string message, params object?[] args)
    {
        if (!Quiet) Console.Write(message, args);
    }

    /// <summary>
    /// Writes a line to <see cref="Console.Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">The line of text to write (no format arguments).</param>
    public static void LogLine(string message)
    {
        if (!Quiet) Console.WriteLine(message);
    }

    /// <summary>
    /// Flushes <see cref="Console.Out"/> unless <see cref="Quiet"/> is <c>true</c>.
    /// </summary>
    public static void Flush()
    {
        if (!Quiet) Console.Out.Flush();
    }

    /// <summary>
    /// Writes a formatted error message to <see cref="Console.Error"/>
    /// unless <see cref="RealQuiet"/> is <c>true</c>.
    /// </summary>
    /// <param name="message">Composite format string.</param>
    /// <param name="args">Format arguments.</param>
    public static void LogErr(string message, params object?[] args)
    {
        if (!RealQuiet) Console.Error.Write(message, args);
    }
}
