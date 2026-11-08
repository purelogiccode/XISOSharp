using System.Buffers.Binary;
using System.Text;
using XISOSharp.Cli;
using XISOSharp.Models;
using GodBlockDevice = XISOSharp.BlockDevice.GodVolumeBlockDevice;
using GodHeader = StfsSharp.GodHeader;

namespace XISOSharp.Tests;

/// <summary>
/// Read-only GOD/SVOD sector input: header detection, fragment mapping,
/// extraction, checksums and the CLI verbs. The fixtures are synthetic XISOs
/// wrapped in a minimal GOD header plus <c>Data0000</c>; no game data is used.
/// </summary>
[Collection("Sequential")]
public sealed class GodBlockDeviceTests : IDisposable
{
    private const uint SignatureLive = 0x4C495645;
    private const uint ContentTypeGod = 0x7000;
    private const uint VolumeTypeSvod = 1;

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void TryDetect_HeaderAndDataFolder_ReportGod()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, string iso) = CreateGodSet(work, "god-game", "GOD Test");
        long expectedLength = new FileInfo(iso).Length;

        Assert.True(XisoImageSource.TryDetect(header, out XisoImageSourceInfo? info));
        Assert.NotNull(info);
        Assert.Equal(XisoImageFormat.God, info.Format);
        Assert.Equal("GOD Test", info.DisplayName);
        Assert.Equal(expectedLength, info.Length);
        Assert.False(info.IsCompressed);
        Assert.False(info.IsSplit);
        Assert.Equal(header, info.FirstPart);
        Assert.Equal(header, info.Parts[0]);

        Assert.True(XisoImageSource.TryDetect(header + ".data", out XisoImageSourceInfo? fromData));
        Assert.NotNull(fromData);
        Assert.Equal(info.Format, fromData.Format);
        Assert.Equal(info.DisplayName, fromData.DisplayName);
        Assert.Equal(info.Length, fromData.Length);
        Assert.Equal(info.FirstPart, fromData.FirstPart);
        Assert.True(XisoImageSource.IsSupportedImage(header));
        Assert.True(XisoImageSource.IsGodImage(header));
        Assert.True(XisoImageSource.IsGodImage(header + ".data"));
    }

    [Fact]
    public void OpenRead_ListExtractAndChecksumMatchIso()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, string iso) = CreateGodSet(work, "god-game", "GOD Test");

        VolumeInfo volume = XisoReader.GetVolumeInfo(header);
        Assert.True(volume.IsValid);

        string godOut = Path.Combine(work, "extract-god");
        string isoOut = Path.Combine(work, "extract-iso");
        Assert.Equal(0, XisoReader.UnpackImage(header, godOut));
        Assert.Equal(0, XisoReader.UnpackImage(iso, isoOut));
        AssertTreesEqual(isoOut, godOut);

        Assert.Equal(
            XisoChecksum.ComputeImageChecksumHex(iso),
            XisoChecksum.ComputeImageChecksumHex(header));

        using XisoImage image = XisoImage.Open(header);
        Assert.Equal(XisoImageFormat.God, image.Source.Format);
        Assert.Equal("GOD Test", image.Source.DisplayName);
        Assert.Contains(image.List(), static entry =>
            string.Equals(entry.Name, "a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void GodBlockDevice_ReadsSectorsAndUnalignedRanges()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, string iso) = CreateGodSet(work, "god-game", "GOD Test");
        byte[] isoBytes = File.ReadAllBytes(iso);

        using GodBlockDevice device = new(header);
        Assert.Equal(isoBytes.Length, device.Length);

        byte[] expected = new byte[2048];
        Array.Copy(isoBytes, 0x10000, expected, 0, expected.Length);
        byte[] actual = new byte[2048];
        Assert.Equal(2048, device.Read(0x10000, actual));
        Assert.Equal(expected, actual);

        // A range crossing a sector boundary must be stitched from two sectors.
        byte[] expectedCross = new byte[300];
        Array.Copy(isoBytes, 0xFF00, expectedCross, 0, expectedCross.Length);
        byte[] actualCross = new byte[300];
        Assert.Equal(300, device.Read(0xFF00, actualCross));
        Assert.Equal(expectedCross, actualCross);

        // The final partial read is bounded by the logical length.
        byte[] tail = new byte[4096];
        int tailRead = device.Read(device.Length - 100, tail);
        Assert.Equal(100, tailRead);
        Assert.Equal(isoBytes[^100..], tail.AsSpan(0, tailRead).ToArray());
    }

    [Fact]
    public void GodBlockDevice_ZeroFillsPrefixBeforeGameData()
    {
        string work = CreateTempDir("xiso_god_device");
        string header = CreateGodHeader(work, "prefix-game", startingBlock: 4, dataBlockCount: 1);
        string dataDir = header + ".data";
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "Data0000"), []);

        using GodBlockDevice device = new(header);
        byte[] buffer = new byte[2048];
        Assert.Equal(2048, device.Read(0, buffer));
        Assert.All(buffer, static b => Assert.Equal(0, b));
        Assert.Equal(2048, device.Read(2048, buffer));
        Assert.All(buffer, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void GodBlockDevice_EnhancedGdfUsesTheSecuritySectorArea()
    {
        string work = CreateTempDir("xiso_god_device");
        string header = CreateGodHeader(
            work, "enhanced", startingBlock: 0, dataBlockCount: 0x22, enhancedGdf: true);

        string dataDir = header + ".data";
        Directory.CreateDirectory(dataDir);
        byte[] gameMarker = new byte[2048];
        gameMarker[0] = 0x11;
        byte[] securityMarker = new byte[2048];
        securityMarker[0] = 0x22;
        string dataPath = Path.Combine(dataDir, "Data0000");
        using (FileStream stream = new(dataPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Seek(0x2000, SeekOrigin.Begin);
            stream.Write(securityMarker);
            stream.Seek(0x3000, SeekOrigin.Begin);
            stream.Write(gameMarker);
        }

        using GodBlockDevice device = new(header);
        byte[] buffer = new byte[2048];
        Assert.Equal(2048, device.Read(0, buffer));
        Assert.Equal(gameMarker, buffer);
        Assert.Equal(2048, device.Read(0x20L * 2048, buffer));
        Assert.Equal(securityMarker, buffer);
    }

    [Fact]
    public void GodBlockDevice_MapsSecondFragment()
    {
        string work = CreateTempDir("xiso_god_device");
        const uint blocksPerFragment = 0xA1C4;
        string header = CreateGodHeader(
            work, "two-fragment", startingBlock: 0, dataBlockCount: blocksPerFragment + 1,
            dataFileCount: 2);

        string dataDir = header + ".data";
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "Data0000"), []);

        byte[] marker = new byte[2048];
        marker[0] = 0x5A;
        marker[^1] = 0xA5;
        string second = Path.Combine(dataDir, "Data0001");
        using (FileStream stream = new(second, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Seek(0x2000, SeekOrigin.Begin);
            stream.Write(marker);
        }

        using GodBlockDevice device = new(header);
        byte[] actual = new byte[2048];
        Assert.Equal(2048, device.Read(blocksPerFragment * 2L * 2048, actual));
        Assert.Equal(marker, actual);
    }

    [Fact]
    public void TryReadVolume_RejectsInvalidHeadersAndMissingFragments()
    {
        string work = CreateTempDir("xiso_god_device");

        string noVolume = CreateGodHeader(work, "stfs-volume", volumeType: 0);
        Directory.CreateDirectory(noVolume + ".data");
        Assert.False(GodHeader.TryReadVolume(noVolume, out _));

        string missing = CreateGodHeader(work, "missing-fragment", dataFileCount: 2);
        Directory.CreateDirectory(missing + ".data");
        File.WriteAllBytes(Path.Combine(missing + ".data", "Data0000"), []);
        Assert.False(GodHeader.TryReadVolume(missing, out _));
        // Header-only metadata still works, and in-place guards still recognize
        // the container even when a fragment is missing.
        Assert.True(GodHeader.IsGodContainer(missing));
        Assert.False(XisoImageSource.IsGodImage(missing));

        string zeroBlocks = CreateGodHeader(work, "zero-blocks", dataBlockCount: 0);
        Directory.CreateDirectory(zeroBlocks + ".data");
        File.WriteAllBytes(Path.Combine(zeroBlocks + ".data", "Data0000"), []);
        Assert.False(GodHeader.TryReadVolume(zeroBlocks, out _));

        string notGod = Path.Combine(work, "plain");
        File.WriteAllBytes(notGod, new byte[GodHeader.MinimumHeaderLength]);
        Directory.CreateDirectory(notGod + ".data");
        Assert.False(GodHeader.TryReadVolume(notGod, out _));
        Assert.False(XisoImageSource.IsGodImage(notGod));
    }

    [Fact]
    public void InPlaceWriteVerbsRefuseGodContainers()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, _) = CreateGodSet(work, "god-game", "GOD Test");
        string hostFile = Path.Combine(work, "host.txt");
        File.WriteAllText(hostFile, "x");

        Assert.Throws<IOException>(() => XisoReader.SetFileTime(header, 0));
        Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(header, hostFile, "/host.txt"));
        Assert.Throws<InvalidDataException>(() => XisoRepairer.RepairInPlace(header));
        Assert.Throws<NotSupportedException>(() =>
            XisoSplitter.Split(header, Path.Combine(work, "split"), 1024 * 1024));
        Assert.Throws<NotSupportedException>(() =>
            XisoReader.Rewrite(header, Path.Combine(work, "rewrite"), out _));
    }

    [Fact]
    public void Cli_ListChecksumAndDecompressWork()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, string iso) = CreateGodSet(work, "god-game", "GOD Test");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["-l", header]));
            Assert.Contains("a.txt", capture.Output, StringComparison.Ordinal);
        }

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["checksum", header]));
            Assert.Contains(
                XisoChecksum.ComputeImageChecksumHex(iso),
                capture.Output,
                StringComparison.OrdinalIgnoreCase);
        }

        string outIso = Path.Combine(work, "decompressed.iso");
        Assert.Equal(0, Program.Main(["decompress", header, outIso, "-y"]));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(outIso));

        // With no explicit output, the title name (not the content-hash header
        // file name) drives the ISO name.
        Assert.Equal(0, Program.Main(["decompress", header, "-y"]));
        string titleIso = Path.Combine(work, "GOD Test.iso");
        Assert.True(File.Exists(titleIso));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(titleIso));

        // A .data source must not overwrite its own header file.
        Assert.Equal(1, Program.Main(["decompress", header + ".data", header, "-y"]));
        Assert.True(File.Exists(header));
        Assert.True(GodHeader.IsGodContainer(header));
    }

    [Fact]
    public void Cli_ConvertGodToCciAndCso()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, string iso) = CreateGodSet(work, "god-game", "GOD Test");

        string cci = Path.Combine(work, "game.cci");
        Assert.Equal(0, Program.Main(["convert", header, cci, "-y"]));
        Assert.True(File.Exists(cci),
            "files: " + string.Join(", ", Directory.GetFiles(work, "*", SearchOption.AllDirectories)));
        Assert.Equal(
            XisoChecksum.ComputeImageChecksumHex(iso),
            XisoChecksum.ComputeImageChecksumHex(cci));

        string cso = Path.Combine(work, "game.cso");
        Assert.Equal(0, Program.Main(["convert", header, cso, "-y"]));
        Assert.True(File.Exists(cso));
        Assert.Equal(
            XisoChecksum.ComputeImageChecksumHex(iso),
            XisoChecksum.ComputeImageChecksumHex(cso));

        // A `.data` folder is a GOD container, not a directory to pack.
        string fromFolder = Path.Combine(work, "from-folder.cci");
        Assert.Equal(0, Program.Main(
            ["compress", "--format", "cci", "--cci-split", "0", header + ".data", fromFolder, "-y"]));
        Assert.True(File.Exists(fromFolder));
        Assert.Equal(
            XisoChecksum.ComputeImageChecksumHex(iso),
            XisoChecksum.ComputeImageChecksumHex(fromFolder));

        string csoFromFolder = Path.Combine(work, "from-folder.cso");
        Assert.Equal(0, Program.Main(["convert", header + ".data", csoFromFolder, "-y"]));
        Assert.True(File.Exists(csoFromFolder));
        Assert.Equal(
            XisoChecksum.ComputeImageChecksumHex(iso),
            XisoChecksum.ComputeImageChecksumHex(csoFromFolder));
    }

    [Fact]
    public void RedumpModesRefuseGodContainers()
    {
        string work = CreateTempDir("xiso_god_device");
        (string header, _) = CreateGodSet(work, "god-game", "GOD Test");

        using LogCapture capture = new();
        Assert.Equal(1, Program.Main(["--wipe", "-y", header]));
        Assert.Contains("GOD", capture.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decompress", capture.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(work, "god-game.wiped.xiso")));
    }

    private static void AssertTreesEqual(string expectedRoot, string actualRoot)
    {
        string[] expectedFiles = Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories);
        string[] actualFiles = Directory.GetFiles(actualRoot, "*", SearchOption.AllDirectories);
        Assert.Equal(
            expectedFiles.Select(p => Path.GetRelativePath(expectedRoot, p)).Order(StringComparer.Ordinal),
            actualFiles.Select(p => Path.GetRelativePath(actualRoot, p)).Order(StringComparer.Ordinal));

        foreach (string expected in expectedFiles)
        {
            string relative = Path.GetRelativePath(expectedRoot, expected);
            string actual = Path.Combine(actualRoot, relative);
            Assert.True(File.Exists(actual), $"missing extracted file: {relative}");
            Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
        }
    }

    private static (string Header, string Iso) CreateGodSet(
        string work,
        string name,
        string title,
        uint startingBlock = 0,
        bool enhancedGdf = false)
    {
        string iso = CreateIso(work);
        byte[] isoBytes = File.ReadAllBytes(iso);
        int sectors = isoBytes.Length / Constants.SectorSize;
        uint dataBlocks = (uint)((sectors + 1) / 2);

        string header = CreateGodHeader(
            work, name, title, startingBlock, dataBlocks,
            dataFileCount: 1, enhancedGdf: enhancedGdf);

        string dataDir = header + ".data";
        Directory.CreateDirectory(dataDir);
        string dataPath = Path.Combine(dataDir, "Data0000");
        using FileStream data = new(
            dataPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            1 << 16, FileOptions.RandomAccess);
        for (int block = 0; block * 0x198 < sectors; block++)
        {
            int startSector = block * 0x198;
            int count = Math.Min(0x198, sectors - startSector);
            data.Seek(0x2000 + block * 0xCD000L, SeekOrigin.Begin);
            data.Write(isoBytes, startSector * Constants.SectorSize, count * Constants.SectorSize);
        }

        return (header, iso);
    }

    private static string CreateGodHeader(
        string work,
        string name,
        string title = "GOD Test",
        uint startingBlock = 0,
        uint dataBlockCount = 1,
        uint dataFileCount = 1,
        uint volumeType = VolumeTypeSvod,
        bool enhancedGdf = false)
    {
        string headerPath = Path.Combine(work, name);
        byte[] header = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), SignatureLive);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), ContentTypeGod);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(GodHeader.TitleIdOffset), 0x4D5307E6);
        header[GodHeader.DiscNumberOffset] = 1;
        header[GodHeader.DiscCountOffset] = 1;

        byte[] titleBytes = Encoding.BigEndianUnicode.GetBytes(title);
        titleBytes.AsSpan(0, Math.Min(titleBytes.Length, GodHeader.TitleNameLength))
            .CopyTo(header.AsSpan(GodHeader.TitleNameOffset));

        header[0x391] = enhancedGdf ? (byte)0x40 : (byte)0;
        WriteUInt24LittleEndian(header, 0x392, dataBlockCount);
        WriteUInt24LittleEndian(header, 0x395, startingBlock);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x39D), dataFileCount);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x3A9), volumeType);

        File.WriteAllBytes(headerPath, header);
        return headerPath;
    }

    private static void WriteUInt24LittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }

    private static string CreateIso(string work)
    {
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string outDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(outDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _origOut = Logger.Out;
        private readonly TextWriter _origErr = Logger.Error;
        private readonly bool _origQuiet = Logger.Quiet;
        private readonly bool _origRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        internal LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _err;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        internal string Output => string.Concat(_out, _err);

        public void Dispose()
        {
            Logger.Out = _origOut;
            Logger.Error = _origErr;
            Logger.Quiet = _origQuiet;
            Logger.RealQuiet = _origRealQuiet;
            _out.Dispose();
            _err.Dispose();
        }
    }
}
