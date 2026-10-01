using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester.Logging;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Forwards Warning-and-above reports to the PureLogicCode bug-report API.
/// See <c>InstructionsToSendBugs.md</c> (AspNet_BugReportEmailService repo).
/// Every report embeds the required Environment / Error / Exception sections.
/// Fire-and-forget (see <see cref="Flush"/> for synchronous shutdown): never throws,
/// throttled to stay under the 10 req/min limit. The only Warning+ events never
/// filed are CLI user-feedback lines bridged from <c>XISOSharp.Logger</c> and tagged
/// <c>NoBugReport</c> (usage/validation text, not bugs). Shared single source of
/// truth compiled into the CLI, GUI, and Tester via linked items with per-host
/// namespaces (BUG-X-001).
/// </summary>
internal static partial class BugReporter
{
    private const string Endpoint = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";

    private const int MaxMessage = 4000;
    private const int MaxErrorMessage = 1500;
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

    internal static void ReportWarning(string message, Exception? ex = null) =>
        Report(ex, message, "Warning");

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

            string fullMessage = ComposeReport(kind, safeMessage, ex, out string stackTrace);

            // BUG-X-003: track the in-flight send so Flush can wait for delivery.
            Task sendTask = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(fullMessage, stackTrace).ConfigureAwait(false);
                }
                catch (Exception sendEx)
                {
                    LogDebug($"BugReporter send failed: {sendEx.Message}");
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
            LogDebug($"BugReporter failed: {reportEx.Message}");
        }
    }

    private static void LogDebug(string message)
    {
        try
        {
            Log.ForContext(BugReportSink.NoBugReportProperty, true).Debug("{Message}", message);
        }
        catch
        {
            // Logging must never break reporting.
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
        }
        catch
        {
            // ignored
        }

        return EnvironmentInfo.IsTestHost();
    }

    /// <summary>
    /// Builds the full bug-report message. Every report carries the three
    /// sections required by <c>InstructionsToSendBugs.md</c> — Environment
    /// (Date, app name/version, OS version, architecture, bitness, platform
    /// version, processor count, base directory, temp path), Error (the
    /// message), and Exception (type, message, source, stack trace) — plus the
    /// standalone stack-trace field. The required sections survive the
    /// <see cref="MaxMessage"/> wire limit: the stack trace is shortened first,
    /// never a section header. Internal so format tests can lock the wire
    /// contract.
    /// </summary>
    /// <param name="kind">Report kind label (<c>Warning</c>/<c>Error</c>/<c>Exception</c>).</param>
    /// <param name="message">Error message already trimmed and defaulted by <see cref="Report"/>.</param>
    /// <param name="ex">Exception to describe, or <c>null</c> for message-only reports.</param>
    /// <param name="stackTrace">Standalone stack-trace field value for the API payload.</param>
    /// <returns>The composed report message.</returns>
    internal static string ComposeReport(string kind, string message, Exception? ex, out string stackTrace)
    {
        string envBlock = EnvironmentInfo.Collect(ApplicationName);
        stackTrace = ex is null ? $"{kind}: {message}" : ex.ToString();
        message = Truncate(message, MaxErrorMessage);

        string prefix = $"{kind}: {message}\n\n";
        string errorBlock = "=== Error Details ===\n" + message;
        const string exceptionHeader = "=== Exception Details ===\n";
        const string stackLabel = "\nStackTrace: ";

        int fixedLength = prefix.Length + envBlock.Length + 2 + errorBlock.Length + 2
                          + exceptionHeader.Length + stackLabel.Length;
        int budget = Math.Max(0, MaxMessage - fixedLength);
        string exceptionFields = BuildExceptionFields(ex, budget);
        int stackBudget = Math.Max(0, budget - exceptionFields.Length);
        string stack = Truncate(GetStack(ex), stackBudget);
        string inner = BuildInnerText(ex);
        int innerBudget = Math.Max(0, MaxMessage - fixedLength - exceptionFields.Length - stack.Length);
        if (inner.Length > innerBudget)
            inner = Truncate(inner, innerBudget);

        string report =
            $"{prefix}{envBlock}\n\n{errorBlock}\n\n{exceptionHeader}{exceptionFields}{stackLabel}{stack}{inner}";
        return report.Length <= MaxMessage ? report : Truncate(report, MaxMessage);
    }

    internal static string BuildExceptionBlock(Exception? ex) => BuildExceptionBlock(ex, int.MaxValue);

    internal static string BuildExceptionBlock(Exception? ex, int stackBudget)
    {
        string stack = GetStack(ex);
        if (stackBudget < stack.Length)
            stack = Truncate(stack, Math.Max(0, stackBudget));
        return $"=== Exception Details ===\n{BuildExceptionFields(ex)}\nStackTrace: {stack}{BuildInnerText(ex)}";
    }

    private static string BuildExceptionFields(Exception? ex, int budget = int.MaxValue)
    {
        if (ex is null)
            return "Type: (none)\nMessage: (none)\nSource: (none)";

        string type;
        string msg;
        string source;
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

        // Always keep the three required labels; only their values are
        // shortened so a huge exception message cannot push a later section
        // past the wire limit.
        const string typeLabel = "Type: ";
        const string msgLabel = "\nMessage: ";
        const string srcLabel = "\nSource: ";
        int available = Math.Max(0, budget - typeLabel.Length - msgLabel.Length - srcLabel.Length);
        type = Truncate(type, Math.Min(200, available));
        available -= type.Length;
        source = Truncate(source, Math.Min(200, Math.Max(0, available)));
        available -= source.Length;
        msg = Truncate(msg, Math.Max(0, available));

        return $"{typeLabel}{type}{msgLabel}{msg}{srcLabel}{source}";
    }

    private static string GetStack(Exception? ex)
    {
        if (ex is null)
            return "(none)";
        try
        {
            return ex.StackTrace ?? "(no stack trace)";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string BuildInnerText(Exception? ex)
    {
        try
        {
            if (ex?.InnerException is not null)
            {
                return
                    $"\nInner Type: {ex.InnerException.GetType().FullName}\nInner Message: {ex.InnerException.Message}";
            }
        }
        catch
        {
            // ignored
        }

        return string.Empty;
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

    /// <summary>
    /// Source-generated JSON context for <see cref="BugReportPayload"/>, keeping
    /// bug-report serialization trim- and AOT-safe.
    /// </summary>
    [JsonSerializable(typeof(BugReportPayload))]
    private sealed partial class BugReportJsonContext : JsonSerializerContext;
}
