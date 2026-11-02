namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="Logger"/> forwarding bridges, quiet-mode suppression,
/// format handling, and thread-safe counters, complementing <c>LoggerTests</c>.
/// </summary>
[Collection("Sequential")]
public class LoggerExtraTests : IDisposable
{
    private readonly TextWriter _origOut = Logger.Out;
    private readonly TextWriter _origErr = Logger.Error;
    private readonly bool _origQuiet = Logger.Quiet;
    private readonly bool _origRealQuiet = Logger.RealQuiet;
    private readonly Action<string>? _origInfo = Logger.ForwardInfo;
    private readonly Action<string>? _origError = Logger.ForwardError;
    private readonly Action<string>? _origDebug = Logger.ForwardDebug;
    private readonly Action<string>? _origWarning = Logger.ForwardWarning;
    private readonly long _origTotalBytes = Logger.TotalBytes;
    private readonly int _origTotalFiles = Logger.TotalFiles;
    private readonly long _origTotalBytesAll = Logger.TotalBytesAllIsos;
    private readonly int _origTotalFilesAll = Logger.TotalFilesAllIsos;

    /// <summary>Restores every process-wide logger setting touched by these tests.</summary>
    public void Dispose()
    {
        Logger.Out = _origOut;
        Logger.Error = _origErr;
        Logger.Quiet = _origQuiet;
        Logger.RealQuiet = _origRealQuiet;
        Logger.ForwardInfo = _origInfo;
        Logger.ForwardError = _origError;
        Logger.ForwardDebug = _origDebug;
        Logger.ForwardWarning = _origWarning;
        Logger.TotalBytes = _origTotalBytes;
        Logger.TotalFiles = _origTotalFiles;
        Logger.TotalBytesAllIsos = _origTotalBytesAll;
        Logger.TotalFilesAllIsos = _origTotalFilesAll;
    }

    /// <summary>Verifies debug messages are forwarded without touching <see cref="Logger.Out"/>.</summary>
    [Fact]
    public void LogDebug_ForwardsOnly()
    {
        StringWriter outWriter = new();
        Logger.Out = outWriter;
        string? forwarded = null;
        Logger.ForwardDebug = m => forwarded = m;

        Logger.LogDebug("diag");

        Assert.Equal("diag", forwarded);
        Assert.Equal(string.Empty, outWriter.ToString());
    }

    /// <summary>Verifies warning messages are forwarded without touching <see cref="Logger.Error"/>.</summary>
    [Fact]
    public void LogWarn_ForwardsOnly()
    {
        StringWriter errWriter = new();
        Logger.Error = errWriter;
        string? forwarded = null;
        Logger.ForwardWarning = m => forwarded = m;

        Logger.LogWarn("warn");

        Assert.Equal("warn", forwarded);
        Assert.Equal(string.Empty, errWriter.ToString());
    }

    /// <summary>Verifies a message without arguments is written verbatim (braces preserved).</summary>
    [Fact]
    public void Log_NoArgs_WritesVerbatim()
    {
        StringWriter outWriter = new();
        Logger.Out = outWriter;

        Logger.Log("{not} a {format}");

        Assert.Equal("{not} a {format}", outWriter.ToString());
    }

    /// <summary>Verifies a message with arguments is composite-formatted.</summary>
    [Fact]
    public void Log_WithArgs_Formats()
    {
        StringWriter outWriter = new();
        Logger.Out = outWriter;

        Logger.Log("value={0}", 42);

        Assert.Equal("value=42", outWriter.ToString());
    }

    /// <summary>Verifies quiet mode suppresses console output but keeps forwarding.</summary>
    [Fact]
    public void Log_Quiet_SuppressesOutputButForwards()
    {
        StringWriter outWriter = new();
        Logger.Out = outWriter;
        Logger.Quiet = true;
        List<string> forwarded = [];
        Logger.ForwardInfo = forwarded.Add;

        Logger.Log("hidden");
        Logger.LogLine("line");

        Assert.Equal(string.Empty, outWriter.ToString());
        Assert.Equal(["hidden", "line"], forwarded);
    }

    /// <summary>Verifies real-quiet mode suppresses error output but keeps forwarding.</summary>
    [Fact]
    public void LogErr_RealQuiet_SuppressesOutputButForwards()
    {
        StringWriter errWriter = new();
        Logger.Error = errWriter;
        Logger.RealQuiet = true;
        List<string> forwarded = [];
        Logger.ForwardError = forwarded.Add;

        Logger.LogErr("hidden error");

        Assert.Equal(string.Empty, errWriter.ToString());
        Assert.Equal(["hidden error"], forwarded);
    }

    /// <summary>Verifies a null <see cref="Logger.Out"/> discards output without throwing.</summary>
    [Fact]
    public void Log_NullOut_DiscardsOutput()
    {
        Logger.Out = null!;
        Logger.Log("discarded");
        Logger.LogLine("discarded");
        Logger.Flush();
    }

    /// <summary>Verifies a null <see cref="Logger.Error"/> discards output without throwing.</summary>
    [Fact]
    public void LogErr_NullError_DiscardsOutput()
    {
        Logger.Error = null!;
        Logger.LogErr("discarded");
    }

    /// <summary>Verifies a throwing forwarding target never breaks logging.</summary>
    [Fact]
    public void Forward_ThrowingTarget_IsSwallowed()
    {
        StringWriter outWriter = new();
        Logger.Out = outWriter;
        Logger.ForwardInfo = _ => throw new InvalidOperationException("boom");

        Logger.Log("still written");

        Assert.Equal("still written", outWriter.ToString());
    }

    /// <summary>Verifies file counters accumulate and reset.</summary>
    [Fact]
    public void RecordFileWritten_Accumulates()
    {
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;

        Logger.RecordFileWritten(100);
        Logger.RecordFileWritten(250);

        Assert.Equal(350, Logger.TotalBytes);
        Assert.Equal(2, Logger.TotalFiles);
    }

    /// <summary>Verifies ISO-level counters accumulate alongside the operation totals.</summary>
    [Fact]
    public void RecordIsoFileWritten_AccumulatesBoth()
    {
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;
        Logger.TotalBytesAllIsos = 0;
        Logger.TotalFilesAllIsos = 0;

        Logger.RecordIsoFileWritten(512);

        Assert.Equal(512, Logger.TotalBytes);
        Assert.Equal(1, Logger.TotalFiles);
        Assert.Equal(512, Logger.TotalBytesAllIsos);
        Assert.Equal(1, Logger.TotalFilesAllIsos);
    }

    /// <summary>Verifies parallel counter updates stay exact.</summary>
    [Fact]
    public void RecordFileWritten_IsThreadSafe()
    {
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;

        Parallel.For(0, 1000, _ => Logger.RecordFileWritten(10));

        Assert.Equal(10_000, Logger.TotalBytes);
        Assert.Equal(1000, Logger.TotalFiles);
    }
}
