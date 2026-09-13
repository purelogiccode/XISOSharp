using Serilog.Core;
using Serilog.Events;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester.Logging;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Serilog sink that forwards Warning-and-above events to the bug-report API.
/// Events tagged with <see cref="NoBugReportProperty"/> (user-facing CLI
/// feedback bridged from <c>XISOSharp.Logger</c>: usage/validation text, not
/// bugs) are skipped so routine operational noise never files reports.
/// Never throws: failures are swallowed so logging can never crash the app.
/// </summary>
internal sealed class BugReportSink : ILogEventSink
{
    /// <summary>
    /// Log-event property marking an event as local-only: the
    /// <see cref="BugReportSink"/> drops events carrying this property with
    /// value <c>true</c> even when they are Warning-or-above.
    /// </summary>
    internal const string NoBugReportProperty = "NoBugReport";

    /// <summary>
    /// Forwards Warning-and-above events to <see cref="BugReporter"/>, skipping
    /// events tagged with <see cref="NoBugReportProperty"/>.
    /// </summary>
    /// <param name="logEvent">The Serilog event to inspect.</param>
    public void Emit(LogEvent logEvent)
    {
        try
        {
            // Forward every Warning-and-above event; Debug/Information/Verbose
            // stay local (file/debug/console sinks only).
            if (logEvent.Level < LogEventLevel.Warning)
                return;

            // User-facing CLI feedback (usage errors, missing-file probes) is
            // bridged from XISOSharp.Logger as Warning but tagged local-only:
            // it is expected behaviour, not a bug, and would flood the API.
            if (logEvent.Properties.TryGetValue(NoBugReportProperty, out LogEventPropertyValue? marker)
                && marker is ScalarValue { Value: true })
                return;

            string message = logEvent.RenderMessage();
            if (string.IsNullOrWhiteSpace(message) && logEvent.Exception != null)
                message = logEvent.Exception.Message;

            if (logEvent.Level == LogEventLevel.Warning)
                BugReporter.ReportWarning(message, logEvent.Exception);
            else
                BugReporter.ReportError(message, logEvent.Exception);
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
