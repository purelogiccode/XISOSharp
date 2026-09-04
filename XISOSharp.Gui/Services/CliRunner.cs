using System.Diagnostics;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Runs the <c>XISOSharp</c> CLI as a child process, streaming combined
/// stdout/stderr lines to a sink. Cancellation kills the process tree.
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
            BugReporter.ReportException(ex, "CliRunner argument validation failed");
            throw;
        }

        Log.Information("Running CLI: {Cli} {Args}", cliPath, string.Join(" ", args));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Failed to start CLI {Cli}", cliPath);
            BugReporter.ReportException(ex, $"Failed to start CLI {cliPath}");
            onLine($"[GUI] Failed to start CLI: {ex.Message}");
            return -1;
        }

        // Pass the process as state to a static callback so the lambda does not
        // capture the outer `using var process` (avoids "captured variable is
        // disposed in the outer scope"). The registration stays in an inner scope
        // so it is always unregistered before `process` is disposed.
        await using (ct.Register(static state =>
        {
            var proc = (Process)state!;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException or ObjectDisposedException)
            {
                // Already exited, disposed, or cannot kill — the wait below still completes.
            }
        }, process))
        {
            var stdout = PumpAsync(process.StandardOutput, onLine, ct);
            var stderr = PumpAsync(process.StandardError, onLine, ct);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Information("CLI run cancelled: {Cli}", cliPath);
                // Kill was requested; fall through to collect remaining output.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Waiting for CLI exit failed: {Cli}", cliPath);
                BugReporter.ReportException(ex, $"Waiting for CLI exit failed: {cliPath}");
            }

            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Collecting CLI output failed: {Cli}", cliPath);
                BugReporter.ReportException(ex, $"Collecting CLI output failed: {cliPath}");
            }

            try
            {
                var code = process.HasExited ? process.ExitCode : -1;
                if (code != 0)
                    Log.Warning("CLI exited with code {Exit}: {Cli}", code, cliPath);
                return code;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                Log.Error(ex, "Reading CLI exit code failed: {Cli}", cliPath);
                BugReporter.ReportException(ex, $"Reading CLI exit code failed: {cliPath}");
                return -1;
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            onLine(line);
        }
    }
}