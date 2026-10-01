using XISOSharp.Cli.Logging;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="ApplicationStats"/>: launch recording is a no-op under
/// test hosts and with the disable flag, and flushing with nothing pending succeeds.
/// </summary>
[Collection("Sequential")]
public class ApplicationStatsExtraTests : IDisposable
{
    private readonly string? _origDisabled = Environment.GetEnvironmentVariable("XISO_DISABLE_STATS");

    /// <summary>Restores the stats opt-out environment variable.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XISO_DISABLE_STATS", _origDisabled);
    }

    /// <summary>Verifies launch recording never throws under a test host.</summary>
    [Fact]
    public void RecordLaunch_UnderTestHost_DoesNotThrow()
    {
        ApplicationStats.RecordLaunch("xisosharp-tests");
        ApplicationStats.RecordLaunch(string.Empty);
        ApplicationStats.RecordLaunch(null!);
    }

    /// <summary>Verifies launch recording is a no-op when explicitly disabled.</summary>
    [Fact]
    public void RecordLaunch_WhenDisabled_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable("XISO_DISABLE_STATS", "1");
        ApplicationStats.RecordLaunch("xisosharp-tests");
        Assert.True(ApplicationStats.Flush(TimeSpan.Zero));
    }

    /// <summary>Verifies flushing with no pending sends returns true.</summary>
    [Fact]
    public void Flush_NoPendingSends_ReturnsTrue()
    {
        Assert.True(ApplicationStats.Flush(TimeSpan.Zero));
        Assert.True(ApplicationStats.Flush(TimeSpan.FromMilliseconds(50)));
    }
}
