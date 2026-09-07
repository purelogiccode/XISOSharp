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
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedError;
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly bool _savedRemoveSystemUpdate;

    public XisoCoverageTests()
    {
        _savedOut = Logger.Out;
        _savedError = Logger.Error;
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        _savedRemoveSystemUpdate = Logger.RemoveSystemUpdate;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
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

        Logger.Out = _savedOut;
        Logger.Error = _savedError;
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        Logger.RemoveSystemUpdate = _savedRemoveSystemUpdate;
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(Action<string> populate, string isoName)
    {
        string src = CreateTempDir("xiso_cov_src");
        populate(src);
        string dir = CreateTempDir("xiso_cov_iso");
        int result = XisoWriter.CreateXiso(src, dir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    private string CopyIso(string isoPath, string prefix)
    {
        string dest = Path.Combine(CreateTempDir(prefix), Path.GetFileName(isoPath));
        File.Copy(isoPath, dest);
        return dest;
    }

    private static long FindEntryHeader(byte[] img, long tableAbs, uint tableSize, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        long tableEnd = tableAbs + tableSize;
        for (long i = tableAbs; i + 14 + nameBytes.Length <= Math.Min(tableEnd, img.Length); i++)
        {
            bool match = true;
            for (int k = 0; k < nameBytes.Length; k++)
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
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid, $"fixture ISO invalid: {isoPath}");
        return (vol.RootDirSize, ((long)vol.RootDirSector * Constants.SectorSize) + vol.DiscLseek);
    }

    // BUG-TEST-017: NTFS gating is discovery-time via [RequiresNtfsFact/Theory]
    // (TEST-004 pattern) + SkipConditions.IsNtfsTempDrive — no private DriveInfo
    // probe duplicate here.

    // ------------------------------------------------------------------
    // Multi-sector directory tables (sector-boundary advance + llCompat)
    // ------------------------------------------------------------------

    private string CreateManyFileIso(int count = 150) =>
        CreateIso(src =>
        {
            for (int i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(src, $"file{i:000}.txt"), $"content {i}\n");
        }, "many.iso");

    [Fact]
    public void ManyFileDir_Extract_CoversAllFiles()
    {
        string isoPath = CreateManyFileIso();
        string dest = CreateTempDir("xiso_cov_dest");
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        for (int i = 0; i < 150; i++)
            Assert.Equal($"content {i}\n", File.ReadAllText(Path.Combine(dest, $"file{i:000}.txt")));
    }

    [Fact]
    public void ManyFileDir_ExtractLlCompat_CoversAllFiles()
    {
        string isoPath = CreateManyFileIso();
        string dest = CreateTempDir("xiso_cov_dest");
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, true));
        for (int i = 0; i < 150; i++)
            Assert.True(File.Exists(Path.Combine(dest, $"file{i:000}.txt")), $"file{i:000}.txt missing");
    }

    [Fact]
    public void ManyFileDir_ListDirectory_CoversAllFiles()
    {
        string isoPath = CreateManyFileIso();
        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath, "/");
        Assert.Equal(150, entries.Count);
    }

    // ------------------------------------------------------------------
    // Zeroed-table shapes (empty sentinels at start vs mid-table)
    // ------------------------------------------------------------------

    private static string ZeroTableStart(string isoPath, Func<string, string> copy)
    {
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        long rootAbs = ((long)vol.RootDirSector * Constants.SectorSize) + vol.DiscLseek;
        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, (int)rootAbs, 14);
        string bad = copy(isoPath);
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void List_ZeroedTableStart_ReturnsZero()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Equal(0, XisoReader.List(bad, false));
    }

    [Fact]
    public void ListDirectory_ZeroedTableStart_ReturnsEmpty()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Empty(XisoReader.ListDirectory(bad, "/"));
    }

    [Fact]
    public void GetSectorLayout_ZeroedTableStart_HasOnlyRootTable()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        SectorLayout layout = XisoReader.GetSectorLayout(bad);
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void Extract_ZeroedNonFirstEntry_ThrowsOutsideTable()
    {
        // Zeroing a reachable non-first header turns it into an empty-table
        // sentinel mid-walk: the sector-advance lands past the one-sector
        // table, tripping the entry-outside-table guard.
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
            File.WriteAllText(Path.Combine(src, "c.txt"), "!");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "c.txt");
        Array.Clear(img, (int)header, 14);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest")));
        // Either the pad-rounding guard or the entry-position guard must name
        // the outside-table failure (both are "invalid TOC entry" errors).
        Assert.Contains("outside the directory table", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Sibling/child pointer guards via the traverse path
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_LeftChildOutsideTable_ThrowsNamed()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), (ushort)(rootSize / 4));
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest")));
        Assert.Contains("left child offset", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside the directory table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContinueOnError_CorruptRoot_RecordsSummary()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), (ushort)(rootSize / 4));
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_cov_dest"), options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("Failed to unpack image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinueOnError_CorruptLeftSubtree_SkipsAndContinues()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
            File.WriteAllText(Path.Combine(src, "c.txt"), "!");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long headerA = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        // a.left -> a itself (valid, in-table): the revisit throws inside the
        // recursive call, hitting the left-subtree continue-on-error catch
        // while the entry itself and all siblings still extract.
        ushort selfA = (ushort)((headerA - rootAbs) / 4);
        Assert.NotEqual(0, selfA);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)headerA), selfA);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_cov_dest");
        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
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
        using MemoryStream stream = new(new byte[1024]);
        int rc = XisoReader.DecodeXiso(stream, "somedir\\", CreateTempDir("xiso_cov_dest"),
            ExtractMode.List, out string? outPath, false);
        Assert.Equal(1, rc);
        Assert.Null(outPath);
    }

    [Fact]
    public void Extract_OutputPathIsFile_ThrowsIOException()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string blocker = Path.Combine(CreateTempDir("xiso_cov_dest"), "blocker");
        File.WriteAllText(blocker, "in the way");

        IOException ex = Assert.Throws<IOException>(() => XisoReader.Extract(isoPath, blocker, false));
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_IsoNameBlockedByFile_ThrowsIOException()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "other.iso");
        string runDir = CreateTempDir("xiso_cov_rundir");
        File.WriteAllText(Path.Combine(runDir, "other"), "in the way");

        string savedCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(runDir);
        try
        {
            IOException ex = Assert.Throws<IOException>(() => XisoReader.Extract(isoPath, null, false));
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
        string isoPath = CreateIso(src =>
        {
            string updateDir = Path.Combine(src, "$SystemUpdate");
            Directory.CreateDirectory(updateDir);
            File.WriteAllText(Path.Combine(updateDir, "update.bin"), "update data");
            File.WriteAllText(Path.Combine(src, "game.txt"), "game data");
        }, "game.iso");

        // Flag off at create time (update IS in the image), on at extract time.
        Logger.RemoveSystemUpdate = true;
        try
        {
            string dest = CreateTempDir("xiso_cov_dest");
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

    private string CreateIsoBytes(Action<string> populate, string isoName) => CreateIso(populate, isoName);

    [Fact]
    public void VerifyXiso_Device_SkipZeroValid()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        using MemoryBlockDevice dev = new(File.ReadAllBytes(isoPath));
        (uint sector, uint size, long lseek) = XisoReader.VerifyXiso(dev, "game.iso", skipSectors: 0);
        Assert.True(sector > 0);
        Assert.True(size > 0);
        Assert.Equal(0, lseek);
    }

    [Fact]
    public void VerifyXiso_Device_NegativeSkip_Throws()
    {
        using MemoryBlockDevice dev = new(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void VerifyXiso_Device_SkipGarbage_ThrowsNoHeader()
    {
        using MemoryBlockDevice dev = new(new byte[Constants.HeaderOffset + 1024]);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: 0));
        Assert.Contains("no header at sector 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyXiso_Device_SkipTiny_ThrowsReadHeader()
    {
        using MemoryBlockDevice dev = new(new byte[10]);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "x.iso", skipSectors: 0));
    }

    [Fact]
    public void VerifyXiso_Device_Garbage_ThrowsInvalid()
    {
        using MemoryBlockDevice dev = new(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "x.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeRootSector_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength)];
        using MemoryBlockDevice dev = new(bytes);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeRootSize_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 4)];
        using MemoryBlockDevice dev = new(bytes);
        Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_CutBeforeTail_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 8)];
        using MemoryBlockDevice dev = new(bytes);
        IOException ex = Assert.Throws<IOException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("trailing magic", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyXiso_Device_TailCorrupt_ThrowsCorrupt()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath);
        Array.Clear(bytes,
            Constants.HeaderOffset + Constants.HeaderDataLength + 4 + 4 + Constants.FileTimeSize +
            Constants.UnusedSize, Constants.HeaderDataLength);
        using MemoryBlockDevice dev = new(bytes);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("Corrupt XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_LargeTable_PadOffsetsDoNotWrap()
    {
        // BUG-LIB-028: past ~64K DWORDs of table, sector-pad rounding used to
        // wrap through (ushort) and mis-walk valid images. A 3500-file table
        // forces pads beyond that range: it must pack, audit, and extract whole.
        const int fileCount = 3500;
        string isoPath = CreateIso(src =>
        {
            for (int i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(src, $"f{i:0000}.txt"), "x");
        }, "big.iso");

        Assert.True(XisoReader.AuditXiso(isoPath).IsValid);

        string dest = CreateTempDir("xiso_cov_bigdest");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));
        Assert.Equal(fileCount, Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void VerifyXiso_Device_EmptyRoot_ThrowsEmpty()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath);
        Array.Clear(bytes, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        using MemoryBlockDevice dev = new(bytes);
        Assert.Throws<XisoEmptyException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_Device_RootBeyondEnd_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(Constants.HeaderOffset + Constants.HeaderDataLength), 0x00FFFFFFu);
        using MemoryBlockDevice dev = new(bytes);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "game.iso"));
        Assert.Contains("beyond end", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditXiso_Device_Valid_ReturnsValid()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        using MemoryBlockDevice dev = new(File.ReadAllBytes(isoPath));
        AuditResult result = XisoReader.AuditXiso(dev, "game.iso");
        Assert.True(result.IsValid);
        // Deep walk, not header-only (BUG-LIB-012): the file must be counted.
        Assert.True(result.FilesChecked > 0);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AuditXiso_Device_CorruptTree_ReturnsInvalid()
    {
        // Same valid image, but the root entry name gets a path separator:
        // header probes pass, so only a real tree walk can fail this.
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath);
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(Constants.HeaderOffset + Constants.HeaderDataLength));
        long rootAbs = (long)rootSector * Constants.SectorSize;
        bytes[rootAbs + 14] = (byte)'/';
        using MemoryBlockDevice dev = new(bytes);
        AuditResult result = XisoReader.AuditXiso(dev, "game.iso");
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void AuditXiso_Device_Garbage_ReturnsInvalid()
    {
        using MemoryBlockDevice dev = new(new byte[Constants.HeaderOffset + 1024]);
        AuditResult result = XisoReader.AuditXiso(dev, "x.iso");
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void GetFileTime_Device_ReturnsStamp()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        DateTimeOffset expected = XisoReader.GetFileTime(isoPath);
        using MemoryBlockDevice dev = new(File.ReadAllBytes(isoPath));
        Assert.Equal(expected, XisoReader.GetFileTime(dev, "game.iso"));
    }

    [Fact]
    public void GetFileTimeRaw_Device_ShortFileTime_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] bytes = File.ReadAllBytes(isoPath)[..(Constants.HeaderOffset + Constants.HeaderDataLength + 8)];
        using MemoryBlockDevice dev = new(bytes);
        Assert.Throws<IOException>(() => XisoReader.GetFileTimeRaw(dev, "game.iso"));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipNegative_Throws()
    {
        using MemoryBlockDevice dev = new(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.GetFileTimeRaw(dev, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipZero_Valid()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        ulong expected = XisoReader.GetFileTimeRaw(isoPath);
        using MemoryBlockDevice dev = new(File.ReadAllBytes(isoPath));
        Assert.Equal(expected, XisoReader.GetFileTimeRaw(dev, "game.iso", skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_Device_SkipBadMagic_Throws()
    {
        using MemoryBlockDevice dev = new(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(dev, "x.iso", skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_Device_Garbage_ThrowsInvalid()
    {
        using MemoryBlockDevice dev = new(new byte[Constants.HeaderOffset + 1024]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(dev, "x.iso"));
    }

    // ------------------------------------------------------------------
    // FILETIME stream-probe gaps
    // ------------------------------------------------------------------

    [Fact]
    public void GetFileTimeRaw_NegativeSkip_Throws()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.GetFileTimeRaw(isoPath, skipSectors: -1));
    }

    [Fact]
    public void GetFileTimeRaw_SkipZero_ReturnsStamp()
    {
        string isoPath = CreateIsoBytes(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Equal(XisoReader.GetFileTimeRaw(isoPath), XisoReader.GetFileTimeRaw(isoPath, skipSectors: 0));
    }

    [Fact]
    public void GetFileTimeRaw_SkipBadMagic_Throws()
    {
        string path = Path.Combine(CreateTempDir("xiso_cov_bad"), "bad.iso");
        File.WriteAllBytes(path, new byte[Constants.HeaderOffset + 1024]);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(path, skipSectors: 0));
        Assert.Contains("no header at sector 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileTimeRaw_TruncatedProbes_ThrowsInvalid()
    {
        string path = Path.Combine(CreateTempDir("xiso_cov_bad"), "short.iso");
        File.WriteAllBytes(path, new byte[Constants.HeaderOffset + 10]);
        Assert.Throws<XisoFormatException>(() => XisoReader.GetFileTimeRaw(path));
    }

    // ------------------------------------------------------------------
    // GetVolumeInfo disc-layout probe ladder (sparse files, NTFS only:
    // SetLength is metadata-only there, reads are tiny 20-byte probes)
    // ------------------------------------------------------------------

    [RequiresNtfsTheory]
    [InlineData(0x0FD90000L)]
    [InlineData(0x02080000L)]
    [InlineData(0x89D80000L)]
    [InlineData(0x18300000L)]
    public void GetVolumeInfo_DiscOffsetMagic_Detected(long discOffset)
    {
        // RequiresNtfsTheory already skipped non-NTFS at discovery (BUG-TEST-017):
        // SetLength stays metadata-only sparse here; no runtime probe needed.
        string dir = CreateTempDir("xiso_cov_bad");

        string path = Path.Combine(dir, "offset.iso");
        // Every earlier probe must read (zeros) rather than throw, so all
        // files are sized past the largest probe (Xgd2Hybrid).
        const long minLength = 0x89D80000L + Constants.HeaderOffset + 1024;
        using (FileStream fs = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(minLength);
            fs.Seek(discOffset + Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));
        }

        VolumeInfo vol = XisoReader.GetVolumeInfo(path);
        Assert.True(vol.IsValid);
        Assert.Equal(discOffset, vol.DiscLseek);
    }

    [Fact]
    public void GetVolumeInfo_BigGarbage_ReturnsNotValid()
    {
        string path = Path.Combine(CreateTempDir("xiso_cov_bad"), "garbage.iso");
        byte[] data = new byte[Constants.HeaderOffset + 1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);

        Assert.False(XisoReader.GetVolumeInfo(path).IsValid);
    }

    [RequiresNtfsFact]
    public void GetVolumeInfo_BigZeros_ReturnsNotValid()
    {
        // All probes read (zeros) but none match: the no-match return, not
        // the short-read catch. Sparse file (NTFS only), reads are tiny.
        // RequiresNtfsFact already skipped non-NTFS at discovery (BUG-TEST-017).
        string dir = CreateTempDir("xiso_cov_bad");

        string path = Path.Combine(dir, "zeros.iso");
        using (FileStream fs = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
        string src = CreateTempDir("xiso_cov_empty");
        string dir = CreateTempDir("xiso_cov_iso");
        int result = XisoWriter.CreateXiso(src, dir, null, null, out string? created, "empty", null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    [Fact]
    public void EmptyRoot_ListDirectory_ReturnsEmpty() => Assert.Empty(XisoReader.ListDirectory(CreateEmptyIso(), "/"));

    [Fact]
    public void EmptyRoot_GetSectorLayout_ReturnsHeaderOnly()
    {
        SectorLayout layout = XisoReader.GetSectorLayout(CreateEmptyIso());
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void EmptyRoot_Audit_ReturnsValid()
    {
        AuditResult result = XisoReader.AuditXiso(CreateEmptyIso());
        Assert.True(result.IsValid);
    }

    // ------------------------------------------------------------------
    // ListDirectory / GetSectorLayout / GetEntryInfo walker gaps
    // ------------------------------------------------------------------

    [Fact]
    public void GetEntryInfo_SlashesOnly_ReturnsNull()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Null(XisoReader.GetEntryInfo(isoPath, "///"));
    }

    [Fact]
    public void ListDirectory_LeftBeyondImage_Throws()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(bad, "/"));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSectorLayout_LeftBeyondImage_Throws()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSectorLayout_LeftSelfCycle_Throws()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), self);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateDotEntryIso()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 13] = 1;
        img[header + 14] = (byte)'.';
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ListDirectory_DotEntry_SkippedButSiblingsListed()
    {
        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(CreateDotEntryIso(), "/");
        Assert.DoesNotContain(entries, static e => string.Equals(e.Name, ".", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, static e => string.Equals(e.Name, "b.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectorLayout_DotEntry_Skipped()
    {
        SectorLayout layout = XisoReader.GetSectorLayout(CreateDotEntryIso());
        Assert.DoesNotContain(layout.Entries,
            static e => string.Equals(e.Path, "/.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(layout.Entries,
            static e => string.Equals(e.Path, "/b.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSectorLayout_SubSizeZero_SkipsTable()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0u);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        SectorLayout layout = XisoReader.GetSectorLayout(bad);
        Assert.Contains(layout.Entries,
            static e => string.Equals(e.Path, "/sub", StringComparison.OrdinalIgnoreCase) && e.FileSize == 0);
    }

    [Fact]
    public void GetSectorLayout_EmptyFile_MergesRanges()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "empty.txt"), "");
            File.WriteAllText(Path.Combine(src, "full.txt"), "data");
        }, "game.iso");

        SectorLayout layout = XisoReader.GetSectorLayout(isoPath);
        Assert.Contains(layout.Entries,
            static e => string.Equals(e.Path, "/empty.txt", StringComparison.OrdinalIgnoreCase) && e.SectorCount == 0);
    }

    [Fact]
    public void GetSectorLayout_SubSizeHuge_Throws()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    private static string CreateDeepTree(string baseDir, int depth)
    {
        string dir = baseDir;
        for (int i = 0; i < depth; i++)
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
        string src = CreateDeepTree(CreateTempDir("xiso_cov_deep"), 70);
        string dir = CreateTempDir("xiso_cov_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, dir, null, null, out string? isoPath, "deep", null));
        Assert.NotNull(isoPath);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(isoPath));
        Assert.Contains("maximum directory depth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeDirectoryHashes_DeepTree_ThrowsDepth()
    {
        string src = CreateDeepTree(CreateTempDir("xiso_cov_deep"), 70);
        string dir = CreateTempDir("xiso_cov_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, dir, null, null, out string? isoPath, "deep", null));
        Assert.NotNull(isoPath);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256));
        Assert.Contains("maximum directory depth", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Audit walker issue branches
    // ------------------------------------------------------------------

    [Fact]
    public void Audit_RightSelfLoop_ReportsCycle()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_LeftBeyondEof_ReportsIssue()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), 0xFFFE);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Left child offset", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_RightBeyondEof_ReportsIssue()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), 0xFFFE);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Right child offset", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_FileSectorBeyondEof_ReportsIssue()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0x00FFFFFFu);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("exceeds file length", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_ReservedAttributeBits_ReportsIssue()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 12] |= Constants.AttributeReservedMask;
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues,
            static i => i.Contains("Reserved attribute bits", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_SubdirSizeHuge_ReportsIssue()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("exceeds file length", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_ZeroedTableStart_ReturnsValidEmpty()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.True(result.IsValid, $"unexpected issues: {string.Join("; ", result.Issues)}");
        Assert.Empty(result.Issues);
    }

    // ------------------------------------------------------------------
    // Hash / XEX / misc single-line guards
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeFileHash_UnsupportedAlgorithm_Throws()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        Assert.Throws<NotSupportedException>(() =>
            XisoReader.ComputeFileHash(isoPath, "/a.txt", new HashAlgorithmName("MD4")));
    }

    [Fact]
    public void ComputeFileHash_TruncatedData_ThrowsIo()
    {
        string isoPath = CreateIso(src =>
        {
            byte[] payload = new byte[20000];
            new Random(7).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(src, "c.bin"), payload);
        }, "game.iso");

        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/c.bin");
        Assert.NotNull(entry);
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        long dataEnd = ((long)entry.StartSector * Constants.SectorSize) + vol.DiscLseek + entry.FileSize;

        string cut = CopyIso(isoPath, "xiso_cov_cut");
        File.WriteAllBytes(cut, File.ReadAllBytes(isoPath)[..(int)(dataEnd - 5000)]);

        IOException ex = Assert.Throws<IOException>(() =>
            XisoReader.ComputeFileHash(cut, "/c.bin", HashAlgorithmName.SHA256));
        Assert.Contains("Unexpected end", ex.Message, StringComparison.Ordinal);
    }

    private string CreateHeaderCorruptIso()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "prog.txt"),
                "hello world, this is a long-enough non-XEX2 file.\n");
        }, "game.iso");

        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset, Constants.HeaderDataLength);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ComputeFileHash_HeaderCorrupt_ThrowsFormat()
    {
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.ComputeFileHash(CreateHeaderCorruptIso(), "/prog.txt", HashAlgorithmName.SHA256));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyOut_HeaderCorrupt_ThrowsFormat()
    {
        string bad = CreateHeaderCorruptIso();
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.CopyOut(bad, "/prog.txt", Path.Combine(CreateTempDir("xiso_cov_dest"), "prog.txt")));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetXexInfo_HeaderCorrupt_ThrowsFormat()
    {
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.GetXexInfo(CreateHeaderCorruptIso(), "/prog.txt"));
        Assert.Contains("Not a valid XISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeDirectoryHashes_HeaderCorrupt_ReturnsEmpty() => Assert.Empty(XisoReader.ComputeDirectoryHashes(CreateHeaderCorruptIso(), "/", HashAlgorithmName.SHA256));

    [Fact]
    public void GetXexInfo_NonXex_ReturnsNull()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "prog.txt"),
                "hello world, this is a long-enough non-XEX2 file.\n");
        }, "game.iso");
        Assert.Null(XisoReader.GetXexInfo(isoPath, "/prog.txt"));
    }

    [Fact]
    public void GetXexInfo_BogusHeaderCount_ReturnsNull()
    {
        string isoPath = CreateIso(src =>
        {
            byte[] fake = new byte[64];
            "XEX2"u8.ToArray().CopyTo(fake, 0);
            BinaryPrimitives.WriteUInt32BigEndian(fake.AsSpan(0x14), 65u);
            File.WriteAllBytes(Path.Combine(src, "fake.xex"), fake);
        }, "game.iso");
        Assert.Null(XisoReader.GetXexInfo(isoPath, "/fake.xex"));
    }

    [Fact]
    public void UnpackImage_Stream_NegativeSkip_Throws()
    {
        using MemoryStream stream = new(new byte[1024]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.UnpackImage(stream, "x.iso", skipSectors: -1));
    }

    [Fact]
    public void IsOptimizedImage_NonReadableStream_Throws()
    {
        string path = Path.Combine(CreateTempDir("xiso_cov_bad"), "wo.bin");
        File.WriteAllBytes(path, new byte[64]);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Throws<ArgumentException>(() => XisoReader.IsOptimizedImage(stream));
    }

    // ------------------------------------------------------------------
    // Empty (sector 0 / size 0) root: the volume header can name no table.
    // ------------------------------------------------------------------

    private string CreateZeroRootIso()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);
        return bad;
    }

    [Fact]
    public void ZeroRoot_ListDirectory_ReturnsEmpty() => Assert.Empty(XisoReader.ListDirectory(CreateZeroRootIso(), "/"));

    [Fact]
    public void ZeroRoot_GetSectorLayout_ReturnsHeaderOnly()
    {
        SectorLayout layout = XisoReader.GetSectorLayout(CreateZeroRootIso());
        Assert.Single(layout.UsedRanges);
        Assert.DoesNotContain(layout.Entries, static e => !e.IsDirectory);
    }

    [Fact]
    public void ZeroRoot_Audit_ReturnsValidEmpty()
    {
        AuditResult result = XisoReader.AuditXiso(CreateZeroRootIso());
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ZeroRoot_Rewrite_ThrowsEmpty()
    {
        // Rewrite also goes through the empty-image check first.
        string bad = CreateZeroRootIso();
        Assert.Throws<XisoEmptyException>(() =>
            XisoReader.Rewrite(bad, CreateTempDir("xiso_cov_dest"), out _));
    }

    // ------------------------------------------------------------------
    // Audit optimized-tag branches
    // ------------------------------------------------------------------

    [Fact]
    public void Audit_MissingTag_ReportsIssue()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.OptimizedTagOffset, Constants.OptimizedTagLength);
        string bad = CopyIso(isoPath, "xiso_cov_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("Optimized tag", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Rewrite over a zeroed table (GenerateAvl empty-sentinel path)
    // ------------------------------------------------------------------

    [Fact]
    public void Rewrite_ZeroedTableStart_Succeeds()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string bad = ZeroTableStart(isoPath, p => CopyIso(p, "xiso_cov_bad"));
        Assert.Equal(0, XisoReader.Rewrite(bad, CreateTempDir("xiso_cov_dest"), out string? rewritten));
        Assert.NotNull(rewritten);
    }

    // NOTE: DecodeXisoCore also guards output creation with UnauthorizedAccess
    // catches (output dir / ISO-name dir). Those need ACL deny rules to
    // trigger (the read-only directory flag does not block creation on
    // Windows), so they stay uncovered by unit tests.
}
