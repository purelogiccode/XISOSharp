using System.Buffers.Binary;
using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.GetSectorLayout"/> (TODO #4, xdvdfs #49):
/// every file and directory table mapped to its sector range, with merged used
/// ranges and free gaps tiling the partition.
/// </summary>
[Collection("Sequential")]
public class XisoSectorLayoutTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
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

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        var src = CreateTempDir("xiso_lay_src");
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir)
    {
        var outDir = CreateTempDir("xiso_lay_out");
        var result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bin = new byte[length];
        new Random(seed).NextBytes(bin);
        return bin;
    }

    private static void PopulateMixed(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "file2.txt"), RandomBytes(5000, 42));
        File.WriteAllBytes(Path.Combine(dir, "empty.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested");
        File.WriteAllBytes(Path.Combine(dir, "subdir", "data.bin"), RandomBytes(7000, 7));
    }

    private static uint SectorCountFor(long byteSize) =>
        byteSize == 0 ? 0u : (uint)((byteSize + Constants.SectorSize - 1) / Constants.SectorSize);

    [Fact]
    public void GetSectorLayout_MapsFilesToCorrectSectorsAndBytes()
    {
        var src = CreateSourceDir(PopulateMixed);
        var iso = CreateIso(src);
        var vol = XisoReader.GetVolumeInfo(iso);
        Assert.True(vol.IsValid);

        var layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(vol.RootDirSector, layout.Volume.RootDirSector);
        var expectedFiles = new[] { "/file1.txt", "/file2.txt", "/empty.txt", "/subdir/nested.txt", "/subdir/data.bin" };
        foreach (var path in expectedFiles)
        {
            var extent = Assert.Single(layout.Entries, e => string.Equals(e.Path, path, StringComparison.Ordinal) && !e.IsDirectory);
            var info = XisoReader.GetEntryInfo(iso, path);
            Assert.NotNull(info);
            Assert.Equal(info.StartSector, extent.StartSector);
            var sourceBytes = File.ReadAllBytes(Path.Combine(src, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal((uint)sourceBytes.Length, extent.FileSize);
            Assert.Equal(SectorCountFor(sourceBytes.Length), extent.SectorCount);

            // Physical proof: bytes at the mapped sectors equal the source file.
            if (sourceBytes.Length > 0)
            {
                using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(vol.DiscLseek + (long)extent.StartSector * Constants.SectorSize, SeekOrigin.Begin);
                var actual = new byte[sourceBytes.Length];
                var read = 0;
                while (read < actual.Length)
                {
                    var n = fs.Read(actual, read, actual.Length - read);
                    Assert.True(n > 0);
                    read += n;
                }

                Assert.Equal(sourceBytes, actual);
            }
        }

        // Multi-sector spans: 5000 bytes -> 3 sectors, 7000 -> 4.
        Assert.Equal(3u, layout.Entries.Single(e => string.Equals(e.Path, "/file2.txt", StringComparison.Ordinal)).SectorCount);
        Assert.Equal(4u, layout.Entries.Single(e => string.Equals(e.Path, "/subdir/data.bin", StringComparison.Ordinal)).SectorCount);
        Assert.Equal(0u, layout.Entries.Single(e => string.Equals(e.Path, "/empty.txt", StringComparison.Ordinal)).SectorCount);
    }

    [Fact]
    public void GetSectorLayout_IncludesDirectoryTables()
    {
        var src = CreateSourceDir(PopulateMixed);
        var iso = CreateIso(src);
        var vol = XisoReader.GetVolumeInfo(iso);

        var layout = XisoReader.GetSectorLayout(iso);

        var root = Assert.Single(layout.Entries, e => string.Equals(e.Path, "/", StringComparison.Ordinal) && e.IsDirectory);
        Assert.Equal(vol.RootDirSector, root.StartSector);
        Assert.Equal(vol.RootDirSize, root.FileSize);
        Assert.Equal(SectorCountFor(vol.RootDirSize), root.SectorCount);

        var sub = Assert.Single(layout.Entries, e => string.Equals(e.Path, "/subdir", StringComparison.Ordinal) && e.IsDirectory);
        var subInfo = XisoReader.GetEntryInfo(iso, "/subdir");
        Assert.NotNull(subInfo);
        Assert.True(subInfo.IsDirectory);
        Assert.Equal(subInfo.StartSector, sub.StartSector);
        Assert.True(sub.SectorCount >= 1);
    }

    [Fact]
    public void GetSectorLayout_UsedAndFreeTilePartition()
    {
        var src = CreateSourceDir(PopulateMixed);
        var iso = CreateIso(src);

        var layout = XisoReader.GetSectorLayout(iso);

        // Entries sorted by start sector.
        for (var i = 1; i < layout.Entries.Count; i++)
            Assert.True(layout.Entries[i].StartSector >= layout.Entries[i - 1].StartSector);

        // Used ranges sorted and non-overlapping.
        for (var i = 1; i < layout.UsedRanges.Count; i++)
            Assert.True((long)layout.UsedRanges[i].StartSector >=
                        (long)layout.UsedRanges[i - 1].StartSector + layout.UsedRanges[i - 1].SectorCount);

        // Used + free cover [0, TotalSectors) exactly once.
        var coverage = new int[(int)layout.TotalSectors];
        foreach (var r in layout.UsedRanges.Concat(layout.FreeRanges))
        {
            for (long s = r.StartSector; s < (long)r.StartSector + r.SectorCount; s++)
                coverage[(int)s]++;
        }

        Assert.All(coverage, c => Assert.Equal(1, c));

        // Header sector is always allocated; every named non-empty extent is covered.
        Assert.Contains(layout.UsedRanges,
            r => r.StartSector <= Constants.HeaderOffset / Constants.SectorSize &&
                 (long)r.StartSector + r.SectorCount > Constants.HeaderOffset / Constants.SectorSize);
        foreach (var e in layout.Entries.Where(e => e.SectorCount > 0))
        {
            Assert.Contains(layout.UsedRanges,
                r => r.StartSector <= e.StartSector &&
                     (long)r.StartSector + r.SectorCount >= (long)e.StartSector + e.SectorCount);
        }
    }

    [Fact]
    public void GetSectorLayout_InvalidImage_Throws()
    {
        var bad = Path.Combine(CreateTempDir("xiso_lay_bad"), "bad.iso");
        File.WriteAllBytes(bad, "not an xiso image at all"u8.ToArray());

        Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
    }

    [Fact]
    public void GetSectorLayout_CorruptRightOffset_ThrowsInvalidToc()
    {
        var src = CreateSourceDir(PopulateMixed);
        var iso = CreateIso(src);
        var vol = XisoReader.GetVolumeInfo(iso);

        // Point the first root entry's right child far outside the table
        // (0xFFFF is the pad sentinel and would merely terminate the branch).
        var rootAbs = vol.DiscLseek + (long)vol.RootDirSector * Constants.SectorSize;
        using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(rootAbs + 2, SeekOrigin.Begin);
            Span<byte> u16 = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(u16, 0xFFFE);
            fs.Write(u16);
        }

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(iso));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSectorLayout_DirectoryCycle_ThrowsInvalidToc()
    {
        var src = CreateSourceDir(PopulateMixed);
        var iso = CreateIso(src);
        var vol = XisoReader.GetVolumeInfo(iso);
        var subInfo = XisoReader.GetEntryInfo(iso, "/subdir");
        Assert.NotNull(subInfo);

        // Rewrite the subdir entry's data sector to the root sector, so the walk
        // re-enters an already-visited table (Burnout-style cycle across tables).
        // NOTE: GetEntryInfo zeroes FileSize for directories, so locate the entry
        // by linearly scanning the packed table for its name instead.
        var rootAbs = vol.DiscLseek + (long)vol.RootDirSector * Constants.SectorSize;
        var table = File.ReadAllBytes(iso);
        var at = FindEntrySectorOffset(table, (int)rootAbs, (int)(rootAbs + vol.RootDirSize), "subdir");
        Assert.True(at >= 0, "subdir entry not found in root table");
        using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(at, SeekOrigin.Begin);
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, vol.RootDirSector);
            fs.Write(u32);
        }

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(iso));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already visited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Linearly scans the packed entries of one directory table for an entry with
    /// the given name (entries are dword-aligned: 14-byte header + name, padded to 4).
    /// Returns the file offset of the entry's sector field, or -1 when absent.
    /// </summary>
    private static int FindEntrySectorOffset(byte[] image, int tableStart, int tableEnd, string name)
    {
        var pos = tableStart;
        while (pos + 14 <= Math.Min(tableEnd, image.Length))
        {
            var nameLen = image[pos + 13];
            if (pos + 14 + nameLen > Math.Min(tableEnd, image.Length))
                break;
            var entryName = Encoding.ASCII.GetString(image, pos + 14, nameLen);
            if (string.Equals(entryName, name, StringComparison.Ordinal) &&
                (image[pos + 12] & 0x10) != 0)
                return pos + 4;
            pos += (14 + nameLen + 3) & ~3;
        }

        return -1;
    }
}
