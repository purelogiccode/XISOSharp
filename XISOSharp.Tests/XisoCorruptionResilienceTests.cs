using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Corruption resilience tests for TODO #1 (full test suite, xdvdfs #107/#137):
/// truncated images, manipulated header pointers, out-of-image extents,
/// absurd (&gt;4 GB) size fields, and invalid filenames must all fail fast
/// with a named error — never hang, OOM, or silently corrupt.
/// </summary>
[Collection("Sequential")]
public class XisoCorruptionResilienceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

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
        string src = CreateTempDir("xiso_corrupt_src");
        populate(src);
        string dir = CreateTempDir("xiso_corrupt_iso");
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

    // ------------------------------------------------------------------
    // Volume-header pointer manipulation
    // ------------------------------------------------------------------

    [Fact]
    public void VerifyXiso_SecondMagicCorrupt_ThrowsCorrupt()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset + 20 + 4 + 4 + Constants.FileTimeSize + Constants.UnusedSize,
            Constants.HeaderDataLength);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        using FileStream fs = new(bad, FileMode.Open, FileAccess.Read, FileShare.Read);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(fs, "game.iso"));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyXiso_ZeroRootSectorAndSize_ThrowsEmpty()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        Array.Clear(img, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        using FileStream fs = new(bad, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<XisoEmptyException>(() => XisoReader.VerifyXiso(fs, "game.iso"));
    }

    [Fact]
    public void VerifyXiso_NonZeroSectorZeroSize_ThrowsNamed()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(Constants.HeaderOffset + 24), 0u);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        using FileStream fs = new(bad, FileMode.Open, FileAccess.Read, FileShare.Read);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(fs, "game.iso"));
        Assert.Contains("size is zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyXiso_OversizedRoot_ThrowsNamed()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        byte[] img = File.ReadAllBytes(isoPath);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(Constants.HeaderOffset + 24), 0xFFFFFF00u);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        using FileStream fs = new(bad, FileMode.Open, FileAccess.Read, FileShare.Read);
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(fs, "game.iso"));
        Assert.Contains("exceeds available space", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Out-of-image extents
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_SubdirStartBeyondEof_ThrowsInvalidToc()
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
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0x00FFFFFFu);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_corrupt_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside the image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_FileStartBeyondEof_ThrowsTruncated()
    {
        string isoPath = CreateIso(src =>
        {
            byte[] payload = new byte[20000];
            new Random(7).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(src, "c.bin"), payload);
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "c.bin");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0x00FFFFFFu);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_corrupt_dest");
        ExtractFileException ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("c.bin", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 32-bit size field maxed out (&gt;4 GB claim on a small image) must fail
    /// fast on every data path while metadata listing still works.
    /// </summary>
    [Fact]
    public void FileSizeMaxValue_FailsFastEverywhere()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "big.bin"), "small"), "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "big.bin");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), uint.MaxValue);
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        // Metadata still reads: the size is reported, not acted on.
        EntryInfo? entry = XisoReader.GetEntryInfo(bad, "/big.bin");
        Assert.NotNull(entry);
        Assert.Equal(uint.MaxValue, entry.FileSize);

        // Every data path fails fast instead of allocating or looping.
        string dest = CreateTempDir("xiso_corrupt_dest");
        Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Throws<IOException>(() => XisoReader.ComputeFileHash(bad, "/big.bin", HashAlgorithmName.SHA256));
        Assert.Throws<ExtractFileException>(() =>
            XisoReader.CopyOut(bad, "/big.bin", Path.Combine(dest, "big.bin")));
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A real &gt;4 GB source file is rejected up front with
    /// <see cref="XisoFileTooLargeException"/> — the size guard runs at
    /// enumeration, so the content is never read. Uses a sparse file (NTFS
    /// only; instant and allocation-free there).
    /// </summary>
    [RequiresNtfsFact]
    public void CreateXiso_FileOver4GB_ThrowsFileTooLarge()
    {
        Assert.True(SkipConditions.IsNtfsTempDrive(), "Requires NTFS temp drive.");

        string src = CreateTempDir("xiso_corrupt_src");
        string bigPath = Path.Combine(src, "huge.bin");
        using (FileStream fs = new(bigPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(5L * 1024 * 1024 * 1024);
        }

        XisoFileTooLargeException ex = Assert.Throws<XisoFileTooLargeException>(() =>
            XisoWriter.CreateXiso(src, CreateTempDir("xiso_corrupt_iso"), null, null, out _, null, null));
        Assert.Contains("huge.bin", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Invalid filenames
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_FilenameWithSlash_ThrowsXisoFormat()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 14] = (byte)'/';
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_corrupt_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(bad, dest));
        Assert.Contains("invalid character", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_FilenameWithSlash_ReportsIssue()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 14] = (byte)'/';
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        AuditResult result = XisoReader.AuditXiso(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, static i => i.Contains("path separator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListDirectory_FilenameWithSlash_ThrowsXisoFormat()
    {
        // BUG-LIB-021: the listing walk rejects separator names exactly like
        // TraverseXiso instead of handing them to Path.Combine.
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 14] = (byte)'/';
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(bad, "/"));
        Assert.Contains("path separator", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rewrite_CaseDuplicateNames_ThrowsXisoFormat()
    {
        // BUG-LIB-023: names colliding case-insensitively cannot round-trip,
        // so rewrite fails naming the duplicate instead of dropping a file.
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        // Same letters, different case: still "b.txt" case-insensitively,
        // so the rewrite tree build must refuse the duplicate.
        "A.TXT"u8.CopyTo(img.AsSpan((int)header + 14));
        img[(int)header + 13] = (byte)"A.TXT".Length;
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        string outDir = CreateTempDir("xiso_corrupt_dest");
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.DecodeXiso(bad, outDir, ExtractMode.Rewrite, out _, true));
        Assert.Contains("duplicate filename", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_CaseDuplicateNames_FailsWithoutDropping()
    {
        // BUG-LIB-023 (writer side): a case-sensitive host holding both cases
        // fails the run instead of packing one file and dropping the other.
        string src = CreateTempDir("xiso_corrupt_casesrc");
        File.WriteAllText(Path.Combine(src, "File.txt"), "upper");
        if (File.Exists(Path.Combine(src, "file.txt")))
            return; // Case-insensitive filesystem: both names are one file.
        File.WriteAllText(Path.Combine(src, "file.txt"), "lower");

        string dir = CreateTempDir("xiso_corrupt_caseiso");
        int rc = XisoWriter.CreateXiso(src, dir, null, null, out string? created, "case.iso", null);
        Assert.Equal(1, rc);
        Assert.True(created == null || !File.Exists(created));
    }

    [Fact]
    public void Extract_FilenameWithNul_ThrowsNamed()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        (uint rootSize, long rootAbs) = RootLayout(isoPath);

        byte[] img = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(img, rootAbs, rootSize, "a.txt");
        img[header + 14] = 0x00;
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img);

        string dest = CreateTempDir("xiso_corrupt_dest");
        Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(bad, dest));
    }

    // ------------------------------------------------------------------
    // Truncated directory table: every reader fails bounded, never hangs.
    // ------------------------------------------------------------------

    private string CreateTableTruncatedIso()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        long rootAbs = RootLayout(isoPath).RootAbs;

        byte[] img = File.ReadAllBytes(isoPath);
        img[rootAbs] = 0x00;
        img[rootAbs + 1] = 0x00;
        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, img[..(int)(rootAbs + 2)]);
        return bad;
    }

    [Fact]
    public void TruncatedTable_Unpack_ThrowsBounded()
    {
        // The header-level check fires first: root sector beyond end of image.
        string bad = CreateTableTruncatedIso();
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() =>
            XisoReader.UnpackImage(bad, CreateTempDir("xiso_corrupt_dest")));
        Assert.Contains("beyond end", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TruncatedTable_ListDirectory_ThrowsBounded()
    {
        string bad = CreateTableTruncatedIso();
        Assert.Throws<IOException>(() => XisoReader.ListDirectory(bad, "/"));
    }

    [Fact]
    public void TruncatedTable_Audit_ThrowsBounded()
    {
        string bad = CreateTableTruncatedIso();
        Assert.Throws<IOException>(() => XisoReader.AuditXiso(bad));
    }

    [Fact]
    public void TruncatedTable_GetSectorLayout_ThrowsBounded()
    {
        // The table-bounds precheck fires: table extends past end of image.
        string bad = CreateTableTruncatedIso();
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
        Assert.Contains("outside the image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds an image whose root table runs exactly to end-of-file with a
    /// zeroed entry header near the end, so the 12-byte sentinel peek reads
    /// past EOF. Every reader must surface a bounded I/O error, never hang.
    /// </summary>
    private string CreateTableEndAtEofIso()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        long rootAbs = RootLayout(isoPath).RootAbs;

        List<byte> img = File.ReadAllBytes(isoPath).ToList();
        int fileLen = img.Count;

        // Position P: dword-aligned, first header ushort in-bounds, 12-byte
        // peek past EOF.
        int p = fileLen - 6 - ((((fileLen - 6 - (int)rootAbs) % 4) + 4) % 4);
        Assert.True(p > rootAbs && p + 2 <= fileLen && p + 14 > fileLen,
            $"no suitable peek-past-end position (rootAbs={rootAbs}, fileLen={fileLen})");

        byte[] imgArr = img.ToArray();
        long bHeader = FindEntryHeader(imgArr, rootAbs, (uint)(fileLen - rootAbs), "b.txt");
        ushort target = (ushort)((p - rootAbs) / 4);
        BinaryPrimitives.WriteUInt16LittleEndian(imgArr.AsSpan((int)bHeader + 2), target);

        // Stretch the root table bound to end-of-file so the walk may reach P.
        BinaryPrimitives.WriteUInt32LittleEndian(imgArr.AsSpan(Constants.HeaderOffset + 24),
            (uint)(fileLen - rootAbs));

        imgArr[p] = 0x00;
        imgArr[p + 1] = 0x00;

        string bad = CopyIso(isoPath, "xiso_corrupt_bad");
        File.WriteAllBytes(bad, imgArr);
        return bad;
    }

    [Fact]
    public void TableEndingAtEof_Unpack_ThrowsBounded()
    {
        string bad = CreateTableEndAtEofIso();
        Assert.Throws<IOException>(() => XisoReader.UnpackImage(bad, CreateTempDir("xiso_corrupt_dest")));
    }

    [Fact]
    public void TableEndingAtEof_ListDirectory_ThrowsBounded()
    {
        string bad = CreateTableEndAtEofIso();
        Assert.Throws<IOException>(() => XisoReader.ListDirectory(bad, "/"));
    }

    [Fact]
    public void TableEndingAtEof_Audit_ThrowsBounded()
    {
        string bad = CreateTableEndAtEofIso();
        Assert.Throws<IOException>(() => XisoReader.AuditXiso(bad));
    }

    [Fact]
    public void TableEndingAtEof_GetSectorLayout_ThrowsBounded()
    {
        string bad = CreateTableEndAtEofIso();
        Assert.Throws<IOException>(() => XisoReader.GetSectorLayout(bad));
    }
}
