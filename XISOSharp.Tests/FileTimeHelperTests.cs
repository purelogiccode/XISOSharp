namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="FileTimeHelper"/>, verifying that FILETIME values
/// are written correctly as 8-byte timestamps representing the current time.
/// </summary>
public class FileTimeHelperTests
{
    /// <summary>
    /// Verifies that <see cref="FileTimeHelper.WriteFileTimeNow"/> writes
    /// a non-zero 8-byte value into the destination span.
    /// </summary>
    [Fact]
    public void WriteFileTimeNow_WritesEightBytes()
    {
        Span<byte> dest = stackalloc byte[8];
        FileTimeHelper.WriteFileTimeNow(dest);

        var nonZero = false;
        for (var i = 0; i < 8; i++)
        {
            if (dest[i] != 0)
            {
                nonZero = true;
                break;
            }
        }

        Assert.True(nonZero, "Expected non-zero FILETIME value");
    }

    /// <summary>
    /// Verifies that repeated calls to <see cref="FileTimeHelper.WriteFileTimeNow"/>
    /// within a short interval produce values that are equal or close together,
    /// and are not all zeros.
    /// </summary>
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

    /// <summary>
    /// Verifies that the FILETIME value produced by
    /// <see cref="FileTimeHelper.WriteFileTimeNow"/> converts to a Unix timestamp
    /// within 10 seconds of the current system time.
    /// </summary>
    [Fact]
    public void WriteFileTimeNow_ReasonableRange()
    {
        Span<byte> dest = stackalloc byte[8];
        FileTimeHelper.WriteFileTimeNow(dest);

        var low = BitConverter.ToUInt32(dest);
        var high = BitConverter.ToUInt32(dest[4..]);

        Assert.NotEqual(0u, high | low);

        var filetime = high * 4.0 * (1L << 30) + low;
        var unixTime = filetime / 1.0e7 -
                       (369.0 * 365.25 * 24.0 * 60.0 * 60.0 - (3.0 * 24.0 * 60.0 * 60.0 + 6.0 * 60.0 * 60.0));

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(unixTime >= now - 10 && unixTime <= now + 10,
            $"FILETIME should represent current time. Got unix={unixTime}, now={now}");
    }
}