using System.Text;
using Serilog;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Obfuscated storage for the shared ApplicationStats / bug-report API key.
/// The literal below is the key encoded twice with Base64; it is decoded once
/// when the class is first touched (application startup for every host), and
/// the plaintext value is handed out through <see cref="Value"/>.
/// This raises the bar for casual string scans of the sources/binaries — it is
/// obfuscation, not encryption: a determined reader can still recover the key,
/// which is acceptable for an anonymous client credential by design.
/// </summary>
internal static class ApiKeyStore
{
    // Base64(Base64(key)) — never store the plaintext spelling.
    private const string EncodedKey =
        "YUdwb04zbDFOblExTm5SNWNqVTBNRzg1ZFRnM05qYzJOelp5TlRZM05EVXpORFExTXpJek5USTJOR00zTldJMmREZG5aMmRvWjJjM05uUnlaalUyTkdVPQ==";

    static ApiKeyStore()
    {
        // Explicit static constructor: removes beforefieldinit so the decode is
        // guaranteed to have run before the first member access (the hosts warm
        // the store from their startup hook via WarmUp).
        Value = Decode();
    }

    /// <summary>
    /// Forces the one-time decode at application startup. Every host calls
    /// <see cref="ApplicationStats.RecordLaunch"/> during launch, which warms
    /// the store before any request needs the key.
    /// </summary>
    internal static void WarmUp()
    {
    }

    /// <summary>The plaintext API key, or an empty string when decoding fails.</summary>
    internal static string Value { get; }

    private static string Decode()
    {
        try
        {
            string once = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedKey));
            return Encoding.UTF8.GetString(Convert.FromBase64String(once));
        }
        catch (FormatException ex)
        {
            // Never throw from a telemetry helper; an empty key just disables
            // the authenticated calls.
            Log.ForContext(BugReportSink.NoBugReportProperty, true)
                .Debug("ApiKeyStore decode failed: {Message}", ex.Message);
            return string.Empty;
        }
    }
}
