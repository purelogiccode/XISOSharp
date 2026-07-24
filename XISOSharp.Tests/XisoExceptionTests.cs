using System.Runtime.Serialization;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the granular exception types: <see cref="XisoFormatException"/>,
/// <see cref="XisoEmptyException"/>, and <see cref="XisoFileTooLargeException"/>.
/// </summary>
public class XisoExceptionTests
{
    #region XisoFormatException

    [Fact]
    public void XisoFormatException_IsIOException()
    {
        var ex = new XisoFormatException("bad format");
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void XisoFormatException_ParameterlessConstructor_HasEmptyMessage()
    {
        var ex = new XisoFormatException();
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void XisoFormatException_StringConstructor_PreservesMessage()
    {
        var ex = new XisoFormatException("corrupt header");
        Assert.Contains("corrupt header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFormatException_InnerException_PreservesBoth()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new XisoFormatException("outer", inner);

        Assert.Contains("outer", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void XisoFormatException_CanBeCaughtAsIOException()
    {
        try
        {
            throw new XisoFormatException("test");
        }
        catch (IOException)
        {
            // Expected
        }
    }

    #endregion

    #region XisoEmptyException

    [Fact]
    public void XisoEmptyException_IsExtractErrorException()
    {
        var ex = new XisoEmptyException();
        Assert.IsAssignableFrom<ExtractErrorException>(ex);
    }

    [Fact]
    public void XisoEmptyException_ParameterlessConstructor_HasErrorCode()
    {
        var ex = new XisoEmptyException();
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_ParameterlessConstructor_HasMessage()
    {
        var ex = new XisoEmptyException();
        Assert.Contains("no files", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XisoEmptyException_StringConstructor_PreservesMessage()
    {
        var ex = new XisoEmptyException("custom empty message");
        Assert.Contains("custom empty message", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_InnerException_PreservesAll()
    {
        var inner = new IOException("disk error");
        var ex = new XisoEmptyException("wrapper", inner);

        Assert.Contains("wrapper", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_CanBeCaughtAsExtractErrorException()
    {
        try
        {
            throw new XisoEmptyException();
        }
        catch (ExtractErrorException)
        {
            // Expected
        }
    }

    [Fact]
    public void XisoEmptyException_CanBeCaughtAsException()
    {
        var caught = false;
        try
        {
            throw new XisoEmptyException();
        }
        catch (Exception)
        {
            caught = true;
        }
        Assert.True(caught);
    }

    #endregion

    #region XisoFileTooLargeException

    [Fact]
    public void XisoFileTooLargeException_IsIOException()
    {
        var ex = new XisoFileTooLargeException("big.bin", 5_000_000_000L);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void XisoFileTooLargeException_ParameterlessConstructor_HasDefaults()
    {
        var ex = new XisoFileTooLargeException();
        Assert.Equal("", ex.FileName);
        Assert.Equal(0, ex.FileSize);
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void XisoFileTooLargeException_StringConstructor_PreservesMessage()
    {
        var ex = new XisoFileTooLargeException("custom message");
        Assert.Contains("custom message", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFileTooLargeException_FileNameAndSizeConstructor_PreservesProperties()
    {
        var ex = new XisoFileTooLargeException("huge.dat", 5_000_000_000L);

        Assert.Equal("huge.dat", ex.FileName);
        Assert.Equal(5_000_000_000L, ex.FileSize);
        Assert.Contains("huge.dat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 GB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFileTooLargeException_InnerException_PreservesAll()
    {
        var inner = new InvalidOperationException("disk full");
        var ex = new XisoFileTooLargeException("wrapper", inner);

        Assert.Contains("wrapper", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void XisoFileTooLargeException_ExactLimit_FileSizeIsUint32MaxPlus1()
    {
        var overLimit = (long)uint.MaxValue + 1;
        var ex = new XisoFileTooLargeException("borderline.bin", overLimit);

        Assert.Equal(overLimit, ex.FileSize);
        Assert.Equal("borderline.bin", ex.FileName);
    }

    [Fact]
    public void XisoFileTooLargeException_CanBeCaughtAsIOException()
    {
        try
        {
            throw new XisoFileTooLargeException("f", 1);
        }
        catch (IOException)
        {
            // Expected
        }
    }

    #endregion

    #region Exception hierarchy integration

    [Fact]
    public void AllXisoExceptions_CanBeCaughtByException()
    {
        Exception[] exceptions =
        [
            new XisoFormatException("fmt"),
            new XisoEmptyException("empty"),
            new XisoFileTooLargeException("big", 1),
        ];

        foreach (var ex in exceptions)
        {
            Assert.IsAssignableFrom<Exception>(ex);
            Assert.NotNull(ex.Message);
        }
    }

    #endregion
}
