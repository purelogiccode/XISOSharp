using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Runs the <c>XISOSharp</c> CLI as a child process, streaming combined
/// stdout/stderr lines to a sink. Cancellation kills the process tree.
/// </summary>
internal static class CliRunner
{
    internal static async Task<int> RunAsync(
        string cliPath,
        IReadOnlyList<string> args,
        Action<string> onLine,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(cliPath);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(onLine);

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
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            onLine($"[GUI] Failed to start CLI: {ex.Message}");
            return -1;
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                // Already exited or cannot kill — the wait below still completes.
            }
        });

        var stdout = PumpAsync(process.StandardOutput, onLine, ct);
        var stderr = PumpAsync(process.StandardError, onLine, ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill was requested; fall through to collect remaining output.
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static async Task PumpAsync(System.IO.StreamReader reader, Action<string> onLine, CancellationToken ct)
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