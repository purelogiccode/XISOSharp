using System.Buffers.Binary;
using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoRanges"/> — filesystem extent discovery, range merging,
/// file-entry enumeration and sector validation.
/// </summary>
[Collection("Sequential")]
public class XisoRangesTests : IDisposable
{
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
                /* best effort */
            }

            if (File.Exists(dir))
            {
                try
                {
                    File.Delete(dir);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_rng_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        string src = Path.Combine(Path.GetTempPath(), $"xiso_rng_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        string outDir = CreateTempDir();
        int result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out string? isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static void PopulateSimple(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "file2.txt"), new string('A', 5000));
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested");
        byte[] bin = new byte[7000];
        new Random(42).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(dir, "subdir", "data.bin"), bin);
    }

    [Fact]
    public void GetXisoRanges_SysRanges_NonEmpty()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);

        (List<(uint Start, uint End)> sys, _) = XisoRanges.GetXisoRanges(iso);

        Assert.NotEmpty(sys);
        // Sys ranges should be sorted and non-overlapping
        for (int i = 1; i < sys.Count; i++)
        {
            Assert.True(sys[i].Start > sys[i - 1].End,
                $"Sys ranges should be sorted and non-overlapping: {sys[i - 1]} then {sys[i]}");
        }

        // First sys range should include header sector (HeaderOffset / SectorSize = 32)
        Assert.Contains(sys,
            r => r.Start <= Constants.HeaderOffset / Constants.SectorSize &&
                 r.End >= Constants.HeaderOffset / Constants.SectorSize);
    }

    [Fact]
    public void GetXisoRanges_FilesRanges_NonEmptyWhenFilesExist()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);

        (_, List<(uint Start, uint End)> files) = XisoRanges.GetXisoRanges(iso);

        Assert.NotEmpty(files);
        Assert.All(files, r => Assert.True(r.End >= r.Start));
    }

    [Fact]
    public void GetXisoRanges_EmptyDirectory_HasSysRangesAndEmptyOrMinimalFiles()
    {
        string src = CreateSourceDir(_ => { });
        string iso = CreateIso(src);

        (List<(uint Start, uint End)> sys, List<(uint Start, uint End)> files) = XisoRanges.GetXisoRanges(iso);

        Assert.NotEmpty(sys);
        // Empty source yields no file extents (or at most zero)
        Assert.Empty(files);
    }

    [Fact]
    public void GetXisoRanges_StringOverloadMatchesStreamOverload()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);

        (List<(uint Start, uint End)> sysPath, List<(uint Start, uint End)> filesPath) = XisoRanges.GetXisoRanges(iso);

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        (List<(uint Start, uint End)> sysStream, List<(uint Start, uint End)> filesStream) =
            XisoRanges.GetXisoRanges(fs, 0, true);

        Assert.Equal(sysPath, sysStream);
        Assert.Equal(filesPath, filesStream);
    }

    [Fact]
    public void GetXisoRanges_WithOffsetParam_WorksForPrependedIso()
    {
        string src = CreateSourceDir(PopulateSimple);
        string normalIso = CreateIso(src);
        string prependedIso = CreateIso(src, prependSectors: 64);

        (_, List<(uint Start, uint End)> filesNormal) =
            XisoRanges.GetXisoRanges(normalIso);
        // For prepended, we must pass the byte offset of the XISO partition
        const long offset = 64L * Constants.SectorSize;
        (List<(uint Start, uint End)> sysPrepend, List<(uint Start, uint End)> filesPrepend) =
            XisoRanges.GetXisoRanges(prependedIso, offset, true);

        // The logical file ranges relative to partition should match; but sys ranges are offset-dependent.
        // At least both should be non-empty and file counts should match.
        Assert.NotEmpty(sysPrepend);
        Assert.NotEmpty(filesPrepend);
        Assert.Equal(filesNormal.Count, filesPrepend.Count);
        // Files ranges for prepended are shifted; but count and structure should be equivalent
        // Verify file entries count via GetFileEntries also matches
    }

    [Fact]
    public void MergeRanges_Overlapping_CoalescesIntoSingle()
    {
        List<(uint Start, uint End)> a = new() { (1, 5) };
        List<(uint Start, uint End)> b = new() { (3, 10) };

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        List<(uint Start, uint End)> expected = new() { (1, 10) };
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void MergeRanges_Disjoint_KeepsSeparate()
    {
        List<(uint Start, uint End)> a = new() { (1, 5) };
        List<(uint Start, uint End)> b = new() { (10, 15) };

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        List<(uint Start, uint End)> expected = new() { (1, 5), (10, 15) };
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void MergeRanges_Adjacent_MergesBecauseEndPlusOne()
    {
        List<(uint Start, uint End)> a = new() { (1, 5) };
        List<(uint Start, uint End)> b = new() { (6, 10) };

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        // Adjacent ranges where start == last.End + 1 are coalesced
        List<(uint Start, uint End)> expected = new() { (1, 10) };
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void MergeRanges_TopOfSpace_MergesOverlapping()
    {
        // BUG-LIB-030: last.End + 1 wraps to 0 at uint.MaxValue, so an
        // overlapping tail compared false and was never merged.
        List<(uint Start, uint End)> a = new() { (0, uint.MaxValue) };
        List<(uint Start, uint End)> b = new() { (uint.MaxValue, uint.MaxValue) };

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        Assert.Equal(new List<(uint Start, uint End)> { (0, uint.MaxValue) }, merged);
    }

    [Fact]
    public void CollectFileEntries_NonFirstFfffLeft_KeepsRightSibling()
    {
        // BUG-LIB-036: any 0xFFFF left child was treated as an empty table,
        // dropping the entry and its right siblings.
        string iso = CreateSentinelIso();

        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(iso);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, static e => string.Equals(e.Path, "a.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Path, "b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void GetXisoRanges_NonFirstFfffLeft_KeepsRightSiblingSectors()
    {
        // BUG-LIB-036 (GetValidSectors twin of the test above).
        string iso = CreateSentinelIso();

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
        (_, List<(uint Start, uint End)> files) = XisoRanges.GetXisoRanges(fs, 0, true);

        Assert.Contains(files, static r => r.Start == 34 && r.End == 35);
    }

    /// <summary>
    /// Builds a minimal image whose root table holds two file entries where the
    /// second (non-first) entry uses a 0xFFFF left child ("no left child",
    /// xdvdfs semantics) with no right sibling after it.
    /// </summary>
    private string CreateSentinelIso()
    {
        byte[] bytes = new byte[64 * Constants.SectorSize];
        byte[] magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
        const int header = Constants.HeaderOffset;
        magic.CopyTo(bytes.AsSpan(header, magic.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 20, 4), 33);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 24, 4), (uint)Constants.SectorSize);
        magic.CopyTo(bytes.AsSpan(header + 20 + 4 + 4 + Constants.FileTimeSize + Constants.UnusedSize,
            magic.Length));

        const int table = 33 * Constants.SectorSize;
        WriteTableEntry(bytes, table, 0, 5, 34, 100, Constants.AttributeArc, "a.txt");
        WriteTableEntry(bytes, table + 20, 0xFFFF, 0, 35, 100, Constants.AttributeArc, "b.txt");

        string dir = CreateTempDir();
        string iso = Path.Combine(dir, "sentinel.iso");
        File.WriteAllBytes(iso, bytes);
        return iso;
    }

    private static void WriteTableEntry(byte[] img, int offset, ushort left, ushort right,
        uint sector, uint size, byte attr, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(offset, 2), left);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(offset + 2, 2), right);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 4, 4), sector);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 8, 4), size);
        img[offset + 12] = attr;
        img[offset + 13] = (byte)nameBytes.Length;
        nameBytes.CopyTo(img.AsSpan(offset + 14, nameBytes.Length));
    }

    [Fact]
    public void MergeRanges_EmptyInputs_ReturnsEmpty()
    {
        List<(uint Start, uint End)> a = new();
        List<(uint Start, uint End)> b = new();

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        Assert.Empty(merged);
    }

    [Fact]
    public void MergeRanges_SingleList_UnchangedWhenOtherEmpty()
    {
        List<(uint Start, uint End)> a = new() { (5, 10), (20, 30) };
        List<(uint Start, uint End)> b = new();

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        Assert.Equal(a, merged);

        List<(uint Start, uint End)> merged2 = XisoRanges.MergeRanges(b, a);
        Assert.Equal(a, merged2);
    }

    [Fact]
    public void MergeRanges_MultipleInterleaved_ProducesSortedCoalesced()
    {
        List<(uint Start, uint End)> a = new() { (1, 2), (10, 12) };
        List<(uint Start, uint End)> b = new() { (3, 5), (11, 15), (20, 25) };

        List<(uint Start, uint End)> merged = XisoRanges.MergeRanges(a, b);

        // Expected merge sorted: (1,2),(3,5) -> adjacent? 3 ==2+1 => merge to (1,5)
        // Then (10,12),(11,15) => overlapping => (10,15)
        // Then (20,25) disjoint
        List<(uint Start, uint End)> expected = new() { (1, 5), (10, 15), (20, 25) };
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void GetFileEntries_ReturnsSortedByOffsetAndCountMatches()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "a");
            File.WriteAllText(Path.Combine(d, "b.txt"), new string('b', 3000));
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "c.txt"), "c");
        });
        string iso = CreateIso(src);

        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(iso);

        Assert.Equal(3, entries.Count);
        // Sorted by offset ascending
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i].Offset >= entries[i - 1].Offset,
                $"Entries not sorted by offset: {entries[i - 1].Offset} > {entries[i].Offset}");
        }

        // Paths should be present (without leading slash per XisoRanges impl)
        Assert.Contains(entries, e => string.Equals(e.Path, "a.txt", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Path, "b.txt", StringComparison.Ordinal));
        Assert.Contains(entries,
            e => string.Equals(e.Path, "sub/c.txt", StringComparison.Ordinal) ||
                 string.Equals(e.Path, "sub\\c.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void GetFileEntries_StringOverloadMatchesStreamOverload()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);

        List<(string Path, long Offset, uint Size)> viaPath = XisoRanges.GetFileEntries(iso);
        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        List<(string Path, long Offset, uint Size)> viaStream = XisoRanges.GetFileEntries(fs, 0);

        Assert.Equal(viaPath.Count, viaStream.Count);
        Assert.Equal(viaPath, viaStream);
    }

    [Fact]
    public void GetFileEntries_NestedStructure_CorrectPathsAndSizes()
    {
        string src = CreateSourceDir(d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "a", "b", "c"));
            File.WriteAllText(Path.Combine(d, "root.txt"), "root");
            File.WriteAllText(Path.Combine(d, "a", "level1.txt"), "l1");
            File.WriteAllText(Path.Combine(d, "a", "b", "level2.txt"), "l2");
            File.WriteAllText(Path.Combine(d, "a", "b", "c", "deep.txt"), "deep");
        });
        string iso = CreateIso(src);

        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(iso);

        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Path, "root.txt", StringComparison.Ordinal) && e.Size == 4);
        Assert.Contains(entries, e => string.Equals(e.Path, "a/level1.txt", StringComparison.Ordinal) && e.Size == 2);
        Assert.Contains(entries, e => string.Equals(e.Path, "a/b/level2.txt", StringComparison.Ordinal) && e.Size == 2);
        Assert.Contains(entries, e => string.Equals(e.Path, "a/b/c/deep.txt", StringComparison.Ordinal) && e.Size == 4);
        // Ensure sorted
        List<long> offsets = entries.ConvertAll(e => e.Offset);
        Assert.Equal(offsets.Order().ToList(), offsets);
    }

    [Fact]
    public void GetValidSectors_PopulatesSysAndFileSectors()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "file.txt"), new string('x', 4096));
            Directory.CreateDirectory(Path.Combine(d, "empty"));
        });
        string iso = CreateIso(src);

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        // Read header to get rootOffset/rootSize like GetXisoRanges does
        const long headerOffset = Constants.HeaderOffset;
        fs.Seek(headerOffset + 20, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[4];
        fs.ReadExactly(buf);
        uint rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        fs.ReadExactly(buf);
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(buf);

        List<uint> sysSectors = new();
        List<uint> fileSectors = new();
        const long isoOffset = 0;
        const long headerOffsetSector = headerOffset / Constants.SectorSize;
        sysSectors.Add((uint)headerOffsetSector);
        // Call GetValidSectors directly
        XisoRanges.GetValidSectors(fs, isoOffset, sysSectors, fileSectors, rootOffset * Constants.SectorSize, rootSize,
            0, true);

        Assert.NotEmpty(sysSectors);
        Assert.NotEmpty(fileSectors);
        // sys should contain header sector
        Assert.Contains((uint)(headerOffset / Constants.SectorSize), sysSectors);
        // file sectors should be > header sector
        Assert.All(fileSectors, s => Assert.True(s > headerOffsetSector));
    }

    [Fact]
    public void GetFileEntries_EmptyDirectory_ReturnsEmpty()
    {
        string src = CreateSourceDir(_ => { });
        string iso = CreateIso(src);

        // XisoRanges.CollectFileEntries treats the all-0xFF table as the
        // empty-directory sentinel at the table start and returns no entries.
        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(iso);
        Assert.Empty(entries);
    }
}
