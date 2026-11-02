namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="Constants"/>: empty-directory detection,
/// attribute masking, and the startup banner, complementing <see cref="ConstantsTests"/>.
/// </summary>
public class ConstantsExtraTests
{
    /// <summary>Verifies an all-0xFF 14-byte header is an empty-directory sentinel.</summary>
    [Fact]
    public void IsEmptyDirectoryHeader_AllFf_True()
    {
        byte[] header = new byte[Constants.FilenameOffset];
        Array.Fill(header, (byte)0xFF);
        Assert.True(Constants.IsEmptyDirectoryHeader(header));
    }

    /// <summary>Verifies an all-zero 14-byte header is an empty-directory sentinel.</summary>
    [Fact]
    public void IsEmptyDirectoryHeader_AllZero_True()
    {
        Assert.True(Constants.IsEmptyDirectoryHeader(new byte[Constants.FilenameOffset]));
    }

    /// <summary>Verifies a mixed 14-byte header is not a sentinel.</summary>
    [Fact]
    public void IsEmptyDirectoryHeader_Mixed_False()
    {
        byte[] header = new byte[Constants.FilenameOffset];
        header[3] = 0x11;
        Assert.False(Constants.IsEmptyDirectoryHeader(header));
    }

    /// <summary>Verifies a header shorter than the filename offset is rejected.</summary>
    [Fact]
    public void IsEmptyDirectoryHeader_TooShort_False()
    {
        Assert.False(Constants.IsEmptyDirectoryHeader(new byte[Constants.FilenameOffset - 1]));
    }

    /// <summary>Verifies both sentinel values are recognized and others are not.</summary>
    [Fact]
    public void IsEmptyDirectorySentinel_Values()
    {
        Assert.True(Constants.IsEmptyDirectorySentinel(Constants.PadShort));
        Assert.True(Constants.IsEmptyDirectorySentinel(Constants.EmptyDirectorySentinel));
        Assert.False(Constants.IsEmptyDirectorySentinel(0x1234));
    }

    /// <summary>Verifies reserved attribute bits are masked out.</summary>
    [Theory]
    [InlineData(0x48, 0x00)]
    [InlineData(0xB7, 0xB7)]
    [InlineData(0xFF, 0xB7)]
    [InlineData(0x00, 0x00)]
    public void MaskAttributes_StripsReservedBits(byte raw, byte expected)
    {
        Assert.Equal(expected, Constants.MaskAttributes(raw));
    }

    /// <summary>Verifies the banner carries the product version, platform, and repository URL.</summary>
    [Fact]
    public void Banner_ContainsProductPlatformAndUrl()
    {
        string banner = Constants.Banner;
        Assert.StartsWith("XISOSharp v", banner, StringComparison.Ordinal);
        Assert.Contains("for ", banner, StringComparison.Ordinal);
        Assert.Contains("https://github.com/purelogiccode/XISOSharp", banner, StringComparison.Ordinal);
        Assert.EndsWith("\n", banner, StringComparison.Ordinal);
    }

    /// <summary>Verifies the optimized tag matches its documented length.</summary>
    [Fact]
    public void OptimizedTag_LengthMatchesConstant()
    {
        Assert.Equal(Constants.OptimizedTagLength, Constants.OptimizedTag.Length);
    }

    /// <summary>Verifies the header magic length matches its documented length.</summary>
    [Fact]
    public void HeaderData_LengthMatchesConstant()
    {
        Assert.Equal(Constants.HeaderDataLength, Constants.HeaderData.Length);
    }
}
