using System;
using System.IO;
using System.Text.Json;

namespace XISOSharp.Gui.Services;

/// <summary>Persisted GUI preferences (CLI location, overwrite default).</summary>
internal sealed class GuiSettings
{
    internal string CliPath { get; set; } = string.Empty;
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

    internal static GuiSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new GuiSettings();
        }
    }

    internal void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Settings are best-effort; the GUI keeps running with in-memory values.
        }
    }
}
