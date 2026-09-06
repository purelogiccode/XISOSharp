using XISOSharp.Cli;

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
        foreach (var dir in _tempDirs)
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
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void PopulateMixed(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello");
        var bin = new byte[5000];
        new Random(42).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(dir, "file2.txt"), bin);
        File.WriteAllBytes(Path.Combine(dir, "empty.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested");
    }

    private string CreateIso(string srcDir)
    {
        var outDir = CreateTempDir("xiso_patch_out");
        Assert.Equal(0, XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private string CopyOutToTemp(string isoPath, string internalPath)
    {
        var dest = Path.Combine(CreateTempDir("xiso_patch_unpack"), "out.bin");
        XisoReader.CopyOut(isoPath, internalPath, dest);
        return dest;
    }

    private static void AssertAllFF(byte[] bytes)
    {
        Assert.All(bytes, static b => Assert.Equal(Constants.PadByte, b));
    }

    private static byte[] ReadSectors(string isoPath, long discLseek, uint startSector, uint sectors)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[sectors * Constants.SectorSize];
        fs.Seek(discLseek + ((long)startSector * Constants.SectorSize), SeekOrigin.Begin);
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            Assert.True(n > 0, "Unexpected end of image.");
            read += n;
        }

        return buf;
    }

    private static void AssertTreeEqual(string isoPath, Dictionary<string, byte[]> expected)
    {
        var dest = Path.Combine(Path.GetTempPath(), $"xiso_patch_tree_{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
            foreach (var (rel, content) in expected)
            {
                var actual = File.ReadAllBytes(Path.Combine(dest, rel));
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
    public void Replace_SmallerFile_UpdatesContentAndSize()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        var content = new byte[100];
        new Random(7).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt");

        var entry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(entry);
        Assert.Equal((uint)content.Length, entry.FileSize);
        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/file2.txt")));
        // Sibling untouched.
        Assert.Equal("hello", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
        Assert.Equal("nested", File.ReadAllText(CopyOutToTemp(iso, "/subdir/nested.txt")));
        // Layout still fully valid.
        var layout = XisoReader.GetSectorLayout(iso);
        Assert.Contains(layout.Entries, static e => e.Path == "/file2.txt" && e.FileSize == 100);
    }

    [Fact]
    public void Replace_SmallerFile_LeavesOtherBytesUntouched()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var before = File.ReadAllBytes(iso);
        var layout = XisoReader.GetSectorLayout(iso);
        var oldEntry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(oldEntry);
        var parent = layout.Entries.First(static e => e.IsDirectory && e.Path == "/");
        var oldSectors = (uint)((oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllBytes(host, new byte[100]);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt", createBackup: false);

        var after = File.ReadAllBytes(iso);
        Assert.Equal(before.Length, after.Length);

        var discLseek = layout.Volume.DiscLseek;
        var dataStart = discLseek + ((long)oldEntry.StartSector * Constants.SectorSize);
        var dataEnd = dataStart + ((long)oldSectors * Constants.SectorSize);
        var tblStart = discLseek + ((long)parent.StartSector * Constants.SectorSize);
        var tblEnd = tblStart + ((long)parent.SectorCount * Constants.SectorSize);

        long tblDiffMin = long.MaxValue, tblDiffMax = long.MinValue;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i])
                continue;
            var inData = i >= dataStart && i < dataEnd;
            var inTable = i >= tblStart && i < tblEnd;
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
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var oldEntry = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(oldEntry);
        var layout = XisoReader.GetSectorLayout(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        var content = new byte[5000];
        new Random(9).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/file1.txt", createBackup: false);

        var updated = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(updated);
        Assert.Equal((uint)content.Length, updated.FileSize);
        Assert.NotEqual(oldEntry.StartSector, updated.StartSector);
        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/file1.txt")));

        // Old run wiped with 0xFF.
        var oldSectors = (uint)((oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize);
        AssertAllFF(ReadSectors(iso, layout.Volume.DiscLseek, oldEntry.StartSector, oldSectors));

        // Whole tree still extracts correctly.
        AssertTreeEqual(iso, new Dictionary<string, byte[]>
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
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "now it has content");

        // Via the XisoReader facade (covers the thin wrapper).
        XisoReader.CopyIn(iso, host, "/empty.txt", createBackup: false);

        var updated = XisoReader.GetEntryInfo(iso, "/empty.txt");
        Assert.NotNull(updated);
        Assert.Equal("now it has content", File.ReadAllText(CopyOutToTemp(iso, "/empty.txt")));
    }

    [Fact]
    public void Replace_DataFile_WithEmpty_WipesOldRun()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var oldEntry = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(oldEntry);
        var layout = XisoReader.GetSectorLayout(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "empty.bin");
        File.WriteAllBytes(host, []);

        XisoPatcher.CopyIntoImage(iso, host, "/file2.txt", createBackup: false);

        var updated = XisoReader.GetEntryInfo(iso, "/file2.txt");
        Assert.NotNull(updated);
        Assert.Equal(0u, updated.FileSize);
        Assert.Equal(0, new FileInfo(CopyOutToTemp(iso, "/file2.txt")).Length);

        var oldSectors = (uint)((oldEntry.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize);
        AssertAllFF(ReadSectors(iso, layout.Volume.DiscLseek, oldEntry.StartSector, oldSectors));
    }

    [Fact]
    public void Add_NewFile_AppearsWithContent_AndBackupMatchesPrePatch()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        var content = new byte[3000];
        new Random(11).NextBytes(content);
        File.WriteAllBytes(host, content);

        XisoPatcher.CopyIntoImage(iso, host, "/brand-new.bin");

        Assert.Equal(content, File.ReadAllBytes(CopyOutToTemp(iso, "/brand-new.bin")));
        var names = XisoReader.ListDirectoryFlat(iso, "/");
        Assert.Contains("brand-new.bin", names);

        var backup = iso + ".old";
        Assert.True(File.Exists(backup));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    [Fact]
    public void Add_NewFile_WithoutBackup_SkipsOldFile()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "no backup please");

        XisoPatcher.CopyIntoImage(iso, host, "/added.txt", createBackup: false);

        Assert.False(File.Exists(iso + ".old"));
        Assert.Equal("no backup please", File.ReadAllText(CopyOutToTemp(iso, "/added.txt")));
    }

    [Fact]
    public void Backup_IsRefreshed_OnSecondPatch()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var hostDir = CreateTempDir("xiso_patch_host");
        File.WriteAllText(Path.Combine(hostDir, "a.bin"), "first");
        XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "a.bin"), "/file1.txt");
        var afterFirst = File.ReadAllBytes(iso);

        File.WriteAllText(Path.Combine(hostDir, "b.bin"), "second!!");
        XisoPatcher.CopyIntoImage(iso, Path.Combine(hostDir, "b.bin"), "/file1.txt");

        Assert.Equal(afterFirst, File.ReadAllBytes(iso + ".old"));
        Assert.Equal("second!!", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
    }

    [Fact]
    public void Add_NewFile_ForcingSubdirTableMove_UpdatesGrandparentOnly()
    {
        // 102 x 20-byte entries = 2040-byte table (1 sector); +1 entry = 2068
        // bytes (2 sectors), forcing the subdir table to move.
        var src = CreateTempDir("xiso_patch_src");
        var sub = Path.Combine(src, "subdir");
        Directory.CreateDirectory(sub);
        for (var i = 0; i < 102; i++)
            File.WriteAllBytes(Path.Combine(sub, $"f{i:D3}"), []);
        File.WriteAllText(Path.Combine(src, "root.txt"), "root");
        var iso = CreateIso(src);

        var before = XisoReader.GetSectorLayout(iso);
        var subBefore = before.Entries.First(static e => e.IsDirectory && e.Path == "/subdir");
        var rootBefore = before.Entries.First(static e => e.IsDirectory && e.Path == "/");
        // On-disk directory sizes are sector-rounded (in-memory table is 2040 bytes).
        Assert.Equal(2048u, subBefore.FileSize);
        Assert.Equal(1u, subBefore.SectorCount);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "0123456789");
        XisoPatcher.CopyIntoImage(iso, host, "/subdir/newf", createBackup: false);

        var after = XisoReader.GetSectorLayout(iso);
        var subAfter = after.Entries.First(static e => e.IsDirectory && e.Path == "/subdir");
        var rootAfter = after.Entries.First(static e => e.IsDirectory && e.Path == "/");
        Assert.Equal(2u, subAfter.SectorCount);
        Assert.NotEqual(subBefore.StartSector, subAfter.StartSector);
        // Grandparent (root) table itself did not move — only its entry changed.
        Assert.Equal(rootBefore.StartSector, rootAfter.StartSector);
        // Old table span wiped.
        AssertAllFF(ReadSectors(iso, after.Volume.DiscLseek, subBefore.StartSector, subBefore.SectorCount));

        Assert.Equal("0123456789", File.ReadAllText(CopyOutToTemp(iso, "/subdir/newf")));
        Assert.Equal("root", File.ReadAllText(CopyOutToTemp(iso, "/root.txt")));
    }

    [Fact]
    public void Add_NewFile_ForcingRootTableMove_UpdatesVolumeHeader()
    {
        var src = CreateTempDir("xiso_patch_src");
        for (var i = 0; i < 102; i++)
            File.WriteAllBytes(Path.Combine(src, $"f{i:D3}"), []);
        var iso = CreateIso(src);

        var volBefore = XisoReader.GetVolumeInfo(iso);
        var layoutBefore = XisoReader.GetSectorLayout(iso);
        var rootBefore = layoutBefore.Entries.First(static e => e.IsDirectory && e.Path == "/");
        // Asymmetry by writer construction: the volume header stores the UNROUNDED
        // root table size, while subdirectory entry records store sector-rounded
        // sizes (the patcher preserves both conventions).
        Assert.Equal(2040u, rootBefore.FileSize);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "0123456789");
        XisoPatcher.CopyIntoImage(iso, host, "/newf", createBackup: false);

        var volAfter = XisoReader.GetVolumeInfo(iso);
        Assert.NotEqual(volBefore.RootDirSector, volAfter.RootDirSector);
        Assert.Equal(2068u, volAfter.RootDirSize);
        AssertAllFF(ReadSectors(iso, volAfter.DiscLseek, rootBefore.StartSector, rootBefore.SectorCount));

        Assert.Equal("0123456789", File.ReadAllText(CopyOutToTemp(iso, "/newf")));
    }

    [Fact]
    public void CopyIn_MissingHost_Throws_AndLeavesImageAlone()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var missing = Path.Combine(CreateTempDir("xiso_patch_host"), "nope.bin");
        Assert.Throws<FileNotFoundException>(() => XisoPatcher.CopyIntoImage(iso, missing, "/file1.txt"));

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_MissingParent_Throws_AndLeavesImageAlone()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "data");
        var ex = Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(iso, host, "/nodir/x.bin"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_OntoDirectory_Throws_AndLeavesImageAlone()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
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
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "data");
        Assert.Throws<InvalidDataException>(() => XisoPatcher.CopyIntoImage(iso, host, internalPath));

        Assert.Equal(before, File.ReadAllBytes(iso));
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void CopyIn_NoSpace_Throws_AndLeavesImageUntouched()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var before = File.ReadAllBytes(iso);

        var layout = XisoReader.GetSectorLayout(iso);
        ulong free = 0;
        foreach (var range in layout.FreeRanges)
            free += range.SectorCount;

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "huge.bin");
        File.WriteAllBytes(host, new byte[(free * Constants.SectorSize) + Constants.SectorSize]);

        var ex = Assert.Throws<InvalidDataException>(() =>
            XisoPatcher.CopyIntoImage(iso, host, "/file1.txt"));
        Assert.Contains("free", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(iso));
    }

    [Fact]
    public void CopyIn_PreservesFileTime()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var stamp = XisoReader.GetFileTimeRaw(iso);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "patched");
        XisoPatcher.CopyIntoImage(iso, host, "/file1.txt", createBackup: false);

        Assert.Equal(stamp, XisoReader.GetFileTimeRaw(iso));
    }

    [Fact]
    public void CopyIn_XbeMediaPatch_AppliedOnlyToXbe()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var xbe = new byte[200];
        File.WriteAllBytes(Path.Combine(src, "orig.xbe"), xbe);
        var iso = CreateIso(src);

        var pattern = new byte[] { 0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D };
        var hostDir = CreateTempDir("xiso_patch_host");
        var xbeHost = new byte[500];
        Buffer.BlockCopy(pattern, 0, xbeHost, 100, pattern.Length);
        File.WriteAllBytes(Path.Combine(hostDir, "patch.xbe"), xbeHost);
        var binHost = new byte[500];
        Buffer.BlockCopy(pattern, 0, binHost, 100, pattern.Length);
        File.WriteAllBytes(Path.Combine(hostDir, "patch.bin"), binHost);

        var origMedia = Logger.MediaEnable;
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

        var vol = XisoReader.GetVolumeInfo(iso);
        var xbeEntry = XisoReader.GetEntryInfo(iso, "/orig.xbe");
        Assert.NotNull(xbeEntry);
        var onDiskXbe = ReadSectors(iso, vol.DiscLseek, xbeEntry.StartSector, 1);
        Assert.Equal(0xEB, onDiskXbe[107]);
        Assert.Equal(pattern[..7], onDiskXbe[100..107]);

        var binEntry = XisoReader.GetEntryInfo(iso, "/file1.txt");
        Assert.NotNull(binEntry);
        var onDiskBin = ReadSectors(iso, vol.DiscLseek, binEntry.StartSector, 1);
        Assert.Equal(pattern, onDiskBin[100..108]);
    }

    [Fact]
    public void CopyIn_CaseInsensitiveInternalPath()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
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
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "via cli");

        Assert.Equal(0, Program.Main(["--copy-in", iso, host, "/file1.txt"]));
        Assert.Equal("via cli", File.ReadAllText(CopyOutToTemp(iso, "/file1.txt")));
        Assert.True(File.Exists(iso + ".old"));
    }

    [Fact]
    public void Cli_CopyIn_MissingPositionals_ReturnsOne()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        Assert.Equal(1, Program.Main(["--copy-in", iso]));
    }

    [Fact]
    public void Cli_CopyIn_CombinedWithExtract_ReturnsOne()
    {
        var src = CreateTempDir("xiso_patch_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var host = Path.Combine(CreateTempDir("xiso_patch_host"), "new.bin");
        File.WriteAllText(host, "x");

        Assert.Equal(1, Program.Main(["-x", "--copy-in", iso, host, "/file1.txt"]));
        Assert.Equal(1, Program.Main(["--no-backup"]));
    }
}
