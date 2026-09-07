using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for TODO #16 (corrupt-TOC traversal hardening, extract-xiso #25):
/// fuzzed directory tables — cycles, child offsets outside the table/image,
/// absurd entry counts, subdirectory cycles — fail fast with a named
/// <c>invalid TOC entry</c> error (the xdvdmulleter shape) instead of hanging
/// then OOMing, and <see cref="UnpackOptions.ContinueOnError"/> skips the bad
/// subtree while the rest still extracts. Every test is bounded: without the
/// fix, each fixture loops or recurses without end.
/// </summary>
[Collection("Sequential")]
public class CorruptTocTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly string _savedCwd;
    private readonly string _runDir;

    public CorruptTocTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;

        _savedCwd = Directory.GetCurrentDirectory();
        _runDir = CreateTempDir("xiso_toc_rundir");
        Directory.SetCurrentDirectory(_runDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_savedCwd);
        }
        catch
        {
            // ignored
        }

        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedOut;
        Logger.Error = _savedErr;
        _outCapture.Dispose();
        _errCapture.Dispose();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                // best effort cleanup
            }
        }
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
        string src = CreateTempDir("xiso_toc_src");
        populate(src);
        string dir = CreateTempDir("xiso_toc_iso");
        int result = XisoWriter.CreateXiso(src, dir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.NotNull(created);

        // Sanity: the fixture starts valid.
        Assert.Equal(0, XisoReader.List(created, llCompat: false));
        return created;
    }

    private string CopyIso(string isoPath, string prefix)
    {
        string dest = Path.Combine(CreateTempDir(prefix), Path.GetFileName(isoPath));
        File.Copy(isoPath, dest);
        return dest;
    }

    /// <summary>
    /// Locates a directory entry header inside a table by its filename.
    /// Returns the absolute byte offset of the 14-byte header.
    /// </summary>
    private static long FindEntryHeader(byte[] img, long tableAbs, uint tableSize, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        long tableEnd = tableAbs + tableSize;
        Assert.True(tableEnd <= img.Length, "table runs past end of image; fixture layout unexpected");

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

    private static (uint RootSector, uint RootSize, long RootAbs, long DiscLseek) RootLayout(string isoPath)
    {
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid, $"fixture ISO invalid: {isoPath}");
        long rootAbs = ((long)vol.RootDirSector * Constants.SectorSize) + vol.DiscLseek;
        return (vol.RootDirSector, vol.RootDirSize, rootAbs, vol.DiscLseek);
    }

    // ------------------------------------------------------------------
    // Single-table cycles
    // ------------------------------------------------------------------

    [Fact]
    public void RightSelfLoop_ListDirectory_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);
        Assert.Equal(2, XisoReader.ListDirectory(isoPath, "/").Count);

        // Patch b.txt's right-sibling pointer to itself: the old walk followed
        // it forever (pushing the same offset, growing the result list to OOM).
        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        Assert.Equal(0, (header - rootAbs) % 4);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(bad, "/"));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RightSelfLoop_Unpack_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_toc_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeftSelfLoop_GetXisoRanges_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);

        // Patch b.txt's left-child pointer to itself: the old recursive range
        // walk descended forever and overflowed the stack.
        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header), self);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoRanges.GetXisoRanges(bad, 0, true));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DagFanout_ListDirectory_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);

        // Point a.txt's left AND right at b.txt: the old stack walk visited the
        // shared child twice per level (exponential fan-out without a revisit
        // check); the bounded walk rejects the second visit as a cycle.
        byte[] img = File.ReadAllBytes(isoPath);
        long headerA = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        long headerB = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort target = (ushort)((headerB - rootAbs) / 4);
        Assert.Equal(0, (headerB - rootAbs) % 4);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)headerA), target);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)headerA + 2), target);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(bad, "/"));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Out-of-table / out-of-image offsets
    // ------------------------------------------------------------------

    [Fact]
    public void ChildOffsetOutsideTable_Unpack_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);
        long fileLength = new FileInfo(isoPath).Length;
        Assert.Equal(0u, rootSize % 4);

        // Point b.txt's right sibling exactly at the table end: inside the
        // image (file data follows) but outside the table. The old walk read
        // whatever followed as entries.
        ushort pastEnd = (ushort)(rootSize / 4);
        Assert.True(rootAbs + rootSize < fileLength, "fixture has no file data after the root table");
        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), pastEnd);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_toc_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside the directory table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChildOffsetOutsideImage_Unpack_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);

        // 0xFFFE dwords past the table start: far beyond both the table and
        // this small image. The old walk warned and silently truncated.
        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), 0xFFFE);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_toc_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("65534", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubdirSizeHuge_Unpack_ThrowsInvalidToc()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        (_, uint rootSize, long rootAbs, _) = RootLayout(isoPath);

        // Inflate sub's reported table size past the end of the image.
        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "sub");
        Assert.Equal(Constants.AttributeDir, img[header + 12] & Constants.AttributeDir);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00);
        string bad = CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_toc_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exceeds image length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Subdirectory cycle (Burnout PAL shape)
    // ------------------------------------------------------------------

    private static string CreateSubdirCycleIso(
        CorruptTocTests self, string isoName, out string subName)
    {
        const string sub = "sub";
        const string inner = "inner.txt";
        string isoPath = self.CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, sub));
            File.WriteAllText(Path.Combine(src, sub, inner), "inner");
        }, isoName);

        (uint rootSector, uint rootSize, long rootAbs, long discLseek) = RootLayout(isoPath);
        byte[] img = File.ReadAllBytes(isoPath);

        // Resolve sub's table, then repoint inner.txt at the ROOT table as a
        // directory: root -> sub -> inner.txt(-> root) -> sub -> ... forever.
        long subHeader = FindEntryHeader(img, rootAbs, rootSize, sub);
        uint subSector = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)subHeader + 4));
        uint subSize = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)subHeader + 8));
        long subAbs = ((long)subSector * Constants.SectorSize) + discLseek;

        long innerHeader = FindEntryHeader(img, subAbs, subSize, inner);
        img[innerHeader + 12] |= Constants.AttributeDir;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)innerHeader + 4), rootSector);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)innerHeader + 8), rootSize);

        string bad = self.CopyIso(isoPath, "xiso_toc_bad");
        File.WriteAllBytes(bad, img);
        subName = sub;
        return bad;
    }

    [Fact]
    public void SubdirCycle_Unpack_ThrowsInvalidToc()
    {
        string bad = CreateSubdirCycleIso(this, "game.iso", out _);

        string dest = CreateTempDir("xiso_toc_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubdirCycle_ContinueOnError_SkipsSubtreeAndSummarizes()
    {
        string bad = CreateSubdirCycleIso(this, "game.iso", out _);

        string dest = CreateTempDir("xiso_toc_dest");
        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(bad, dest, options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("Failed to unpack image", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The healthy entry outside the corrupt subtree still extracts.
        Assert.Equal("top", File.ReadAllText(Path.Combine(dest, "top.txt")));
    }

    [Fact]
    public void SubdirCycle_Audit_ReportsInvalidToc()
    {
        string bad = CreateSubdirCycleIso(this, "game.iso", out _);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.True(
            result.Issues.Any(i => i.Contains("invalid TOC entry", StringComparison.OrdinalIgnoreCase)),
            "audit issues: " + string.Join("; ", result.Issues));
    }

    [Fact]
    public void SubdirCycle_CopyOut_ThrowsInvalidToc()
    {
        string bad = CreateSubdirCycleIso(this, "game.iso", out string subName);

        string dest = Path.Combine(CreateTempDir("xiso_toc_dest"), "out");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.CopyOut(bad, "/" + subName, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Absurd entry count: a huge but well-formed chain stays bounded
    // ------------------------------------------------------------------

    [Fact]
    public void HugeValidChain_ListDirectory_CompletesBounded()
    {
        // Offsets are 16-bit dwords and entries are dword-aligned, so 16383
        // 16-byte entries is the longest chain addressable in one table; any
        // walk attempting more visits necessarily revisits an offset and fails
        // as a cycle above. This test proves the near-worst-case valid walk
        // terminates with exact results instead of hanging or OOMing.
        const int entryCount = 16000;
        string dir = CreateTempDir("xiso_toc_huge");
        string isoPath = Path.Combine(dir, "huge.iso");

        // Hand-crafted image: valid header + one root table holding a long
        // right-sibling chain of empty files. Offsets are 16-bit dwords, so at
        // most 65536 distinct entries can ever exist per table — the walk must
        // terminate; without bounds it would not.
        const uint rootSector = 40;
        const uint rootSize = (uint)(entryCount * 16);
        const long rootAbs = (long)rootSector * Constants.SectorSize;
        using (FileStream fs = new(isoPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(rootAbs + rootSize);
            fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, rootSector);
            fs.Write(u32);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, rootSize);
            fs.Write(u32);
            fs.Write(new byte[Constants.FileTimeSize + Constants.UnusedSize]);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));

            fs.Seek(rootAbs, SeekOrigin.Begin);
            byte[] entryBuf = new byte[16];
            for (int i = 0; i < entryCount; i++)
            {
                // Entry i lives at byte offset i*16, i.e. dword i*4; the chain
                // links each entry to the next dword (last entry terminates).
                Span<byte> entry = entryBuf.AsSpan();
                entry.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(entry, 0); // left: none
                BinaryPrimitives.WriteUInt16LittleEndian(entry[2..],
                    (ushort)(i + 1 < entryCount ? (i + 1) * 4 : 0));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 0); // sector
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 0); // size
                entry[12] = Constants.AttributeArc;
                entry[13] = 1;
                entry[14] = (byte)('a' + (i % 26));
                entry[15] = 0; // dword-align pad
                fs.Write(entry);
            }
        }

        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath, "/");
        Assert.Equal(entryCount, entries.Count);
    }
}
