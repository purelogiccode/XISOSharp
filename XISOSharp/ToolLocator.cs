namespace XISOSharp;

/// <summary>
/// Shared executable lookup chain (BUG-X-005): explicit override, then a sibling of the
/// app executable (publish layouts), then <c>PATH</c>, with a <c>-v</c> probe built on
/// <see cref="ProcessRunner"/>. GUI (XISOSharp.Cli), Tester (extract-xiso), and BattleTests
/// (extract-xiso) all delegate here so coverage stays identical; only the file names differ.
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// Gets the OS-aware file name for a tool base name (<c>.exe</c> on Windows, extensionless elsewhere).
    /// </summary>
    /// <param name="baseName">Tool base name without extension (e.g. <c>extract-xiso</c>).</param>
    /// <returns>File name for the current OS.</returns>
    public static string GetFileName(string baseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        return OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
    }

    /// <summary>
    /// Resolves a tool via explicit override, then a sibling of the app executable, then <c>PATH</c>.
    /// </summary>
    /// <param name="overridePath">User-configured path; ignored when missing or blank.</param>
    /// <param name="windowsFileName">File name on Windows (e.g. <c>extract-xiso.exe</c>).</param>
    /// <param name="unixFileName">File name on non-Windows (e.g. <c>extract-xiso</c>, extensionless).</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    public static string? Resolve(string? overridePath, string windowsFileName, string unixFileName)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }

            var fileName = OperatingSystem.IsWindows() ? windowsFileName : unixFileName;
            var sibling = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(sibling))
            {
                return sibling;
            }

            return FindOnPath(fileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a tool by base name, deriving the OS-aware file name
    /// (<c>.exe</c> on Windows, extensionless elsewhere).
    /// </summary>
    /// <param name="overridePath">User-configured path; ignored when missing or blank.</param>
    /// <param name="baseName">Tool base name without extension (e.g. <c>extract-xiso</c>).</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    public static string? ResolveByBaseName(string? overridePath, string baseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        return Resolve(overridePath, baseName + ".exe", baseName);
    }

    /// <summary>
    /// Searches <c>PATH</c> for <paramref name="fileName"/> (exact file name for the current OS).
    /// </summary>
    /// <param name="fileName">File name to search for.</param>
    /// <returns>Full path when found, otherwise <c>null</c>.</returns>
    public static string? FindOnPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return null;
            }

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Malformed PATH entry — skip it.
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the tool with <c>-v</c> and returns its first output line.
    /// Uses <see cref="ProcessRunner"/> (async drains, timeout, tree-kill), so probes
    /// never leak or hang. Never throws: any failure yields <c>null</c>.
    /// </summary>
    /// <param name="toolPath">Resolved path to the tool executable.</param>
    /// <param name="timeout">Probe timeout; defaults to 15 seconds.</param>
    /// <param name="cancellationToken">Cancels the probe and kills the child process.</param>
    /// <returns>The version banner line, <c>(no version output)</c> when empty, or <c>null</c> on failure.</returns>
    public static async Task<string?> ProbeVersionAsync(
        string toolPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return null;
        }

        try
        {
            var result = await ProcessRunner
                .RunAsync(toolPath, ["-v"], timeout ?? TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 && result.ExitCode != 255)
            {
                return null;
            }

            var text = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardError
                : result.StandardOutput;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return "(no version output)";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
