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
/// <item>Both: deny (returns <c>false</c>); deny always wins over allow.</item>
/// <item>Neither: prompt <c>Would you like to overwrite? (Y/N)</c> on stdout when the
/// output file or directory exists; only <c>Y</c>/<c>YES</c> (case-insensitive) proceeds.</item>
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
            output ??= Console.Out;

            // CLI-021: contradictory flags deny, never overwrite. (MainInner
            // rejects -y/-n up front; this is the backstop for direct callers.)
            if (assumeYes && assumeNo)
            {
                output.WriteLine("[ERROR] Cannot use both --no (-n) and --yes (-y)");
                return false;
            }

            // CLI-022: an existing directory output must not sail through —
            // every caller passes a file output, so refusal/prompt applies.
            if (!File.Exists(path) && !Directory.Exists(path))
                return true;

            if (assumeNo)
            {
                output.WriteLine($"[ERROR] File already exists: {path}");
                Log.Warning("Overwrite refused (assume-no): {Path}", path);
                return false;
            }

            if (assumeYes)
                return true;

            // CLI-026: never block on redirected input. A host that pipes stdin
            // without closing it (unit-test hosts, CI runners, GUIs that shell
            // out) would otherwise leave ReadLine blocked forever the moment an
            // output collision needed a prompt — observed as a full test-suite
            // hang (xunit.v3 test run wedged at the split-overwrite prompt).
            // Interactive consoles (IsInputRedirected == false) still prompt.
            // An injected reader (tests, embedded hosts) overrides the guard:
            // its I/O is fully controlled and cannot wedge the host.
            if (input is null && Console.IsInputRedirected)
            {
                output.WriteLine(
                    $"[ERROR] Cannot prompt to overwrite {path}: standard input is redirected; pass -y/--yes to overwrite or -n/--no to refuse\n");
                Log.Warning("Overwrite prompt refused (stdin redirected): {Path}", path);
                return false;
            }

            input ??= Console.In;
            output.WriteLine($"[WARNING] File already exists: {path}");
            output.WriteLine("Would you like to overwrite? (Y/N)");
            string? response = input.ReadLine()?.Trim();
            return string.Equals(response, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(response, "YES", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Overwrite prompt failed for {Path}", path);
            BugReporter.ReportException(ex, $"Overwrite prompt failed for {path}");
            output ??= Console.Out;
            try
            {
                output.WriteLine($"[ERROR] Overwrite check failed: {path} ({ex.Message})");
            }
            catch
            {
                // ignored
            }

            return false;
        }
    }
}
