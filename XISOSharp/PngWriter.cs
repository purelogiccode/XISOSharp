using System.Buffers.Binary;
using System.IO.Compression;

namespace XISOSharp;

/// <summary>
/// Minimal BCL-only PNG encoder for the RGBA8888 buffers produced by
/// <see cref="DxtDecoder"/>. Writes a single 8-bit RGBA image with no ancillary
/// chunks, so XBE assets can be extracted without an image-library dependency.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes tightly packed RGBA8888 pixels as a PNG file.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if ((long)width * height * 4 > rgba.Length)
            throw new ArgumentException("The RGBA buffer is smaller than the declared dimensions.", nameof(rgba));

        using MemoryStream output = new();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] scanline = new byte[(width * 4) + 1];
            for (int y = 0; y < height; y++)
            {
                scanline[0] = 0; // filter: none
                rgba.Slice(y * width * 4, width * 4).CopyTo(scanline.AsSpan(1));
                zlib.Write(scanline);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte value in type)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        output.Write(crcBytes);
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (int i = 0; i < table.Length; i++)
        {
            uint value = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }

        return table;
    }
}
