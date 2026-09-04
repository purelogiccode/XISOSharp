using System.Diagnostics;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Finds the <c>XISOSharp</c> CLI: explicit override, then a sibling of the
/// GUI executable (publish layouts), then <c>PATH</c>.
/// </summary>
internal static class CliLocator
{
    /// <summary>
    /// Gets the CLI file name for the current OS.
    /// </summary>
    internal static string CliFileName =>
        OperatingSystem.IsWindows() ? "XISOSharp.Cli.exe" : "XISOSharp.Cli";

    /// <summary>
    /// Resolves the CLI via explicit override, then a sibling of the GUI executable, then <c>PATH</c>.
    /// </summary>
    /// <param name="overridePath">User-configured CLI path; ignored when missing or blank.</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    internal static string? Resolve(string? overridePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }

            var sibling = Path.Combine(AppContext.BaseDirectory, CliFileName);
            if (File.Exists(sibling))
            {
                return sibling;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), CliFileName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        Log.Debug(ex, "Skipping malformed PATH entry");
                        // Malformed PATH entry — skip it.
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CliLocator.Resolve failed");
            BugReporter.ReportException(ex, "CliLocator.Resolve failed");
            return null;
        }
    }

    /// <summary>
    /// Runs the CLI with <c>-v</c> and returns its first output line.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <param name="ct">Cancellation token for the probe process.</param>
    /// <returns>The version banner line, or <c>null</c> when the probe fails.</returns>
    internal static async Task<string?> ProbeVersionAsync(string cliPath, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("-v");
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return "(no version output)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Warning(ex, "CLI version probe failed for {Cli}", cliPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI version probe failed for {Cli}", cliPath);
            BugReporter.ReportException(ex, $"CLI version probe failed for {cliPath}");
            return null;
        }
    }
}