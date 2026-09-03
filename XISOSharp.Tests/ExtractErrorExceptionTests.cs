namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="ExtractErrorException"/>, verifying that the exception
/// correctly stores the <see cref="ExtractError"/> error code and behaves as
/// a standard exception.
/// </summary>
public class ExtractErrorExceptionTests
{
    /// <summary>
    /// Verifies that the constructor stores the <see cref="ExtractError.ErrIsoNoFiles"/>
    /// error code.
    /// </summary>
    [Fact]
    public void Constructor_StoresErrorCode()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    /// <summary>
    /// Verifies that the constructor stores the <see cref="ExtractError.ErrEndOfSector"/>
    /// error code.
    /// </summary>
    [Fact]
    public void Constructor_StoresErrorCode_ErrEndOfSector()
    {
        var ex = new ExtractErrorException(ExtractError.ErrEndOfSector);
        Assert.Equal(ExtractError.ErrEndOfSector, ex.ErrorCode);
    }

    /// <summary>
    /// Verifies that the constructor stores the <see cref="ExtractError.ErrIsoRewritten"/>
    /// error code.
    /// </summary>
    [Fact]
    public void Constructor_StoresErrorCode_ErrIsoRewritten()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoRewritten);
        Assert.Equal(ExtractError.ErrIsoRewritten, ex.ErrorCode);
    }

    /// <summary>
    /// Verifies that <see cref="ExtractErrorException"/> is assignable from <see cref="Exception"/>.
    /// </summary>
    [Fact]
    public void Exception_IsException()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.IsType<Exception>(ex, exactMatch: false);
    }

    /// <summary>
    /// Verifies that <see cref="ExtractErrorException"/> has a non-null
    /// <see cref="Exception.Message"/>.
    /// </summary>
    [Fact]
    public void Exception_HasMessage()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.NotNull(ex.Message);
    }
}