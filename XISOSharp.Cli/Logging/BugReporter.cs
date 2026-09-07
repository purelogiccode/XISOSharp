using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester.Logging;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Forwards error-and-above reports to the PureLogicCode bug-report API.
/// See <c>InstructionsToSendBugs.md</c> (AspNet_BugReportEmailService repo).
/// Every report embeds the required Environment / Error / Exception sections.
/// Fire-and-forget (see <see cref="Flush"/> for synchronous shutdown): never throws,
/// throttled to stay under the 10 req/min limit. Warning-level routine events are
/// never filed (BUG-X-002). Shared single source of truth compiled into the CLI,
/// GUI, and Tester via linked items with per-host namespaces (BUG-X-001).
/// </summary>
internal static partial class BugReporter
{
    private const string Endpoint = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";

    private const int MaxMessage = 4000;
    private const int MaxStackTrace = 8000;
    private const int MaxAppName = 100;
    private const int MaxVersion = 20;
    private const int MaxEnvironment = 50;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
#if NET9_0_OR_GREATER
    private static readonly Lock Gate = new();
#else
    private static readonly object Gate = new();
#endif
    private static readonly Queue<DateTime> RecentSends = new();
    private static readonly Dictionary<string, DateTime> LastByKey = new(StringComparer.Ordinal);
    private static readonly HashSet<Task> PendingSends = new();

#if LOGGING_NS_GUI
    internal static string ApplicationName { get; set; } = "XISOSharp.Gui";
#elif LOGGING_NS_TESTER
    internal static string ApplicationName { get; set; } = "XISOSharpTester";
#else
    internal static string ApplicationName { get; set; } = "XISOSharp";
#endif

    internal static void ReportWarning(string message) =>
        // BUG-X-002: Warning-level routine events (user-error probes, non-zero exits,
        // missing files) are operational noise, not crashes. Never file a bug report
        // for them and never consume the 8/min throttle budget reserved for real
        // crashes (ReportError/ReportException). Kept as a sink so existing call
        // sites need no edits; visible in the debugger log only.
        Debug.WriteLine($"BugReporter warning suppressed (no report filed): {message}");

    internal static void ReportError(string message, Exception? ex = null) => Report(ex, message, "Error");

    internal static void ReportException(Exception ex, string context) => Report(ex, context, "Exception");

    private static void Report(Exception? ex, string message, string kind)
    {
        try
        {
            if (IsTestHost())
                return; // never file real bug reports from unit-test runs
            string safeMessage = string.IsNullOrWhiteSpace(message) ? $"{kind} (no message)" : message.Trim();
            string key =
                $"{kind}:{(safeMessage.Length > 200 ? safeMessage[..200] : safeMessage)}:{ex?.GetType().FullName}";
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                while (RecentSends.Count > 0 && (now - RecentSends.Peek()) > TimeSpan.FromMinutes(1))
                    _ = RecentSends.Dequeue();
                if (RecentSends.Count >= 8)
                    return; // over throttle budget — drop, stay under 10 req/min
                if (LastByKey.TryGetValue(key, out DateTime last) && (now - last) < TimeSpan.FromMinutes(1))
                    return; // same report already sent recently
                RecentSends.Enqueue(now);
                LastByKey[key] = now;
            }

            string envBlock = EnvironmentInfo.Collect(ApplicationName);
            string errorBlock = "=== Error Details ===\n" + safeMessage;
            string exceptionBlock = BuildExceptionBlock(ex);

            string fullMessage = $"{kind}: {safeMessage}\n\n{envBlock}\n\n{errorBlock}\n\n{exceptionBlock}";
            string stackTrace = ex is null ? $"{kind}: {safeMessage}" : ex.ToString();

            // BUG-X-003: track the in-flight send so Flush can wait for delivery.
            Task sendTask = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(fullMessage, stackTrace).ConfigureAwait(false);
                }
                catch (Exception sendEx)
                {
                    Debug.WriteLine($"BugReporter send failed: {sendEx.Message}");
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
        catch (Exception reportEx)
        {
            Debug.WriteLine($"BugReporter failed: {reportEx.Message}");
        }
    }

    /// <summary>
    /// Synchronously waits for pending bug-report sends to finish, up to
    /// <paramref name="timeout"/> (BUG-X-003). Short-lived hosts call this on their
    /// shutdown path so crash reports are delivered instead of being lost when the
    /// process exits. Never throws; returns <c>true</c> when nothing remained pending
    /// (delivered), <c>false</c> on timeout.
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

    private static bool IsTestHost()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("XISO_DISABLE_BUGREPORT"), "1",
                    StringComparison.Ordinal))
            {
                return true;
            }

            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry?.Contains("test", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static string BuildExceptionBlock(Exception? ex)
    {
        if (ex is null)
            return "=== Exception Details ===\nType: (none)\nMessage: (none)\nSource: (none)\nStackTrace: (none)";

        string type;
        string msg;
        string source;
        string stack;
        try
        {
            type = ex.GetType().FullName ?? ex.GetType().Name;
        }
        catch
        {
            type = "Unknown";
        }

        try
        {
            msg = ex.Message;
        }
        catch
        {
            msg = "Unknown";
        }

        try
        {
            source = ex.Source ?? "(unknown)";
        }
        catch
        {
            source = "Unknown";
        }

        try
        {
            stack = ex.StackTrace ?? "(no stack trace)";
        }
        catch
        {
            stack = "Unknown";
        }

        // Include inner exceptions (first level) for diagnosability.
        string inner = string.Empty;
        try
        {
            if (ex.InnerException is not null)
            {
                inner =
                    $"\nInner Type: {ex.InnerException.GetType().FullName}\nInner Message: {ex.InnerException.Message}";
            }
        }
        catch
        {
            inner = string.Empty;
        }

        return $"=== Exception Details ===\nType: {type}\nMessage: {msg}\nSource: {source}\nStackTrace: {stack}{inner}";
    }

    private static async Task SendAsync(string fullMessage, string stackTrace)
    {
        string version = EnvironmentInfo.ApplicationVersion();
        string environment;
        try
        {
            environment = RuntimeInformation.OSDescription;
        }
        catch
        {
            environment = "Unknown";
        }

        BugReportPayload payload = new(
            Truncate(fullMessage, MaxMessage),
            Truncate(ApplicationName, MaxAppName),
            Truncate(version, MaxVersion),
            null,
            Truncate(environment, MaxEnvironment),
            Truncate(stackTrace, MaxStackTrace));

        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-API-KEY", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, BugReportJsonContext.Default.BugReportPayload),
            Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _ = Assembly.GetExecutingAssembly();
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
            return value;
        return max <= 3 ? value[..max] : value[..(max - 3)] + "...";
    }

    /// <summary>
    /// Trim-safe bug-report payload. Property names match the wire format
    /// previously produced by the anonymous type (camelCase).
    /// </summary>
    private sealed record BugReportPayload(
        [property: JsonPropertyName("message")]
        string Message,
        [property: JsonPropertyName("applicationName")]
        string AppName,
        [property: JsonPropertyName("version")]
        string Version,
        [property: JsonPropertyName("userInfo")]
        string? UserInfo,
        [property: JsonPropertyName("environment")]
        string Environment,
        [property: JsonPropertyName("stackTrace")]
        string StackTrace);

    [JsonSerializable(typeof(BugReportPayload))]
    private sealed partial class BugReportJsonContext : JsonSerializerContext;
}
