using XISOSharp.Cli.Logging;

namespace XISOSharp.Tests;

/// <summary>
/// Locks the bug-report wire contract from <c>InstructionsToSendBugs.md</c>:
/// every report carries the Environment, Error, and Exception sections with
/// all of their required fields.
/// </summary>
public class BugReportFormatTests
{
    /// <summary>
    /// Verifies the environment block lists every required field, including
    /// the application name it was collected for.
    /// </summary>
    [Fact]
    public void EnvironmentBlock_HasEveryRequiredField()
    {
        string block = EnvironmentInfo.Collect("XISOSharp");

        Assert.Contains("=== Environment Details ===", block, StringComparison.Ordinal);
        foreach (string field in new[]
                 {
                     "Date:",
                     "Application Name: XISOSharp",
                     "Application Version:",
                     "OS Version:",
                     "Architecture:",
                     "Bitness:",
                     "Windows Version:",
                     "Processor Count:",
                     "Base Directory:",
                     "Temp Path:",
                 })
        {
            Assert.Contains(field, block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies the exception block exposes the required type, message, source,
    /// and stack-trace fields.
    /// </summary>
    [Fact]
    public void ExceptionBlock_HasEveryRequiredField()
    {
        Exception ex = new InvalidOperationException("boom") { Source = "XISOSharp.Tests" };

        string block = BugReporter.BuildExceptionBlock(ex);

        Assert.Contains("=== Exception Details ===", block, StringComparison.Ordinal);
        Assert.Contains("Type: System.InvalidOperationException", block, StringComparison.Ordinal);
        Assert.Contains("Message: boom", block, StringComparison.Ordinal);
        Assert.Contains("Source: XISOSharp.Tests", block, StringComparison.Ordinal);
        Assert.Contains("StackTrace:", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the exception block still has all four fields (with placeholders)
    /// when a message-only report is filed.
    /// </summary>
    [Fact]
    public void ExceptionBlock_NoException_HasPlaceholderFields()
    {
        string block = BugReporter.BuildExceptionBlock(null);

        Assert.Contains("Type: (none)", block, StringComparison.Ordinal);
        Assert.Contains("Message: (none)", block, StringComparison.Ordinal);
        Assert.Contains("Source: (none)", block, StringComparison.Ordinal);
        Assert.Contains("StackTrace: (none)", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a full report embeds the Environment, Error, and Exception
    /// sections and carries the exception in the standalone stack-trace field.
    /// </summary>
    [Fact]
    public void ComposeReport_EmbedsEnvironmentErrorAndExceptionSections()
    {
        Exception ex = new InvalidOperationException("boom");

        string report = BugReporter.ComposeReport("Error", "job failed", ex, out string stackTrace);

        Assert.Contains("Error: job failed", report, StringComparison.Ordinal);
        Assert.Contains("=== Environment Details ===", report, StringComparison.Ordinal);
        Assert.Contains("=== Error Details ===\njob failed", report, StringComparison.Ordinal);
        Assert.Contains("=== Exception Details ===", report, StringComparison.Ordinal);
        Assert.Contains("boom", stackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a message-only (no exception) report still contains every
    /// required section.
    /// </summary>
    [Fact]
    public void ComposeReport_NoException_StillHasAllSections()
    {
        string report = BugReporter.ComposeReport("Warning", "disk slow", null, out string stackTrace);

        Assert.Contains("=== Environment Details ===", report, StringComparison.Ordinal);
        Assert.Contains("=== Error Details ===\ndisk slow", report, StringComparison.Ordinal);
        Assert.Contains("=== Exception Details ===", report, StringComparison.Ordinal);
        Assert.Equal("Warning: disk slow", stackTrace);
    }
}
