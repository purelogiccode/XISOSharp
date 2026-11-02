using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Single async process-runner shared by the GUI, Tester, and BattleTests (BUG-X-004):
/// async stdout+stderr drains, timeout, cancellation with
/// <c>Kill(entireProcessTree:true)</c>, <c>ArgumentList</c> spawning, and
/// non-zero/failed-start exit reporting. Callers keep their own logging and
/// map <see cref="ProcessRunResult.ExitCode"/> to their public contracts.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="args"/>, draining stdout/stderr
    /// concurrently, observing <paramref name="cancellationToken"/> and <paramref name="timeout"/>.
    /// </summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="args">Argument list (passed via <c>ArgumentList</c>, no shell quoting).</param>
    /// <param name="timeout">Optional kill timeout; <c>null</c> or infinite waits indefinitely.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <param name="onLine">Optional sink receiving each stdout/stderr line as it arrives (serialized).</param>
    /// <param name="outputEncoding">
    /// Text encoding for stdout/stderr; defaults to UTF-8. Legacy tools that
    /// print raw Latin-1 bytes (for example <c>extract-xiso</c>) need
    /// <see cref="Encoding.Latin1"/> so non-ASCII names survive decoding.
    /// </param>
    /// <returns>Exit code plus captured output; -1 when the process could not start.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="TimeoutException">Thrown when <paramref name="timeout"/> expires.</exception>
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<string>? onLine = null,
        Encoding? outputEncoding = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // A child inherits the process working directory. If it was deleted
        // (long-running hosts, tests that clean their temp cwd), Process.Start
        // fails with a misleading "directory name is invalid"; fall back to a
        // stable existing directory instead.
        if (!WorkingDirectoryExists())
            psi.WorkingDirectory = Path.GetTempPath();

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Logger.LogDebug($"Process start failed for '{fileName}': {ex.Message}");
            return new ProcessRunResult { ExitCode = -1, StandardOutput = string.Empty, StandardError = ex.Message };
        }

        if (process is null)
        {
            return new ProcessRunResult
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = "Failed to start process.",
            };
        }

        using (process)
        {
            bool hasTimeout = timeout.HasValue && timeout.Value != Timeout.InfiniteTimeSpan;
            using CancellationTokenSource? timeoutCts = hasTimeout ? new CancellationTokenSource(timeout!.Value) : null;
            using CancellationTokenSource? linkedCts = timeoutCts is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            CancellationToken effectiveToken = linkedCts?.Token ?? cancellationToken;

            // Static callback with state avoids capturing the outer `using var process`
            // (disposed-capture analyzer) and guarantees unregistration before dispose.
            await using (effectiveToken.Register(static state =>
                         {
                             Process proc = (Process)state!;
                             try
                             {
                                 if (!proc.HasExited)
                                 {
                                     proc.Kill(entireProcessTree: true);
                                 }
                             }
                             catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                                            or NotSupportedException or ObjectDisposedException)
                             {
                                 // Already exited, disposed, or cannot kill — the wait below still completes.
                             }
                         }, process))
            {
                StringBuilder stdoutBuilder = new();
                StringBuilder stderrBuilder = new();
                object lineGate = new();
                Task stdoutTask = PumpAsync(process.StandardOutput, stdoutBuilder, onLine, lineGate);
                Task stderrTask = PumpAsync(process.StandardError, stderrBuilder, onLine, lineGate);
                try
                {
                    await process.WaitForExitAsync(effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"Process timed out after {timeout!.Value.TotalSeconds:N0} seconds: {fileName} {string.Join(" ", args)}",
                        ex);
                }

                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                int exitCode = GetExitCodeSafe(process);
                return new ProcessRunResult
                {
                    ExitCode = exitCode,
                    StandardOutput = stdoutBuilder.ToString(),
                    StandardError = stderrBuilder.ToString(),
                };
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output, Action<string>? onLine, object gate)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            output.AppendLine(line);
            if (onLine is not null)
            {
                lock (gate)
                {
                    onLine(line);
                }
            }
        }
    }

    /// <summary>
    /// True when the process working directory can be read and still exists;
    /// <c>false</c> when it was deleted or is unreadable.
    /// </summary>
    private static bool WorkingDirectoryExists()
    {
        try
        {
            return Directory.Exists(Directory.GetCurrentDirectory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                       or NotSupportedException or ObjectDisposedException)
        {
            // Best effort — already exited or cannot kill.
        }
    }

    private static int GetExitCodeSafe(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Logger.LogDebug($"Exit-code probe failed: {ex.Message}");
            return -1;
        }
    }
}
