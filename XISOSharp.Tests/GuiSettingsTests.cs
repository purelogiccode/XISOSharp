#if NET10_0_OR_GREATER
using System.Text.Json;
using XISOSharp.Gui.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Regression for the GUI settings persistence bug: System.Text.Json only
/// serializes public properties, so the (internal) class's properties must be
/// public or every save writes <c>{}</c> and settings never survive a restart.
/// </summary>
public class GuiSettingsTests
{
    /// <summary>Verifies the persisted shape and a full JSON round-trip.</summary>
    [Fact]
    public void Serialize_RoundTripsCliPathAndOverwrite()
    {
        GuiSettings settings = new() { CliPath = "/opt/xiso/XISOSharp", OverwriteByDefault = true };

        string json = JsonSerializer.Serialize(settings);

        Assert.Contains("\"CliPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"OverwriteByDefault\"", json, StringComparison.Ordinal);

        GuiSettings? roundTrip = JsonSerializer.Deserialize<GuiSettings>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal("/opt/xiso/XISOSharp", roundTrip.CliPath);
        Assert.True(roundTrip.OverwriteByDefault);
    }
}
#endif
