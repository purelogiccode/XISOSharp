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
/// Serilog sink that forwards Error-and-above events to the bug-report API.
/// Warning-and-below events are routine operational noise (non-zero CLI exits,
/// missing-file probes) and never file bug reports (BUG-X-002).
/// Never throws: failures are swallowed so logging can never crash the app.
/// </summary>
internal sealed class BugReportSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        try
        {
            // BUG-X-002: only Error/Fatal (real crashes) forward, keeping the 8/min
            // throttle budget for them. Warning-level routine events are dropped here;
            // direct ReportWarning calls are likewise suppressed in BugReporter.
            if (logEvent.Level < LogEventLevel.Error)
                return;

            var message = logEvent.RenderMessage();
            if (string.IsNullOrWhiteSpace(message) && logEvent.Exception != null)
                message = logEvent.Exception.Message;

            BugReporter.ReportError(message, logEvent.Exception);
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
