namespace XISOSharp;

/// <summary>
/// Helper for converting Unix timestamps to Windows FILETIME values
/// and writing them into byte spans in little-endian format.
/// </summary>
public static class FileTimeHelper
{
    /// <summary>
    /// Computes the current UTC time as a Windows FILETIME value
    /// and writes it into the destination buffer as two little-endian 32-bit words.
    /// The destination must be at least 8 bytes.
    /// </summary>
    /// <param name="destination">A span of at least 8 bytes to receive the FILETIME.</param>
    public static void WriteFileTimeNow(Span<byte> destination)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double tmp = (now + (369.0 * 365.25 * 24.0 * 60.0 * 60.0 - (3.0 * 24.0 * 60.0 * 60.0 + 6.0 * 60.0 * 60.0))) * 1.0e7;

        uint h = (uint)(tmp * (1.0 / (4.0 * (double)(1L << 30))));
        uint l = (uint)(tmp - ((double)h) * 4.0 * (double)(1L << 30));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, l);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], h);
    }
}
