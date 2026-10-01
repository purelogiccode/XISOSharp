using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Fire-and-forget launch ping to the PureLogicCode ApplicationStats API
/// (POST <c>/stats</c>). See <c>InstructionsToUseApiEndpoints.md</c>
/// (AspNet_ApplicationStats repo). Skips test hosts and honors
/// <c>XISO_DISABLE_STATS=1</c>; failures are Debug-logged with NoBugReport so
/// telemetry can never file a bug report. Shared single source of truth
/// compiled into the CLI, GUI, and Tester via linked items with per-host
/// namespaces (BUG-X-001).
/// </summary>
internal static partial class ApplicationStats
{
    private const string Endpoint = "https://www.purelogiccode.com/ApplicationStats/stats";

    // Double-Base64-encoded literal decoded at startup (see ApiKeyStore).
    private static string ApiKey => ApiKeyStore.Value;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
#if NET9_0_OR_GREATER
    private static readonly Lock Gate = new();
#else
    private static readonly object Gate = new();
#endif
    private static readonly HashSet<Task> PendingSends = new();

    /// <summary>
    /// Records one launch of <paramref name="applicationId"/> with the current
    /// application version. Never blocks the caller, never throws.
    /// </summary>
    /// <param name="applicationId">Stable, lower-case application identifier.</param>
    internal static void RecordLaunch(string applicationId)
    {
        // Decode the obfuscated API key once at application startup.
        ApiKeyStore.WarmUp();
        try
        {
            if (IsDisabled())
                return;

            string version = Truncate(EnvironmentInfo.ApplicationVersion(), 20);
            Task sendTask = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(applicationId, version).ConfigureAwait(false);
                }
                catch (Exception sendEx)
                {
                    LogDebug($"ApplicationStats send failed: {sendEx.Message}");
                }
            });
            lock (Gate)
            {
                _ = PendingSends.Add(sendTask);
            }

            _ = sendTask.ContinueWith(
                static t =>
                {
                    lock (Gate)
                    {
                        _ = PendingSends.Remove(t);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            LogDebug($"ApplicationStats failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronously waits for the pending launch ping, up to
    /// <paramref name="timeout"/>, so short-lived hosts deliver it before exit.
    /// Never throws.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for delivery.</param>
    /// <returns><c>true</c> if all pending sends completed; otherwise <c>false</c>.</returns>
    internal static bool Flush(TimeSpan timeout)
    {
        try
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Task[] snapshot;
                lock (Gate)
                {
                    if (PendingSends.Count == 0)
                        return true;
                    snapshot = new Task[PendingSends.Count];
                    PendingSends.CopyTo(snapshot);
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return false;
                if (!Task.WaitAll(snapshot, remaining))
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the <c>XISO_DISABLE_STATS=1</c> opt-out is set. Split from
    /// <see cref="IsDisabled"/> so tests can exercise the flag independently of
    /// test-host detection, which always short-circuits it (Todo #38).
    /// </summary>
    internal static bool IsDisabledByEnvironment()
    {
        try
        {
            return string.Equals(Environment.GetEnvironmentVariable("XISO_DISABLE_STATS"), "1",
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDisabled() => IsDisabledByEnvironment() || EnvironmentInfo.IsTestHost();

    private static async Task SendAsync(string applicationId, string version)
    {
        StatsPayload payload = new(applicationId, version);
        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, StatsJsonContext.Default.StatsPayload),
            Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            LogDebug($"ApplicationStats returned {(int)response.StatusCode} for {applicationId}");
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
            return value;
        return max <= 3 ? value[..max] : value[..(max - 3)] + "...";
    }

    private static void LogDebug(string message)
    {
        try
        {
            Log.ForContext(BugReportSink.NoBugReportProperty, true).Debug("{Message}", message);
        }
        catch
        {
            // Logging must never break telemetry.
        }
    }

    /// <summary>
    /// Trim-safe stats payload. Property names match the wire format
    /// documented in <c>InstructionsToUseApiEndpoints.md</c> (camelCase).
    /// </summary>
    private sealed record StatsPayload(
        [property: JsonPropertyName("applicationId")]
        string ApplicationId,
        [property: JsonPropertyName("version")]
        string Version);

    /// <summary>
    /// Source-generated JSON context for <see cref="StatsPayload"/>, keeping
    /// stats serialization trim- and AOT-safe.
    /// </summary>
    [JsonSerializable(typeof(StatsPayload))]
    private sealed partial class StatsJsonContext : JsonSerializerContext;
}
