using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XISOSharp.Cli.Logging;

/// <summary>
/// Forwards warning-and-above reports to the PureLogicCode bug-report API.
/// See <c>InstructionsToSendBugs.md</c> (AspNet_BugReportEmailService repo).
/// Every report embeds the required Environment / Error / Exception sections.
/// Fire-and-forget: never throws, throttled to stay under the 10 req/min limit.
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
    private static readonly Lock Gate = new();
    private static readonly Queue<DateTime> RecentSends = new();
    private static readonly Dictionary<string, DateTime> LastByKey = new(StringComparer.Ordinal);

    internal static string ApplicationName { get; set; } = "XISOSharp.Cli";

    internal static void ReportWarning(string message)
    {
        Report(null, message, "Warning");
    }

    internal static void ReportError(string message, Exception? ex = null)
    {
        Report(ex, message, "Error");
    }

    internal static void ReportException(Exception ex, string context)
    {
        Report(ex, context, "Exception");
    }

    private static void Report(Exception? ex, string message, string kind)
    {
        try
        {
            if (IsTestHost())
                return; // never file real bug reports from unit-test runs
            var safeMessage = string.IsNullOrWhiteSpace(message) ? $"{kind} (no message)" : message.Trim();
            var key = $"{kind}:{(safeMessage.Length > 200 ? safeMessage[..200] : safeMessage)}:{ex?.GetType().FullName}";
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                while (RecentSends.Count > 0 && (now - RecentSends.Peek()) > TimeSpan.FromMinutes(1))
                    _ = RecentSends.Dequeue();
                if (RecentSends.Count >= 8)
                    return; // over throttle budget — drop, stay under 10 req/min
                if (LastByKey.TryGetValue(key, out var last) && (now - last) < TimeSpan.FromMinutes(1))
                    return; // same report already sent recently
                RecentSends.Enqueue(now);
                LastByKey[key] = now;
            }

            var envBlock = EnvironmentInfo.Collect(ApplicationName);
            var errorBlock = "=== Error Details ===\n" + safeMessage;
            var exceptionBlock = BuildExceptionBlock(ex);

            var fullMessage = $"{kind}: {safeMessage}\n\n{envBlock}\n\n{errorBlock}\n\n{exceptionBlock}";
            var stackTrace = ex is null ? $"{kind}: {safeMessage}" : ex.ToString();

            _ = Task.Run(async () =>
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
        }
        catch (Exception reportEx)
        {
            Debug.WriteLine($"BugReporter failed: {reportEx.Message}");
        }
    }

    private static bool IsTestHost()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("XISO_DISABLE_BUGREPORT"), "1", StringComparison.Ordinal))
                return true;
            var entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry is not null && entry.Contains("test", StringComparison.OrdinalIgnoreCase))
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
        try { type = ex.GetType().FullName ?? ex.GetType().Name; } catch { type = "Unknown"; }
        try { msg = ex.Message; } catch { msg = "Unknown"; }
        try { source = ex.Source ?? "(unknown)"; } catch { source = "Unknown"; }
        try { stack = ex.StackTrace ?? "(no stack trace)"; } catch { stack = "Unknown"; }

        // Include inner exceptions (first level) for diagnosability.
        var inner = string.Empty;
        try
        {
            if (ex.InnerException is not null)
                inner = $"\nInner Type: {ex.InnerException.GetType().FullName}\nInner Message: {ex.InnerException.Message}";
        }
        catch
        {
            inner = string.Empty;
        }

        return $"=== Exception Details ===\nType: {type}\nMessage: {msg}\nSource: {source}\nStackTrace: {stack}{inner}";
    }

    private static async Task SendAsync(string fullMessage, string stackTrace)
    {
        var version = EnvironmentInfo.ApplicationVersion();
        string environment;
        try
        {
            environment = RuntimeInformation.OSDescription;
        }
        catch
        {
            environment = "Unknown";
        }

        var payload = new BugReportPayload(
            Truncate(fullMessage, MaxMessage),
            Truncate(ApplicationName, MaxAppName),
            Truncate(version, MaxVersion),
            null,
            Truncate(environment, MaxEnvironment),
            Truncate(stackTrace, MaxStackTrace));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-API-KEY", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, BugReportJsonContext.Default.BugReportPayload),
            Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
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
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("applicationName")] string AppName,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("userInfo")] string? UserInfo,
        [property: JsonPropertyName("environment")] string Environment,
        [property: JsonPropertyName("stackTrace")] string StackTrace);

    [JsonSerializable(typeof(BugReportPayload))]
    private sealed partial class BugReportJsonContext : JsonSerializerContext;
}


