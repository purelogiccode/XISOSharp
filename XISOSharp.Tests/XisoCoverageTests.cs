using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Targeted gap-closers for TODO #1 (full test suite, xdvdfs #107/#137):
/// exercises the <c>XisoReader</c> branches the suite never reached —
/// multi-sector directory tables, disc-layout probe ladders, block-device
/// error paths, sentinel/edge table shapes, and single-line guards — to
/// bring <c>XisoReader.cs</c> line coverage over 85%.
/// </summary>
[Collection("Sequential")]
public class XisoCoverageTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                /* best effort cleanup */
            }
        }

        Logger.Out = Console.Out;
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(Action<string> populate, string isoName)
    {
        var src = CreateTempDir("xiso_cov_src");
        populate(src);
        var dir = CreateTempDir("xiso_cov_iso");
        var result = XisoWriter.CreateXiso(src, dir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    private string CopyIso(string isoPath, string prefix)
    {
        var dest = Path.Combine(CreateTempDir(prefix), Path.GetFileName(isoPath));
        File.Copy(isoPath, dest);
        return dest;
    }

    private static long FindEntryHeader(byte[] img, long tableAbs, uint tableSize, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var tableEnd = tableAbs + tableSize;
        for (var i = tableAbs; i + 14 + nameBytes.Length <= Math.Min(tableEnd, img.Length); i++)
        {
            var match = true;
            for (var k = 0; k < nameBytes.Length; k++)
            {
                if (img[i + 14 + k] != nameBytes[k])
                {
                    match = false;
                    break;
                }
            }

            if (match && img[i + 13] == nameBytes.Length)
                return i;
        }

        Assert.Fail($"entry '{name}' not found in directory table at {tableAbs}");
        return -1;
    }

    private static (uint RootSize, long RootAbs) RootLayout(string isoPath)
    {
        var vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid, $"fixture ISO invalid: {isoPath}");
        return (vol.RootDirSize, (long)vol.RootDirSector * Constants.SectorSize + vol.DiscLseek);
    }

    private static bool IsNtfs(string path)
    {
        try
        {
            return string.Equals(new DriveInfo(Path.GetPathRoot(path)!).DriveFormat, "NTFS",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Multi-sector directory tables (sector-boundary advance + llCompat)
    // ------------------------------------------------------------------

    private string CreateManyFileIso(int count = 150)
    {
        return CreateIso(src =>
        {
            for (var i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(src, $"file{i:000}.txt"), $"content {i}\n");
        }, "many.iso");
    }

    [Fact]
    public void ManyFileDir_Extract_CoversAllFiles()
    {
        var isoPath = CreateManyFileIso();
        var dest = CreateTempDir("xiso_cov_dest");
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        for (var i = 0; i < 150; i++)
            Assert.Equal($"content {i}\n", File.ReadAllText(Path.Combine(dest, $"file{i:000}.txt")));
    }

    [Fact]
    public void ManyFileDir_ExtractLlCompat_CoversAllFiles()
    {
        var isoPath = CreateManyFileIso();
        var dest = CreateTempDir("xiso_cov_dest");
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, true));
        for (var i = 0; i < 150; i++)
            Assert.True(File.Exists(Path.Combine(dest, $"file{i:000}.txt")), $"file{i:000}.txt missing");
    }

    [Fact]
    public void ManyFileDir_ListDirectory_CoversAllFiles()
    {
        var isoPath = CreateManyFileIso();
        var entries = XisoReader.ListDirectory(isoPath, "/");
        Assert.Equal(150, entries.Count);
    }

    // ------------------------------------------------------------------
    // Zeroed-table shapes (empty sentinels at start vs mid-table)
    // ------------------------------------------------------------------

    private static string ZeroTableStart(string isoPath, Func<string, string> copy)
    {
        var vol = XisoReader.GetVolumeInfo(isoPath);
        var rootAbs = (long)vol.RootDirSector * Constants.SectorSize + vol.DiscLseek;
        var img = File.ReadAllBytes(isoPath);
        Array.Clear(img, (int)rootAbs, 14);
        var bad = copy(isoPath);
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void List_ZeroedTableStart_ReturnsZero()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Equal(0, XisoReader.List(bad, false));
    }

    [Fact]
    public void ListDirectory_ZeroedTableStart_ReturnsEmpty()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Empty(XisoReader.ListDirectory(bad, "/"));
    }

    [Fact]
    public void GetSectorLayout_ZeroedTableStart_HasOnlyRootTable()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        var layout = XisoReader.GetSectorLayout(bad);
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void Extract_ZeroedNonFirstEntry_ThrowsOutsideTable()
    {
        // Zeroing a reachable non-first header turns it into an empty-table
        // sentinel mid-walk: the sector-advance lands past the one-sector
        // table, tripping the entry-outside-table guard.
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
            File.WriteAllText(Path.Combine(src, "c.txt"), "!");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "c.txt");
        Array.Clear(img, (int)header, 14);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest")));
        Assert.Contains("lies outside the directory table", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Sibling/child pointer guards via the traverse path
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_LeftChildOutsideTable_ThrowsNamed()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), (ushort)(rootSize / 4));
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest")));
        Assert.Contains("left child offset", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside the directory table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContinueOnError_CorruptRoot_RecordsSummary()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), (ushort)(rootSize / 4));
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var options = new UnpackOptions { ContinueOnError = true };
        var ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest"), options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("Failed to unpack image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinueOnError_CorruptLeftSubtree_SkipsAndContinues()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
            File.WriteAllText(Path.Combine(src, "c.txt"), "!");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var headerA = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        // a.left -> a itself (valid, in-table): the revisit throws inside the
        // recursive call, hitting the left-subtree continue-on-error catch
        // while the entry itself and all siblings still extract.
        var selfA = (ushort)((headerA - rootAbs) / 4);
        Assert.NotEqual(0, selfA);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)headerA), selfA);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var dest = CreateTempDir("xiso_cov_dest");
        var options = new UnpackOptions { ContinueOnError = true };
        var ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(bad, dest, options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(dest, "b.txt")));
        Assert.Equal("!", File.ReadAllText(Path.Combine(dest, "c.txt")));
    }

    // ------------------------------------------------------------------
    // DecodeXisoCore scaffolding guards
    // ------------------------------------------------------------------

    [Fact]
    public void DecodeXiso_Stream_EmptyImageName_ReturnsOne()
    {
        using var stream = new MemoryStream(new byte[1024]);
        var rc = XisoReader.DecodeXiso(stream, "somedir\\", CreateTempDir("xiso_cov_dest"),
            ExtractMode.List, out var outPath, false);
        Assert.Equal(1, rc);
        Assert.Null(outPath);
    }

    [Fact]
    public void Extract_OutputPathIsFile_ThrowsIOException()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var blocker = Path.Combine(CreateTempDir("xiso_cov_dest"), "blocker");
        File.WriteAllText(blocker, "in the way");

        var ex = Assert.Throws<IOException>(() => XisoReader.Extract(isoPath, blocker, false));
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_IsoNameBlockedByFile_ThrowsIOException()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "other.iso");
        var runDir = CreateTempDir("xiso_cov_rundir");
        File.WriteAllText(Path.Combine(runDir, "other"), "in the way");

        var savedCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(runDir);
        try
        {
            var ex = Assert.Throws<IOException>(() => XisoReader.Extract(isoPath, null, false));
            Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
        }
    }

    [Fact]
    public void Extract_SystemUpdateSkippedWhenFlagSetAfterCreate()
    {
        var isoPath = CreateIso(src =>
        {
            var updateDir = Path.Combine(src, "$SystemUpdate");
            Directory.CreateDirectory(updateDir);
            File.WriteAllText(Path.Combine(updateDir, "update.bin"), "update data");
            File.WriteAllText(Path.Combine(src, "game.txt"), "game data");
        }, "game.iso");

        // Flag off at create time (update IS in the image), on at extract time.
        Logger.RemoveSystemUpdate = true;
        try
        {
            var dest = CreateTempDir("xiso_cov_dest");
            Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
            Assert.True(File.Exists(Path.Combine(dest, "game.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "$SystemUpdate", "update.bin")),
                "$SystemUpdate should be skipped at extract time");
        }
        finally
        {
            Logger.RemoveSystemUpdate = false;
        }
    }

    // ------------------------------------------------------------------
    // Block-device overloads (MemoryBlockDevice doubles)
    // ------------------------------------------------------------------

    private string CreateIsoBytes(Action<string> populate, string isoName)
    {
        return CreateIso(populate, isoName);
    }

    [Fact]
    public void VerifyXiso_Device_SkipZeroValid()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        using var dev = new MemoryBlockDevice(File.ReadAllBytes(isoPath));
        var (sector, size, lseek) = XisoReader.VerifyXiso(dev, "game.iso", skipSectors: 0);
        Assert.True(sector > 0);
        Assert.True(size > 0);
        Assert.Equal(0, lseek);
    }

    [Fact]
    public void VerifyXiso_Device_NegativeSkip_Throws()
    {
        using var dev = new MemoryBlockDevice(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void VerifyXiso_Device_SkipGarbage_ThrowsNoHeader()
    {
        using var dev = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 1024]);
        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: 0));
        Assert.Contains("no header at sector 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyXiso_Device_SkipTiny_ThrowsReadHeader()
    {
        using var dev = new MemoryBlockDevice(new byte[10]);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: 0));
    }

    [Fact]
    public void VerifyXiso_Device_Garbage_ThrowsInvalid()
    {
        using var dev = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "x.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeRootSector_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength)];
        using var dev = new MemoryBlockDevice(bytes);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeRootSize_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 4)];
        using var dev = new MemoryBlockDevice(bytes);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeTail_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 8)];
        using var dev = new MemoryBlockDevice(bytes);
        var ex = Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("trailing magic", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyXiso_Device_TailCorrupt_ThrowsCorrupt()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath);
        Array.Clear(bytes,
            Constants.HeaderOffset + Constants.HeaderDataLength + 4 + 4 + Constants.FileTimeSize +
            Constants.UnusedSize, Constants.HeaderDataLength);
        using var dev = new MemoryBlockDevice(bytes);
        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("Corrupt XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyXiso_Device_EmptyRoot_ThrowsEmpty()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath);
        Array.Clear(bytes, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        using var dev = new MemoryBlockDevice(bytes);
        Assert.Throws<XisoEmptyException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_RootBeyondEnd_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(Constants.HeaderOffset + Constants.HeaderDataLength), 0x00FFFFFFu);
        using var dev = new MemoryBlockDevice(bytes);
        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("beyond end", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditXiso_Device_Valid_ReturnsValid()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        using var dev = new MemoryBlockDevice(File.ReadAllBytes(isoPath));
        var result = XisoReader.AuditXiso(dev, "game.iso");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void AuditXiso_Device_Garbage_ReturnsInvalid()
    {
        using var dev = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 1024]);
        var result = XisoReader.AuditXiso(dev, "x.iso");
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void GetFileTime_Device_ReturnsStamp()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var expected = XisoReader.GetFileTime(isoPath);
        using var dev = new MemoryBlockDevice(File.ReadAllBytes(isoPath));
        Assert.Equal(expected, XisoReader.GetFileTime(dev, "game.iso"));
    }

    [Fact]
    public void GetFileTimeRaw_Device_ShortFileTime_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 8)];
        using var dev = new MemoryBlockDevice(bytes);
        Assert.Throws<IOException>(() => XisoReader.GetFileTimeRaw(dev, "game.iso"));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipNegative_Throws()
    {
        using var dev = new MemoryBlockDevice(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.GetFileTimeRaw(dev, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipZero_Valid()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var expected = XisoReader.GetFileTimeRaw(isoPath);
        using var dev = new MemoryBlockDevice(File.ReadAllBytes(isoPath));
        Assert.Equal(expected, XisoReader.GetFileTimeRaw(dev, "game.iso", skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipBadMagic_Throws()
    {
        using var dev = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(dev, "x.iso", skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_Device_Garbage_ThrowsInvalid()
    {
        using var dev = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(dev, "x.iso"));
    }

    // ------------------------------------------------------------------
    // FILETIME stream-probe gaps
    // ------------------------------------------------------------------

    [Fact]
    public void GetFileTimeRaw_NegativeSkip_Throws()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.GetFileTimeRaw(isoPath, skipSectors: -1));
    }

    [Fact]
    public void GetFileTimeRaw_SkipZero_ReturnsStamp()
    {
        var isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Equal(XisoReader.GetFileTimeRaw(isoPath), XisoReader.GetFileTimeRaw(isoPath, skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_SkipBadMagic_Throws()
    {
        var path = Path.Combine(CreateTempDir("xiso_cov_bad"), "bad.iso");
        File.WriteAllBytes(path, new byte[Constants.HeaderOffset + 1024]);
        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(path, skipSectors: 0));
        Assert.Contains("no header at sector 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileTimeRaw_TruncatedProbes_ThrowsInvalid()
    {
        var path = Path.Combine(CreateTempDir("xiso_cov_bad"), "short.iso");
        File.WriteAllBytes(path, new byte[Constants.HeaderOffset + 10]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(path));
    }

    // ------------------------------------------------------------------
    // GetVolumeInfo disc-layout probe ladder (sparse files, NTFS only:
    // SetLength is metadata-only there, reads are tiny 20-byte probes)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0x0FD90000L)]
    [InlineData(0x02080000L)]
    [InlineData(0x89D80000L)]
    [InlineData(0x18300000L)]
    public void GetVolumeInfo_DiscOffsetMagic_Detected(long discOffset)
    {
        var dir = CreateTempDir("xiso_cov_bad");
        if (!IsNtfs(dir))
            return;

        var path = Path.Combine(dir, "offset.iso");
        // Every earlier probe must read (zeros) rather than throw, so all
        // files are sized past the largest probe (Xgd2Hybrid).
        const long minLength = 0x89D80000L + Constants.HeaderOffset + 1024;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(minLength);
            fs.Seek(discOffset + Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));
        }

        var vol = XisoReader.GetVolumeInfo(path);
        Assert.True(vol.IsValid);
        Assert.Equal(discOffset, vol.DiscLseek);
    }

    [Fact]
    public void GetVolumeInfo_BigGarbage_ReturnsNotValid()
    {
        var path = Path.Combine(CreateTempDir("xiso_cov_bad"), "garbage.iso");
        var data = new byte[Constants.HeaderOffset + 1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);

        Assert.False(XisoReader.GetVolumeInfo(path).IsValid);
    }

    [Fact]
    public void GetVolumeInfo_BigZeros_ReturnsNotValid()
    {
        // All probes read (zeros) but none match: the no-match return, not
        // the short-read catch. Sparse file (NTFS only), reads are tiny.
        var dir = CreateTempDir("xiso_cov_bad");
        if (!IsNtfs(dir))
            return;

        var path = Path.Combine(dir, "zeros.iso");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(0x89D80000L + Constants.HeaderOffset + 1024);
        }

        Assert.False(XisoReader.GetVolumeInfo(path).IsValid);
    }

    // ------------------------------------------------------------------
    // Empty-root image (sector 0 / size 0)
    // ------------------------------------------------------------------

    private string CreateEmptyIso()
    {
        var src = CreateTempDir("xiso_cov_empty");
        var dir = CreateTempDir("xiso_cov_iso");
        var result = XisoWriter.CreateXiso(src, dir, null, null, out var created, "empty", null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    [Fact]
    public void EmptyRoot_ListDirectory_ReturnsEmpty()
    {
        Assert.Empty(XisoReader.ListDirectory(CreateEmptyIso(), "/"));
    }

    [Fact]
    public void EmptyRoot_GetSectorLayout_ReturnsHeaderOnly()
    {
        var layout = XisoReader.GetSectorLayout(CreateEmptyIso());
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void EmptyRoot_Audit_ReturnsValid()
    {
        var result = XisoReader.AuditXiso(CreateEmptyIso());
        Assert.True(result.IsValid);
    }

    // ------------------------------------------------------------------
    // ListDirectory / GetSectorLayout / GetEntryInfo walker gaps
    // ------------------------------------------------------------------

    [Fact]
    public void GetEntryInfo_SlashesOnly_ReturnsNull()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Null(XisoReader.GetEntryInfo(isoPath, "///"));
    }

    [Fact]
    public void ListDirectory_LeftBeyondImage_Throws()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(bad, "/"));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSectorLayout_LeftBeyondImage_Throws()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSectorLayout_LeftSelfCycle_Throws()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        var self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), self);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateDotEntryIso()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 13] = 1;
        img[header + 14] = (byte)'.';
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ListDirectory_DotEntry_SkippedButSiblingsListed()
    {
        var entries = XisoReader.ListDirectory(CreateDotEntryIso(), "/");
        Assert.DoesNotContain(entries, static e => string.Equals(e.Name, ".", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, static e => string.Equals(e.Name, "b.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectorLayout_DotEntry_Skipped()
    {
        var layout = XisoReader.GetSectorLayout(CreateDotEntryIso());
        Assert.DoesNotContain(layout.Entries, static e => string.Equals(e.Path, "/.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(layout.Entries, static e => string.Equals(e.Path, "/b.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectorLayout_SubSizeZero_SkipsTable()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0u);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var layout = XisoReader.GetSectorLayout(bad);
        Assert.Contains(layout.Entries, static e => string.Equals(e.Path, "/sub", StringComparison.OrdinalIgnoreCase) && e.FileSize == 0);
    }

    [Fact]
    public void GetSectorLayout_EmptyFile_MergesRanges()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "empty.txt"), "");
            File.WriteAllText(Path.Combine(src, "full.txt"), "data");
        }, "game.iso");

        var layout = XisoReader.GetSectorLayout(isoPath);
        Assert.Contains(layout.Entries, static e => string.Equals(e.Path, "/empty.txt", StringComparison.OrdinalIgnoreCase) && e.SectorCount == 0);
    }

    [Fact]
    public void GetSectorLayout_SubSizeHuge_Throws()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    private static string CreateDeepTree(string baseDir, int depth)
    {
        var dir = baseDir;
        for (var i = 0; i < depth; i++)
        {
            dir = Path.Combine(dir, "a");
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(Path.Combine(dir, "leaf.txt"), "leaf\n");
        return baseDir;
    }

    [Fact]
    public void GetSectorLayout_DeepTree_ThrowsDepth()
    {
        var src = CreateDeepTree(CreateTempDir("xiso_cov_deep"), 70);
        var dir = CreateTempDir("xiso_cov_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, dir, null, null, out var isoPath, "deep", null));
        Assert.NotNull(isoPath);

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(isoPath));
        Assert.Contains("maximum directory depth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeDirectoryHashes_DeepTree_ThrowsDepth()
    {
        var src = CreateDeepTree(CreateTempDir("xiso_cov_deep"), 70);
        var dir = CreateTempDir("xiso_cov_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, dir, null, null, out var isoPath, "deep", null));
        Assert.NotNull(isoPath);

        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256));
        Assert.Contains("maximum directory depth", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Audit walker issue branches
    // ------------------------------------------------------------------

    [Fact]
    public void Audit_RightSelfLoop_ReportsCycle()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        var self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_LeftBeyondEof_ReportsIssue()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Left child offset", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_RightBeyondEof_ReportsIssue()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), 0xFFFE);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Right child offset", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_FileSectorBeyondEof_ReportsIssue()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0x00FFFFFFu);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("exceeds file length", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_ReservedAttributeBits_ReportsIssue()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 12] |= Constants.AttributeReservedMask;
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            static i => i.Contains("Reserved attribute bits", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_SubdirSizeHuge_ReportsIssue()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        var (rootSize, rootAbs) = RootLayout(isoPath);

        var img = File.ReadAllBytes(isoPath);
        var header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("exceeds file length", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_ZeroedTableStart_ReturnsValidEmpty()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));

        var result = XisoReader.AuditXiso(bad);
        Assert.True(result.IsValid, $"unexpected issues: {string.Join("; ", result.Issues)}");
        Assert.Empty(result.Issues);
    }

    // ------------------------------------------------------------------
    // Hash / XEX / misc single-line guards
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeFileHash_UnsupportedAlgorithm_Throws()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Throws<NotSupportedException>(() =>
            XisoReader.ComputeFileHash(isoPath, "/a.txt", new HashAlgorithmName("MD4")));
    }

    [Fact]
    public void ComputeFileHash_TruncatedData_ThrowsIo()
    {
        var isoPath = CreateIso(src =>
        {
            var payload = new byte[20000];
            new Random(7).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(src, "c.bin"), payload);
        }, "game.iso");

        var entry = XisoReader.GetEntryInfo(isoPath, "/c.bin");
        Assert.NotNull(entry);
        var vol = XisoReader.GetVolumeInfo(isoPath);
        var dataEnd = (long)entry.StartSector * Constants.SectorSize + vol.DiscLseek + entry.FileSize;

        var cut = CopyIso(isoPath, "xiso_cov_cut");
        File.WriteAllBytes(cut, File.ReadAllBytes(isoPath)[..(int)(dataEnd - 5000)]);

        var ex = Assert.Throws<IOException>(() =>
            XisoReader.ComputeFileHash(cut, "/c.bin", HashAlgorithmName.SHA256));
        Assert.Contains("Unexpected end", ex.Message, StringComparison.Ordinal);
    }

    private string CreateHeaderCorruptIso()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "prog.txt"),
                "hello world, this is a long-enough non-XEX2 file.\n");
        }, "game.iso");

        var img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset, Constants.HeaderDataLength);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ComputeFileHash_HeaderCorrupt_ThrowsFormat()
    {
        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.ComputeFileHash(CreateHeaderCorruptIso(), "/prog.txt", HashAlgorithmName.SHA256));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyOut_HeaderCorrupt_ThrowsFormat()
    {
        var bad = CreateHeaderCorruptIso();
        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.CopyOut(bad, "/prog.txt", Path.Combine(CreateTempDir("xiso_cov_dest"), "prog.txt")));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetXexInfo_HeaderCorrupt_ThrowsFormat()
    {
        var ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.GetXexInfo(CreateHeaderCorruptIso(), "/prog.txt"));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeDirectoryHashes_HeaderCorrupt_ReturnsEmpty()
    {
        Assert.Empty(XisoReader.ComputeDirectoryHashes(CreateHeaderCorruptIso(), "/", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void GetXexInfo_NonXex_ReturnsNull()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "prog.txt"),
                "hello world, this is a long-enough non-XEX2 file.\n");
        }, "game.iso");
        Assert.Null(XisoReader.GetXexInfo(isoPath, "/prog.txt"));
    }

    [Fact]
    public void GetXexInfo_BogusHeaderCount_ReturnsNull()
    {
        var isoPath = CreateIso(src =>
        {
            var fake = new byte[64];
            "XEX2"u8.ToArray().CopyTo(fake, 0);
            BinaryPrimitives.WriteUInt32BigEndian(fake.AsSpan(0x14), 65u);
            File.WriteAllBytes(Path.Combine(src, "fake.xex"), fake);
        }, "game.iso");
        Assert.Null(XisoReader.GetXexInfo(isoPath, "/fake.xex"));
    }

    [Fact]
    public void UnpackImage_Stream_NegativeSkip_Throws()
    {
        using var stream = new MemoryStream(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.UnpackImage(stream, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void IsOptimizedImage_NonReadableStream_Throws()
    {
        var path = Path.Combine(CreateTempDir("xiso_cov_bad"), "wo.bin");
        File.WriteAllBytes(path, new byte[64]);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Throws<ArgumentException>(() => XisoReader.IsOptimizedImage(stream));
    }

    // ------------------------------------------------------------------
    // Empty (sector 0 / size 0) root: the volume header can name no table.
    // ------------------------------------------------------------------

    private string CreateZeroRootIso()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ZeroRoot_ListDirectory_ReturnsEmpty()
    {
        Assert.Empty(XisoReader.ListDirectory(CreateZeroRootIso(), "/"));
    }

    [Fact]
    public void ZeroRoot_GetSectorLayout_ReturnsHeaderOnly()
    {
        var layout = XisoReader.GetSectorLayout(CreateZeroRootIso());
        Assert.Single(layout.UsedRanges);
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void ZeroRoot_Audit_ReturnsValidEmpty()
    {
        var result = XisoReader.AuditXiso(CreateZeroRootIso());
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ZeroRoot_Rewrite_ThrowsEmpty()
    {
        // Rewrite also goes through the empty-image check first.
        var bad = CreateZeroRootIso();
        Assert.Throws<XisoEmptyException>(() =>
            XisoReader.Rewrite(bad, CreateTempDir("xiso_cov_dest"), out _));
    }

    // ------------------------------------------------------------------
    // Audit optimized-tag branches
    // ------------------------------------------------------------------

    [Fact]
    public void Audit_MissingTag_ReportsIssue()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.OptimizedTagOffset, Constants.OptimizedTagLength);
        var bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        var result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Optimized tag", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Rewrite over a zeroed table (GenerateAvl empty-sentinel path)
    // ------------------------------------------------------------------

    [Fact]
    public void Rewrite_ZeroedTableStart_Succeeds()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Equal(0, XisoReader.Rewrite(bad, CreateTempDir("xiso_cov_dest"), out var rewritten));
        Assert.NotNull(rewritten);
    }

    // NOTE: DecodeXisoCore also guards output creation with UnauthorizedAccess
    // catches (output dir / ISO-name dir). Those need ACL deny rules to
    // trigger (the read-only directory flag does not block creation on
    // Windows), so they stay uncovered by unit tests.
}
