namespace XISOSharp.Tests;

/// <summary>
/// Tests for the Logger utility class, verifying output routing, suppression flags, and field management.
/// </summary>
public class LoggerTests : IDisposable
{
    private readonly StringWriter _outCapture;
    private readonly StringWriter _errorCapture;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public LoggerTests()
    {
        _originalOut = Logger.Out;
        _originalError = Logger.Error;

        _outCapture = new StringWriter();
        _errorCapture = new StringWriter();

        Logger.Out = _outCapture;
        Logger.Error = _errorCapture;

        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.Warned = false;
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;
        Logger.TotalBytesAllIsos = 0;
        Logger.TotalFilesAllIsos = 0;
        Logger.RemoveSystemUpdate = false;
        Logger.MediaEnable = true;
        Logger.XboxDiscLseek = 0;
    }

    public void Dispose()
    {
        Logger.Out = _originalOut;
        Logger.Error = _originalError;
        _outCapture.Dispose();
        _errorCapture.Dispose();
    }

    /// <summary>
    /// Verifies that Log writes the given message to the output stream.
    /// </summary>
    [Fact]
    public void Log_WritesToOut()
    {
        Logger.Log("hello");
        Assert.Equal("hello", _outCapture.ToString());
    }

    /// <summary>
    /// Verifies that Log formats messages with string placeholders and arguments.
    /// </summary>
    [Fact]
    public void Log_WithArgs()
    {
        Logger.Log("hello {0} {1}", "world", 42);
        Assert.Equal("hello world 42", _outCapture.ToString());
    }

    /// <summary>
    /// Verifies that Log produces no output when Quiet mode is enabled.
    /// </summary>
    [Fact]
    public void Log_SuppressedWhenQuiet()
    {
        Logger.Quiet = true;
        Logger.Log("should not appear");
        Assert.Equal("", _outCapture.ToString());
    }

    /// <summary>
    /// Verifies that LogLine writes the message followed by a newline to the output stream.
    /// </summary>
    [Fact]
    public void LogLine_WritesWithNewline()
    {
        Logger.LogLine("test");
        Assert.Equal("test\r\n", _outCapture.ToString());
    }

    /// <summary>
    /// Verifies that LogLine produces no output when Quiet mode is enabled.
    /// </summary>
    [Fact]
    public void LogLine_SuppressedWhenQuiet()
    {
        Logger.Quiet = true;
        Logger.LogLine("should not appear");
        Assert.Equal("", _outCapture.ToString());
    }

    /// <summary>
    /// Verifies that LogErr writes the given message to the error stream.
    /// </summary>
    [Fact]
    public void LogErr_WritesToError()
    {
        Logger.LogErr("error msg");
        Assert.Equal("error msg", _errorCapture.ToString());
    }

    /// <summary>
    /// Verifies that LogErr formats error messages with string placeholders and arguments.
    /// </summary>
    [Fact]
    public void LogErr_WithArgs()
    {
        Logger.LogErr("error {0}", 99);
        Assert.Equal("error 99", _errorCapture.ToString());
    }

    /// <summary>
    /// Verifies that LogErr produces no output when RealQuiet mode is enabled.
    /// </summary>
    [Fact]
    public void LogErr_SuppressedWhenRealQuiet()
    {
        Logger.RealQuiet = true;
        Logger.LogErr("should not appear");
        Assert.Equal("", _errorCapture.ToString());
    }

    /// <summary>
    /// Verifies that Flush does not throw when Quiet is disabled.
    /// </summary>
    [Fact]
    public void Flush_DoesNotThrow_WhenNotQuiet()
    {
        Logger.Out = _outCapture;
        Logger.Flush();
    }

    /// <summary>
    /// Verifies that Flush does not throw when Quiet mode is enabled.
    /// </summary>
    [Fact]
    public void Flush_DoesNotThrow_WhenQuiet()
    {
        Logger.Quiet = true;
        Logger.Flush();
    }

    /// <summary>
    /// Verifies the default values of all Logger static fields after construction.
    /// </summary>
    [Fact]
    public void Fields_DefaultValues()
    {
        Assert.False(Logger.Quiet);
        Assert.False(Logger.RealQuiet);
        Assert.False(Logger.Warned);
        Assert.Equal(0, Logger.TotalBytes);
        Assert.Equal(0, Logger.TotalFiles);
        Assert.Equal(0, Logger.TotalBytesAllIsos);
        Assert.Equal(0, Logger.TotalFilesAllIsos);
        Assert.False(Logger.RemoveSystemUpdate);
        Assert.True(Logger.MediaEnable);
        Assert.Equal(0, Logger.XboxDiscLseek);
    }

    /// <summary>
    /// Verifies that all Logger static fields can be set to non-default values and read back correctly.
    /// </summary>
    [Fact]
    public void Fields_CanBeSet()
    {
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        Logger.Warned = true;
        Logger.TotalBytes = 100;
        Logger.TotalFiles = 5;
        Logger.TotalBytesAllIsos = 200;
        Logger.TotalFilesAllIsos = 10;
        Logger.RemoveSystemUpdate = true;
        Logger.MediaEnable = false;
        Logger.XboxDiscLseek = 0x10000;

        Assert.True(Logger.Quiet);
        Assert.True(Logger.RealQuiet);
        Assert.True(Logger.Warned);
        Assert.Equal(100, Logger.TotalBytes);
        Assert.Equal(5, Logger.TotalFiles);
        Assert.Equal(200, Logger.TotalBytesAllIsos);
        Assert.Equal(10, Logger.TotalFilesAllIsos);
        Assert.True(Logger.RemoveSystemUpdate);
        Assert.False(Logger.MediaEnable);
        Assert.Equal(0x10000, Logger.XboxDiscLseek);
    }
}