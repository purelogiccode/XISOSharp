using System.Buffers.Binary;
using System.Globalization;

namespace XISOSharp;

/// <summary>
/// Helper for converting Unix timestamps to Windows FILETIME values
/// and writing them into byte spans in little-endian format.
/// FILETIME is a 64-bit little-endian value counting 100ns intervals since 1601-01-01 UTC
/// (offset 116444736000000000 from the Unix epoch 1970-01-01).
/// Ported from <c>extract-xiso.c:alloc_filetime_now</c> (double formula) with
/// precise integer helpers for inspection/editing.
/// </summary>
public static class FileTimeHelper
{
    /// <summary>FILETIME epoch (1601-01-01 UTC) as <see cref="DateTimeOffset"/>.</summary>
    public static readonly DateTimeOffset FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>FILETIME representing the epoch itself (raw 0).</summary>
    public const ulong FileTimeZero = 0UL;

    /// <summary>
    /// Computes the current UTC time as a Windows FILETIME value
    /// and writes it into the destination buffer as two little-endian 32-bit words.
    /// The destination must be at least 8 bytes.
    /// </summary>
    /// <param name="destination">A span of at least 8 bytes to receive the FILETIME.</param>
    public static void WriteFileTimeNow(Span<byte> destination)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tmp = (now + ((369.0 * 365.25 * 24.0 * 60.0 * 60.0) - ((3.0 * 24.0 * 60.0 * 60.0) + (6.0 * 60.0 * 60.0)))) *
                  1.0e7;

        var h = (uint)(tmp * (1.0 / (4.0 * (1L << 30))));
        var l = (uint)(tmp - (h * 4.0 * (1L << 30)));

        BinaryPrimitives.WriteUInt32LittleEndian(destination, l);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], h);
    }

    /// <summary>
    /// Writes a raw 64-bit FILETIME value into <paramref name="destination"/> little-endian.
    /// </summary>
    /// <param name="destination">Span of at least 8 bytes.</param>
    /// <param name="fileTime">Raw FILETIME (little-endian on disk).</param>
    public static void WriteFileTime(Span<byte> destination, ulong fileTime)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, fileTime);
    }

    /// <summary>
    /// Writes a <see cref="DateTimeOffset"/> as FILETIME into <paramref name="destination"/> little-endian.
    /// </summary>
    /// <param name="destination">Span of at least 8 bytes.</param>
    /// <param name="dateTime">UTC time to encode (offset is normalized to UTC).</param>
    public static void WriteFileTime(Span<byte> destination, DateTimeOffset dateTime)
    {
        WriteFileTime(destination, ToFileTimeRaw(dateTime));
    }

    /// <summary>
    /// Reads a raw little-endian FILETIME from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Span of at least 8 bytes (little-endian).</param>
    /// <returns>Raw 64-bit FILETIME.</returns>
    public static ulong ReadFileTimeRaw(ReadOnlySpan<byte> source)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(source);
    }

    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to a raw Windows FILETIME.
    /// </summary>
    /// <param name="dateTime">Time to convert (normalized to UTC).</param>
    /// <returns>Raw FILETIME value.</returns>
    public static ulong ToFileTimeRaw(DateTimeOffset dateTime)
    {
        // DateTime.ToFileTimeUtc is precise integer arithmetic (BCL).
        // Clamp to valid FILETIME range: 0 .. DateTime.MaxValue.
        DateTime utc = dateTime.UtcDateTime;
        if (utc < FileTimeEpoch.UtcDateTime)
            return 0UL;
        try
        {
            var ft = utc.ToFileTimeUtc();
            return (ulong)ft;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Before epoch or after max — clamp.
            return utc < FileTimeEpoch.UtcDateTime ? 0UL : ulong.MaxValue;
        }
    }

    /// <summary>
    /// Converts a raw Windows FILETIME to a <see cref="DateTimeOffset"/> (UTC).
    /// </summary>
    /// <param name="fileTime">Raw FILETIME value.</param>
    /// <returns>UTC time; raw 0 maps to 1601-01-01.</returns>
    public static DateTimeOffset FromFileTimeRaw(ulong fileTime)
    {
        if (fileTime == 0UL)
            return FileTimeEpoch;
        if (fileTime <= long.MaxValue)
        {
            try
            {
                DateTime utc = DateTime.FromFileTimeUtc((long)fileTime);
                return new DateTimeOffset(utc, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fall through to manual.
            }
        }

        // Manual fallback for values > long.MaxValue (year > ~30828) — return MaxValue.
        try
        {
            var ticks1601 = FileTimeEpoch.Ticks;
            // FILETIME ticks are 100ns; DateTime ticks are same.
            // Guard overflow: fileTime > (DateTime.MaxValue.Ticks - ticks1601) => MaxValue.
            var maxFileTime = DateTime.MaxValue.Ticks - ticks1601;
            if (fileTime > (ulong)maxFileTime)
                return DateTimeOffset.MaxValue;
            var ticks = ticks1601 + (long)fileTime;
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch
        {
            return DateTimeOffset.MaxValue;
        }
    }

    /// <summary>
    /// Tries to parse a filetime string.
    /// Accepts: <c>now</c>, decimal raw, hex <c>0x...</c>, or ISO-8601 (<see cref="DateTimeOffset"/> parsing).
    /// </summary>
    /// <param name="input">Input string.</param>
    /// <param name="fileTime">Parsed raw FILETIME on success.</param>
    /// <param name="dateTime">Parsed UTC time on success.</param>
    /// <returns>True on success.</returns>
    public static bool TryParseFileTime(string input, out ulong fileTime, out DateTimeOffset dateTime)
    {
        input = input.Trim();
        fileTime = 0UL;
        dateTime = FileTimeEpoch;

        if (string.Equals(input, "now", StringComparison.OrdinalIgnoreCase))
        {
            dateTime = DateTimeOffset.UtcNow;
            fileTime = ToFileTimeRaw(dateTime);
            return true;
        }

        if (string.Equals(input, "0", StringComparison.Ordinal))
        {
            fileTime = 0UL;
            dateTime = FileTimeEpoch;
            return true;
        }

        // Hex raw: 0x...
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = input[2..];
            if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fileTime))
            {
                dateTime = FromFileTimeRaw(fileTime);
                return true;
            }

            return false;
        }

        // Decimal raw (all digits)
        var allDigits = input.Length > 0 && input.All(static c => char.IsDigit(c));
        if (allDigits && ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out fileTime))
        {
            dateTime = FromFileTimeRaw(fileTime);
            return true;
        }

        // ISO-8601 / general DateTimeOffset
        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dateTime))
        {
            fileTime = ToFileTimeRaw(dateTime);
            return true;
        }

        // Also try DateTime parse with explicit roundtrip
        if (DateTimeOffset.TryParseExact(input, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out dateTime))
        {
            fileTime = ToFileTimeRaw(dateTime);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a raw FILETIME as <c>YYYY-MM-DDTHH:MM:SS.fffffffZ (raw)</c>.
    /// </summary>
    /// <param name="fileTime">Raw FILETIME.</param>
    /// <returns>Formatted string.</returns>
    public static string FormatFileTime(ulong fileTime)
    {
        DateTimeOffset dto = FromFileTimeRaw(fileTime);
        return $"{dto:O} ({fileTime})";
    }
}