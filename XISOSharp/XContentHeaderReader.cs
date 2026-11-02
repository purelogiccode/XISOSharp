using System.Buffers.Binary;
using System.Text;

namespace XISOSharp;

/// <summary>
/// Shared reader for the plaintext XContent header at the start of Xbox 360
/// STFS packages and GOD/SVOD containers. Only the fixed metadata fields used
/// by <see cref="GodHeader"/> and <see cref="StfsHeader"/> are read.
/// </summary>
internal static class XContentHeaderReader
{
    internal const int ContentTypeOffset = 0x344;
    internal const int TitleIdOffset = 0x360;
    internal const int DiscNumberOffset = 0x366;
    internal const int DiscCountOffset = 0x367;
    internal const int TitleNameOffset = 0x411;
    internal const int TitleNameLength = 0x80;
    internal const int MinimumHeaderLength = TitleNameOffset + TitleNameLength;

    internal const uint SignatureConsole = 0x434F4E20; // "CON "
    internal const uint SignatureLive = 0x4C495645;    // "LIVE"
    internal const uint SignaturePirs = 0x50495253;    // "PIRS"

    internal const uint ContentTypeNxe = 0x4000;
    internal const uint ContentTypeGod = 0x7000;
    internal const uint ContentTypeXbla = 0x000D0000;
    internal const uint ContentTypeXblig = 0x00000002;

    /// <summary>
    /// Reads the fixed-size header prefix from <paramref name="headerPath"/>.
    /// Returns <c>false</c> for a missing/short file, an unreadable file, or a
    /// file without a CON/LIVE/PIRS signature.
    /// </summary>
    internal static bool TryReadHeader(string headerPath, out byte[] header)
    {
        header = [];
        if (string.IsNullOrWhiteSpace(headerPath) || !File.Exists(headerPath))
            return false;

        try
        {
            using FileStream stream = new(
                headerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.RandomAccess);
            if (stream.Length < MinimumHeaderLength)
                return false;

            byte[] buffer = new byte[MinimumHeaderLength];
            stream.ReadExactly(buffer);
            if (!IsXContent(buffer))
                return false;

            header = buffer;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsXContent(ReadOnlySpan<byte> header)
    {
        if (header.Length < ContentTypeOffset + sizeof(uint))
            return false;

        uint signature = BinaryPrimitives.ReadUInt32BigEndian(header);
        return signature is SignatureConsole or SignatureLive or SignaturePirs;
    }

    internal static uint ReadContentType(ReadOnlySpan<byte> header) =>
        BinaryPrimitives.ReadUInt32BigEndian(header.Slice(ContentTypeOffset, sizeof(uint)));

    internal static string DecodeTitleName(ReadOnlySpan<byte> bytes)
    {
        string value = Encoding.BigEndianUnicode.GetString(bytes);
        int nul = value.IndexOf('\0');
        if (nul >= 0)
            value = value[..nul];
        return value.Trim();
    }

    internal static string SanitizeTitleName(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (char c in name)
        {
            if (c >= 0x80 || char.IsControl(c))
                continue;

            builder.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c);
        }

        return builder.ToString().Trim().TrimEnd('.', ' ');
    }
}
