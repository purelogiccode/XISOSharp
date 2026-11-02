using System.Buffers.Binary;

namespace XISOSharp;

/// <summary>
/// Decodes the texture formats used by XBE title/save images and the Microsoft
/// logo bitmap into tightly packed RGBA8888 pixels. The decoders are pure
/// managed and allocation-bounded by the declared image dimensions.
/// </summary>
internal static class DxtDecoder
{
    /// <summary>Decodes a BC1/DXT1 block stream to RGBA8888.</summary>
    internal static byte[] DecodeDxt1(ReadOnlySpan<byte> data, int width, int height) =>
        DecodeColorBlocks(data, width, height, blockBytes: 8, alphaKind: 0);

    /// <summary>Decodes a BC2/DXT3 block stream to RGBA8888.</summary>
    internal static byte[] DecodeDxt3(ReadOnlySpan<byte> data, int width, int height) =>
        DecodeColorBlocks(data, width, height, blockBytes: 16, alphaKind: 1);

    /// <summary>Decodes a BC3/DXT5 block stream to RGBA8888.</summary>
    internal static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height) =>
        DecodeColorBlocks(data, width, height, blockBytes: 16, alphaKind: 2);

    /// <summary>Converts packed BGR24 pixels to RGBA8888.</summary>
    internal static byte[] DecodeBgr24(ReadOnlySpan<byte> data, int width, int height)
    {
        long needed = (long)width * height * 3;
        if (width <= 0 || height <= 0 || needed > data.Length)
            throw new InvalidDataException("BGR24 image data is truncated.");

        byte[] rgba = new byte[(long)width * height * 4];
        int source = 0;
        int destination = 0;
        for (int i = 0; i < width * height; i++)
        {
            rgba[destination] = data[source + 2];
            rgba[destination + 1] = data[source + 1];
            rgba[destination + 2] = data[source];
            rgba[destination + 3] = 0xFF;
            source += 3;
            destination += 4;
        }

        return rgba;
    }

    /// <summary>Converts packed BGRA8888 pixels to RGBA8888.</summary>
    internal static byte[] DecodeBgra32(ReadOnlySpan<byte> data, int width, int height)
    {
        long needed = (long)width * height * 4;
        if (width <= 0 || height <= 0 || needed > data.Length)
            throw new InvalidDataException("BGRA image data is truncated.");

        byte[] rgba = new byte[needed];
        for (int i = 0; i < width * height; i++)
        {
            int source = i * 4;
            rgba[source] = data[source + 2];
            rgba[source + 1] = data[source + 1];
            rgba[source + 2] = data[source];
            rgba[source + 3] = data[source + 3];
        }

        return rgba;
    }

    /// <summary>Converts packed grayscale pixels to RGBA8888.</summary>
    internal static byte[] DecodeGray8(ReadOnlySpan<byte> data, int width, int height)
    {
        long needed = (long)width * height;
        if (width <= 0 || height <= 0 || needed > data.Length)
            throw new InvalidDataException("Grayscale image data is truncated.");

        byte[] rgba = new byte[needed * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte value = data[i];
            int destination = i * 4;
            rgba[destination] = value;
            rgba[destination + 1] = value;
            rgba[destination + 2] = value;
            rgba[destination + 3] = 0xFF;
        }

        return rgba;
    }

    private static byte[] DecodeColorBlocks(ReadOnlySpan<byte> data, int width, int height, int blockBytes,
        int alphaKind)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Image dimensions are invalid.");

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        long needed = (long)blocksX * blocksY * blockBytes;
        if (needed > data.Length)
            throw new InvalidDataException("Compressed image data is truncated.");

        byte[] rgba = new byte[(long)width * height * 4];
        byte[] alpha = new byte[16];
        byte[] colors = new byte[16];
        int source = 0;
        for (int blockY = 0; blockY < blocksY; blockY++)
        {
            for (int blockX = 0; blockX < blocksX; blockX++)
            {
                if (alphaKind == 1)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int nibble = (data[source + (i / 2)] >> ((i & 1) * 4)) & 0x0F;
                        alpha[i] = (byte)(nibble * 17);
                    }

                    source += 8;
                }
                else if (alphaKind == 2)
                {
                    DecodeDxt5Alpha(data.Slice(source, 8), alpha);
                    source += 8;
                }

                ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(data[source..]);
                ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(source + 2)..]);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(data[(source + 4)..]);
                source += 8;

                DecodeColorTable(color0, color1, colors, alphaKind == 0 && color0 <= color1);

                for (int y = 0; y < 4; y++)
                {
                    int destinationY = blockY * 4 + y;
                    if (destinationY >= height)
                        continue;

                    for (int x = 0; x < 4; x++)
                    {
                        int destinationX = blockX * 4 + x;
                        if (destinationX >= width)
                            continue;

                        int pixel = y * 4 + x;
                        int colorIndex = (int)((indices >> (pixel * 2)) & 0x03);
                        int destination = ((destinationY * width) + destinationX) * 4;
                        int color = colorIndex * 4;
                        rgba[destination] = colors[color];
                        rgba[destination + 1] = colors[color + 1];
                        rgba[destination + 2] = colors[color + 2];
                        rgba[destination + 3] = alphaKind == 0
                            ? colors[color + 3]
                            : alpha[pixel];
                    }
                }
            }
        }

        return rgba;
    }

    private static void DecodeColorTable(ushort color0, ushort color1, Span<byte> colors, bool transparentMode)
    {
        DecodeRgb565(color0, colors);
        DecodeRgb565(color1, colors[4..]);
        colors[7] = 0xFF;
        colors[11] = 0xFF;

        if (transparentMode)
        {
            colors[8] = (byte)((colors[0] + colors[4]) / 2);
            colors[9] = (byte)((colors[1] + colors[5]) / 2);
            colors[10] = (byte)((colors[2] + colors[6]) / 2);
            colors[11] = 0xFF;
            colors[12] = 0;
            colors[13] = 0;
            colors[14] = 0;
            colors[15] = 0;
        }
        else
        {
            colors[8] = (byte)(((2 * colors[0]) + colors[4]) / 3);
            colors[9] = (byte)(((2 * colors[1]) + colors[5]) / 3);
            colors[10] = (byte)(((2 * colors[2]) + colors[6]) / 3);
            colors[11] = 0xFF;
            colors[12] = (byte)((colors[0] + (2 * colors[4])) / 3);
            colors[13] = (byte)((colors[1] + (2 * colors[5])) / 3);
            colors[14] = (byte)((colors[2] + (2 * colors[6])) / 3);
            colors[15] = 0xFF;
        }
    }

    private static void DecodeRgb565(ushort value, Span<byte> rgba)
    {
        int r = (value >> 11) & 0x1F;
        int g = (value >> 5) & 0x3F;
        int b = value & 0x1F;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 0xFF;
    }

    private static void DecodeDxt5Alpha(ReadOnlySpan<byte> data, Span<byte> alpha)
    {
        byte alpha0 = data[0];
        byte alpha1 = data[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (int i = 1; i <= 6; i++)
                palette[i + 1] = (byte)((((7 - i) * alpha0) + (i * alpha1)) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++)
                palette[i + 1] = (byte)((((5 - i) * alpha0) + (i * alpha1)) / 5);
            palette[6] = 0;
            palette[7] = 0xFF;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)data[2 + i] << (i * 8);

        for (int i = 0; i < 16; i++)
            alpha[i] = palette[(int)((bits >> (i * 3)) & 0x07)];
    }
}
