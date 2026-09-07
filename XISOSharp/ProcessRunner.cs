using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace XISOSharp;

/// <summary>
/// Result of a <see cref="ProcessRunner"/> execution: exit code plus captured output.
/// </summary>
public sealed class ProcessRunResult
{
    /// <summary>Gets the process exit code, or -1 when it could not start or already exited unknown.</summary>
    public int ExitCode { get; init; }

    /// <summary>Gets the captured standard-output text (lines joined with newlines).</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Gets the captured standard-error text.</summary>
    public string StandardError { get; init; } = string.Empty;
}

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
    /// <returns>Exit code plus captured output; -1 when the process could not start.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="TimeoutException">Thrown when <paramref name="timeout"/> expires.</exception>
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<string>? onLine = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(args);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
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
            var hasTimeout = timeout.HasValue && timeout.Value != Timeout.InfiniteTimeSpan;
            using var timeoutCts = hasTimeout ? new CancellationTokenSource(timeout!.Value) : null;
            using var linkedCts = timeoutCts is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var effectiveToken = linkedCts?.Token ?? cancellationToken;

            // Static callback with state avoids capturing the outer `using var process`
            // (disposed-capture analyzer) and guarantees unregistration before dispose.
            using (effectiveToken.Register(static state =>
                   {
                       var proc = (Process)state!;
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
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();
                var lineGate = new object();
                var stdoutTask = PumpAsync(process.StandardOutput, stdoutBuilder, onLine, lineGate);
                var stderrTask = PumpAsync(process.StandardError, stderrBuilder, onLine, lineGate);
                try
                {
                    await process.WaitForExitAsync(effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"Process timed out after {timeout!.Value.TotalSeconds:N0} seconds: {fileName} {string.Join(" ", args)}");
                }

                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                var exitCode = GetExitCodeSafe(process);
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
            return -1;
        }
    }
}
