using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Reads the plaintext XDBF/XSRC resources carried in an Xbox 360 XEX2 header
/// section. The parser is deliberately bounded and returns <c>false</c> for
/// malformed metadata instead of throwing into the public reader surface.
/// </summary>
internal static class XexDbdf
{
    private const uint HeaderSectionTableKey = 0x000002FF;
    private const uint XsrcEntryId = 0x58535243; // "XSRC" big-endian
    private const uint ThumbnailEntryId = 0x8000;
    private const int MaxHeaderSectionCount = 4096;
    private const int MaxSectionBytes = 16 * 1024 * 1024;
    private const int MaxXdbfEntries = 65536;
    private const int MaxXstrEntries = 4096;
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;
    private const int MaxXsrcBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Locates the XDBF section described by the XEX header-section table and
    /// parses its XSTR/XSRC strings and optional thumbnail.
    /// </summary>
    /// <param name="fs">Seekable image stream.</param>
    /// <param name="xexStart">Absolute byte offset of the XEX file in the image.</param>
    /// <param name="xexSize">Size of the XEX file in bytes.</param>
    /// <param name="xexPath">Image-internal XEX path.</param>
    /// <param name="info">Already-parsed XEX header metadata.</param>
    /// <param name="title">Receives the title metadata when readable.</param>
    /// <returns><c>true</c> when a readable XDBF section was found.</returns>
    internal static bool TryReadTitle(Stream fs, long xexStart, long xexSize, string xexPath, XexInfo info,
        out XexTitleInfo? title)
    {
        title = null;
        try
        {
            if (xexSize < 0x18 || xexStart < 0)
                return false;

            int headerLength = (int)Math.Min(xexSize, 0x8000);
            byte[] header = new byte[headerLength];
            if (!TryReadAt(fs, xexStart, header))
                return false;

            uint headerCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x14));
            if (headerCount > 64 || 0x18 + ((long)headerCount * 8) > header.Length)
                return false;

            uint headerSectionTableOffset = 0;
            for (int i = 0; i < headerCount; i++)
            {
                int pair = 0x18 + (i * 8);
                uint key = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(pair));
                if (key == HeaderSectionTableKey)
                {
                    headerSectionTableOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(pair + 4));
                    break;
                }
            }

            if (headerSectionTableOffset == 0 || (long)headerSectionTableOffset + 4 > xexSize)
                return false;

            byte[] tableSizeBytes = new byte[4];
            if (!TryReadAt(fs, xexStart + headerSectionTableOffset, tableSizeBytes))
                return false;

            uint tableSize = BinaryPrimitives.ReadUInt32BigEndian(tableSizeBytes);
            if (tableSize == 0 || tableSize % 16 != 0 || tableSize > MaxHeaderSectionCount * 16u)
                return false;

            long tableStart = xexStart + headerSectionTableOffset + 4;
            if (tableStart + tableSize > xexStart + xexSize)
                return false;

            byte[] table = new byte[tableSize];
            if (!TryReadAt(fs, tableStart, table))
                return false;

            int sectionCount = (int)(tableSize / 16);
            for (int i = 0; i < sectionCount; i++)
            {
                ReadOnlySpan<byte> entry = table.AsSpan(i * 16, 16);
                string name = ReadSectionName(entry[..8]);
                if (!MatchesTitleId(name, info.TitleId))
                    continue;

                uint virtualAddress = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                uint virtualSize = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                if (virtualSize == 0 || virtualSize > MaxSectionBytes)
                    continue;

                long sectionOffset = (long)virtualAddress - info.LoadAddress;
                if (!LooksLikeXdbf(fs, xexStart, xexSize, sectionOffset))
                {
                    sectionOffset = virtualAddress;
                    if (!LooksLikeXdbf(fs, xexStart, xexSize, sectionOffset))
                        continue;
                }

                int sectionLength = (int)Math.Min(virtualSize, xexSize - sectionOffset);
                byte[] section = new byte[sectionLength];
                if (!TryReadAt(fs, xexStart + sectionOffset, section))
                    continue;

                if (TryParseXdbf(section, xexPath, info, out title))
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or
                                       OverflowException or InvalidDataException or XmlException or
                                       DecoderFallbackException)
        {
            title = null;
            return false;
        }
    }

    private static bool LooksLikeXdbf(Stream fs, long xexStart, long xexSize, long sectionOffset)
    {
        if (sectionOffset < 0 || sectionOffset + 4 > xexSize)
            return false;

        byte[] magic = new byte[4];
        if (!TryReadAt(fs, xexStart + sectionOffset, magic))
            return false;

        return magic[0] == (byte)'X' && magic[1] == (byte)'D' && magic[2] == (byte)'B' && magic[3] == (byte)'F';
    }

    private static bool TryParseXdbf(byte[] data, string xexPath, XexInfo info, out XexTitleInfo? title)
    {
        title = null;
        if (data.Length < 24 || !HasMagic(data, "XDBF"))
            return false;

        uint entryTableLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x08));
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x0C));
        uint freeTableLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x10));
        if (entryTableLength > MaxXdbfEntries || entryCount > entryTableLength ||
            freeTableLength > MaxXdbfEntries)
        {
            return false;
        }

        long dataBase = 24 + ((long)entryTableLength * 18) + ((long)freeTableLength * 8);
        if (dataBase > data.Length)
            return false;

        string? titleName = null;
        string? publisher = null;
        string? developer = null;
        string? genre = null;
        string? description = null;
        byte[]? thumbnail = null;

        for (int i = 0; i < entryCount; i++)
        {
            long entryOffset = 24 + ((long)i * 18);
            if (entryOffset + 18 > data.Length)
                break;

            int p = (int)entryOffset;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
            uint id1 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 2));
            uint id2 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 6));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 0x0A));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 0x0E));
            if (length == 0)
                continue;

            long start = dataBase + offset;
            if (start < 0 || start + length > data.Length)
                continue;

            ReadOnlySpan<byte> entry = data.AsSpan((int)start, (int)length);
            if (type == 3 && id1 == 0 && id2 == 1)
            {
                Dictionary<ushort, string>? strings = ParseXstr(entry);
                if (strings != null && strings.TryGetValue(0x8000, out string? value))
                    titleName = value;
            }
            else if (type == 2 && id1 == 0 && id2 == ThumbnailEntryId && length <= MaxThumbnailBytes)
            {
                thumbnail = entry.ToArray();
            }
            else if (type == 1 && id1 == 0 && id2 == XsrcEntryId)
            {
                XsrcMetadata? metadata = ParseXsrc(entry);
                if (metadata != null)
                {
                    if (!string.IsNullOrEmpty(metadata.TitleName))
                        titleName = metadata.TitleName;
                    publisher = metadata.Publisher;
                    developer = metadata.Developer;
                    genre = metadata.Genre;
                    description = metadata.Description;
                }
            }
        }

        title = new XexTitleInfo(
            xexPath,
            titleName ?? string.Empty,
            publisher,
            developer,
            genre,
            description,
            thumbnail,
            info);
        return true;
    }

    private static Dictionary<ushort, string>? ParseXstr(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14 || !HasMagic(data, "XSTR"))
            return null;

        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (count > MaxXstrEntries)
            return null;

        Dictionary<ushort, string> result = new(count);
        int position = 14;
        for (int i = 0; i < count; i++)
        {
            if (position + 4 > data.Length)
                break;

            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            position += 4;
            if (position + length > data.Length)
                break;

            string value = Encoding.UTF8.GetString(data.Slice(position, length)).TrimEnd('\0');
            result[id] = value;
            position += length;
        }

        return result;
    }

    private static XsrcMetadata? ParseXsrc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !HasMagic(data, "XSRC"))
            return null;

        uint fileNameLength = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        if (fileNameLength > 1024 || 16 + (long)fileNameLength + 8 > data.Length)
            return null;

        int body = (int)(16 + fileNameLength);
        uint decompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data[body..]);
        uint compressedSize = BinaryPrimitives.ReadUInt32BigEndian(data[(body + 4)..]);
        body += 8;
        if (compressedSize == 0 || compressedSize > data.Length - body ||
            decompressedSize == 0 || decompressedSize > MaxXsrcBytes)
        {
            return null;
        }

        byte[]? xml = TryDecompress(data.Slice(body, (int)compressedSize).ToArray(), (int)decompressedSize);
        return xml == null ? null : ParseXsrcXml(xml);
    }

    private static byte[]? TryDecompress(byte[] compressed, int expectedLength)
    {
        foreach (int kind in new[] { 0, 1, 2 })
        {
            try
            {
                using MemoryStream input = new(compressed, writable: false);
                using Stream decoder = kind switch
                {
                    0 => new GZipStream(input, CompressionMode.Decompress),
                    1 => new ZLibStream(input, CompressionMode.Decompress),
                    _ => new DeflateStream(input, CompressionMode.Decompress),
                };
                using MemoryStream output = new(expectedLength);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxXsrcBytes)
                        return null;
                    output.Write(buffer, 0, read);
                }

                if (output.Length > 0)
                    return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // Try the next compression wrapper.
            }
            catch (IOException)
            {
                // Try the next compression wrapper.
            }
        }

        return null;
    }

    private static XsrcMetadata? ParseXsrcXml(byte[] xml)
    {
        string text;
        if (xml.Length >= 2 && xml[0] == 0xFF && xml[1] == 0xFE)
            text = Encoding.Unicode.GetString(xml, 2, xml.Length - 2);
        else if (xml.Length >= 2 && xml[0] == 0xFE && xml[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(xml, 2, xml.Length - 2);
        else
            text = Encoding.UTF8.GetString(xml);

        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }

        Dictionary<string, string> localized = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in document.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "LocalizedString", StringComparison.Ordinal))
                continue;

            string? id = AttributeValue(element, "id");
            if (id == null)
                continue;

            string? value = null;
            string? fallback = null;
            foreach (XElement translation in element.Elements())
            {
                if (!string.Equals(translation.Name.LocalName, "Translation", StringComparison.Ordinal))
                    continue;

                fallback ??= translation.Value.Trim();
                if (string.Equals(AttributeValue(translation, "locale"), "en-US",
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = translation.Value.Trim();
                    break;
                }
            }

            localized[id] = value ?? fallback ?? string.Empty;
        }

        XElement? gameConfig = document.Descendants()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "GameConfigProject",
                StringComparison.Ordinal));
        XElement? product = document.Descendants()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "ProductInformation",
                StringComparison.Ordinal));

        string? titleName = Resolve(localized, "32768");
        if (string.IsNullOrEmpty(titleName))
            titleName = AttributeValue(gameConfig, "titleName");
        string? description = Resolve(localized, AttributeValue(product, "sellTextStringId"));
        string? publisher = Resolve(localized, AttributeValue(product, "publisherStringId"));
        string? developer = Resolve(localized, AttributeValue(product, "developerStringId"));
        string? genre = Resolve(localized, AttributeValue(product, "genreTextStringId"));

        return new XsrcMetadata(titleName, publisher, developer, genre, description);
    }

    private static string? Resolve(Dictionary<string, string> localized, string? id) =>
        id != null && localized.TryGetValue(id, out string? value) ? value : null;

    private static string? AttributeValue(XElement? element, string name) =>
        element?.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string ReadSectionName(ReadOnlySpan<byte> name)
    {
        int end = name.IndexOf((byte)0);
        if (end < 0)
            end = name.Length;
        return Encoding.ASCII.GetString(name[..end]);
    }

    private static bool MatchesTitleId(string name, uint titleId) =>
        string.Equals(name, "XDBF", StringComparison.OrdinalIgnoreCase) ||
        (uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) &&
         parsed == titleId);

    private static bool HasMagic(ReadOnlySpan<byte> data, string magic) =>
        data.Length >= 4 &&
        data[0] == (byte)magic[0] && data[1] == (byte)magic[1] &&
        data[2] == (byte)magic[2] && data[3] == (byte)magic[3];

    private static bool TryReadAt(Stream fs, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > fs.Length)
            return false;

        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(buffer);
        return true;
    }

    private sealed record XsrcMetadata(
        string? TitleName,
        string? Publisher,
        string? Developer,
        string? Genre,
        string? Description);
}
