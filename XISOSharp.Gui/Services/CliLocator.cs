namespace XISOSharp.Gui.Services;

/// <summary>
/// Finds the <c>XISOSharp</c> CLI: explicit override, then a sibling of the
/// GUI executable (publish layouts), then <c>PATH</c>.
/// Thin delegate over <see cref="XISOSharp.ToolLocator"/> (BUG-X-005 shared chain).
/// </summary>
internal static class CliLocator
{
    /// <summary>
    /// Gets the CLI file name for the current OS (published binary is
    /// <c>XISOSharp</c>; <c>XISOSharp.Cli</c> is still accepted as a legacy fallback).
    /// </summary>
    internal static string CliFileName => ToolLocator.GetFileName("XISOSharp");

    /// <summary>
    /// Resolves the CLI via explicit override, then a sibling of the GUI executable, then <c>PATH</c>.
    /// Accepts the legacy <c>XISOSharp.Cli(.exe)</c> name as a fallback so older installs keep working.
    /// </summary>
    /// <param name="overridePath">User-configured CLI path; ignored when missing or blank.</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    internal static string? Resolve(string? overridePath) =>
        ToolLocator.ResolveByBaseName(overridePath, "XISOSharp")
        ?? ToolLocator.ResolveByBaseName(overridePath, "XISOSharp.Cli");

    /// <summary>
    /// Runs the CLI with <c>-v</c> and returns its first output line.
    /// Delegates to <see cref="XISOSharp.ToolLocator"/> so the probe uses the shared
    /// runner (async drains, timeout, tree-kill) instead of a bespoke spawn.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <param name="ct">Cancellation token for the probe process.</param>
    /// <returns>The version banner line, or <c>null</c> when the probe fails.</returns>
    internal static Task<string?> ProbeVersionAsync(string cliPath, CancellationToken ct) => ToolLocator.ProbeVersionAsync(cliPath, TimeSpan.FromSeconds(15), ct);
}
