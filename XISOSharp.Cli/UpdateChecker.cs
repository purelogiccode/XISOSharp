using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace XISOSharp.Cli;

/// <summary>
/// GitHub release update check for the CLI. Once per launch (at most one
/// network request per 24 hours, daily result cached on disk) the latest
/// GitHub release is compared against the running version. When a newer
/// release exists the user is notified on stderr with the release URL and,
/// on an interactive console, offered to open it in a browser.
/// Release assets follow the <c>release_&lt;version&gt;_&lt;rid&gt;.zip</c>
/// convention (e.g. <c>release_1.0.0_win-x64.zip</c>).
/// Best effort and silent on failure: an offline machine or API error only
/// delays startup by the HTTP timeout, never fails the run, and never files
/// a bug report. Set <c>XISO_NO_UPDATE_CHECK=1</c> to disable.
/// </summary>
internal static class UpdateChecker
{
    internal const string DisableEnvVar = "XISO_NO_UPDATE_CHECK";

    private const string LatestReleaseUrl = "https://api.github.com/repos/purelogiccode/XISOSharp/releases/latest";
    private const string UserAgent = "XISOSharp-CLI";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks for updates and notifies on stderr when a newer release exists.
    /// Call once per process start. Never throws.
    /// </summary>
    /// <param name="args">Raw command-line arguments (quiet/version runs skip the check).</param>
    internal static void CheckForUpdates(string[] args)
    {
        try
        {
            // Quiet runs stay machine-readable and -v output stays parseable
            // (the GUI probes it); both skip the check. Mirrors the parser,
            // which treats these spellings as flags anywhere before positionals.
            foreach (string arg in args)
            {
                if (string.Equals(arg, "-q", StringComparison.Ordinal) ||
                    string.Equals(arg, "-Q", StringComparison.Ordinal) ||
                    string.Equals(arg, "-v", StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "1",
                    StringComparison.Ordinal))
            {
                return;
            }

            if (IsTestHost())
                return; // unit tests drive Program.Main; never touch the network

            string cachePath = DefaultCachePath();
            string? rid = MapCurrentRid();

            ReleaseInfo? cached = ReadCache(cachePath);
            bool cacheFresh = cached is not null && (DateTime.UtcNow - cached.CheckedUtc) < CheckInterval;

            ReleaseInfo? latest = cacheFresh ? cached : FetchLatest(cachePath);
            if (latest is null || string.IsNullOrWhiteSpace(latest.Tag))
                return; // offline, no releases yet, or API error — stay silent

            string local = Logging.EnvironmentInfo.ApplicationVersion();
            if (!IsUpdateAvailable(local, latest.Tag))
                return;

            Notify(local, latest, rid);
        }
        catch
        {
            // Update checks must never fail the CLI.
        }
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
        return Version.TryParse(t, out core!) && core is not null;
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
        catch
        {
            return null;
        }
    }

    internal static string DefaultCachePath()
    {
        try
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "XISOSharp", "update-check.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "XISOSharp", "update-check.json");
        }
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
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime checkedUtc))
            {
                return null;
            }

            string tag = root.TryGetProperty("tag", out JsonElement tagEl) &&
                tagEl.ValueKind == JsonValueKind.String ? tagEl.GetString() ?? string.Empty : string.Empty;
            string url = root.TryGetProperty("url", out JsonElement urlEl) &&
                urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() ?? string.Empty : string.Empty;
            string? assetName = root.TryGetProperty("asset", out JsonElement assetEl) &&
                assetEl.ValueKind == JsonValueKind.String ? assetEl.GetString() : null;
            string? assetUrl = root.TryGetProperty("assetUrl", out JsonElement assetUrlEl) &&
                assetUrlEl.ValueKind == JsonValueKind.String ? assetUrlEl.GetString() : null;
            return new ReleaseInfo(checkedUtc, tag, url, assetName, assetUrl);
        }
        catch
        {
            return null;
        }
    }

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
            sb.Append("\"asset\":").Append(info.AssetName is null ? "null" : $"\"{Escape(info.AssetName)}\"").Append(',');
            sb.Append("\"assetUrl\":").Append(info.AssetUrl is null ? "null" : $"\"{Escape(info.AssetUrl)}\"").Append('}');
            File.WriteAllText(cachePath, sb.ToString());
        }
        catch
        {
            // Cache is best effort.
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static ReleaseInfo? FetchLatest(string cachePath)
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
                // No releases yet (404) or API trouble: remember the miss so an
                // offline machine pays the timeout at most once per interval.
                WriteCache(cachePath, new ReleaseInfo(DateTime.UtcNow, string.Empty, string.Empty, null, null));
                return null;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out JsonElement tagEl) &&
                tagEl.ValueKind == JsonValueKind.String ? tagEl.GetString() ?? string.Empty : string.Empty;
            string url = root.TryGetProperty("html_url", out JsonElement urlEl) &&
                urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            string? rid = MapCurrentRid();
            string? assetName = rid is null ? null : BuildAssetName(tag, rid);
            string? assetUrl = rid is null ? null : FindAssetUrl(root, tag, rid);
            ReleaseInfo info = new(DateTime.UtcNow, tag, url, assetName, assetUrl);
            WriteCache(cachePath, info);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static void Notify(string local, ReleaseInfo latest, string? rid)
    {
        try
        {
            Logger.LogErr($"[UPDATE] XISOSharp {latest.Tag.TrimStart('v', 'V')} is available (you have {local}).\n");
            if (!string.IsNullOrWhiteSpace(latest.AssetUrl))
            {
                Logger.LogErr($"[UPDATE] Download: {latest.AssetUrl}\n");
            }
            else if (rid is null)
            {
                Logger.LogErr("[UPDATE] No prebuilt asset is published for this platform; build from source.\n");
            }
            else
            {
                Logger.LogErr($"[UPDATE] No {latest.AssetName} asset is attached to the release.\n");
            }

            if (!string.IsNullOrWhiteSpace(latest.Url))
            {
                Logger.LogErr($"[UPDATE] Release notes: {latest.Url}\n");
            }

            Logger.LogErr($"[UPDATE] Set {DisableEnvVar}=1 to disable this check.\n");

            // Offer the redirect only on an interactive console: scripts and
            // pipes get the notice above, never a blocking prompt.
            if (!Environment.UserInteractive || Console.IsInputRedirected || Console.IsOutputRedirected ||
                string.IsNullOrWhiteSpace(latest.Url))
            {
                return;
            }

            Console.Error.Write("Open the release page in your browser now? [y/N]: ");
            string? answer = Console.ReadLine();
            if (string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                _ = Process.Start(new ProcessStartInfo(latest.Url) { UseShellExecute = true });
                Logger.LogErr("[UPDATE] Opening the release page.\n");
            }
        }
        catch
        {
            // Notification must never fail the run.
        }
    }

    private static bool IsTestHost()
    {
        try
        {
            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry?.Contains("test", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignored
        }

        return false;
    }
}
