using System.Buffers.Binary;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.GetXexInfo"/> — Xbox 360 XEX2 executable header parsing.
/// </summary>
[Collection("Sequential")]
public class XexInfoTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_xex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Builds a synthetic XEX2 executable with distinct values for every parsed field.
    /// Layout follows the XEX2 specification (big-endian): fixed header at 0x00,
    /// optional-header (key, value) pairs at 0x18, execution info, file format info,
    /// and security info.
    /// </summary>
    private static byte[] BuildXex2()
    {
        var data = new byte[0x1000];
        var span = data.AsSpan();

        // Fixed header
        "XEX2"u8.CopyTo(span);
        WriteU32(span, 0x04, 0x89); // module flags: Title + DllModule + UserMode
        WriteU32(span, 0x08, 0x400); // header size
        WriteU32(span, 0x0C, 0); // reserved
        WriteU32(span, 0x10, 0x300); // security offset
        WriteU32(span, 0x14, 4); // header count

        // Optional-header pairs
        WriteU32(span, 0x18 + 0 * 8, 0x00010100);
        WriteU32(span, 0x18 + 0 * 8 + 4, 0x12345678); // entry point
        WriteU32(span, 0x18 + 1 * 8, 0x00010201);
        WriteU32(span, 0x18 + 1 * 8 + 4, 0x82000000); // image base
        WriteU32(span, 0x18 + 2 * 8, 0x00040006);
        WriteU32(span, 0x18 + 2 * 8 + 4, 0x200); // execution info
        WriteU32(span, 0x18 + 3 * 8, 0x000003FF);
        WriteU32(span, 0x18 + 3 * 8 + 4, 0x218); // file format info

        // Execution info (0x18 bytes)
        WriteU32(span, 0x200, 0x2B35C136); // media id
        WriteU32(span, 0x204, 0x00000002); // version
        WriteU32(span, 0x208, 0x00000002); // base version
        WriteU32(span, 0x20C, 0x4D5307D3); // title id
        data[0x210] = 0x00; // platform
        data[0x211] = 0x00; // executable table
        data[0x212] = 0x01; // disc number
        data[0x213] = 0x02; // disc count
        WriteU32(span, 0x214, 0); // savegame id

        // File format info
        WriteU32(span, 0x218, 0x24); // info size
        WriteU16(span, 0x21C, 1); // encryption: normal
        WriteU16(span, 0x21E, 2); // compression: normal

        // Security info
        WriteU32(span, 0x300, 0x1F3C); // header size
        WriteU32(span, 0x304, 0x013D0000); // image size
        WriteU32(span, 0x30C, 0x8); // image flags
        WriteU32(span, 0x410, 0x82000000); // load address (security + 0x110)
        WriteU32(span, 0x478, 0xFD00); // region (NTSC-J) (security + 0x178)
        WriteU32(span, 0x47C, 0x10); // allowed media types (DVD-9) (security + 0x17C)
        WriteU32(span, 0x480, 0); // page descriptor count

        return data;

        static void WriteU32(Span<byte> s, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s[offset..], value);
        }

        static void WriteU16(Span<byte> s, int offset, ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[offset..], value);
        }
    }

    private string CreateIsoWithFile(string fileName, byte[] content)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"xiso_xex_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, fileName), content);
        _tempDirs.Add(srcDir);

        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void GetXexInfo_ParsesAllFields()
    {
        var isoPath = CreateIsoWithFile("default.xex", BuildXex2());

        var xex = XisoReader.GetXexInfo(isoPath, "/default.xex");

        Assert.NotNull(xex);
        Assert.Equal(0x89u, xex.ModuleFlags);
        Assert.Equal(0x400u, xex.HeaderSize);
        Assert.Equal(0x12345678u, xex.EntryPoint);
        Assert.Equal(0x82000000u, xex.ImageBaseAddress);
        Assert.Equal(0x013D0000u, xex.ImageSize);
        Assert.Equal(0x82000000u, xex.LoadAddress);
        Assert.Equal(0xFD00u, xex.Region);
        Assert.Equal(0x10u, xex.AllowedMediaTypes);
        Assert.Equal(0x2B35C136u, xex.MediaId);
        Assert.Equal(0x4D5307D3u, xex.TitleId);
        Assert.Equal(0x2u, xex.Version);
        Assert.Equal(0, xex.Platform);
        Assert.Equal(1, xex.DiscNumber);
        Assert.Equal(2, xex.DiscCount);
        Assert.Equal(1, xex.EncryptionType);
        Assert.Equal(2, xex.CompressionType);
    }

    [Fact]
    public void GetXexInfo_NonXexFile_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("readme.txt", "hello world"u8.ToArray());

        Assert.Null(XisoReader.GetXexInfo(isoPath, "/readme.txt"));
    }

    [Fact]
    public void GetXexInfo_MissingPath_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("default.xex", BuildXex2());

        Assert.Null(XisoReader.GetXexInfo(isoPath, "/nope.xex"));
    }

    [Fact]
    public void GetXexInfo_DirectoryPath_ReturnsNull()
    {
        var srcDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        File.WriteAllText(Path.Combine(srcDir, "default.xex"), "x");

        var isoPath = CreateIsoWithDirectory(srcDir);

        Assert.Null(XisoReader.GetXexInfo(isoPath, "/sub"));
    }

    private string CreateIsoWithDirectory(string srcDir)
    {
        var outputDir = CreateTempDir();
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void GetXexInfo_TooShortFile_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("tiny.xex", new byte[0x10]);

        Assert.Null(XisoReader.GetXexInfo(isoPath, "/tiny.xex"));
    }

    [Fact]
    public void GetXexInfo_WrappedSectionOffsets_DoNotCrash()
    {
        // Malformed header with huge section offsets that would wrap uint arithmetic:
        // the parser must skip the sections instead of reading out of bounds.
        var data = new byte[0x1000];
        var span = data.AsSpan();
        "XEX2"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x04..], 1); // module flags
        BinaryPrimitives.WriteUInt32BigEndian(span[0x08..], 0x400); // header size
        BinaryPrimitives.WriteUInt32BigEndian(span[0x10..], 0xFFFFFFF8); // security offset (would wrap)
        BinaryPrimitives.WriteUInt32BigEndian(span[0x14..], 2); // header count
        BinaryPrimitives.WriteUInt32BigEndian(span[0x18..], 0x00040006); // execution info key
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], 0xFFFFFFF0); // execution info offset (would wrap)
        BinaryPrimitives.WriteUInt32BigEndian(span[0x20..], 0x000003FF); // file format info key
        BinaryPrimitives.WriteUInt32BigEndian(span[0x24..], 0xFFFFFFF8); // format offset (would wrap)

        var isoPath = CreateIsoWithFile("default.xex", data);

        var xex = XisoReader.GetXexInfo(isoPath, "/default.xex");

        Assert.NotNull(xex);
        Assert.Equal(1u, xex.ModuleFlags); // fixed header still parsed
        Assert.Equal(0u, xex.ImageSize); // security info skipped
        Assert.Equal(0u, xex.MediaId); // execution info skipped
        Assert.Equal(0, xex.EncryptionType); // format info skipped
    }

    [Fact]
    public void GetXexInfo_InvalidIso_Throws()
    {
        var junkDir = CreateTempDir();
        var junkFile = Path.Combine(junkDir, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        Assert.Throws<XisoFormatException>(() => XisoReader.GetXexInfo(junkFile, "/default.xex"));
    }

    [Fact]
    public void GetXexInfo_MissingIsoFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => XisoReader.GetXexInfo("no_such_file.iso", "/default.xex"));
    }
}
