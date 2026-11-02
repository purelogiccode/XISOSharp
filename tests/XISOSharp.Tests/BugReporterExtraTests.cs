using XISOSharp.Cli.Logging;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="BugReporter"/> report entry points, exception-block
/// composition, and flush behavior, complementing <c>BugReportFormatTests</c>.
/// </summary>
public class BugReporterExtraTests
{
    /// <summary>Verifies report entry points are no-ops (and never throw) under a test host.</summary>
    [Fact]
    public void ReportEntryPoints_UnderTestHost_DoNotThrow()
    {
        InvalidOperationException ex = new("boom");
        BugReporter.ReportWarning("warning");
        BugReporter.ReportWarning("warning with exception", ex);
        BugReporter.ReportError("error");
        BugReporter.ReportError("error with exception", ex);
        BugReporter.ReportException(ex, "context");
        BugReporter.ReportException(ex, string.Empty);
    }

    /// <summary>Verifies null messages and exceptions are tolerated by the entry points.</summary>
    [Fact]
    public void ReportEntryPoints_NullInputs_DoNotThrow()
    {
        BugReporter.ReportWarning(null!);
        BugReporter.ReportError(null!);
    }

    /// <summary>Verifies flushing with no pending sends succeeds immediately.</summary>
    [Fact]
    public void Flush_NoPendingSends_ReturnsTrue()
    {
        Assert.True(BugReporter.Flush(TimeSpan.Zero));
        Assert.True(BugReporter.Flush(TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>Verifies the exception block for a null exception keeps the required labels.</summary>
    [Fact]
    public void BuildExceptionBlock_NullException_HasLabels()
    {
        string block = BugReporter.BuildExceptionBlock(null);
        Assert.Contains("=== Exception Details ===", block, StringComparison.Ordinal);
        Assert.Contains("Type: (none)", block, StringComparison.Ordinal);
        Assert.Contains("Message: (none)", block, StringComparison.Ordinal);
        Assert.Contains("Source: (none)", block, StringComparison.Ordinal);
        Assert.Contains("StackTrace: (none)", block, StringComparison.Ordinal);
    }

    /// <summary>Verifies the exception block reports the type, message, source, and inner exception.</summary>
    [Fact]
    public void BuildExceptionBlock_WithInnerException_ReportsBoth()
    {
        InvalidOperationException inner = new("inner failure");
        InvalidOperationException outer = new("outer failure", inner);

        string block = BugReporter.BuildExceptionBlock(outer);

        Assert.Contains("System.InvalidOperationException", block, StringComparison.Ordinal);
        Assert.Contains("outer failure", block, StringComparison.Ordinal);
        Assert.Contains("Inner Message: inner failure", block, StringComparison.Ordinal);
    }

    /// <summary>Verifies a zero stack budget drops the stack trace but keeps every label.</summary>
    [Fact]
    public void BuildExceptionBlock_ZeroStackBudget_DropsStack()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("with stack");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        string block = BugReporter.BuildExceptionBlock(ex, 0);

        Assert.Contains("Type: System.InvalidOperationException", block, StringComparison.Ordinal);
        Assert.Contains("StackTrace: ", block, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", block, StringComparison.Ordinal);
    }

    /// <summary>Verifies a budgeted exception block never exceeds the budget for the stack portion.</summary>
    [Fact]
    public void BuildExceptionBlock_TruncatesLongStack()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("deep");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        string block = BugReporter.BuildExceptionBlock(ex, 10);

        int index = block.IndexOf("StackTrace: ", StringComparison.Ordinal);
        Assert.True(index >= 0);
        string stackPart = block[(index + "StackTrace: ".Length)..];
        Assert.True(stackPart.Length <= 13, $"stack portion should be truncated, was {stackPart.Length} chars");
    }
}
