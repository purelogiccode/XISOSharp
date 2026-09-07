namespace XISOSharp.Gui.Services;

/// <summary>
/// Finds the <c>XISOSharp</c> CLI: explicit override, then a sibling of the
/// GUI executable (publish layouts), then <c>PATH</c>.
/// Thin delegate over <see cref="XISOSharp.ToolLocator"/> (BUG-X-005 shared chain).
/// </summary>
internal static class CliLocator
{
    /// <summary>
    /// Gets the CLI file name for the current OS.
    /// </summary>
    internal static string CliFileName => XISOSharp.ToolLocator.GetFileName("XISOSharp.Cli");

    /// <summary>
    /// Resolves the CLI via explicit override, then a sibling of the GUI executable, then <c>PATH</c>.
    /// </summary>
    /// <param name="overridePath">User-configured CLI path; ignored when missing or blank.</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    internal static string? Resolve(string? overridePath)
    {
        return XISOSharp.ToolLocator.Resolve(overridePath, "XISOSharp.Cli.exe", "XISOSharp.Cli");
    }

    /// <summary>
    /// Runs the CLI with <c>-v</c> and returns its first output line.
    /// Delegates to <see cref="XISOSharp.ToolLocator"/> so the probe uses the shared
    /// runner (async drains, timeout, tree-kill) instead of a bespoke spawn.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <param name="ct">Cancellation token for the probe process.</param>
    /// <returns>The version banner line, or <c>null</c> when the probe fails.</returns>
    internal static Task<string?> ProbeVersionAsync(string cliPath, CancellationToken ct)
    {
        return XISOSharp.ToolLocator.ProbeVersionAsync(cliPath, TimeSpan.FromSeconds(15), ct);
    }
}
