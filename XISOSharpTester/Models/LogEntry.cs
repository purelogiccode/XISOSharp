namespace XISOSharpTester.Models;

/// <summary>
/// Represents a single log entry displayed in the application's
/// scrolling log output, with a message and timestamp.
/// </summary>
public class LogEntry
{
    /// <summary>
    /// Gets or sets the log message text.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp string (e.g. "14:30:05").
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;
}
