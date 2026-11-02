using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Reads the logo bitmap and the <c>$$XTIMAGE</c>/<c>$$XSIMAGE</c> sections of
/// an original-Xbox XBE executable. The section walk is bounded by the XBE
/// header/section table, and unsupported payloads are returned as raw bytes
/// instead of throwing.
/// </summary>
public static class XbeAssets
{
    private const int MaxAssetBytes = 16 * 1024 * 1024;
    private const int MaxImageDimension = 2048;
    private const int XbeHeaderReadLimit = 0x8000;
    private const int XbeSectionHeaderSize = 0x38;
    private const uint MaxXbeSections = 256;
    private const int LogoWidth = 100;
    private const int LogoHeight = 17;

    /// <summary>Reads the Microsoft logo bitmap from an XBE inside an image.</summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <returns>The logo asset, or <c>null</c> when the executable has no logo bitmap.</returns>
    public static XbeImage? GetLogo(string imagePath, string internalPath) =>
        GetImage(imagePath, internalPath, XbeImageKind.Logo);

    /// <summary>Reads the <c>$$XTIMAGE</c> title image from an XBE inside an image.</summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <returns>The title image asset, or <c>null</c> when the section is absent.</returns>
    public static XbeImage? GetTitleImage(string imagePath, string internalPath) =>
        GetImage(imagePath, internalPath, XbeImageKind.Title);

    /// <summary>Reads the <c>$$XSIMAGE</c> save image from an XBE inside an image.</summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <returns>The save image asset, or <c>null</c> when the section is absent.</returns>
    public static XbeImage? GetSaveImage(string imagePath, string internalPath) =>
        GetImage(imagePath, internalPath, XbeImageKind.Save);

    /// <summary>Reads one XBE image asset from an image.</summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <param name="kind">Image kind to read.</param>
    /// <returns>The requested asset, or <c>null</c> when it is absent.</returns>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="IOException">The image cannot be read.</exception>
    public static XbeImage? GetImage(string imagePath, string internalPath, XbeImageKind kind)
    {
        using Stream stream = XisoReader.OpenImageStream(imagePath);
        return GetImage(stream, imagePath, internalPath, kind);
    }

    /// <summary>
    /// Reads one XBE image asset from an already-open image. The stream must be
    /// readable and seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <param name="kind">Image kind to read.</param>
    /// <returns>The requested asset, or <c>null</c> when it is absent.</returns>
    public static XbeImage? GetImage(Stream imageStream, string imageName, string internalPath, XbeImageKind kind)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (kind is not (XbeImageKind.Logo or XbeImageKind.Title or XbeImageKind.Save))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown XBE image kind.");

        EntryInfo? entry = XisoReader.GetEntryInfo(imageStream, imageName, internalPath);
        if (entry?.IsDirectory != false || entry.FileSize < 0x178)
            return null;

        VolumeInfo volume = XisoReader.GetVolumeInfo(imageStream);
        if (!volume.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {imageName}");

        long start = ((long)entry.StartSector * Constants.SectorSize) + volume.DiscLseek;
        int headerLength = (int)Math.Min(entry.FileSize, XbeHeaderReadLimit);
        byte[] header = new byte[headerLength];
        if (!TryReadAt(imageStream, start, header) || !HasMagic(header, "XBEH"))
            return null;

        uint baseAddress = ReadU32(header, 0x104);
        return kind == XbeImageKind.Logo
            ? ReadLogo(imageStream, start, entry.FileSize, header, baseAddress)
            : ReadSection(imageStream, start, entry.FileSize, header, baseAddress, kind);
    }

    /// <summary>Attempts to read one XBE image asset.</summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <param name="kind">Image kind to read.</param>
    /// <param name="image">Receives the asset when found.</param>
    /// <returns><c>true</c> when the asset exists.</returns>
    public static bool TryGetImage(string imagePath, string internalPath, XbeImageKind kind,
        out XbeImage? image)
    {
        image = GetImage(imagePath, internalPath, kind);
        return image is not null;
    }

    private static XbeImage? ReadLogo(Stream stream, long start, long fileSize, byte[] header, uint baseAddress)
    {
        uint logoAddress = ReadU32(header, 0x170);
        uint logoSize = ReadU32(header, 0x174);
        if (logoAddress == 0 || logoSize == 0 || logoSize > MaxAssetBytes)
            return null;

        long offset = (long)logoAddress - baseAddress;
        if (offset < 0 || offset + logoSize > fileSize)
            return null;

        byte[] raw = new byte[logoSize];
        return TryReadAt(stream, start + offset, raw) ? DecodeLogo(raw) : null;
    }

    private static XbeImage? ReadSection(Stream stream, long start, long fileSize, byte[] header,
        uint baseAddress, XbeImageKind kind)
    {
        uint sectionCount = ReadU32(header, 0x11C);
        uint sectionHeadersAddress = ReadU32(header, 0x120);
        if (sectionCount == 0 || sectionCount > MaxXbeSections)
            return null;

        long tableOffset = (long)sectionHeadersAddress - baseAddress;
        if (tableOffset < 0 || tableOffset + ((long)sectionCount * XbeSectionHeaderSize) > fileSize)
            return null;

        string wanted = kind == XbeImageKind.Title ? "$$XTIMAGE" : "$$XSIMAGE";
        byte[] sectionHeader = new byte[XbeSectionHeaderSize];
        for (uint i = 0; i < sectionCount; i++)
        {
            if (!TryReadAt(stream, start + tableOffset + (i * XbeSectionHeaderSize), sectionHeader))
                return null;

            uint nameAddress = ReadU32(sectionHeader, 0x14);
            if (nameAddress == 0)
                continue;

            long nameOffset = (long)nameAddress - baseAddress;
            byte[] nameBytes = new byte[20];
            if (nameOffset < 0 || nameOffset + nameBytes.Length > fileSize ||
                !TryReadAt(stream, start + nameOffset, nameBytes))
            {
                continue;
            }

            string name = ReadName(nameBytes);
            if (!string.Equals(name, wanted, StringComparison.Ordinal))
                continue;

            uint rawAddress = ReadU32(sectionHeader, 0x0C);
            uint rawSize = ReadU32(sectionHeader, 0x10);
            if (rawSize == 0 || rawSize > MaxAssetBytes || (long)rawAddress + rawSize > fileSize)
                return null;

            byte[] raw = new byte[rawSize];
            if (!TryReadAt(stream, start + rawAddress, raw))
                return null;

            return DecodeTexture(kind, wanted, raw);
        }

        return null;
    }

    private static XbeImage DecodeTexture(XbeImageKind kind, string sectionName, byte[] raw)
    {
        DecodedTexture? texture = TryDecodeDds(raw) ?? TryDecodeXpr0(raw) ?? TryDecodeLegacy(raw);
        if (texture is null)
            return new XbeImage(kind, sectionName, 0, 0, "Unknown", raw, null, null);

        byte[] png = PngWriter.Encode(texture.Rgba, texture.Width, texture.Height);
        return new XbeImage(kind, sectionName, texture.Width, texture.Height, texture.PixelFormat, raw, png,
            texture.Dds);
    }

    private static DecodedTexture? TryDecodeDds(byte[] raw)
    {
        if (raw.Length < 128 || !HasMagic(raw, "DDS ") ||
            ReadU32(raw, 0x04) != 124 || ReadU32(raw, 0x4C) != 32)
        {
            return null;
        }

        uint height = ReadU32(raw, 0x0C);
        uint width = ReadU32(raw, 0x10);
        if (width == 0 || height == 0 || width > MaxImageDimension || height > MaxImageDimension)
            return null;

        try
        {
            uint fourCc = ReadU32(raw, 0x54);
            ReadOnlySpan<byte> payload = raw.AsSpan(128);
            if (fourCc == 0x31545844) // DXT1
                return new DecodedTexture((int)width, (int)height, "DDS/DXT1",
                    DxtDecoder.DecodeDxt1(payload, (int)width, (int)height), raw);
            if (fourCc == 0x33545844) // DXT3
                return new DecodedTexture((int)width, (int)height, "DDS/DXT3",
                    DxtDecoder.DecodeDxt3(payload, (int)width, (int)height), raw);
            if (fourCc == 0x35545844) // DXT5
                return new DecodedTexture((int)width, (int)height, "DDS/DXT5",
                    DxtDecoder.DecodeDxt5(payload, (int)width, (int)height), raw);

            uint bitCount = ReadU32(raw, 0x58);
            if (bitCount == 24)
                return new DecodedTexture((int)width, (int)height, "DDS/BGR24",
                    DxtDecoder.DecodeBgr24(payload, (int)width, (int)height), raw);
            if (bitCount == 32)
                return new DecodedTexture((int)width, (int)height, "DDS/BGRA32",
                    DxtDecoder.DecodeBgra32(payload, (int)width, (int)height), raw);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return null;
    }

    private static DecodedTexture? TryDecodeXpr0(byte[] raw)
    {
        if (raw.Length < 0x20 || !HasMagic(raw, "XPR0"))
            return null;

        uint headerSize = ReadU32(raw, 0x08);
        uint dataOffset = ReadU32(raw, 0x10);
        uint format = ReadU32(raw, 0x18);
        int width = 1 << (int)((format >> 20) & 0x0F);
        int height = 1 << (int)((format >> 24) & 0x0F);
        if (width > MaxImageDimension || height > MaxImageDimension)
            return null;

        if (headerSize > raw.Length && dataOffset > raw.Length)
            return null;

        int payloadOffset = headerSize >= 0x20 && headerSize <= raw.Length
            ? (int)headerSize
            : dataOffset >= 0x20 && dataOffset <= raw.Length
                ? (int)dataOffset
                : 0x20;
        if (payloadOffset >= raw.Length)
            return null;

        ReadOnlySpan<byte> payload = raw.AsSpan(payloadOffset);
        int formatCode = (int)((format >> 8) & 0xFF);
        int blocks = ((width + 3) / 4) * ((height + 3) / 4);
        try
        {
            switch (formatCode)
            {
                case 0x0C:
                    return new DecodedTexture(width, height, "XPR0/DXT1",
                        DxtDecoder.DecodeDxt1(payload, width, height),
                        BuildDds(width, height, "DXT1", payload[..(blocks * 8)]));
                case 0x0E:
                    return new DecodedTexture(width, height, "XPR0/DXT3",
                        DxtDecoder.DecodeDxt3(payload, width, height),
                        BuildDds(width, height, "DXT3", payload[..(blocks * 16)]));
                case 0x0F:
                    return new DecodedTexture(width, height, "XPR0/DXT5",
                        DxtDecoder.DecodeDxt5(payload, width, height),
                        BuildDds(width, height, "DXT5", payload[..(blocks * 16)]));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return null;
    }

    private static DecodedTexture? TryDecodeLegacy(byte[] raw)
    {
        if (raw.Length >= 128 && HasMagic(raw, "DDS ") &&
            ReadU32(raw, 0x04) == 124 && ReadU32(raw, 0x4C) == 32)
        {
            return null;
        }

        if (raw.Length == 49152)
        {
            try
            {
                byte[] rgba = DxtDecoder.DecodeBgr24(raw, 128, 128);
                return new DecodedTexture(128, 128, "BGR24", rgba,
                    BuildDds(128, 128, "BGR24", raw));
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        if (raw.Length < 54)
            return null;

        ReadOnlySpan<byte> payload = raw.AsSpan(54);
        (int Width, int Height)[] candidates = [(128, 128)];
        uint headerHeight = ReadU32(raw, 0x0C);
        uint headerWidth = ReadU32(raw, 0x10);
        if (headerWidth is > 0 and <= MaxImageDimension && headerHeight is > 0 and <= MaxImageDimension &&
            (headerWidth != 128 || headerHeight != 128))
        {
            candidates = [((int)headerWidth, (int)headerHeight), (128, 128)];
        }

        foreach ((int width, int height) in candidates)
        {
            long pixels = (long)width * height;
            int blocks = ((width + 3) / 4) * ((height + 3) / 4);
            try
            {
                if (payload.Length == pixels * 3)
                {
                    byte[] rgba = DxtDecoder.DecodeBgr24(payload, width, height);
                    return new DecodedTexture(width, height, "BGR24", rgba,
                        BuildDds(width, height, "BGR24", payload[..(int)(pixels * 3)]));
                }

                if (payload.Length == pixels * 4)
                {
                    byte[] rgba = DxtDecoder.DecodeBgra32(payload, width, height);
                    return new DecodedTexture(width, height, "BGRA32", rgba,
                        BuildDds(width, height, "BGRA32", payload[..(int)(pixels * 4)]));
                }

                if (payload.Length == blocks * 8)
                {
                    byte[] rgba = DxtDecoder.DecodeDxt1(payload, width, height);
                    return new DecodedTexture(width, height, "DXT1", rgba,
                        BuildDds(width, height, "DXT1", payload[..(blocks * 8)]));
                }

                if (payload.Length == blocks * 16)
                {
                    string fourCc = string.Equals(FindFourCc(raw.AsSpan(0, 54)), "DXT3",
                        StringComparison.Ordinal)
                        ? "DXT3"
                        : "DXT5";
                    byte[] rgba = string.Equals(fourCc, "DXT3", StringComparison.Ordinal)
                        ? DxtDecoder.DecodeDxt3(payload, width, height)
                        : DxtDecoder.DecodeDxt5(payload, width, height);
                    return new DecodedTexture(width, height, fourCc, rgba,
                        BuildDds(width, height, fourCc, payload[..(blocks * 16)]));
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    private static XbeImage DecodeLogo(byte[] raw)
    {
        byte[]? rgba = null;
        string format = "Unknown";
        if (raw.Length == LogoWidth * LogoHeight * 4)
        {
            rgba = DxtDecoder.DecodeBgra32(raw, LogoWidth, LogoHeight);
            format = "BGRA32";
        }
        else if (raw.Length == LogoWidth * LogoHeight)
        {
            rgba = DxtDecoder.DecodeGray8(raw, LogoWidth, LogoHeight);
            format = "Gray8";
        }
        else
        {
            rgba = TryDecodeLogoRle(raw);
            format = rgba is null ? "Unknown" : "RLE8";
        }

        if (rgba is null)
            return new XbeImage(XbeImageKind.Logo, "logo", 0, 0, format, raw, null, null);

        byte[] png = PngWriter.Encode(rgba, LogoWidth, LogoHeight);
        byte[] bgr = RgbaToBgr(rgba, LogoWidth, LogoHeight);
        byte[] dds = BuildDds(LogoWidth, LogoHeight, "BGR24", bgr);
        return new XbeImage(XbeImageKind.Logo, "logo", LogoWidth, LogoHeight, format, raw, png, dds);
    }

    private static byte[]? TryDecodeLogoRle(byte[] data)
    {
        byte[] rgba = new byte[LogoWidth * LogoHeight * 4];
        int pixel = 0;
        int i = 0;
        while (i < data.Length && pixel < LogoWidth * LogoHeight)
        {
            if ((data[i] & 0x01) != 0)
            {
                int length = (data[i] >> 1) & 0x07;
                byte value = (byte)(((data[i] >> 4) & 0x0F) * 17);
                for (int n = 0; n < length && pixel < LogoWidth * LogoHeight; n++)
                    WriteGrayPixel(rgba, pixel++, value);
                i++;
            }
            else
            {
                if (i + 1 >= data.Length)
                    return null;

                ushort value = (ushort)(data[i] | (data[i + 1] << 8));
                if ((value & 0x0002) != 0)
                    return null;

                int length = (value >> 2) & 0x03FF;
                byte gray = (byte)(((value >> 12) & 0x0F) * 17);
                for (int n = 0; n < length && pixel < LogoWidth * LogoHeight; n++)
                    WriteGrayPixel(rgba, pixel++, gray);
                i += 2;
            }
        }

        return pixel == LogoWidth * LogoHeight ? rgba : null;
    }

    private static void WriteGrayPixel(byte[] rgba, int pixel, byte value)
    {
        int destination = pixel * 4;
        rgba[destination] = value;
        rgba[destination + 1] = value;
        rgba[destination + 2] = value;
        rgba[destination + 3] = 0xFF;
    }

    private static byte[] RgbaToBgr(byte[] rgba, int width, int height)
    {
        byte[] bgr = new byte[(long)width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            bgr[(i * 3) + 2] = rgba[i * 4];
            bgr[(i * 3) + 1] = rgba[(i * 4) + 1];
            bgr[i * 3] = rgba[(i * 4) + 2];
        }

        return bgr;
    }

    private static byte[] BuildDds(int width, int height, string format, ReadOnlySpan<byte> data)
    {
        byte[] dds = new byte[128 + data.Length];
        "DDS "u8.CopyTo(dds);
        WriteU32(dds, 0x04, 124);
        bool compressed = format is "DXT1" or "DXT3" or "DXT5";
        WriteU32(dds, 0x08, compressed ? 0x81007u : 0x100Fu);
        WriteU32(dds, 0x0C, (uint)height);
        WriteU32(dds, 0x10, (uint)width);
        uint pitch = format switch
        {
            "BGR24" => (uint)(width * 3),
            "BGRA32" => (uint)(width * 4),
            _ => (uint)data.Length,
        };
        WriteU32(dds, 0x14, pitch);
        WriteU32(dds, 0x18, 0);
        WriteU32(dds, 0x1C, 0);

        WriteU32(dds, 0x4C, 32);
        if (compressed)
        {
            WriteU32(dds, 0x50, 0x4);
            WriteU32(dds, 0x54, format switch
            {
                "DXT1" => 0x31545844u,
                "DXT3" => 0x33545844u,
                _ => 0x35545844u,
            });
        }
        else
        {
            bool isBgra = string.Equals(format, "BGRA32", StringComparison.Ordinal);
            WriteU32(dds, 0x50, isBgra ? 0x41u : 0x40u);
            WriteU32(dds, 0x58, isBgra ? 32u : 24u);
            WriteU32(dds, 0x5C, 0x00FF0000);
            WriteU32(dds, 0x60, 0x0000FF00);
            WriteU32(dds, 0x64, 0x000000FF);
            WriteU32(dds, 0x68, isBgra ? 0xFF000000u : 0u);
        }

        WriteU32(dds, 0x6C, 0x1000);
        data.CopyTo(dds.AsSpan(128));
        return dds;
    }

    private static string? FindFourCc(ReadOnlySpan<byte> header)
    {
        for (int i = 0; i + 4 <= header.Length; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(header[i..]);
            if (value == 0x31545844)
                return "DXT1";
            if (value == 0x33545844)
                return "DXT3";
            if (value == 0x35545844)
                return "DXT5";
        }

        return null;
    }

    private static string ReadName(ReadOnlySpan<byte> name)
    {
        int end = name.IndexOf((byte)0);
        if (end < 0)
            end = name.Length;
        return Encoding.ASCII.GetString(name[..end]);
    }

    private static bool HasMagic(ReadOnlySpan<byte> data, string magic) =>
        data.Length >= 4 &&
        data[0] == (byte)magic[0] && data[1] == (byte)magic[1] &&
        data[2] == (byte)magic[2] && data[3] == (byte)magic[3];

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static void WriteU32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;

        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return true;
    }

    private sealed record DecodedTexture(
        int Width,
        int Height,
        string PixelFormat,
        byte[] Rgba,
        byte[]? Dds);
}
