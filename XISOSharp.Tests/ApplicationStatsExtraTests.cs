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

    /// <summary>
    /// Verifies the explicit disable flag is honored. The assertion targets the
    /// flag check itself: under a test host <c>IsDisabled</c> is true anyway, so
    /// asserting only "does not throw" could never fail (Todo #38).
    /// </summary>
    [Fact]
    public void RecordLaunch_WhenDisabled_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable("XISO_DISABLE_STATS", "1");
        Assert.True(ApplicationStats.IsDisabledByEnvironment());

        ApplicationStats.RecordLaunch("xisosharp-tests");
        Assert.True(ApplicationStats.Flush(TimeSpan.Zero));

        Environment.SetEnvironmentVariable("XISO_DISABLE_STATS", null);
        Assert.False(ApplicationStats.IsDisabledByEnvironment());
    }

    /// <summary>Verifies flushing with no pending sends returns true.</summary>
    [Fact]
    public void Flush_NoPendingSends_ReturnsTrue()
    {
        Assert.True(ApplicationStats.Flush(TimeSpan.Zero));
        Assert.True(ApplicationStats.Flush(TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>
    /// Verifies the obfuscated literal decodes to the expected key: the SHA-256
    /// of the decoded value is pinned, so the plaintext key never appears in the
    /// test sources either.
    /// </summary>
    [Fact]
    public void ApiKeyStore_DecodesExpectedKey()
    {
        string key = ApiKeyStore.Value;

        Assert.NotEmpty(key);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        Assert.Equal("BF8A92A0948A5B4C89B56EA8A03681892158608FB867C0E75D9C5F252471264C",
            Convert.ToHexString(hash));
    }
}
