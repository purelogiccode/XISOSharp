using Serilog.Core;
using Serilog.Events;

namespace XISOSharp.Cli.Logging;

/// <summary>
/// Serilog sink that forwards every Warning-and-above event to the bug-report API.
/// Never throws: failures are swallowed so logging can never crash the app.
/// </summary>
internal sealed class BugReportSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        try
        {
            if (logEvent.Level < LogEventLevel.Warning)
                return;

            var message = logEvent.RenderMessage();
            if (string.IsNullOrWhiteSpace(message) && logEvent.Exception != null)
                message = logEvent.Exception.Message;

            if (logEvent.Exception != null)
                BugReporter.ReportError(message, logEvent.Exception);
            else
                BugReporter.ReportWarning(message);
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
