using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Serilog;

#if UPDATE_NS_GUI
namespace XISOSharp.Gui.Services;
#else
namespace XISOSharp.Cli.Update;
#endif

/// <summary>
/// Shared GitHub release update-check core (Todo #80): version parsing and
/// comparison, RID mapping, asset naming, and the 24-hour on-disk cache used by
/// both the CLI (<c>UpdateChecker</c>) and the GUI (<c>UpdateService</c>).
/// Single source of truth, compiled into both hosts via a linked item with a
/// per-host namespace, so the two front-ends can never drift and the GUI pays
/// the network probe at most once per <see cref="CheckInterval"/> like the CLI.
/// </summary>
internal static class UpdateCore
{
    /// <summary>Environment variable that disables the check when set to <c>1</c>.</summary>
    internal const string DisableEnvVar = "XISO_NO_UPDATE_CHECK";

    /// <summary>GitHub API endpoint returning the newest non-prerelease release.</summary>
    internal const string LatestReleaseUrl = "https://api.github.com/repos/purelogiccode/XISOSharp/releases/latest";

    /// <summary>Releases page used when the API payload carries no release URL.</summary>
    internal const string ReleasesPageUrl = "https://github.com/purelogiccode/XISOSharp/releases";

#if UPDATE_NS_GUI
    private const string UserAgent = "XISOSharp-GUI";
#else
    private const string UserAgent = "XISOSharp-CLI";
#endif

    /// <summary>How long a cached probe result stays fresh before re-fetching.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Reports whether <c>XISO_NO_UPDATE_CHECK=1</c> is set.</summary>
    internal static bool IsDisabledByEnvironment()
    {
        try
        {
            return string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "1",
                StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: environment probe failed");
            return false;
        }
    }

    /// <summary>Per-user cache file path (<c>%LOCALAPPDATA%/XISOSharp/update-check.json</c>).</summary>
    internal static string DefaultCachePath()
    {
        try
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "XISOSharp", "update-check.json");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: cache path resolution failed; using the temp path");
            return Path.Combine(Path.GetTempPath(), "XISOSharp", "update-check.json");
        }
    }

    /// <summary>Reads the cached probe result, or <c>null</c> when absent/corrupt.</summary>
    internal static ReleaseInfo? ReadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cachePath));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("checkedUtc", out JsonElement checkedEl) ||
                checkedEl.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(checkedEl.GetString(), null,
                    DateTimeStyles.RoundtripKind, out DateTime checkedUtc))
            {
                return null;
            }

            string tag = root.TryGetProperty("tag", out JsonElement tagEl) &&
                         tagEl.ValueKind == JsonValueKind.String
                ? tagEl.GetString() ?? string.Empty
                : string.Empty;
            string url = root.TryGetProperty("url", out JsonElement urlEl) &&
                         urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString() ?? string.Empty
                : string.Empty;
            string? assetName = root.TryGetProperty("asset", out JsonElement assetEl) &&
                                assetEl.ValueKind == JsonValueKind.String
                ? assetEl.GetString()
                : null;
            string? assetUrl = root.TryGetProperty("assetUrl", out JsonElement assetUrlEl) &&
                               assetUrlEl.ValueKind == JsonValueKind.String
                ? assetUrlEl.GetString()
                : null;
            return new ReleaseInfo(checkedUtc, tag, url, assetName, assetUrl);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: cache read failed for {CachePath}", cachePath);
            return null;
        }
    }

    /// <summary>Persists a probe result (best effort).</summary>
    internal static void WriteCache(string cachePath, ReleaseInfo info)
    {
        try
        {
            string? dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                _ = Directory.CreateDirectory(dir);
            // Hand-rolled JSON (trim-safe): values are tags/URLs we control.
            StringBuilder sb = new();
            sb.Append("{\"checkedUtc\":\"").Append(info.CheckedUtc.ToString("o")).Append("\",");
            sb.Append("\"tag\":\"").Append(Escape(info.Tag)).Append("\",");
            sb.Append("\"url\":\"").Append(Escape(info.Url)).Append("\",");
            sb.Append("\"asset\":").Append(info.AssetName is null ? "null" : $"\"{Escape(info.AssetName)}\"")
                .Append(',');
            sb.Append("\"assetUrl\":").Append(info.AssetUrl is null ? "null" : $"\"{Escape(info.AssetUrl)}\"")
                .Append('}');
            File.WriteAllText(cachePath, sb.ToString());
        }
        catch (Exception ex)
        {
            // Cache is best effort.
            Log.Debug(ex, "Update check: cache write failed for {CachePath}", cachePath);
        }
    }

    /// <summary>
    /// Fetches the newest release synchronously and caches the outcome
    /// (including a miss, so an offline host pays the timeout at most once per
    /// interval). Returns <c>null</c> on any failure.
    /// </summary>
    internal static ReleaseInfo? FetchLatest(string cachePath)
    {
        try
        {
            using HttpClient http = new();
            http.Timeout = HttpTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response =
                http.GetAsync(LatestReleaseUrl).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                WriteCache(cachePath, new ReleaseInfo(DateTime.UtcNow, string.Empty, string.Empty, null, null));
                return null;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ParseAndCache(cachePath, json);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: latest-release fetch failed");
            return null;
        }
    }

    /// <summary>Async variant of <see cref="FetchLatest"/> for UI hosts.</summary>
    internal static async Task<ReleaseInfo?> FetchLatestAsync(string cachePath, CancellationToken ct)
    {
        try
        {
            using HttpClient http = new();
            http.Timeout = HttpTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response =
                await http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                WriteCache(cachePath, new ReleaseInfo(DateTime.UtcNow, string.Empty, string.Empty, null, null));
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseAndCache(cachePath, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                                       or InvalidOperationException or IOException)
        {
            Log.Debug(ex, "Update check: latest-release fetch failed");
            return null;
        }
    }

    private static ReleaseInfo? ParseAndCache(string cachePath, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out JsonElement tagEl) &&
                     tagEl.ValueKind == JsonValueKind.String
            ? tagEl.GetString() ?? string.Empty
            : string.Empty;
        string url = root.TryGetProperty("html_url", out JsonElement urlEl) &&
                     urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        string? rid = MapCurrentRid();
        string? assetName = rid is null ? null : BuildAssetName(tag, rid);
        string? assetUrl = rid is null ? null : FindAssetUrl(root, tag, rid);
        ReleaseInfo info = new(DateTime.UtcNow, tag, url, assetName, assetUrl);
        WriteCache(cachePath, info);
        return info;
    }

    /// <summary>
    /// Maps the current OS/architecture to the release-asset RID fragment
    /// (<c>win-x64</c>, <c>linux-arm64</c>, <c>MacOsX-x64</c>, ...), or
    /// <c>null</c> when no prebuilt asset is published for this platform.
    /// </summary>
    internal static string? MapCurrentRid()
    {
        if (OperatingSystem.IsWindows())
            return MapRid(OSPlatform.Windows, RuntimeInformation.OSArchitecture);
        if (OperatingSystem.IsLinux())
            return MapRid(OSPlatform.Linux, RuntimeInformation.OSArchitecture);
        if (OperatingSystem.IsMacOS())
            return MapRid(OSPlatform.OSX, RuntimeInformation.OSArchitecture);
        return null;
    }

    /// <summary>
    /// Maps an OS/architecture pair to the release-asset RID fragment.
    /// Case-sensitive by convention (<c>MacOsX-x64</c>, not <c>osx-x64</c>).
    /// </summary>
    internal static string? MapRid(OSPlatform platform, Architecture architecture)
    {
        if (platform == OSPlatform.Windows)
        {
            return architecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null,
            };
        }

        if (platform == OSPlatform.Linux)
        {
            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => null,
            };
        }

        if (platform == OSPlatform.OSX)
        {
            return architecture switch
            {
                Architecture.X64 => "MacOsX-x64",
                Architecture.Arm64 => "MacOsX-arm64",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Builds the expected asset file name for a release tag
    /// (<c>release_1.0.0_win-x64.zip</c>). A leading <c>v</c> on the tag is
    /// stripped to match the convention.
    /// </summary>
    internal static string BuildAssetName(string tag, string rid) =>
        $"release_{tag.TrimStart('v', 'V')}_{rid}.zip";

    /// <summary>
    /// Reports whether <paramref name="remoteTag"/> is newer than the running
    /// <paramref name="localVersion"/> (MinVer informational strings and
    /// <c>v</c>-prefixed tags accepted; <c>+metadata</c> ignored). A finished
    /// release counts as newer than a local prerelease of the same core.
    /// Unparseable input means "unknown": no update is reported.
    /// </summary>
    internal static bool IsUpdateAvailable(string? localVersion, string? remoteTag)
    {
        if (!TryParseVersion(localVersion, out Version localCore, out bool localPre) ||
            !TryParseVersion(remoteTag, out Version remoteCore, out bool remotePre))
        {
            return false;
        }

        int cmp = remoteCore.CompareTo(localCore);
        if (cmp != 0)
            return cmp > 0;
        return localPre && !remotePre;
    }

    /// <summary>
    /// Parses <c>[v]1.2.3[-prerelease][+metadata]</c> into its numeric core
    /// plus a prerelease flag.
    /// </summary>
    internal static bool TryParseVersion(string? text, out Version core, out bool prerelease)
    {
        core = new Version(0, 0);
        prerelease = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string t = text.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            t = t[1..];
        int plus = t.IndexOf('+');
        if (plus >= 0)
            t = t[..plus];
        int dash = t.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = true;
            t = t[..dash];
        }

        // Version needs at least major.minor.
        if (!t.Contains('.'))
            t += ".0";
        if (!Version.TryParse(t, out Version? parsed) || parsed is null)
        {
            core = new Version(0, 0);
            return false;
        }

        core = parsed;
        return true;
    }

    /// <summary>
    /// Picks the platform asset download URL from a <c>releases/latest</c>
    /// payload. Returns <c>null</c> when this RID has no attached asset.
    /// </summary>
    internal static string? FindAssetUrl(JsonElement release, string tag, string rid)
    {
        try
        {
            string expected = BuildAssetName(tag, rid);
            if (!release.TryGetProperty("assets", out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                if (!asset.TryGetProperty("name", out JsonElement name) ||
                    name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (string.Equals(name.GetString(), expected, StringComparison.Ordinal) &&
                    asset.TryGetProperty("browser_download_url", out JsonElement url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: release asset lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Returns the release page URL to open, falling back to the releases
    /// index when the payload carries no URL.
    /// </summary>
    internal static string ReleasePageUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url;

    private static string Escape(string value) =>
        value.Replace("\\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>
/// Cached result of the latest GitHub release probe, persisted to the
/// per-user cache file so offline runs do not re-probe before the check
/// interval elapses.
/// </summary>
/// <param name="CheckedUtc">UTC time of the probe that produced this entry.</param>
/// <param name="Tag">Release tag from <c>tag_name</c> (for example <c>1.0.1</c>).</param>
/// <param name="Url">Release page URL from <c>html_url</c>.</param>
/// <param name="AssetName">Expected RID bundle file name, or <c>null</c> when the RID is unmapped.</param>
/// <param name="AssetUrl">Matching bundle download URL, or <c>null</c> when absent or unmapped.</param>
internal sealed record ReleaseInfo(
    DateTime CheckedUtc,
    string Tag,
    string Url,
    string? AssetName,
    string? AssetUrl);
