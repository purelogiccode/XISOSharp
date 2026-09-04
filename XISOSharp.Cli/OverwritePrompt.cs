namespace XISOSharp.Cli;

using Serilog;
using Logging;

/// <summary>
/// Interactive overwrite confirmation for CLI file outputs
/// (<c>XboxKit/Helpers.cs::ConfirmOverwrite</c> parity).
/// <list type="bullet">
/// <item><c>-y</c>/<c>--yes</c>: never prompt, always overwrite.</item>
/// <item><c>-n</c>/<c>--no</c>: never prompt, refuse when the output exists
/// (prints <c>[ERROR] File already exists</c> and the caller skips the operation).</item>
/// <item>Neither: prompt <c>Would you like to overwrite? (Y/N)</c> on stdout when the
/// output file exists; only <c>Y</c>/<c>YES</c> (case-insensitive) proceeds.</item>
/// </list>
/// The prompt I/O is injectable so tests can drive it without a console.
/// </summary>
internal static class OverwritePrompt
{
    /// <summary>
    /// Returns true when <paramref name="path"/> may be (over)written.
    /// Missing files always return true without prompting.
    /// </summary>
    internal static bool ConfirmOverwrite(string path, bool assumeYes, bool assumeNo,
        TextReader? input = null, TextWriter? output = null)
    {
        try
        {
            if (assumeYes)
                return true;
            if (!File.Exists(path))
                return true;

            output ??= Console.Out;
            if (assumeNo)
            {
                output.WriteLine($"[ERROR] File already exists: {path}");
                Log.Warning("Overwrite refused (assume-no): {Path}", path);
                return false;
            }

            input ??= Console.In;
            output.WriteLine($"[WARNING] File already exists: {path}");
            output.WriteLine("Would you like to overwrite? (Y/N)");
            var response = input.ReadLine()?.Trim();
            return string.Equals(response, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(response, "YES", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Overwrite prompt failed for {Path}", path);
            BugReporter.ReportException(ex, $"Overwrite prompt failed for {path}");
            output ??= Console.Out;
            try { output.WriteLine($"[ERROR] Overwrite check failed: {path} ({ex.Message})"); }
            catch
            {
                // ignored
            }

            return false;
        }
    }
}