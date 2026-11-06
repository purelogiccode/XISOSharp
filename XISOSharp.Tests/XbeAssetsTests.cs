using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XbeAssets"/>: logo, <c>$$XTIMAGE</c> and
/// <c>$$XSIMAGE</c> extraction, DXT/BGR decoding, PNG/DDS output, and the
/// <c>--xbe-image</c> CLI mode.
/// </summary>
[Collection("Sequential")]
public class XbeAssetsTests : IDisposable
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_xbe_assets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIsoWithXbe(byte[] xbe)
    {
        string srcDir = CreateTempDir();
        File.WriteAllBytes(Path.Combine(srcDir, "default.xbe"), xbe);
        string outputDir = CreateTempDir();
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static byte[] BuildXbeWithSection(string sectionName, byte[] raw)
    {
        const int rawOffset = 0x1000;
        byte[] data = new byte[rawOffset + raw.Length + 0x100];
        Span<byte> span = data.AsSpan();

        "XBEH"u8.CopyTo(span);
        WriteU32(span, 0x104, 0x00010000);
        WriteU32(span, 0x118, 0x00010400);
        WriteU32(span, 0x11C, 1);
        WriteU32(span, 0x120, 0x00010500);

        const int cert = 0x400;
        WriteU32(span, cert, 0x1D0);
        WriteU32(span, cert + 0x04, 0x3A2B1C00);
        WriteU32(span, cert + 0x08, 0x4D530004);
        Encoding.Unicode.GetBytes("Image Test").CopyTo(span[(cert + 0x0C)..]);
        WriteU32(span, cert + 0x9C, 0x08);
        WriteU32(span, cert + 0xA0, 0x07);
        WriteU32(span, cert + 0xA8, 1);
        WriteU32(span, cert + 0xAC, 0x00010000);

        WriteU32(span, 0x50C, rawOffset);
        WriteU32(span, 0x510, (uint)raw.Length);
        WriteU32(span, 0x514, 0x00010700);
        Encoding.ASCII.GetBytes(sectionName).CopyTo(span[0x700..]);
        raw.CopyTo(span[rawOffset..]);
        return data;

        static void WriteU32(Span<byte> target, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);
    }

    private static byte[] BuildDxt3Blocks()
    {
        byte[] data = new byte[128 * 128 / 16 * 16];
        for (int block = 0; block < 128 * 128 / 16; block++)
        {
            int p = block * 16;
            for (int i = 0; i < 8; i++)
                data[p + i] = 0xFF;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p + 8), 0xF800);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p + 10), 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p + 12), 0);
        }

        return data;
    }

    private static byte[] BuildDxt5Blocks()
    {
        byte[] data = new byte[128 * 128 / 16 * 16];
        for (int block = 0; block < 128 * 128 / 16; block++)
        {
            int p = block * 16;
            data[p] = 0xFF;
            data[p + 1] = 0x00;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p + 8), 0xF800);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p + 10), 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p + 12), 0);
        }

        return data;
    }

    private static byte[] BuildLegacyTexture(string fourCc, byte[] payload)
    {
        byte[] raw = new byte[54 + payload.Length];
        "DDS "u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x10), 128);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(raw.AsSpan(0x20));
        payload.CopyTo(raw, 54);
        return raw;
    }

    private static byte[] BuildXpr0(int formatCode, byte[] payload)
    {
        byte[] raw = new byte[0x800 + payload.Length];
        "XPR0"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x04), (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x08), 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C), 0x00040001);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x18),
            ((uint)formatCode << 8) | (7u << 20) | (7u << 24));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x20), 0xFFFFFFFF);
        payload.CopyTo(raw, 0x800);
        return raw;
    }

    private static byte[] BuildXbeWithImages()
    {
        const int titleOffset = 0x3000;
        const int saveOffset = 0x10000;
        const int logoOffset = 0x800;
        const int logoSize = 100 * 17 * 4;
        const int titleSize = 54 + (128 * 128 * 3);
        const int saveSize = 54 + (128 * 128 / 16 * 8);

        byte[] data = new byte[saveOffset + saveSize + 0x1000];
        Span<byte> span = data.AsSpan();

        "XBEH"u8.CopyTo(span);
        WriteU32(span, 0x104, 0x00010000); // base
        WriteU32(span, 0x118, 0x00010400); // certificate
        WriteU32(span, 0x11C, 2); // sections
        WriteU32(span, 0x120, 0x00010500); // section headers
        WriteU32(span, 0x170, 0x00010000 + logoOffset); // logo
        WriteU32(span, 0x174, logoSize);

        // Certificate at 0x400.
        const int cert = 0x400;
        WriteU32(span, cert, 0x1D0);
        WriteU32(span, cert + 0x04, 0x3A2B1C00);
        WriteU32(span, cert + 0x08, 0x4D530004);
        Encoding.Unicode.GetBytes("Image Test").CopyTo(span[(cert + 0x0C)..]);
        WriteU32(span, cert + 0x9C, 0x08);
        WriteU32(span, cert + 0xA0, 0x07);
        WriteU32(span, cert + 0xA8, 1);
        WriteU32(span, cert + 0xAC, 0x00010000);

        // Section headers at 0x500.
        WriteSection(span, 0x500, titleOffset, titleSize, 0x700);
        WriteSection(span, 0x538, saveOffset, saveSize, 0x720);

        // Section names.
        "$$XTIMAGE\0"u8.CopyTo(span[0x700..]);
        "$$XSIMAGE\0"u8.CopyTo(span[0x720..]);

        // Logo: 100x17 BGRA.
        for (int i = 0; i < 100 * 17; i++)
        {
            int p = logoOffset + (i * 4);
            data[p] = (byte)(i & 0xFF);
            data[p + 1] = (byte)((i * 3) & 0xFF);
            data[p + 2] = (byte)((i * 7) & 0xFF);
            data[p + 3] = 0xFF;
        }

        // Title image: 54-byte DDS-like header + 128x128 BGR24.
        "DDS "u8.CopyTo(span[titleOffset..]);
        WriteU32(span, titleOffset + 0x0C, 128);
        WriteU32(span, titleOffset + 0x10, 128);
        for (int i = 0; i < 128 * 128; i++)
        {
            int p = titleOffset + 54 + (i * 3);
            data[p] = (byte)(i & 0xFF);
            data[p + 1] = (byte)((i * 5) & 0xFF);
            data[p + 2] = (byte)((i * 11) & 0xFF);
        }

        // Save image: 54-byte header + 128x128 DXT1 (solid red).
        "DDS "u8.CopyTo(span[saveOffset..]);
        WriteU32(span, saveOffset + 0x0C, 128);
        WriteU32(span, saveOffset + 0x10, 128);
        "DXT1"u8.CopyTo(span[(saveOffset + 0x20)..]);
        for (int block = 0; block < 128 * 128 / 16; block++)
        {
            int p = saveOffset + 54 + (block * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[p..], 0xF800); // red
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + 2)..], 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 4)..], 0);
        }

        return data;

        static void WriteSection(Span<byte> target, int offset, int rawOffset, int rawSize, int nameOffset)
        {
            WriteU32(target, offset + 0x0C, (uint)rawOffset);
            WriteU32(target, offset + 0x10, (uint)rawSize);
            WriteU32(target, offset + 0x14, 0x00010000 + (uint)nameOffset);
        }

        static void WriteU32(Span<byte> target, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);
    }

    [Theory]
    [InlineData("DXT3")]
    [InlineData("DXT5")]
    public void GetTitleImage_DecodesLegacyDxt(string format)
    {
        byte[] payload = string.Equals(format, "DXT3", StringComparison.Ordinal)
            ? BuildDxt3Blocks()
            : BuildDxt5Blocks();
        string isoPath = CreateIsoWithXbe(
            BuildXbeWithSection("$$XTIMAGE", BuildLegacyTexture(format, payload)));

        XbeImage? image = XbeAssets.GetTitleImage(isoPath, "/default.xbe");

        Assert.NotNull(image);
        Assert.Equal(format, image.PixelFormat);
        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
        Assert.NotNull(image.Png);
        Assert.NotNull(image.Dds);
    }

    [Fact]
    public void GetTitleImage_DecodesXpr0Dxt5()
    {
        string isoPath = CreateIsoWithXbe(
            BuildXbeWithSection("$$XTIMAGE", BuildXpr0(0x0F, BuildDxt5Blocks())));

        XbeImage? image = XbeAssets.GetTitleImage(isoPath, "/default.xbe");

        Assert.NotNull(image);
        Assert.Equal("XPR0/DXT5", image.PixelFormat);
        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
        Assert.NotNull(image.Png);
        Assert.NotNull(image.Dds);
    }

    [Fact]
    public void GetTitleImage_DdsPassthrough()
    {
        string firstIso = CreateIsoWithXbe(BuildXbeWithImages());
        XbeImage first = XbeAssets.GetTitleImage(firstIso, "/default.xbe")!;
        Assert.NotNull(first.Dds);

        string secondIso = CreateIsoWithXbe(BuildXbeWithSection("$$XTIMAGE", first.Dds));
        XbeImage? second = XbeAssets.GetTitleImage(secondIso, "/default.xbe");

        Assert.NotNull(second);
        Assert.Equal("DDS/BGR24", second.PixelFormat);
        Assert.Equal(first.Dds, second.Dds);
    }

    [Fact]
    public void GetXbeInfo_ReportsImagePresence()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());

        XbeInfo? info = XisoReader.GetXbeInfo(isoPath, "/default.xbe");

        Assert.NotNull(info);
        Assert.True(info.HasLogoBitmap);
        Assert.True(info.HasTitleImage);
        Assert.True(info.HasSaveImage);
    }

    [Fact]
    public void GetTitleImage_DecodesBgr24()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());

        XbeImage? image = XbeAssets.GetTitleImage(isoPath, "/default.xbe");

        Assert.NotNull(image);
        Assert.Equal(XbeImageKind.Title, image.Kind);
        Assert.Equal("$$XTIMAGE", image.SectionName);
        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
        Assert.Equal("BGR24", image.PixelFormat);
        Assert.NotNull(image.Png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, image.Png[..4]);
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(16)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(20)));
        Assert.NotNull(image.Dds);
        Assert.Equal("DDS "u8.ToArray(), image.Dds[..4]);
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(image.Dds.AsSpan(0x0C)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(image.Dds.AsSpan(0x10)));
    }

    [Fact]
    public void GetSaveImage_DecodesDxt1()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());

        XbeImage? image = XbeAssets.GetSaveImage(isoPath, "/default.xbe");

        Assert.NotNull(image);
        Assert.Equal(XbeImageKind.Save, image.Kind);
        Assert.Equal("$$XSIMAGE", image.SectionName);
        Assert.Equal("DXT1", image.PixelFormat);
        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
        Assert.NotNull(image.Png);
        Assert.NotNull(image.Dds);
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(16)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(20)));
        Assert.Equal(0x31545844u, BinaryPrimitives.ReadUInt32LittleEndian(image.Dds.AsSpan(0x54)));
    }

    [Fact]
    public void GetLogo_DecodesBgra32()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());

        XbeImage? image = XbeAssets.GetLogo(isoPath, "/default.xbe");

        Assert.NotNull(image);
        Assert.Equal(XbeImageKind.Logo, image.Kind);
        Assert.Equal("logo", image.SectionName);
        Assert.Equal(100, image.Width);
        Assert.Equal(17, image.Height);
        Assert.Equal("BGRA32", image.PixelFormat);
        Assert.NotNull(image.Png);
        Assert.NotNull(image.Dds);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(16)));
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32BigEndian(image.Png.AsSpan(20)));
    }

    [Fact]
    public void GetImage_MissingSections_ReturnsNull()
    {
        string isoPath = CreateIsoWithXbe(XbeInfoTests.BuildXbe());

        Assert.Null(XbeAssets.GetTitleImage(isoPath, "/default.xbe"));
        Assert.Null(XbeAssets.GetSaveImage(isoPath, "/default.xbe"));
        Assert.Null(XbeAssets.GetLogo(isoPath, "/default.xbe"));
        Assert.False(XbeAssets.TryGetImage(isoPath, "/default.xbe", XbeImageKind.Title, out _));
    }

    [Fact]
    public void CliXbeImage_WritesPngAndDds()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());
        string outputDir = CreateTempDir();
        string png = Path.Combine(outputDir, "title.png");
        string dds = Path.Combine(outputDir, "title.dds");

        Assert.Equal(0, Program.Main(["--xbe-image", isoPath, "/default.xbe", "--type", "title", "-o", png]));
        Assert.Equal(0, Program.Main(["xbe-image", isoPath, "/default.xbe", "--type", "title", "-o", dds]));

        Assert.True(File.Exists(png));
        Assert.True(File.Exists(dds));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(png)[..4]);
        Assert.Equal("DDS "u8.ToArray(), File.ReadAllBytes(dds)[..4]);
    }

    [Fact]
    public void CliXbeInfoJson_ReportsPresenceFlags()
    {
        string isoPath = CreateIsoWithXbe(BuildXbeWithImages());
        StringWriter output = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedErr = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = new StringWriter();
            Assert.Equal(0, Program.Main(["--xbe-info", "--json", isoPath, "/default.xbe"]));
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedErr;
        }

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("hasLogoBitmap").GetBoolean());
        Assert.True(root.GetProperty("hasTitleImage").GetBoolean());
        Assert.True(root.GetProperty("hasSaveImage").GetBoolean());
    }
}
