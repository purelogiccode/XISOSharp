using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoPatcher"/> in-place patching (TODO #5, xdvdfs #165):
/// smaller-in-place replacement, larger reallocation, empty-file edges, new-file
/// adds (including parent-table moves), error paths, backups, .xbe patching,
/// and the <c>--copy-in</c> CLI.
/// </summary>
[Collection("Sequential")]
public class XisoPatcherTests : IDisposable
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
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
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

    private static void PopulateMixed(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello");
        byte[] bin = new byte[5000];
        new Random(42).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(dir, "file2.txt"), bin);
        File.WriteAllBytes(Path.Combine(dir, "empty.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested");
    }

    private string CreateIso(string srcDir)
    {
        string outDir = CreateTempDir("xiso_patch_out");
        Assert.Equal(0, XisoWriter.CreateXiso(srcDir, outDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private string CopyOutToTemp(string isoPath, string internalPath)
    {
        string dest = Path.Combine(CreateTempDir("xiso_patch_unpack"), "out.bin");
        XisoReader.CopyOut(isoPath, internalPath, dest);
        return dest;
    }

    private static void AssertAllFf(byte[] bytes) => Assert.All(bytes, static b => Assert.Equal(Constants.PadByte, b));

    private static byte[] ReadSectors(string isoPath, long discLseek, uint startSector, uint sectors)
    {
        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buf = new byte[sectors * Constants.SectorSize];
        fs.Seek(discLseek + ((long)startSector * Constants.SectorSize), SeekOrigin.Begin);
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            Assert.True(n > 0, "Unexpected end of image.");
            read += n;
        }

        return buf;
    }

    private static void AssertTreeEqual(string isoPath, Dictionary<string, byte[]> expected)
    {
        string dest = Path.Combine(Path.GetTempPath(), $"xiso_patch_tree_{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
            foreach ((string rel, byte[] content) in expected)
            {
                byte[] actual = File.ReadAllBytes(Path.Combine(dest, rel));
                Assert.Equal(content, actual);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void CopyIn_SecondRun_KeepsFirstBackup()
    {
        // BUG-LIB-027: a repeat patch must not destroy the previous `.old` —
        // the backup keeps the true pre-patch original.
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] original = File.ReadAllBytes(iso);

        string hostA = Path.Combine(CreateTempDir("xiso_patch_host"), "a.bin");
        File.WriteAllBytes(hostA, new byte[100]);
        XisoPatcher.CopyIntoImage(iso, hostA, "/file2.txt");
        Assert.Equal(original, File.ReadAllBytes(iso + ".old"));

        string hostB = Path.Combine(CreateTempDir("xiso_patch_host"), "b.bin");
        File.WriteAllBytes(hostB, new byte[200]);
        XisoPatcher.CopyIntoImage(iso, hostB, "/file2.txt");

        Assert.Equal(original, File.ReadAllBytes(iso + ".old"));
        Assert.Equal((uint)200, XisoReader.GetEntryInfo(iso, "/file2.txt")!.FileSize);
    }

    [Fact]
    public void Replace_SmallerFile_UpdatesContentAndSize()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        byte[] content = new byte[100];
        new Random(7).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt");

        EntryInfo? entry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(entry);
        Assert.Equal((uint)content.Length, entry.FileSize);
        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/file2.txt")));
        // Sibling untouched.
        Assert.Equal("hello", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
        Assert.Equal("nested", File.ReadAllText(CopyOutToTemp(iso, "/subdir/nested.txt")));
        // Layout still fully valid.
        SectorLayout layout = XisoReader.GetSectorLayout(iso);
        Assert.Contains(layout.Entries,
            static e => string.Equals(e.Path, "/file2.txt", StringComparison.OrdinalIgnoreCase) && e.FileSize == 100);
    }

    [Fact]
    public void Replace_SmallerFile_LeavesOtherBytesUntouched()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        byte[] before = File.ReadAllBytes(iso);
        SectorLayout layout = XisoReader.GetSectorLayout(iso);
        EntryInfo? oldEntry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(oldEntry);
        FileSectorExtent parent = layout.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        uint oldSectors = (oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize;

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllBytes(host, new byte[100]);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt", createBackup: false);

        byte[] after = File.ReadAllBytes(iso);
        Assert.Equal(before.Length, after.Length);

        long discLseek = layout.Volume.DiscLseek;
        long dataStart = discLseek + ((long)oldEntry.StartSector * Constants.SectorSize);
        long dataEnd = dataStart + ((long)oldSectors * Constants.SectorSize);
        long tblStart = discLseek + ((long)parent.StartSector * Constants.SectorSize);
        long tblEnd = tblStart + ((long)parent.SectorCount * Constants.SectorSize);

        long tblDiffMin = long.MaxValue, tblDiffMax = long.MinValue;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i])
                continue;
            bool inData = i >= dataStart && i < dataEnd;
            bool inTable = i >= tblStart && i < tblEnd;
            Assert.True(inData || inTable, $"Byte {i} changed outside the data run and parent table.");
            if (inTable)
            {
                tblDiffMin = Math.Min(tblDiffMin, i);
                tblDiffMax = Math.Max(tblDiffMax, i);
            }
        }

        // The parent-table change is a single 8-byte entry-record patch.
        Assert.True(tblDiffMax - tblDiffMin < 8, "Parent table change exceeds one entry record.");
    }

    [Fact]
    public void Replace_LargerFile_ReallocatesAndWipesOld()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        EntryInfo? oldEntry = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(oldEntry);
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        byte[] content = new byte[5000];
        new Random(9).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/file1.txt", createBackup: false);

        EntryInfo? updated = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(updated);
        Assert.Equal((uint)content.Length, updated.FileSize);
        Assert.NotEqual(oldEntry.StartSector, updated.StartSector);
        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/file1.txt")));

        // Old run wiped with 0xFF.
        uint oldSectors = (oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize;
        AssertAllFf(ReadSectors(iso, layout.Volume.DiscLseek, oldEntry.StartSector, oldSectors));

        // Whole tree still extracts correctly.
        AssertTreeEqual(iso, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["file1.txt"] = content,
            ["file2.txt"] = File.ReadAllBytes(Path.Combine(src, "file2.txt")),
            ["empty.txt"] = [],
            ["subdir/nested.txt"] = "nested"u8.ToArray(),
        });
    }

    [Fact]
    public void Replace_EmptyFile_WithData_TakesReallocPath()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "now it has content");

        // Via the XisoReader facade (covers the thin wrapper).
        XisoReader.CopyIn(iso, host, "/empty.txt", createBackup: false);

        EntryInfo? updated = XisoReader.GetEntryInfo(iso, "/empty.txt");
        Assert.NotNull(updated);
        Assert.Equal("now it has content", File.ReadAllText(CopyOutToTemp(iso, "/empty.txt")));
    }

    [Fact]
    public void Replace_DataFile_WithEmpty_WipesOldRun()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        EntryInfo? oldEntry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(oldEntry);
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "empty.bin");
        File.WriteAllBytes(host, []);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt", createBackup: false);

        EntryInfo? updated = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(updated);
        Assert.Equal(0u, updated.FileSize);
        Assert.Equal(0, new FileInfo(CopyOutToTemp(iso, "/file2.txt")).Length);

        uint oldSectors = (oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize;
        AssertAllFf(ReadSectors(iso, layout.Volume.DiscLseek, oldEntry.StartSector, oldSectors));
    }

    [Fact]
    public void Add_NewFile_AppearsWithContent_AndBackupMatchesPrePatch()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        byte[] content = new byte[3000];
        new Random(11).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/brand-new.bin");

        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/brand-new.bin")));
        IReadOnlyList<string> names = XisoReader.ListDirectoryFlat(iso, "/");
        Assert.Contains("brand-new.bin", names, StringComparer.OrdinalIgnoreCase);

        string backup = iso + ".old";
        Assert.True(File.Exists(backup));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    [Fact]
    public void Add_NewFile_WithoutBackup_SkipsOldFile()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "no backup please");

        XisoPatcher.CopyIntoImage(iso, host, "/added.txt", createBackup: false);

        Assert.False(File.Exists(iso + ".old"));
        Assert.Equal("no backup please", File.ReadAllText(CopyOutToTemp(iso, "/added.txt")));
    }

    [Fact]
    public void Backup_KeepsFirstSnapshot_OnSecondPatch()
    {
        // BUG-LIB-027: a repeat patch must not destroy the previous `.old` —
        // the backup keeps the true pre-patch original.
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string hostDir = CreateTempDir("xiso_patch_host");
        File.WriteAllText(Path.Combine(hostDir, "a.bin"), "first");
        XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "a.bin"), "/file1.txt");
        Assert.Equal(before, File.ReadAllBytes(iso + ".old"));

        File.WriteAllText(Path.Combine(hostDir, "b.bin"), "second!!");
        XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "b.bin"), "/file1.txt");

        Assert.Equal(before, File.ReadAllBytes(iso + ".old"));
        Assert.Equal("second!!", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
    }

    [Fact]
    public void Add_NewFile_ForcingSubdirTableMove_UpdatesGrandparentOnly()
    {
        // 102 x 20-byte entries = 2040-byte table (1 sector); +1 entry = 2068
        // bytes (2 sectors), forcing the subdir table to move.
        string src = CreateTempDir("xiso_patch_src");
        string sub = Path.Combine(src, "subdir");
        Directory.CreateDirectory(sub);
        for (int i = 0; i < 102; i++)
            File.WriteAllBytes(Path.Combine(sub, $"f{i:D3}"), []);
        File.WriteAllText(Path.Combine(src, "root.txt"), "root");
        string iso = CreateIso(src);

        SectorLayout before = XisoReader.GetSectorLayout(iso);
        FileSectorExtent subBefore = before.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/subdir", StringComparison.OrdinalIgnoreCase));
        FileSectorExtent rootBefore = before.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        // On-disk directory sizes are sector-rounded (in-memory table is 2040 bytes).
        Assert.Equal(2048u, subBefore.FileSize);
        Assert.Equal(1u, subBefore.SectorCount);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "0123456789");
        XisoPatcher.CopyIntoImage(iso, host, "/subdir/newf", createBackup: false);

        SectorLayout after = XisoReader.GetSectorLayout(iso);
        FileSectorExtent subAfter = after.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/subdir", StringComparison.OrdinalIgnoreCase));
        FileSectorExtent rootAfter = after.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2u, subAfter.SectorCount);
        Assert.NotEqual(subBefore.StartSector, subAfter.StartSector);
        // Grandparent (root) table itself did not move — only its entry changed.
        Assert.Equal(rootBefore.StartSector, rootAfter.StartSector);
        // Old table span wiped.
        AssertAllFf(ReadSectors(iso, after.Volume.DiscLseek, subBefore.StartSector, subBefore.SectorCount));

        Assert.Equal("0123456789", File.ReadAllText(CopyOutToTemp(iso, "/subdir/newf")));
        Assert.Equal("root", File.ReadAllText(CopyOutToTemp(iso, "/root.txt")));
    }

    [Fact]
    public void Add_NewFile_ForcingRootTableMove_UpdatesVolumeHeader()
    {
        string src = CreateTempDir("xiso_patch_src");
        for (int i = 0; i < 102; i++)
            File.WriteAllBytes(Path.Combine(src, $"f{i:D3}"), []);
        string iso = CreateIso(src);

        VolumeInfo volBefore = XisoReader.GetVolumeInfo(iso);
        SectorLayout layoutBefore = XisoReader.GetSectorLayout(iso);
        FileSectorExtent rootBefore = layoutBefore.Entries.First(static e =>
            e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        // Asymmetry by writer construction: the volume header stores the UNROUNDED
        // root table size, while subdirectory entry records store sector-rounded
        // sizes (the patcher preserves both conventions).
        Assert.Equal(2040u, rootBefore.FileSize);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "0123456789");
        XisoPatcher.CopyIntoImage(iso, host, "/newf", createBackup: false);

        VolumeInfo volAfter = XisoReader.GetVolumeInfo(iso);
        Assert.NotEqual(volBefore.RootDirSector, volAfter.RootDirSector);
        Assert.Equal(2068u, volAfter.RootDirSize);
        AssertAllFf(ReadSectors(iso, volAfter.DiscLseek, rootBefore.StartSector, rootBefore.SectorCount));

        Assert.Equal("0123456789", File.ReadAllText(CopyOutToTemp(iso, "/newf")));
    }

    [Fact]
    public void CopyIn_MissingHost_Throws_AndLeavesImageAlone()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string missing = Path.Combine(CreateTempDir("xiso_patch_host"), "nope.bin");
        Assert.Throws<FileNotFoundException>(() => XisoPatcher.CopyIntoImage(iso, missing, "/file1.txt"));

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_MissingParent_Throws_AndLeavesImageAlone()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "data");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(iso, host, "/nodir/x.bin"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_OntoDirectory_Throws_AndLeavesImageAlone()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "data");
        Assert.Throws<InvalidDataException>(() => XisoPatcher.CopyIntoImage(iso, host, "/subdir"));

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/bad\\name")]
    public void CopyIn_BadInternalPath_Throws(string internalPath)
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "data");
        Assert.Throws<InvalidDataException>(() => XisoPatcher.CopyIntoImage(iso, host, internalPath));

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_NoSpace_Throws_AndLeavesImageUntouched()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        SectorLayout layout = XisoReader.GetSectorLayout(iso);
        ulong free = 0;
        foreach (SectorRange range in layout.FreeRanges)
            free += range.SectorCount;

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "huge.bin");
        File.WriteAllBytes(host, new byte[(free * Constants.SectorSize) + Constants.SectorSize]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(iso, host, "/file1.txt"));
        Assert.Contains("free", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(iso));
    }

    [Fact]
    public void CopyIn_PreservesFileTime()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        ulong stamp = XisoReader.GetFileTimeRaw(iso);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "patched");
        XisoPatcher.CopyIntoImage(iso, host, "/file1.txt", createBackup: false);

        Assert.Equal(stamp, XisoReader.GetFileTimeRaw(iso));
    }

    [Fact]
    public void CopyIn_XbeMediaPatch_AppliedOnlyToXbe()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        byte[] xbe = new byte[200];
        File.WriteAllBytes(Path.Combine(src, "orig.xbe"), xbe);
        string iso = CreateIso(src);

        byte[] pattern = new byte[] { 0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D };
        string hostDir = CreateTempDir("xiso_patch_host");
        byte[] xbeHost = new byte[500];
        Buffer.BlockCopy(pattern, 0, xbeHost, 100, pattern.Length);
        File.WriteAllBytes(Path.Combine(hostDir, "patch.xbe"), xbeHost);
        byte[] binHost = new byte[500];
        Buffer.BlockCopy(pattern, 0, binHost, 100, pattern.Length);
        File.WriteAllBytes(Path.Combine(hostDir, "patch.bin"), binHost);

        bool origMedia = Logger.MediaEnable;
        try
        {
            Logger.MediaEnable = true;
            XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "patch.xbe"), "/orig.xbe",
                createBackup: false);
            XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "patch.bin"), "/file1.txt",
                createBackup: false);
        }
        finally
        {
            Logger.MediaEnable = origMedia;
        }

        VolumeInfo vol = XisoReader.GetVolumeInfo(iso);
        EntryInfo? xbeEntry = XisoReader.GetEntryInfo(iso, "/orig.xbe");
        Assert.NotNull(xbeEntry);
        byte[] onDiskXbe = ReadSectors(iso, vol.DiscLseek, xbeEntry.StartSector, 1);
        Assert.Equal(0xEB, onDiskXbe[107]);
        Assert.Equal(pattern[..7], onDiskXbe[100..107]);

        EntryInfo? binEntry = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(binEntry);
        byte[] onDiskBin = ReadSectors(iso, vol.DiscLseek, binEntry.StartSector, 1);
        Assert.Equal(pattern, onDiskBin[100..108]);
    }

    [Fact]
    public void CopyIn_CaseInsensitiveInternalPath()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "upper");
        XisoPatcher.CopyIntoImage(iso, host, "/FILE1.TXT", createBackup: false);
        Assert.Equal("upper", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));

        File.WriteAllText(host, "sub add");
        XisoPatcher.CopyIntoImage(iso, host, "/SUBDIR/added.txt", createBackup: false);
        Assert.Equal("sub add", File.ReadAllText(CopyOutToTemp(iso, "/subdir/added.txt")));
    }

    [Fact]
    public void Cli_CopyIn_EndToEnd()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "via cli");

        Assert.Equal(0, Program.Main(["--copy-in", iso, host, "/file1.txt"]));
        Assert.Equal("via cli", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
        Assert.True(File.Exists(iso + ".old"));
    }

    [Fact]
    public void Cli_CopyIn_MissingPositionals_ReturnsOne()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);

        Assert.Equal(1, Program.Main(["--copy-in", iso]));
    }

    [Fact]
    public void Cli_CopyIn_CombinedWithExtract_ReturnsOne()
    {
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        string host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "x");

        Assert.Equal(1, Program.Main(["-x", "--copy-in", iso, host, "/file1.txt"]));
        Assert.Equal(1, Program.Main(["--no-backup"]));
    }

    [Fact]
    public void CopyIn_HostDirectory_ThrowsInvalidData_AndLeavesImageUntouched()
    {
        // Single-file limit (TODO #22): a host directory fails fast with the
        // documented InvalidDataException — not a misleading FileNotFoundException.
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        string iso = CreateIso(src);
        byte[] before = File.ReadAllBytes(iso);

        string hostDir = CreateTempDir("xiso_patch_hostdir");
        File.WriteAllText(Path.Combine(hostDir, "inner.txt"), "x");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(iso, hostDir, "/file1.txt", createBackup: false));
        Assert.Contains("director", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_ImageSizeNeverChanges()
    {
        // Fixed-size limit (TODO #22): both the in-place and realloc paths keep
        // the image byte length identical.
        string src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);

        string iso1 = CreateIso(src);
        long len1 = new FileInfo(iso1).Length;
        string small = Path.Combine(CreateTempDir("xiso_patch_host"), "small.bin");
        File.WriteAllText(small, "tiny");
        XisoPatcher.CopyIntoImage(iso1, small, "/file2.txt", createBackup: false); // 5000 -> 4 B, in place
        Assert.Equal(len1, new FileInfo(iso1).Length);

        string iso2 = CreateIso(src);
        long len2 = new FileInfo(iso2).Length;
        string big = Path.Combine(CreateTempDir("xiso_patch_host"), "big.bin");
        byte[] content = new byte[5000];
        new Random(9).NextBytes(content);
        File.WriteAllBytes(big, content);
        XisoPatcher.CopyIntoImage(iso2, big, "/file1.txt", createBackup: false); // 5 -> 5000 B, realloc
        Assert.Equal(len2, new FileInfo(iso2).Length);
        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso2, "/file1.txt")));
    }
}
