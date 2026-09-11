using XISOSharp.Models;

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
        XisoFormatException ex = new("bad format");
        Assert.IsType<IOException>(ex, exactMatch: false);
    }

    [Fact]
    public void XisoFormatException_ParameterlessConstructor_HasEmptyMessage()
    {
        XisoFormatException ex = new();
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void XisoFormatException_StringConstructor_PreservesMessage()
    {
        XisoFormatException ex = new("corrupt header");
        Assert.Contains("corrupt header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFormatException_InnerException_PreservesBoth()
    {
        InvalidOperationException inner = new("root cause");
        XisoFormatException ex = new("outer", inner);

        Assert.Contains("outer", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void XisoFormatException_CanBeCaughtAsIOException()
    {
        Assert.ThrowsAny<IOException>(ThrowFormat);
        return;

        static void ThrowFormat()
        {
            throw new XisoFormatException("test");
        }
    }

    #endregion

    #region XisoEmptyException

    [Fact]
    public void XisoEmptyException_IsExtractErrorException()
    {
        XisoEmptyException ex = new();
        Assert.IsType<ExtractErrorException>(ex, exactMatch: false);
    }

    [Fact]
    public void XisoEmptyException_ParameterlessConstructor_HasErrorCode()
    {
        XisoEmptyException ex = new();
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_ParameterlessConstructor_HasMessage()
    {
        XisoEmptyException ex = new();
        Assert.Contains("no files", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XisoEmptyException_StringConstructor_PreservesMessage()
    {
        XisoEmptyException ex = new("custom empty message");
        Assert.Contains("custom empty message", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_InnerException_PreservesAll()
    {
        IOException inner = new("disk error");
        XisoEmptyException ex = new("wrapper", inner);

        Assert.Contains("wrapper", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void XisoEmptyException_CanBeCaughtAsExtractErrorException()
    {
        Assert.ThrowsAny<ExtractErrorException>(ThrowEmpty);
        return;

        static void ThrowEmpty()
        {
            throw new XisoEmptyException();
        }
    }

    [Fact]
    public void XisoEmptyException_CanBeCaughtAsException()
    {
        bool caught;
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
        XisoFileTooLargeException ex = new("big.bin", 5_000_000_000L);
        Assert.IsType<IOException>(ex, exactMatch: false);
    }

    [Fact]
    public void XisoFileTooLargeException_ParameterlessConstructor_HasDefaults()
    {
        XisoFileTooLargeException ex = new();
        Assert.Equal("", ex.FileName);
        Assert.Equal(0, ex.FileSize);
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void XisoFileTooLargeException_StringConstructor_PreservesMessage()
    {
        XisoFileTooLargeException ex = new("custom message");
        Assert.Contains("custom message", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFileTooLargeException_FileNameAndSizeConstructor_PreservesProperties()
    {
        XisoFileTooLargeException ex = new("huge.dat", 5_000_000_000L);

        Assert.Equal("huge.dat", ex.FileName);
        Assert.Equal(5_000_000_000L, ex.FileSize);
        Assert.Contains("huge.dat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 GB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XisoFileTooLargeException_InnerException_PreservesAll()
    {
        InvalidOperationException inner = new("disk full");
        XisoFileTooLargeException ex = new("wrapper", inner);

        Assert.Contains("wrapper", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void XisoFileTooLargeException_ExactLimit_FileSizeIsUint32MaxPlus1()
    {
        const long overLimit = (long)uint.MaxValue + 1;
        XisoFileTooLargeException ex = new("borderline.bin", overLimit);

        Assert.Equal(overLimit, ex.FileSize);
        Assert.Equal("borderline.bin", ex.FileName);
    }

    [Fact]
    public void XisoFileTooLargeException_CanBeCaughtAsIOException()
    {
        Assert.ThrowsAny<IOException>(ThrowTooLarge);
        return;

        static void ThrowTooLarge()
        {
            throw new XisoFileTooLargeException("f", 1);
        }
    }

    #endregion

    #region ExtractFileException inner-exception contract

    [Fact]
    public void ExtractFileException_NullInner_LeavesInnerExceptionNull()
    {
        ExtractFileException ex = new(
            ExtractError.ErrFileTruncated, "inner.bin", "out.bin", 0, 100, "truncated", 10, null);

        Assert.Null(ex.InnerException);
        Assert.Contains("inner.bin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFileException_WithInner_PreservesInner()
    {
        IOException inner = new("disk gone");
        ExtractFileException ex = new(
            ExtractError.ErrFileWrite, "inner.bin", "out.bin", 0, 100, "write failed", -1, inner);

        Assert.Same(inner, ex.InnerException);
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
            new XisoFileTooLargeException("big", 1)
        ];

        foreach (Exception ex in exceptions)
        {
            Assert.IsType<Exception>(ex, exactMatch: false);
            Assert.NotNull(ex.Message);
        }
    }

    #endregion
}
