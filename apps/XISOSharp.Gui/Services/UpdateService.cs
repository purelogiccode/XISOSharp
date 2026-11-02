using System.Text.Json;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Startup update check against the project's GitHub releases. Best effort and
/// silent on failure: an offline machine or API error never fails startup and
/// never files a bug report. Delegates to the shared <see cref="UpdateCore"/>
/// (Todo #80): same version comparison, same RID/asset rules, and the same
/// 24-hour on-disk cache the CLI uses, so both front-ends can never drift and
/// the GUI no longer probes GitHub on every start.
/// Set <c>XISO_NO_UPDATE_CHECK=1</c> to disable.
/// </summary>
internal static class UpdateService
{
    /// <summary>Environment variable that disables the check when set to <c>1</c>.</summary>
    internal const string DisableEnvVar = UpdateCore.DisableEnvVar;

    /// <summary>GitHub API endpoint returning the newest non-prerelease release.</summary>
    internal const string LatestReleaseUrl = UpdateCore.LatestReleaseUrl;

    /// <summary>Releases page used when the API payload carries no release URL.</summary>
    internal const string ReleasesPageUrl = UpdateCore.ReleasesPageUrl;

    /// <summary>
    /// Probes the newest GitHub release (through the shared daily cache) and
    /// returns it when it is newer than the running version. Never throws.
    /// </summary>
    /// <param name="ct">Cancellation token for the HTTP probe.</param>
    /// <returns>
    /// The update result, or <c>null</c> when the check is disabled, the app is
    /// current, or the probe failed.
    /// </returns>
    internal static async Task<UpdateCheckResult?> CheckAsync(CancellationToken ct)
    {
        if (UpdateCore.IsDisabledByEnvironment())
        {
            return null;
        }

        try
        {
            string cachePath = UpdateCore.DefaultCachePath();
            ReleaseInfo? cached = UpdateCore.ReadCache(cachePath);
            bool cacheFresh = cached is not null &&
                              (DateTime.UtcNow - cached.CheckedUtc) < UpdateCore.CheckInterval;

            ReleaseInfo? latest = cacheFresh
                ? cached
                : await UpdateCore.FetchLatestAsync(cachePath, ct).ConfigureAwait(false);
            if (latest is null || string.IsNullOrWhiteSpace(latest.Tag))
            {
                return null;
            }

            string local = EnvironmentInfo.ApplicationVersion();
            if (!UpdateCore.IsUpdateAvailable(local, latest.Tag))
            {
                return null;
            }

            return new UpdateCheckResult(local, latest.Tag, UpdateCore.ReleasePageUrl(latest.Url));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                                       or InvalidOperationException)
        {
            // Offline or API trouble is expected behaviour, not a defect.
            Log.Debug(ex, "GUI update check failed");
            return null;
        }
    }

    /// <summary>
    /// Reports whether <paramref name="remoteTag"/> is newer than the running
    /// <paramref name="localVersion"/> (MinVer informational strings and
    /// <c>v</c>-prefixed tags accepted; <c>+metadata</c> ignored).
    /// </summary>
    /// <param name="localVersion">Running version string.</param>
    /// <param name="remoteTag">Remote release tag.</param>
    /// <returns><c>true</c> when the remote release is newer.</returns>
    internal static bool IsUpdateAvailable(string? localVersion, string? remoteTag) =>
        UpdateCore.IsUpdateAvailable(localVersion, remoteTag);

    /// <summary>
    /// Parses <c>[v]1.2.3[-prerelease][+metadata]</c> into its numeric core
    /// plus a prerelease flag.
    /// </summary>
    /// <param name="text">Version or tag text.</param>
    /// <param name="core">Parsed numeric core, <c>0.0</c> on failure.</param>
    /// <param name="prerelease">Whether the text carried a prerelease suffix.</param>
    /// <returns><c>true</c> when the text parsed.</returns>
    internal static bool TryParseVersion(string? text, out Version core, out bool prerelease) =>
        UpdateCore.TryParseVersion(text, out core, out prerelease);

    /// <summary>
    /// Returns the release page URL to open, falling back to the releases
    /// index when the payload carries no URL.
    /// </summary>
    /// <param name="url">Release URL from the API payload.</param>
    /// <returns>Absolute URL for the browser.</returns>
    internal static string ReleasePageUrl(string? url) => UpdateCore.ReleasePageUrl(url);
}
