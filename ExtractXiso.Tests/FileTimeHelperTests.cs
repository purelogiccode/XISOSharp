namespace ExtractXiso.Tests;

public class FileTimeHelperTests
{
    [Fact]
    public void WriteFileTimeNow_WritesEightBytes()
    {
        Span<byte> dest = stackalloc byte[8];
        FileTimeHelper.WriteFileTimeNow(dest);

        var nonZero = false;
        for (int i = 0; i < 8; i++)
        {
            if (dest[i] != 0)
            {
                nonZero = true;
                break;
            }
        }

        Assert.True(nonZero, "Expected non-zero FILETIME value");
    }

    [Fact]
    public void WriteFileTimeNow_IdempotentPerCall()
    {
        Span<byte> first = stackalloc byte[8];
        Span<byte> second = stackalloc byte[8];
        Span<byte> immediate = stackalloc byte[8];

        FileTimeHelper.WriteFileTimeNow(first);
        FileTimeHelper.WriteFileTimeNow(immediate);
        FileTimeHelper.WriteFileTimeNow(second);

        Assert.True(first.SequenceEqual(immediate) || first.SequenceEqual(second),
            "FILETIME should be within 1-2 seconds");

        Assert.False(first.SequenceEqual(stackalloc byte[8]),
            "FILETIME should not be all zeros");
    }

    [Fact]
    public void WriteFileTimeNow_ReasonableRange()
    {
        Span<byte> dest = stackalloc byte[8];
        FileTimeHelper.WriteFileTimeNow(dest);

        uint low = BitConverter.ToUInt32(dest);
        uint high = BitConverter.ToUInt32(dest[4..]);

        Assert.NotEqual(0u, high | low);

        double filetime = (double)high * 4.0 * (double)(1L << 30) + low;
        double unixTime = filetime / 1.0e7 - (369.0 * 365.25 * 24.0 * 60.0 * 60.0 - (3.0 * 24.0 * 60.0 * 60.0 + 6.0 * 60.0 * 60.0));

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(unixTime >= now - 10 && unixTime <= now + 10,
            $"FILETIME should represent current time. Got unix={unixTime}, now={now}");
    }
}
