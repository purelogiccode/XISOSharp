using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for Xbox 360 title metadata: XEX header-section table walking,
/// XDBF/XSTR/XSRC parsing, and <c>title-info</c> resolution.
/// </summary>
[Collection("Sequential")]
public class XexTitleInfoTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_xex_title_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(params (string Name, byte[] Content)[] files)
    {
        string srcDir = CreateTempDir();
        foreach ((string name, byte[] content) in files)
            File.WriteAllBytes(Path.Combine(srcDir, name), content);

        string outputDir = CreateTempDir();
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static byte[] BuildXexWithXdbf(string titleName = "XDBF Game",
        string publisher = "Test Publisher", string developer = "Test Developer",
        string genre = "Action", string description = "A test game")
    {
        byte[] xdbf = BuildXdbf(titleName, publisher, developer, genre, description);
        byte[] data = new byte[0x2000];
        Span<byte> span = data.AsSpan();

        "XEX2"u8.CopyTo(span);
        WriteU32(span, 0x04, 0x01); // module flags: title
        WriteU32(span, 0x08, 0x400); // header size
        WriteU32(span, 0x10, 0x300); // security offset
        WriteU32(span, 0x14, 2); // optional header count

        WriteU32(span, 0x18, 0x00040006); // execution info
        WriteU32(span, 0x1C, 0x200);
        WriteU32(span, 0x20, 0x000002FF); // header section table
        WriteU32(span, 0x24, 0x280);

        // Execution info (0x18 bytes): media id, version, base version, title id,
        // platform, executable table, disc number, disc count, savegame id.
        WriteU32(span, 0x200, 0x2B35C136);
        WriteU32(span, 0x204, 2);
        WriteU32(span, 0x208, 2);
        WriteU32(span, 0x20C, 0x4D5307D3);
        span[0x210] = 0;
        span[0x211] = 0;
        span[0x212] = 1;
        span[0x213] = 2;
        WriteU32(span, 0x214, 0);

        // Security info: load address at +0x110.
        WriteU32(span, 0x300, 0x1F3C);
        WriteU32(span, 0x304, 0x013D0000);
        WriteU32(span, 0x410, 0x82000000);
        WriteU32(span, 0x478, 0x00000001); // NTSC-U
        WriteU32(span, 0x47C, 0x10);

        // Header section table: size + one (name, virtual address, virtual size) entry.
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
        byte[] xsrc = BuildXsrc(publisher, developer, genre, description);

        const int entryCount = 3;
        const int entryTableLength = 3;
        const int freeTableLength = 1;
        int dataBase = 24 + (entryTableLength * 18) + (freeTableLength * 8);
        int total = dataBase + xstr.Length + thumbnail.Length + xsrc.Length;
        byte[] data = new byte[total];
        Span<byte> span = data.AsSpan();

        "XDBF"u8.CopyTo(span);
        WriteU32(span, 0x04, 0x10000);
        WriteU32(span, 0x08, entryTableLength);
        WriteU32(span, 0x0C, entryCount);
        WriteU32(span, 0x10, freeTableLength);
        WriteU32(span, 0x14, 1);

        int xstrOffset = 0;
        int thumbOffset = xstr.Length;
        int xsrcOffset = thumbOffset + thumbnail.Length;
        WriteEntry(span, 0, 3, 0, 1, (uint)xstrOffset, (uint)xstr.Length);
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

    private static byte[] BuildXsrc(string publisher, string developer, string genre, string description)
    {
        string xml =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <XboxLiveSubmissionProject xmlns="http://www.xboxlive.com/xlast">
               <GameConfigProject titleName="Fallback Name">
                 <LocalizedStrings>
                   <LocalizedString id="32768"><Translation locale="en-US">XDBF Game</Translation></LocalizedString>
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
            gzip.Write(xmlBytes);

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

    [Fact]
    public void XisoImageFacade_GetTitleInfoReadsXex()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbf()));
        using XisoImage image = XisoImage.Open(isoPath);

        TitleInfo? title = image.GetTitleInfo();

        Assert.NotNull(title);
        Assert.Equal(TitleKind.Xex, title.Kind);
        Assert.Equal("XDBF Game", title.TitleName);
        Assert.NotNull(title.Xex);
    }

    [Fact]
    public void GetTitleInfo_XexReadsXdbfMetadata()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbf()));

        TitleInfo? title = XisoReader.GetTitleInfo(isoPath);

        Assert.NotNull(title);
        Assert.Equal(TitleKind.Xex, title.Kind);
        Assert.Null(title.Xbe);
        Assert.Equal("/default.xex", title.XbePath);
        Assert.Equal("XDBF Game", title.TitleName);
        Assert.Equal(0x4D5307D3u, title.TitleId);
        Assert.Equal("0x4D5307D3", title.TitleIdHex);
        Assert.Equal("NTSC-U", title.GameRegionName);
        Assert.Equal(1u, title.DiskNumber);
        Assert.Equal(2u, title.Version);
        Assert.NotNull(title.Xex);
        Assert.Equal("Test Publisher", title.Xex.Publisher);
        Assert.Equal("Test Developer", title.Xex.Developer);
        Assert.Equal("Action", title.Xex.Genre);
        Assert.Equal("A test game", title.Xex.Description);
        Assert.True(title.Xex.HasThumbnail);
        Assert.Equal(2, title.Xex.Info.DiscCount);
    }

    [Fact]
    public void GetXexTitleInfo_DirectCallReadsMetadata()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbf()));

        XexTitleInfo? title = XisoReader.GetXexTitleInfo(isoPath, "/default.xex");

        Assert.NotNull(title);
        Assert.Equal("XDBF Game", title.TitleName);
        Assert.Equal("Test Publisher", title.Publisher);
        Assert.True(title.HasThumbnail);
    }

    [Fact]
    public void GetTitleInfo_XexWithoutXdbfStillReportsXex()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbfNoXdbf()));

        TitleInfo? title = XisoReader.GetTitleInfo(isoPath);

        Assert.NotNull(title);
        Assert.Equal(TitleKind.Xex, title.Kind);
        Assert.Equal(string.Empty, title.TitleName);
        Assert.Equal("/default.xex", title.XbePath);
        Assert.NotNull(title.Xex);
        Assert.False(title.Xex.HasThumbnail);
    }

    private static byte[] BuildXexWithXdbfNoXdbf()
    {
        byte[] data = new byte[0x800];
        Span<byte> span = data.AsSpan();
        "XEX2"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x400);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x10..], 0x300);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x14..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x18..], 0x00040006);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], 0x200);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x200..], 0x2B35C136);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x20C..], 0x4D5307D3);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x300..], 0x1F3C);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x410..], 0x82000000);
        return data;
    }

    [Fact]
    public void GetTitleInfo_PrefersDefaultXbeOverXex()
    {
        string isoPath = CreateIso(
            ("default.xbe", XbeInfoTests.BuildXbe()),
            ("default.xex", BuildXexWithXdbf()));

        TitleInfo? title = XisoReader.GetTitleInfo(isoPath);

        Assert.NotNull(title);
        Assert.Equal(TitleKind.Xbe, title.Kind);
        Assert.Equal("/default.xbe", title.XbePath);
        Assert.NotNull(title.Xbe);
        Assert.Null(title.Xex);
    }

    [Fact]
    public void GetTitleInfo_FallsBackToDefaultMpXbe()
    {
        string isoPath = CreateIso(("default_mp.xbe", XbeInfoTests.BuildXbe()));

        TitleInfo? title = XisoReader.GetTitleInfo(isoPath);

        Assert.NotNull(title);
        Assert.Equal(TitleKind.Xbe, title.Kind);
        Assert.Equal("/default_mp.xbe", title.XbePath);
    }

    [Fact]
    public void CliTitleInfo_TextAndJsonReportXexMetadata()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbf()));

        StringWriter textOut = new();
        StringWriter textErr = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedErr = Logger.Error;
        try
        {
            Logger.Out = textOut;
            Logger.Error = textErr;
            Assert.Equal(0, Program.Main(["title-info", isoPath]));
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedErr;
        }

        Assert.Contains("Title:          XDBF Game", textOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("XEX:            /default.xex", textOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("Publisher:      Test Publisher", textOut.ToString(), StringComparison.Ordinal);

        StringWriter jsonOut = new();
        try
        {
            Logger.Out = jsonOut;
            Logger.Error = new StringWriter();
            Assert.Equal(0, Program.Main(["title-info", "--json", isoPath]));
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedErr;
        }

        using JsonDocument document = JsonDocument.Parse(jsonOut.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("title-info", root.GetProperty("command").GetString());
        Assert.Equal("xex", root.GetProperty("kind").GetString());
        Assert.Equal("/default.xex", root.GetProperty("xexPath").GetString());
        Assert.Equal("XDBF Game", root.GetProperty("titleName").GetString());
        Assert.Equal("Test Publisher", root.GetProperty("publisher").GetString());
        Assert.True(root.GetProperty("hasThumbnail").GetBoolean());
        Assert.Equal(2, root.GetProperty("discCount").GetInt32());
    }

    [Fact]
    public void CliXexInfo_PrintsXdbfTitle()
    {
        string isoPath = CreateIso(("default.xex", BuildXexWithXdbf()));
        StringWriter output = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedErr = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = new StringWriter();
            Assert.Equal(0, Program.Main(["--xex-info", isoPath, "/default.xex"]));
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedErr;
        }

        Assert.Contains("Title name:        XDBF Game", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Publisher:         Test Publisher", output.ToString(), StringComparison.Ordinal);
    }
}
