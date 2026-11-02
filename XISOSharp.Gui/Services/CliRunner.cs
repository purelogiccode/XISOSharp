using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Models;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Runs the <c>XISOSharp</c> CLI as a child process, streaming combined
/// stdout/stderr lines to a sink. Cancellation kills the process tree.
/// Thin delegate over <see cref="XISOSharp.ProcessRunner"/> (BUG-X-004).
/// </summary>
internal static class CliRunner
{
    /// <summary>
    /// Starts the CLI with <paramref name="args"/> and streams each stdout/stderr line to
    /// <paramref name="onLine"/>. Cancellation kills the process tree.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <param name="args">Argument list built by <see cref="CliCommands"/>.</param>
    /// <param name="onLine">Sink receiving each combined output line.</param>
    /// <param name="ct">Cancels the run and kills the CLI process.</param>
    /// <returns>The CLI exit code, or -1 when it could not start or already exited unknown.</returns>
    internal static async Task<int> RunAsync(
        string cliPath,
        IReadOnlyList<string> args,
        Action<string> onLine,
        CancellationToken ct)
    {
        try
        {
            ArgumentException.ThrowIfNullOrEmpty(cliPath);
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(onLine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CliRunner argument validation failed");
            throw;
        }

        Log.Information("Running CLI: {Cli} {Args}", cliPath, string.Join(" ", args));
        try
        {
            ProcessRunResult result = await ProcessRunner
                .RunAsync(cliPath, args, timeout: null, cancellationToken: ct, onLine: onLine)
                .ConfigureAwait(false);
            if (result.ExitCode == -1 && !string.IsNullOrWhiteSpace(result.StandardError)
                                      && string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                // Shared runner reports failed start as -1 without streaming; keep the GUI message.
                onLine($"[GUI] Failed to start CLI: {result.StandardError.Trim()}");
            }

            if (result.ExitCode != 0)
            {
                Log.ForContext(BugReportSink.NoBugReportProperty, true)
                    .Warning("CLI exited with code {Exit}: {Cli}", result.ExitCode, cliPath);
            }

            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Log.Information("CLI run cancelled: {Cli}", cliPath);
            SafeOnLine(onLine, "[GUI] CLI run cancelled.");
            return -1;
        }
        catch (TimeoutException ex)
        {
            Log.Error(ex, "CLI run timed out: {Cli}", cliPath);
            SafeOnLine(onLine, $"[GUI] CLI run timed out: {ex.Message}");
            return -1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI run failed: {Cli}", cliPath);
            SafeOnLine(onLine, $"[GUI] CLI run failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Invokes the output sink without letting a sink failure mask the original
    /// error: cancellation/timeout/start failures must reach the UI log panel
    /// instead of only the file log (Todo #64).
    /// </summary>
    private static void SafeOnLine(Action<string> onLine, string message)
    {
        try
        {
            onLine(message);
        }
        catch
        {
            // ignored: the file log already has the failure.
        }
    }
}
