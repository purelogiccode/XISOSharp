using System.Text.Json;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>Persisted GUI preferences (CLI location, overwrite default).</summary>
internal sealed class GuiSettings
{
    /// <summary>
    /// Gets or sets the user-configured CLI executable path.
    /// </summary>
    internal string CliPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether overwrite-by-default (<c>-y</c>) is selected.
    /// </summary>
    internal bool OverwriteByDefault { get; set; }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XISOSharp");
            return Path.Combine(dir, "gui-settings.json");
        }
    }

    /// <summary>
    /// Loads settings from the per-user JSON file, returning defaults when missing or unreadable.
    /// </summary>
    /// <returns>The loaded or default settings.</returns>
    internal static GuiSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
        }
        catch (FileNotFoundException)
        {
            return new GuiSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new GuiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            Log.Warning(ex, "GUI settings load failed; using defaults");
            BugReporter.ReportWarning($"GUI settings load failed; using defaults: {ex.Message}");
            return new GuiSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI settings load failed");
            BugReporter.ReportException(ex, "GUI settings load failed");
            return new GuiSettings();
        }
    }

    /// <summary>
    /// Saves settings to the per-user JSON file; failures are ignored (best-effort).
    /// </summary>
    internal void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            Log.Information("GUI settings saved to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning(ex, "GUI settings save failed (best-effort)");
            BugReporter.ReportWarning($"GUI settings save failed: {ex.Message}");
            // Settings are best-effort; the GUI keeps running with in-memory values.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI settings save failed");
            BugReporter.ReportException(ex, "GUI settings save failed");
        }
    }
}