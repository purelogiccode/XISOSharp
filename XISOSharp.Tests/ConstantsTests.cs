namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="Constants.NumSectors"/>, verifying sector count calculations
/// for exact multiples, values with remainder, zero, and large values.
/// </summary>
public class ConstantsTests
{
    /// <summary>
    /// Verifies that <see cref="Constants.NumSectors"/> returns the correct sector count
    /// for byte counts that are exact multiples of the sector size.
    /// </summary>
    [Fact]
    public void NumSectors_ExactMultiple()
    {
        Assert.Equal(1u, Constants.NumSectors(Constants.SectorSize));
        Assert.Equal(2u, Constants.NumSectors(Constants.SectorSize * 2));
        Assert.Equal(100u, Constants.NumSectors(Constants.SectorSize * 100));
    }

    /// <summary>
    /// Verifies that <see cref="Constants.NumSectors"/> rounds up when the byte count
    /// is not an exact multiple of the sector size.
    /// </summary>
    [Fact]
    public void NumSectors_WithRemainder()
    {
        Assert.Equal(1u, Constants.NumSectors(1));
        Assert.Equal(1u, Constants.NumSectors(Constants.SectorSize - 1));
        Assert.Equal(2u, Constants.NumSectors(Constants.SectorSize + 1));
        Assert.Equal(3u, Constants.NumSectors((Constants.SectorSize * 2) + 1));
    }

    /// <summary>
    /// Verifies that <see cref="Constants.NumSectors"/> returns zero when the byte count is zero.
    /// </summary>
    [Fact]
    public void NumSectors_Zero()
    {
        Assert.Equal(0u, Constants.NumSectors(0));
    }

    /// <summary>
    /// Verifies that <see cref="Constants.NumSectors"/> correctly handles a large
    /// byte count value, matching the expected ceiling division result.
    /// </summary>
    [Fact]
    public void NumSectors_LargeValue()
    {
        var expected = (uint)Math.Ceiling((uint.MaxValue >> 1) / (double)Constants.SectorSize);
        Assert.Equal(expected, Constants.NumSectors(uint.MaxValue >> 1));
    }
}