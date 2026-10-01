using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;

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
/// The parsing/comparison/cache core is shared with the GUI via
/// <see cref="UpdateCore"/> (Todo #80); this class only adds CLI notification.
/// </summary>
internal static class UpdateChecker
{
    internal const string DisableEnvVar = UpdateCore.DisableEnvVar;

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

            if (UpdateCore.IsDisabledByEnvironment())
                return;

            if (IsTestHost())
                return; // unit tests drive Program.Main; never touch the network

            string cachePath = UpdateCore.DefaultCachePath();
            string? rid = UpdateCore.MapCurrentRid();

            ReleaseInfo? cached = UpdateCore.ReadCache(cachePath);
            bool cacheFresh = cached is not null && (DateTime.UtcNow - cached.CheckedUtc) < UpdateCore.CheckInterval;

            ReleaseInfo? latest = cacheFresh ? cached : UpdateCore.FetchLatest(cachePath);
            if (latest is null || string.IsNullOrWhiteSpace(latest.Tag))
                return; // offline, no releases yet, or API error — stay silent

            string local = Logging.EnvironmentInfo.ApplicationVersion();
            if (!UpdateCore.IsUpdateAvailable(local, latest.Tag))
                return;

            Notify(local, latest, rid);
        }
        catch (Exception ex)
        {
            // Update checks must never fail the CLI. Debug-only: an offline
            // machine is expected behaviour, so this never reaches the
            // Warning+ bug-report sink.
            Log.Debug(ex, "Update check failed");
        }
    }

    /// <summary>Maps the current platform to the release-asset RID fragment.</summary>
    internal static string? MapCurrentRid() => UpdateCore.MapCurrentRid();

    /// <summary>Maps an OS/architecture pair to the release-asset RID fragment.</summary>
    internal static string? MapRid(OSPlatform platform, Architecture architecture) =>
        UpdateCore.MapRid(platform, architecture);

    /// <summary>Builds the expected asset file name for a release tag.</summary>
    internal static string BuildAssetName(string tag, string rid) => UpdateCore.BuildAssetName(tag, rid);

    /// <summary>Reports whether the remote tag is newer than the local version.</summary>
    internal static bool IsUpdateAvailable(string? localVersion, string? remoteTag) =>
        UpdateCore.IsUpdateAvailable(localVersion, remoteTag);

    /// <summary>Parses <c>[v]1.2.3[-prerelease][+metadata]</c>.</summary>
    internal static bool TryParseVersion(string? text, out Version core, out bool prerelease) =>
        UpdateCore.TryParseVersion(text, out core, out prerelease);

    /// <summary>Picks the platform asset download URL from a release payload.</summary>
    internal static string? FindAssetUrl(JsonElement release, string tag, string rid) =>
        UpdateCore.FindAssetUrl(release, tag, rid);

    /// <summary>Per-user cache file path for the daily probe result.</summary>
    internal static string DefaultCachePath() => UpdateCore.DefaultCachePath();

    /// <summary>Reads the cached probe result, or <c>null</c> when absent/corrupt.</summary>
    internal static ReleaseInfo? ReadCache(string cachePath) => UpdateCore.ReadCache(cachePath);

    /// <summary>Persists a probe result (best effort).</summary>
    internal static void WriteCache(string cachePath, ReleaseInfo info) => UpdateCore.WriteCache(cachePath, info);

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
        catch (Exception ex)
        {
            // Notification must never fail the run.
            Log.Debug(ex, "Update check: notification failed");
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
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check: test-host detection failed");
        }

        return false;
    }
}
