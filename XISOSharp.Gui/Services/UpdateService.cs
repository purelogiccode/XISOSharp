using System.Text.Json;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Startup update check against the project's GitHub releases. Best effort and
/// silent on failure: an offline machine or API error never fails startup and
/// never files a bug report. The version comparison mirrors the CLI's
/// <c>UpdateChecker</c> so both front-ends agree on what counts as an update.
/// Set <c>XISO_NO_UPDATE_CHECK=1</c> to disable.
/// </summary>
internal static class UpdateService
{
    /// <summary>Environment variable that disables the check when set to <c>1</c>.</summary>
    internal const string DisableEnvVar = "XISO_NO_UPDATE_CHECK";

    /// <summary>GitHub API endpoint returning the newest non-prerelease release.</summary>
    internal const string LatestReleaseUrl = "https://api.github.com/repos/purelogiccode/XISOSharp/releases/latest";

    /// <summary>Releases page used when the API payload carries no release URL.</summary>
    internal const string ReleasesPageUrl = "https://github.com/purelogiccode/XISOSharp/releases";

    private const string UserAgent = "XISOSharp-GUI";
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Probes the newest GitHub release and returns it when it is newer than
    /// the running version. Never throws.
    /// </summary>
    /// <param name="ct">Cancellation token for the HTTP probe.</param>
    /// <returns>
    /// The update result, or <c>null</c> when the check is disabled, the app is
    /// current, or the probe failed.
    /// </returns>
    internal static async Task<UpdateCheckResult?> CheckAsync(CancellationToken ct)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "1", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using HttpClient http = new();
            http.Timeout = HttpTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) &&
                         tagElement.ValueKind == JsonValueKind.String
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            string url = root.TryGetProperty("html_url", out JsonElement urlElement) &&
                         urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            string local = EnvironmentInfo.ApplicationVersion();
            if (!IsUpdateAvailable(local, tag))
            {
                return null;
            }

            return new UpdateCheckResult(local, tag, ReleasePageUrl(url));
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
    /// <c>v</c>-prefixed tags accepted; <c>+metadata</c> ignored). A finished
    /// release counts as newer than a local prerelease of the same core.
    /// Unparseable input means "unknown": no update is reported.
    /// </summary>
    /// <param name="localVersion">Running version string.</param>
    /// <param name="remoteTag">Remote release tag.</param>
    /// <returns><c>true</c> when the remote release is newer.</returns>
    internal static bool IsUpdateAvailable(string? localVersion, string? remoteTag)
    {
        if (!TryParseVersion(localVersion, out Version localCore, out bool localPre) ||
            !TryParseVersion(remoteTag, out Version remoteCore, out bool remotePre))
        {
            return false;
        }

        int cmp = remoteCore.CompareTo(localCore);
        if (cmp != 0)
        {
            return cmp > 0;
        }

        return localPre && !remotePre;
    }

    /// <summary>
    /// Parses <c>[v]1.2.3[-prerelease][+metadata]</c> into its numeric core
    /// plus a prerelease flag.
    /// </summary>
    /// <param name="text">Version or tag text.</param>
    /// <param name="core">Parsed numeric core, <c>0.0</c> on failure.</param>
    /// <param name="prerelease">Whether the text carried a prerelease suffix.</param>
    /// <returns><c>true</c> when the text parsed.</returns>
    internal static bool TryParseVersion(string? text, out Version core, out bool prerelease)
    {
        core = new Version(0, 0);
        prerelease = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = true;
            trimmed = trimmed[..dash];
        }

        if (!trimmed.Contains('.'))
        {
            trimmed += ".0";
        }

        if (!Version.TryParse(trimmed, out Version? parsed) || parsed is null)
        {
            core = new Version(0, 0);
            return false;
        }

        core = parsed;
        return true;
    }

    /// <summary>
    /// Returns the release page URL to open, falling back to the releases
    /// index when the payload carries no URL.
    /// </summary>
    /// <param name="url">Release URL from the API payload.</param>
    /// <returns>Absolute URL for the browser.</returns>
    internal static string ReleasePageUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url;
}
