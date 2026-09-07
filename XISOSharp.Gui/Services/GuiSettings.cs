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
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XISOSharp");
            return Path.Combine(dir, "gui-settings.json");
        }
    }

    /// <summary>
    /// Loads settings from the per-user JSON file, returning defaults when missing or unreadable.
    /// A corrupt JSON file is moved aside to a timestamped backup so user edits
    /// are preserved for recovery instead of being silently discarded.
    /// </summary>
    /// <returns>The loaded or default settings.</returns>
    internal static GuiSettings Load()
    {
        try
        {
            string json = File.ReadAllText(SettingsPath);
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
        catch (JsonException ex)
        {
            BackUpCorruptFile(ex);
            return new GuiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
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

    private static void BackUpCorruptFile(JsonException ex)
    {
        try
        {
            string path = SettingsPath;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture);
            string backup = $"{path}.corrupt-{stamp}.bak";
            try
            {
                File.Move(path, backup);
            }
            catch (IOException)
            {
                File.Copy(path, backup, overwrite: false);
            }

            Log.Warning(ex, "GUI settings file was corrupt; moved aside to {Backup}; using defaults", backup);
            BugReporter.ReportWarning($"GUI settings file was corrupt; backed up to {backup}; using defaults.");
        }
        catch (Exception backupEx)
        {
            Log.Warning(backupEx, "GUI settings corrupt-file backup failed; using defaults");
            BugReporter.ReportWarning($"GUI settings corrupt-file backup failed; using defaults: {backupEx.Message}");
        }
    }

    /// <summary>
    /// Saves settings to the per-user JSON file atomically (temp file + rename).
    /// Failures are surfaced to the caller (which logs them); nothing is swallowed.
    /// </summary>
    internal void Save()
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"gui-settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
            Log.Information("GUI settings saved to {Path}", path);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }
}
