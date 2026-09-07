using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

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

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        string src = CreateTempDir("xiso_lay_src");
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir)
    {
        string outDir = CreateTempDir("xiso_lay_out");
        int result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        byte[] bin = new byte[length];
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
        string src = CreateSourceDir(PopulateMixed);
        string iso = CreateIso(src);
        VolumeInfo vol = XisoReader.GetVolumeInfo(iso);
        Assert.True(vol.IsValid);

        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(vol.RootDirSector, layout.Volume.RootDirSector);
        string[] expectedFiles = new[]
            { "/file1.txt", "/file2.txt", "/empty.txt", "/subdir/nested.txt", "/subdir/data.bin" };
        foreach (string path in expectedFiles)
        {
            FileSectorExtent extent = Assert.Single(layout.Entries,
                e => string.Equals(e.Path, path, StringComparison.Ordinal) && !e.IsDirectory);
            EntryInfo? info = XisoReader.GetEntryInfo(iso, path);
            Assert.NotNull(info);
            Assert.Equal(info.StartSector, extent.StartSector);
            byte[] sourceBytes =
                File.ReadAllBytes(Path.Combine(src, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal((uint)sourceBytes.Length, extent.FileSize);
            Assert.Equal(SectorCountFor(sourceBytes.Length), extent.SectorCount);

            // Physical proof: bytes at the mapped sectors equal the source file.
            if (sourceBytes.Length > 0)
            {
                using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(vol.DiscLseek + ((long)extent.StartSector * Constants.SectorSize), SeekOrigin.Begin);
                byte[] actual = new byte[sourceBytes.Length];
                int read = 0;
                while (read < actual.Length)
                {
                    int n = fs.Read(actual, read, actual.Length - read);
                    Assert.True(n > 0);
                    read += n;
                }

                Assert.Equal(sourceBytes, actual);
            }
        }

        // Multi-sector spans: 5000 bytes -> 3 sectors, 7000 -> 4.
        Assert.Equal(3u,
            layout.Entries.Single(e => string.Equals(e.Path, "/file2.txt", StringComparison.Ordinal)).SectorCount);
        Assert.Equal(4u,
            layout.Entries.Single(e => string.Equals(e.Path, "/subdir/data.bin", StringComparison.Ordinal))
                .SectorCount);
        Assert.Equal(0u,
            layout.Entries.Single(e => string.Equals(e.Path, "/empty.txt", StringComparison.Ordinal)).SectorCount);
    }

    [Fact]
    public void GetSectorLayout_IncludesDirectoryTables()
    {
        string src = CreateSourceDir(PopulateMixed);
        string iso = CreateIso(src);
        VolumeInfo vol = XisoReader.GetVolumeInfo(iso);

        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        FileSectorExtent root = Assert.Single(layout.Entries,
            e => string.Equals(e.Path, "/", StringComparison.Ordinal) && e.IsDirectory);
        Assert.Equal(vol.RootDirSector, root.StartSector);
        Assert.Equal(vol.RootDirSize, root.FileSize);
        Assert.Equal(SectorCountFor(vol.RootDirSize), root.SectorCount);

        FileSectorExtent sub = Assert.Single(layout.Entries,
            e => string.Equals(e.Path, "/subdir", StringComparison.Ordinal) && e.IsDirectory);
        EntryInfo? subInfo = XisoReader.GetEntryInfo(iso, "/subdir");
        Assert.NotNull(subInfo);
        Assert.True(subInfo.IsDirectory);
        Assert.Equal(subInfo.StartSector, sub.StartSector);
        Assert.True(sub.SectorCount >= 1);
    }

    [Fact]
    public void GetSectorLayout_UsedAndFreeTilePartition()
    {
        string src = CreateSourceDir(PopulateMixed);
        string iso = CreateIso(src);

        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        // Entries sorted by start sector.
        for (int i = 1; i < layout.Entries.Count; i++)
            Assert.True(layout.Entries[i].StartSector >= layout.Entries[i - 1].StartSector);

        // Used ranges sorted and non-overlapping.
        for (int i = 1; i < layout.UsedRanges.Count; i++)
        {
            Assert.True(layout.UsedRanges[i].StartSector >=
                        (long)layout.UsedRanges[i - 1].StartSector + layout.UsedRanges[i - 1].SectorCount);
        }

        // Used + free cover [0, TotalSectors) exactly once.
        int[] coverage = new int[(int)layout.TotalSectors];
        foreach (SectorRange r in layout.UsedRanges.Concat(layout.FreeRanges))
        {
            for (long s = r.StartSector; s < (long)r.StartSector + r.SectorCount; s++)
                coverage[(int)s]++;
        }

        Assert.All(coverage, c => Assert.Equal(1, c));

        // Header sector is always allocated; every named non-empty extent is covered.
        Assert.Contains(layout.UsedRanges,
            r => r.StartSector <= Constants.HeaderOffset / Constants.SectorSize &&
                 (long)r.StartSector + r.SectorCount > Constants.HeaderOffset / Constants.SectorSize);
        foreach (FileSectorExtent e in layout.Entries.Where(e => e.SectorCount > 0))
        {
            Assert.Contains(layout.UsedRanges,
                r => r.StartSector <= e.StartSector &&
                     (long)r.StartSector + r.SectorCount >= (long)e.StartSector + e.SectorCount);
        }
    }

    [Fact]
    public void GetSectorLayout_InvalidImage_Throws()
    {
        string bad = Path.Combine(CreateTempDir("xiso_lay_bad"), "bad.iso");
        File.WriteAllBytes(bad, "not an xiso image at all"u8.ToArray());

        Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(bad));
    }

    [Fact]
    public void GetSectorLayout_CorruptRightOffset_ThrowsInvalidToc()
    {
        string src = CreateSourceDir(PopulateMixed);
        string iso = CreateIso(src);
        VolumeInfo vol = XisoReader.GetVolumeInfo(iso);

        // Point the first root entry's right child far outside the table
        // (0xFFFF is the pad sentinel and would merely terminate the branch).
        long rootAbs = vol.DiscLseek + ((long)vol.RootDirSector * Constants.SectorSize);
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(rootAbs + 2, SeekOrigin.Begin);
            Span<byte> u16 = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(u16, 0xFFFE);
            fs.Write(u16);
        }

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(iso));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSectorLayout_DirectoryCycle_ThrowsInvalidToc()
    {
        string src = CreateSourceDir(PopulateMixed);
        string iso = CreateIso(src);
        VolumeInfo vol = XisoReader.GetVolumeInfo(iso);
        EntryInfo? subInfo = XisoReader.GetEntryInfo(iso, "/subdir");
        Assert.NotNull(subInfo);

        // Rewrite the subdir entry's data sector to the root sector, so the walk
        // re-enters an already-visited table (Burnout-style cycle across tables).
        // NOTE: GetEntryInfo zeroes FileSize for directories, so locate the entry
        // by linearly scanning the packed table for its name instead.
        long rootAbs = vol.DiscLseek + ((long)vol.RootDirSector * Constants.SectorSize);
        byte[] table = File.ReadAllBytes(iso);
        int at = FindEntrySectorOffset(table, (int)rootAbs, (int)(rootAbs + vol.RootDirSize), "subdir");
        Assert.True(at >= 0, "subdir entry not found in root table");
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(at, SeekOrigin.Begin);
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, vol.RootDirSector);
            fs.Write(u32);
        }

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.GetSectorLayout(iso));
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
        int pos = tableStart;
        while (pos + 14 <= Math.Min(tableEnd, image.Length))
        {
            byte nameLen = image[pos + 13];
            if (pos + 14 + nameLen > Math.Min(tableEnd, image.Length))
                break;
            string entryName = Encoding.ASCII.GetString(image, pos + 14, nameLen);
            if (string.Equals(entryName, name, StringComparison.Ordinal) &&
                (image[pos + 12] & 0x10) != 0)
            {
                return pos + 4;
            }

            pos += (14 + nameLen + 3) & ~3;
        }

        return -1;
    }
}
