namespace XISOSharp.Tests;

public class ConstantsTests
{
    [Fact]
    public void NumSectors_ExactMultiple()
    {
        Assert.Equal(1u, Constants.NumSectors(Constants.SectorSize));
        Assert.Equal(2u, Constants.NumSectors(Constants.SectorSize * 2));
        Assert.Equal(100u, Constants.NumSectors(Constants.SectorSize * 100));
    }

    [Fact]
    public void NumSectors_WithRemainder()
    {
        Assert.Equal(1u, Constants.NumSectors(1));
        Assert.Equal(1u, Constants.NumSectors(Constants.SectorSize - 1));
        Assert.Equal(2u, Constants.NumSectors(Constants.SectorSize + 1));
        Assert.Equal(3u, Constants.NumSectors(Constants.SectorSize * 2 + 1));
    }

    [Fact]
    public void NumSectors_Zero()
    {
        Assert.Equal(0u, Constants.NumSectors(0));
    }

    [Fact]
    public void NumSectors_LargeValue()
    {
        var expected = (uint)Math.Ceiling((uint.MaxValue >> 1) / (double)Constants.SectorSize);
        Assert.Equal(expected, Constants.NumSectors(uint.MaxValue >> 1));
    }
}
