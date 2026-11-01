using Serilog;
using Serilog.Core;
using Serilog.Events;
using XISOSharp.Cli;
using XISOSharp.Cli.Logging;

namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="OverwritePrompt"/>: contradictory flags,
/// existing file/directory handling, and interactive prompt responses.
/// </summary>
public class OverwritePromptExtraTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>Removes temporary directories.</summary>
    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_prompt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static bool Confirm(string path, bool yes, bool no, string input, out string output)
    {
        StringWriter writer = new();
        bool result = OverwritePrompt.ConfirmOverwrite(path, yes, no, new StringReader(input), writer);
        output = writer.ToString();
        return result;
    }

    /// <summary>Verifies a missing output is accepted without prompting.</summary>
    [Fact]
    public void MissingPath_ReturnsTrue()
    {
        string missing = Path.Combine(CreateTempDir(), "missing.bin");
        Assert.True(Confirm(missing, false, false, string.Empty, out string output));
        Assert.Equal(string.Empty, output);
    }

    /// <summary>Verifies contradictory flags deny even when the output does not exist.</summary>
    [Fact]
    public void BothFlags_Deny()
    {
        string missing = Path.Combine(CreateTempDir(), "missing.bin");
        Assert.False(Confirm(missing, true, true, string.Empty, out string output));
        Assert.Contains("Cannot use both", output, StringComparison.Ordinal);
    }

    /// <summary>Verifies assume-no refuses an existing file.</summary>
    [Fact]
    public void AssumeNo_ExistingFile_Refuses()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "out.bin");
        File.WriteAllText(file, "x");

        Assert.False(Confirm(file, false, true, string.Empty, out string output));
        Assert.Contains("File already exists", output, StringComparison.Ordinal);
    }

    /// <summary>Verifies assume-no refuses an existing directory output.</summary>
    [Fact]
    public void AssumeNo_ExistingDirectory_Refuses()
    {
        string dir = CreateTempDir();
        Assert.False(Confirm(dir, false, true, string.Empty, out string output));
        Assert.Contains("File already exists", output, StringComparison.Ordinal);
    }

    /// <summary>Verifies assume-yes overwrites an existing file without prompting.</summary>
    [Fact]
    public void AssumeYes_ExistingFile_ReturnsTrue()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "out.bin");
        File.WriteAllText(file, "x");

        Assert.True(Confirm(file, true, false, string.Empty, out _));
    }

    /// <summary>
    /// Expected refusals are user decisions, not bugs: the warning must carry
    /// the <c>NoBugReport</c> marker so <see cref="BugReportSink"/> drops it.
    /// </summary>
    [Fact]
    public void AssumeNo_Warning_IsTaggedNoBugReport()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "out.bin");
        File.WriteAllText(file, "x");

        List<LogEvent> events = [];
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(new CaptureSink(events)).CreateLogger();
        try
        {
            Assert.False(Confirm(file, false, true, string.Empty, out _));
        }
        finally
        {
            Log.Logger = previous;
        }

        LogEvent warning = Assert.Single(events, e => e.Level == LogEventLevel.Warning);
        Assert.Contains("Overwrite refused", warning.RenderMessage(), StringComparison.Ordinal);
        Assert.True(warning.Properties.TryGetValue(BugReportSink.NoBugReportProperty, out LogEventPropertyValue? marker));
        Assert.Equal(true, Assert.IsType<ScalarValue>(marker).Value);
    }

    /// <summary>Verifies interactive yes answers proceed and other answers deny.</summary>
    [Theory]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData(" yes ", true)]
    [InlineData("n", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData("maybe", false)]
    public void Prompt_Responses(string response, bool expected)
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "out.bin");
        File.WriteAllText(file, "x");

        Assert.Equal(expected, Confirm(file, false, false, response, out string output));
        Assert.Contains("Would you like to overwrite?", output, StringComparison.Ordinal);
    }

    /// <summary>Captures every Serilog event emitted while the test owns <see cref="Log.Logger"/>.</summary>
    private sealed class CaptureSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
