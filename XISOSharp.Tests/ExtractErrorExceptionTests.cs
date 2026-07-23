namespace XISOSharp.Tests;

public class ExtractErrorExceptionTests
{
    [Fact]
    public void Constructor_StoresErrorCode()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.Equal(ExtractError.ErrIsoNoFiles, ex.ErrorCode);
    }

    [Fact]
    public void Constructor_StoresErrorCode_ErrEndOfSector()
    {
        var ex = new ExtractErrorException(ExtractError.ErrEndOfSector);
        Assert.Equal(ExtractError.ErrEndOfSector, ex.ErrorCode);
    }

    [Fact]
    public void Constructor_StoresErrorCode_ErrIsoRewritten()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoRewritten);
        Assert.Equal(ExtractError.ErrIsoRewritten, ex.ErrorCode);
    }

    [Fact]
    public void Exception_IsException()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void Exception_HasMessage()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.NotNull(ex.Message);
    }
}
