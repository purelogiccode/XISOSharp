namespace XISOSharp.Gui.Services;

using System.Diagnostics;

/// <summary>
/// Finds the <c>XISOSharp</c> CLI: explicit override, then a sibling of the
/// GUI executable (publish layouts), then <c>PATH</c>.
/// Thin delegate over <see cref="XISOSharp.ToolLocator"/> (BUG-X-005 shared chain).
/// </summary>
internal static class CliLocator
{
    /// <summary>
    /// Gets the CLI file name for the current OS (the shipped binary is
    /// <c>XISOSharp</c>; the build/publish aliases produce that name from
    /// <c>XISOSharp.Cli</c>).
    /// </summary>
    internal static string CliFileName => ToolLocator.GetFileName("XISOSharp");

    /// <summary>
    /// Resolves the CLI via explicit override, then a sibling of the GUI executable (publish/build
    /// layouts — including self-extracting single-file bundles), then <c>PATH</c>.
    /// </summary>
    /// <param name="overridePath">User-configured CLI path; ignored when missing or blank.</param>
    /// <returns>The resolved executable path, or <c>null</c> when not found.</returns>
    internal static string? Resolve(string? overridePath) =>
        ToolLocator.ResolveByBaseName(overridePath, "XISOSharp");

    /// <summary>
    /// Runs the CLI with <c>-v</c> and returns its first output line.
    /// Delegates to <see cref="XISOSharp.ToolLocator"/> so the probe uses the shared
    /// runner (async drains, timeout, tree-kill) instead of a bespoke spawn.
    /// Use <see cref="ProductLabel"/> for user-facing text: it reads the CLI
    /// binary's own version metadata instead of the probe line.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <param name="ct">Cancellation token for the probe process.</param>
    /// <returns>The version banner line, or <c>null</c> when the probe fails.</returns>
    internal static Task<string?> ProbeVersionAsync(string cliPath, CancellationToken ct) =>
        ToolLocator.ProbeVersionAsync(cliPath, TimeSpan.FromSeconds(15), ct);

    /// <summary>
    /// Builds the user-facing product label for the resolved CLI
    /// (e.g. <c>XISOSharp 1.0.1</c>), from the binary's own version metadata —
    /// not the <c>-v</c> compatibility banner, which is extract-xiso branded.
    /// Falls back to <c>XISOSharp CLI</c> when metadata is unavailable.
    /// </summary>
    /// <param name="cliPath">Resolved path to the CLI executable.</param>
    /// <returns>Product label for logs and status text.</returns>
    internal static string ProductLabel(string cliPath)
    {
        string? version = ProductVersion(cliPath);
        return version is null ? "XISOSharp CLI" : $"XISOSharp {version}";
    }

    /// <summary>
    /// Reads the CLI's informational version from its file metadata (MinVer
    /// stamps <c>ProductVersion</c>; <c>+build</c> metadata is trimmed). Returns
    /// <c>null</c> when the file or its metadata is unavailable.
    /// </summary>
    internal static string? ProductVersion(string cliPath)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(cliPath);
            string? version = info.ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = info.FileVersion;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            int plus = version.IndexOf('+', StringComparison.Ordinal);
            return (plus >= 0 ? version[..plus] : version).Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}
