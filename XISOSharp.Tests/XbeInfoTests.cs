using System.Buffers.Binary;
using System.Text;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.GetXbeInfo(string, string)"/> — original-Xbox XBEH executable
/// header + certificate parsing.
/// </summary>
[Collection("Sequential")]
public class XbeInfoTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_xbe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Builds a synthetic XBEH executable with distinct values for every parsed
    /// field. Layout follows the XBE specification (little-endian): fixed header
    /// at 0x00 with the certificate address as a load pointer (file offset =
    /// address - base), certificate at 0x400.
    /// </summary>
    private static byte[] BuildXbe()
    {
        var data = new byte[0x1000];
        var span = data.AsSpan();

        // Fixed header
        "XBEH"u8.CopyTo(span);
        WriteU32(span, 0x104, 0x00010000); // base address
        WriteU32(span, 0x118, 0x00010400); // certificate address (file offset 0x400)
        WriteU32(span, 0x11C, 3); // section count
        WriteU32(span, 0x124, 0x10); // init flags
        WriteU32(span, 0x128, 0x0001102C); // entry point

        // Certificate at 0x400
        const int cert = 0x400;
        WriteU32(span, cert + 0x00, 0x1D0); // size
        WriteU32(span, cert + 0x04, 0x3A2B1C00); // timestamp
        WriteU32(span, cert + 0x08, 0x4D530004); // title id
        Encoding.Unicode.GetBytes("Halo: Combat Evolved").CopyTo(span[(cert + 0x0C)..]);
        WriteU32(span, cert + 0x5C, 0x4D530005); // alternate title id [0]
        WriteU32(span, cert + 0x9C, 0x08); // allowed media: DVD-9 RO
        WriteU32(span, cert + 0xA0, 0x07); // region: worldwide
        WriteU32(span, cert + 0xA4, 0x00000000); // ratings
        WriteU32(span, cert + 0xA8, 1); // disc number
        WriteU32(span, cert + 0xAC, 0x00010000); // version

        return data;

        static void WriteU32(Span<byte> s, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[offset..], value);
        }
    }

    private string CreateIsoWithFile(string fileName, byte[] content)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"xiso_xbe_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, fileName), content);
        _tempDirs.Add(srcDir);

        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
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
    public void GetXbeInfo_ParsesAllFields()
    {
        var isoPath = CreateIsoWithFile("default.xbe", BuildXbe());

        var xbe = XisoReader.GetXbeInfo(isoPath, "/default.xbe");

        Assert.NotNull(xbe);
        Assert.Equal(0x00010000u, xbe.BaseAddress);
        Assert.Equal(0x0001102Cu, xbe.EntryPoint);
        Assert.Equal(3u, xbe.SectionCount);
        Assert.Equal(0x10u, xbe.InitFlags);
        Assert.Equal(0x1D0u, xbe.CertSize);
        Assert.Equal(0x3A2B1C00u, xbe.CertTimeDate);
        Assert.Equal(0x4D530004u, xbe.TitleId);
        Assert.Equal("Halo: Combat Evolved", xbe.TitleName);
        Assert.Equal(16, xbe.AlternateTitleIds.Length);
        Assert.Equal(0x4D530005u, xbe.AlternateTitleIds[0]);
        Assert.All(xbe.AlternateTitleIds[1..], id => Assert.Equal(0u, id));
        Assert.Equal(0x08u, xbe.AllowedMedia);
        Assert.Equal(0x07u, xbe.GameRegion);
        Assert.Equal(0u, xbe.GameRatings);
        Assert.Equal(1u, xbe.DiskNumber);
        Assert.Equal(0x00010000u, xbe.Version);
    }

    [Fact]
    public void GetXbeInfo_NonXbeFile_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("readme.txt", "hello world"u8.ToArray());

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/readme.txt"));
    }

    [Fact]
    public void GetXbeInfo_MissingPath_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("default.xbe", BuildXbe());

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/nope.xbe"));
    }

    [Fact]
    public void GetXbeInfo_DirectoryPath_ReturnsNull()
    {
        var srcDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        File.WriteAllBytes(Path.Combine(srcDir, "default.xbe"), "x"u8.ToArray());

        var isoPath = CreateIsoWithDirectory(srcDir);

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/sub"));
    }

    [Fact]
    public void GetXbeInfo_TooShortFile_ReturnsNull()
    {
        var isoPath = CreateIsoWithFile("tiny.xbe", new byte[0x10]);

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/tiny.xbe"));
    }

    [Fact]
    public void GetXbeInfo_CertOffsetBeyondEnd_ReturnsNull()
    {
        // Malformed header: the certificate pointer aims past the file.
        var data = BuildXbe();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x118), 0x0001F000);

        var isoPath = CreateIsoWithFile("default.xbe", data);

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/default.xbe"));
    }

    [Fact]
    public void GetXbeInfo_CertAddressBelowBase_ReturnsNull()
    {
        // Malformed header: file offset would go negative.
        var data = BuildXbe();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x118), 0x00000080);

        var isoPath = CreateIsoWithFile("default.xbe", data);

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/default.xbe"));
    }

    [Fact]
    public void GetXbeInfo_WrongCertSize_ReturnsNull()
    {
        var data = BuildXbe();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x400), 0x100);

        var isoPath = CreateIsoWithFile("default.xbe", data);

        Assert.Null(XisoReader.GetXbeInfo(isoPath, "/default.xbe"));
    }

    [Fact]
    public void GetXbeInfo_InvalidIso_Throws()
    {
        var junkDir = CreateTempDir();
        var junkFile = Path.Combine(junkDir, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        Assert.Throws<XisoFormatException>(() => XisoReader.GetXbeInfo(junkFile, "/default.xbe"));
    }

    [Fact]
    public void GetXbeInfo_MissingIsoFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => XisoReader.GetXbeInfo("no_such_file.iso", "/default.xbe"));
    }

    [Fact]
    public void Explorer_GetXbeInfo_ParsesCert()
    {
        var isoPath = CreateIsoWithFile("default.xbe", BuildXbe());
        var explorer = new XisoExplorer(isoPath);

        var xbe = explorer.GetXbeInfo("/default.xbe");

        Assert.NotNull(xbe);
        Assert.Equal(0x4D530004u, xbe.TitleId);
        Assert.Equal("Halo: Combat Evolved", xbe.TitleName);
        Assert.Null(explorer.GetXbeInfo("/nope.xbe"));
    }

    [Fact]
    public void Cli_XbeInfo_EndToEnd()
    {
        var isoPath = CreateIsoWithFile("default.xbe", BuildXbe());

        Assert.Equal(0, Program.Main(["--xbe-info", isoPath, "/default.xbe"]));
    }

    [Fact]
    public void Cli_XbeInfo_NonXbe_ReturnsOne()
    {
        var isoPath = CreateIsoWithFile("readme.txt", "hello world"u8.ToArray());

        Assert.Equal(1, Program.Main(["--xbe-info", isoPath, "/readme.txt"]));
    }

    [Fact]
    public void Cli_XbeInfo_CombinedWithExtract_ReturnsOne()
    {
        var isoPath = CreateIsoWithFile("default.xbe", BuildXbe());

        Assert.Equal(1, Program.Main(["-x", "--xbe-info", isoPath, "/default.xbe"]));
    }
}