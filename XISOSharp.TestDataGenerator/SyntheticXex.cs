using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Builds deterministic, copyright-free XEX2 executables with an XDBF title
/// resource (XSTR name/description and XSRC publisher/developer/genre XML).
/// Used by the title-info corpus fixtures.
/// </summary>
public static class SyntheticXex
{
    /// <summary>
    /// Builds a XEX2 title executable with XDBF/XSTR/XSRC metadata.
    /// </summary>
    /// <param name="titleName">XSTR title name.</param>
    /// <param name="publisher">XSRC publisher.</param>
    /// <param name="developer">XSRC developer.</param>
    /// <param name="genre">XSRC genre.</param>
    /// <param name="description">XSRC description.</param>
    /// <returns>Raw XEX bytes.</returns>
    public static byte[] BuildWithXdbf(
        string titleName = "XDBF Game",
        string publisher = "Test Publisher",
        string developer = "Test Developer",
        string genre = "Action",
        string description = "A test game")
    {
        byte[] xdbf = BuildXdbf(titleName, publisher, developer, genre, description);
        byte[] data = new byte[0x2000];
        Span<byte> span = data.AsSpan();

        "XEX2"u8.CopyTo(span);
        WriteU32(span, 0x04, 0x01);
        WriteU32(span, 0x08, 0x400);
        WriteU32(span, 0x10, 0x300);
        WriteU32(span, 0x14, 2);

        WriteU32(span, 0x18, 0x00040006);
        WriteU32(span, 0x1C, 0x200);
        WriteU32(span, 0x20, 0x000002FF);
        WriteU32(span, 0x24, 0x280);

        WriteU32(span, 0x200, 0x2B35C136);
        WriteU32(span, 0x204, 2);
        WriteU32(span, 0x208, 2);
        WriteU32(span, 0x20C, 0x4D5307D3);
        span[0x210] = 0;
        span[0x211] = 0;
        span[0x212] = 1;
        span[0x213] = 2;
        WriteU32(span, 0x214, 0);

        WriteU32(span, 0x300, 0x1F3C);
        WriteU32(span, 0x304, 0x013D0000);
        WriteU32(span, 0x410, 0x82000000);
        WriteU32(span, 0x478, 0x00000001);
        WriteU32(span, 0x47C, 0x10);

        WriteU32(span, 0x280, 16);
        "4D5307D3"u8.CopyTo(span[0x284..]);
        WriteU32(span, 0x28C, 0x82000000 + 0x1000);
        WriteU32(span, 0x290, (uint)xdbf.Length);

        xdbf.CopyTo(span[0x1000..]);
        return data;

        static void WriteU32(Span<byte> target, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);
    }

    private static byte[] BuildXdbf(string titleName, string publisher, string developer,
        string genre, string description)
    {
        byte[] xstr = BuildXstr(titleName);
        byte[] thumbnail = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];
        byte[] xsrc = BuildXsrc(titleName, publisher, developer, genre, description);

        const int entryTableLength = 3;
        const int freeTableLength = 1;
        int dataBase = 24 + (entryTableLength * 18) + (freeTableLength * 8);
        int total = dataBase + xstr.Length + thumbnail.Length + xsrc.Length;
        byte[] data = new byte[total];
        Span<byte> span = data.AsSpan();

        "XDBF"u8.CopyTo(span);
        WriteU32(span, 0x04, 0x10000);
        WriteU32(span, 0x08, entryTableLength);
        WriteU32(span, 0x0C, 3);
        WriteU32(span, 0x10, freeTableLength);
        WriteU32(span, 0x14, 1);

        int thumbOffset = xstr.Length;
        int xsrcOffset = thumbOffset + thumbnail.Length;
        WriteEntry(span, 0, 3, 0, 1, 0, (uint)xstr.Length);
        WriteEntry(span, 1, 2, 0, 0x8000, (uint)thumbOffset, (uint)thumbnail.Length);
        WriteEntry(span, 2, 1, 0, 0x58535243, (uint)xsrcOffset, (uint)xsrc.Length);

        xstr.CopyTo(span[dataBase..]);
        thumbnail.CopyTo(span[(dataBase + thumbOffset)..]);
        xsrc.CopyTo(span[(dataBase + xsrcOffset)..]);
        return data;

        static void WriteEntry(Span<byte> target, int index, ushort type, uint id1, uint id2,
            uint offset, uint length)
        {
            int p = 24 + (index * 18);
            BinaryPrimitives.WriteUInt16BigEndian(target[p..], type);
            WriteU32(target, p + 2, id1);
            WriteU32(target, p + 6, id2);
            WriteU32(target, p + 0x0A, offset);
            WriteU32(target, p + 0x0E, length);
        }

        static void WriteU32(Span<byte> target, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);
    }

    private static byte[] BuildXstr(string titleName)
    {
        byte[] title = Encoding.UTF8.GetBytes(titleName);
        byte[] description = Encoding.UTF8.GetBytes("XSTR description");
        int total = 14 + 4 + title.Length + 4 + description.Length;
        byte[] data = new byte[total];
        Span<byte> span = data.AsSpan();
        "XSTR"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], 2);

        int p = 14;
        BinaryPrimitives.WriteUInt16BigEndian(span[p..], 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(span[(p + 2)..], (ushort)title.Length);
        p += 4;
        title.CopyTo(span[p..]);
        p += title.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[p..], 0x8001);
        BinaryPrimitives.WriteUInt16BigEndian(span[(p + 2)..], (ushort)description.Length);
        p += 4;
        description.CopyTo(span[p..]);
        return data;
    }

    private static byte[] BuildXsrc(string titleName, string publisher, string developer,
        string genre, string description)
    {
        string xml =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <XboxLiveSubmissionProject xmlns="http://www.xboxlive.com/xlast">
               <GameConfigProject titleName="Fallback Name">
                 <LocalizedStrings>
                   <LocalizedString id="32768"><Translation locale="en-US">{titleName}</Translation></LocalizedString>
                   <LocalizedString id="32769"><Translation locale="en-US">{description}</Translation></LocalizedString>
                   <LocalizedString id="32770"><Translation locale="en-US">{publisher}</Translation></LocalizedString>
                   <LocalizedString id="32771"><Translation locale="en-US">{developer}</Translation></LocalizedString>
                   <LocalizedString id="32772"><Translation locale="en-US">{genre}</Translation></LocalizedString>
                 </LocalizedStrings>
                 <ProductInformation sellTextStringId="32769" publisherStringId="32770"
                                    developerStringId="32771" genreTextStringId="32772" />
               </GameConfigProject>
             </XboxLiveSubmissionProject>
             """;
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(xmlBytes);
        }

        byte[] compressedBytes = compressed.ToArray();
        byte[] fileName = Encoding.UTF8.GetBytes("test.xlast");
        byte[] data = new byte[16 + fileName.Length + 8 + compressedBytes.Length];
        Span<byte> span = data.AsSpan();
        "XSRC"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)fileName.Length);
        fileName.CopyTo(span[16..]);
        int body = 16 + fileName.Length;
        BinaryPrimitives.WriteUInt32BigEndian(span[body..], (uint)xmlBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[(body + 4)..], (uint)compressedBytes.Length);
        compressedBytes.CopyTo(span[(body + 8)..]);
        return data;
    }
}
