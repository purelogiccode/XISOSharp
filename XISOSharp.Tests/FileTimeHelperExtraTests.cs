namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="FileTimeHelper"/> parsing, formatting,
/// conversions, and little-endian read/write round-trips.
/// </summary>
public class FileTimeHelperExtraTests
{
    private const ulong UnixEpochFileTime = 116444736000000000UL;

    /// <summary>Verifies <c>now</c> parses to a value close to the current UTC time.</summary>
    [Fact]
    public void TryParseFileTime_Now_ReturnsRecentValue()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(FileTimeHelper.TryParseFileTime("now", out ulong fileTime, out DateTimeOffset dateTime));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.InRange(dateTime, before, after);
        Assert.Equal(FileTimeHelper.ToFileTimeRaw(dateTime), fileTime);
    }

    /// <summary>Verifies <c>now</c> is case-insensitive and whitespace-tolerant.</summary>
    [Fact]
    public void TryParseFileTime_Now_TrimmedAndCaseInsensitive()
    {
        Assert.True(FileTimeHelper.TryParseFileTime("  NOW  ", out _, out _));
    }

    /// <summary>Verifies raw zero maps to the FILETIME epoch.</summary>
    [Fact]
    public void TryParseFileTime_Zero_ReturnsEpoch()
    {
        Assert.True(FileTimeHelper.TryParseFileTime("0", out ulong fileTime, out DateTimeOffset dateTime));
        Assert.Equal(0UL, fileTime);
        Assert.Equal(FileTimeHelper.FileTimeEpoch, dateTime);
    }

    /// <summary>Verifies a decimal raw value parses to the Unix epoch.</summary>
    [Fact]
    public void TryParseFileTime_Decimal_UnixEpoch()
    {
        Assert.True(FileTimeHelper.TryParseFileTime("116444736000000000", out ulong fileTime, out DateTimeOffset dateTime));
        Assert.Equal(UnixEpochFileTime, fileTime);
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), dateTime);
    }

    /// <summary>Verifies a hex raw value parses to the Unix epoch.</summary>
    [Fact]
    public void TryParseFileTime_Hex_UnixEpoch()
    {
        Assert.True(FileTimeHelper.TryParseFileTime("0x019DB1DED53E8000", out ulong fileTime, out DateTimeOffset dateTime));
        Assert.Equal(UnixEpochFileTime, fileTime);
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), dateTime);
    }

    /// <summary>Verifies ISO-8601 input round-trips through the raw value.</summary>
    [Fact]
    public void TryParseFileTime_Iso8601_RoundTrips()
    {
        Assert.True(FileTimeHelper.TryParseFileTime("2024-01-02T03:04:05Z", out ulong fileTime, out DateTimeOffset dateTime));
        DateTimeOffset expected = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal(expected, dateTime);
        Assert.Equal(FileTimeHelper.ToFileTimeRaw(expected), fileTime);
    }

    /// <summary>Verifies invalid hex input fails cleanly.</summary>
    [Fact]
    public void TryParseFileTime_InvalidHex_ReturnsFalse()
    {
        Assert.False(FileTimeHelper.TryParseFileTime("0xZZ", out ulong fileTime, out _));
        Assert.Equal(0UL, fileTime);
    }

    /// <summary>Verifies garbage input fails cleanly and yields defaults.</summary>
    [Fact]
    public void TryParseFileTime_Garbage_ReturnsFalse()
    {
        Assert.False(FileTimeHelper.TryParseFileTime("not-a-time", out ulong fileTime, out DateTimeOffset dateTime));
        Assert.Equal(0UL, fileTime);
        Assert.Equal(default, dateTime);
    }

    /// <summary>Verifies empty input fails cleanly.</summary>
    [Fact]
    public void TryParseFileTime_Empty_ReturnsFalse()
    {
        Assert.False(FileTimeHelper.TryParseFileTime(string.Empty, out _, out _));
    }

    /// <summary>Verifies raw zero formats as the epoch with the raw value appended.</summary>
    [Fact]
    public void FormatFileTime_Zero_Epoch()
    {
        Assert.Equal("1601-01-01T00:00:00.0000000+00:00 (0)", FileTimeHelper.FormatFileTime(0));
    }

    /// <summary>Verifies a known raw value formats with the round-trip timestamp.</summary>
    [Fact]
    public void FormatFileTime_KnownValue()
    {
        string formatted = FileTimeHelper.FormatFileTime(UnixEpochFileTime);
        Assert.StartsWith("1970-01-01T00:00:00.0000000+00:00", formatted, StringComparison.Ordinal);
        Assert.EndsWith($"({UnixEpochFileTime})", formatted, StringComparison.Ordinal);
    }

    /// <summary>Verifies raw writes are little-endian.</summary>
    [Fact]
    public void WriteFileTime_Raw_IsLittleEndian()
    {
        Span<byte> destination = stackalloc byte[8];
        FileTimeHelper.WriteFileTime(destination, 0x0102030405060708UL);
        Assert.Equal([0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01], destination.ToArray());
    }

    /// <summary>Verifies a <see cref="DateTimeOffset"/> write round-trips through the raw read.</summary>
    [Fact]
    public void WriteFileTime_DateTimeOffset_RoundTrips()
    {
        DateTimeOffset value = new(2024, 6, 1, 12, 30, 45, TimeSpan.Zero);
        Span<byte> destination = stackalloc byte[8];
        FileTimeHelper.WriteFileTime(destination, value);
        Assert.Equal(FileTimeHelper.ToFileTimeRaw(value), FileTimeHelper.ReadFileTimeRaw(destination));
    }

    /// <summary>Verifies <c>WriteFileTimeNow</c> writes a recent timestamp.</summary>
    [Fact]
    public void WriteFileTimeNow_WritesRecentValue()
    {
        Span<byte> destination = stackalloc byte[8];
        FileTimeHelper.WriteFileTimeNow(destination);
        ulong raw = FileTimeHelper.ReadFileTimeRaw(destination);
        DateTimeOffset parsed = FileTimeHelper.FromFileTimeRaw(raw);
        Assert.InRange(parsed, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    /// <summary>Verifies pre-epoch times clamp to zero.</summary>
    [Fact]
    public void ToFileTimeRaw_PreEpoch_ClampsToZero()
    {
        Assert.Equal(0UL, FileTimeHelper.ToFileTimeRaw(new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>Verifies the epoch itself maps to zero.</summary>
    [Fact]
    public void ToFileTimeRaw_Epoch_IsZero()
    {
        Assert.Equal(0UL, FileTimeHelper.ToFileTimeRaw(FileTimeHelper.FileTimeEpoch));
    }

    /// <summary>Verifies non-UTC offsets are normalized to UTC before conversion.</summary>
    [Fact]
    public void ToFileTimeRaw_OffsetNormalizedToUtc()
    {
        DateTimeOffset withOffset = new(1970, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(UnixEpochFileTime, FileTimeHelper.ToFileTimeRaw(withOffset));
    }

    /// <summary>Verifies raw zero maps back to the epoch.</summary>
    [Fact]
    public void FromFileTimeRaw_Zero_IsEpoch()
    {
        Assert.Equal(FileTimeHelper.FileTimeEpoch, FileTimeHelper.FromFileTimeRaw(0));
    }

    /// <summary>Verifies the Unix epoch raw value maps back to 1970-01-01 UTC.</summary>
    [Fact]
    public void FromFileTimeRaw_UnixEpoch()
    {
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            FileTimeHelper.FromFileTimeRaw(UnixEpochFileTime));
    }

    /// <summary>Verifies an out-of-range raw value clamps to <see cref="DateTimeOffset.MaxValue"/>.</summary>
    [Fact]
    public void FromFileTimeRaw_OutOfRange_ClampsToMax()
    {
        Assert.Equal(DateTimeOffset.MaxValue, FileTimeHelper.FromFileTimeRaw(ulong.MaxValue));
    }
}
