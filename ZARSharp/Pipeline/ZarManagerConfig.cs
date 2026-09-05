using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZARSharp.Pipeline;

/// <summary>
/// Persistent settings. Ports <c>ConfigManager</c> (<c>config.py</c>,
/// ZarManager 1.2.0): same defaults, same merge-old-files-forward load,
/// same save location rule (per-user config dir for deployed apps, local
/// directory otherwise). JSON uses a source-generated context so the
/// trimmable/AOT posture of the library is preserved.
/// </summary>
public sealed record ZarManagerConfig
{
    /// <summary>Default batch source directory.</summary>
    public string SourceDir { get; init; } = "";

    /// <summary>Default batch target directory.</summary>
    public string TargetDir { get; init; } = "";

    /// <summary>Batch worker count (default 4, clamped to >= 1 on use).</summary>
    public int Workers { get; init; } = 4;

    /// <summary>UI language tag (default pt-br).</summary>
    public string Language { get; init; } = "pt-br";

    /// <summary>UI theme name (default Sistema).</summary>
    public string Theme { get; init; } = "Sistema";

    /// <summary>Whether the app self-updates (default true).</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>Last window geometry blob (default empty).</summary>
    public string WindowGeometry { get; init; } = "";

    /// <summary>Collision policy for batch outputs (default Fail).</summary>
    public ZarCollisionPolicy CollisionPolicy { get; init; } = ZarCollisionPolicy.Fail;

    /// <summary>Pipeline mode for batch runs (default Auto).</summary>
    public ZarProcessMode Mode { get; init; } = ZarProcessMode.Auto;

    /// <summary>
    /// Loads <c>settings.json</c> from <paramref name="directory"/> (or the
    /// default location): missing files and broken JSON fall back to
    /// defaults, and old files gain new keys — like <c>load_config</c>.
    /// </summary>
    public static ZarManagerConfig Load(string? directory = null, string fileName = "settings.json")
    {
        var path = Path.Combine(DefaultDirectory(directory), fileName);
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ZarConfigJsonContext.Default.ZarManagerConfig)
                ?? new ZarManagerConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new ZarManagerConfig();
        }
    }

    /// <summary>Saves this config as <c>settings.json</c> (best effort, like <c>save_config</c>).</summary>
    public void Save(string? directory = null, string fileName = "settings.json", Action<string>? log = null)
    {
        try
        {
            var dir = DefaultDirectory(directory);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, ZarConfigJsonContext.Default.ZarManagerConfig);
            File.WriteAllText(Path.Combine(dir, fileName), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(ex.Message);
        }
    }

    /// <summary>Builds <see cref="ZarPipelineOptions"/> from worker/policy settings.</summary>
    public ZarPipelineOptions ToPipelineOptions() => new()
    {
        MaxDegreeOfParallelism = Workers,
        CollisionPolicy = CollisionPolicy,
    };

    internal static string DefaultDirectory(string? overrideDirectory)
    {
        if (overrideDirectory != null)
        {
            return overrideDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "ZarManager");
            }
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, ".config", "zarmanager");
            }
        }

        return AppContext.BaseDirectory;
    }
}
