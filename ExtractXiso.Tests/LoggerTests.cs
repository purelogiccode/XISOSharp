namespace ExtractXiso.Tests;

public class LoggerTests : IDisposable
{
    private readonly StringWriter _stdOutCapture;
    private readonly StringWriter _stdErrCapture;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public LoggerTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;

        _stdOutCapture = new StringWriter();
        _stdErrCapture = new StringWriter();

        Console.SetOut(_stdOutCapture);
        Console.SetError(_stdErrCapture);

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
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _stdOutCapture.Dispose();
        _stdErrCapture.Dispose();
    }

    [Fact]
    public void Log_WritesToStdOut()
    {
        Logger.Log("hello");
        Assert.Equal("hello", _stdOutCapture.ToString());
    }

    [Fact]
    public void Log_WithArgs()
    {
        Logger.Log("hello {0} {1}", "world", 42);
        Assert.Equal("hello world 42", _stdOutCapture.ToString());
    }

    [Fact]
    public void Log_SuppressedWhenQuiet()
    {
        Logger.Quiet = true;
        Logger.Log("should not appear");
        Assert.Equal("", _stdOutCapture.ToString());
    }

    [Fact]
    public void LogLine_WritesWithNewline()
    {
        Logger.LogLine("test");
        Assert.Equal("test\r\n", _stdOutCapture.ToString());
    }

    [Fact]
    public void LogLine_SuppressedWhenQuiet()
    {
        Logger.Quiet = true;
        Logger.LogLine("should not appear");
        Assert.Equal("", _stdOutCapture.ToString());
    }

    [Fact]
    public void LogErr_WritesToStdErr()
    {
        Logger.LogErr("error msg");
        Assert.Equal("error msg", _stdErrCapture.ToString());
    }

    [Fact]
    public void LogErr_WithArgs()
    {
        Logger.LogErr("error {0}", 99);
        Assert.Equal("error 99", _stdErrCapture.ToString());
    }

    [Fact]
    public void LogErr_SuppressedWhenRealQuiet()
    {
        Logger.RealQuiet = true;
        Logger.LogErr("should not appear");
        Assert.Equal("", _stdErrCapture.ToString());
    }

    [Fact]
    public void Flush_DoesNotThrow_WhenNotQuiet()
    {
        Logger.Flush();
    }

    [Fact]
    public void Flush_DoesNotThrow_WhenQuiet()
    {
        Logger.Quiet = true;
        Logger.Flush();
    }

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
