using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="SectorAllocator"/> and deterministic image generation
/// (TODO #2, xdvdfs #101): contiguous allocation with overlap prevention, plus
/// byte-identical output for identical input (sorted entries, fixed FILETIME).
/// </summary>
[Collection("Sequential")]
public class SectorAllocatorTests : IDisposable
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

    private string CreateIso(string srcDir, ulong? fileTime = null)
    {
        var outDir = CreateTempDir("xiso_alloc_out");
        var rc = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null,
            fileTime: fileTime);
        Assert.Equal(0, rc);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Theory]
    [InlineData(0UL, 0u)]
    [InlineData(1UL, 1u)]
    [InlineData(2047UL, 1u)]
    [InlineData(2048UL, 1u)]
    [InlineData(2049UL, 2u)]
    [InlineData(4096UL, 2u)]
    [InlineData(4097UL, 3u)]
    public void RequiredSectors_RoundsUp(ulong bytes, uint expected)
    {
        Assert.Equal(expected, SectorAllocator.RequiredSectors(bytes));
    }

    [Fact]
    public void AllocateContiguous_StartsAtRootSectorAndBumps()
    {
        var allocator = new SectorAllocator();

        Assert.Equal((uint)Constants.RootDirectorySector, allocator.FirstFreeSector);
        Assert.Equal((uint)Constants.RootDirectorySector, allocator.NextFree);

        var first = allocator.AllocateContiguous(1);
        var second = allocator.AllocateContiguous(3);

        Assert.Equal((uint)Constants.RootDirectorySector, first);
        Assert.Equal((uint)Constants.RootDirectorySector + 1, second);
        Assert.Equal((uint)Constants.RootDirectorySector + 4, allocator.NextFree);
        Assert.Equal(4UL, allocator.AllocatedSectorCount);
    }

    [Fact]
    public void AllocateForBytes_AdvancesByCeiling()
    {
        var allocator = new SectorAllocator();
        var start = allocator.AllocateForBytes(5000);

        Assert.Equal((uint)Constants.RootDirectorySector, start);
        Assert.Equal((uint)Constants.RootDirectorySector + 3, allocator.NextFree);
    }

    [Fact]
    public void AllocateContiguous_ZeroCount_DoesNotConsume()
    {
        var allocator = new SectorAllocator();

        var first = allocator.AllocateContiguous(0);
        var second = allocator.AllocateContiguous(0);

        Assert.Equal(first, second);
        Assert.Equal((uint)Constants.RootDirectorySector, allocator.NextFree);
        Assert.Equal(0UL, allocator.AllocatedSectorCount);
        Assert.Empty(allocator.UsedRanges);
    }

    [Fact]
    public void NextFree_TopOfSpace_ThrowsInsteadOfClamping()
    {
        // BUG-LIB-031: the old uint.MaxValue clamp hid allocation overflow.
        var allocator = new SectorAllocator(0);
        allocator.MarkUsed(uint.MaxValue, 1);

        Assert.Throws<InvalidOperationException>(() => allocator.NextFree);
    }

    [Theory]
    [InlineData(302u, 1u)] // inside
    [InlineData(298u, 5u)] // straddles start
    [InlineData(300u, 5u)] // exact duplicate
    [InlineData(299u, 10u)] // contains
    [InlineData(304u, 10u)] // straddles end
    public void MarkUsed_Overlap_Throws(uint start, uint count)
    {
        var allocator = new SectorAllocator();
        allocator.MarkUsed(300, 5);

        Assert.Throws<ArgumentException>(() => allocator.MarkUsed(start, count));
    }

    [Theory]
    [InlineData(295u, 5u)] // adjacent below
    [InlineData(305u, 3u)] // adjacent above
    [InlineData(400u, 7u)] // disjoint
    public void MarkUsed_AdjacentOrDisjoint_Succeeds(uint start, uint count)
    {
        var allocator = new SectorAllocator();
        allocator.MarkUsed(300, 5);
        allocator.MarkUsed(start, count);

        Assert.False(allocator.IsFree(start, count));
    }

    [Fact]
    public void MarkUsed_CoalescesAdjacentRanges()
    {
        var allocator = new SectorAllocator(10, 100);
        allocator.MarkUsed(12, 3);
        allocator.MarkUsed(15, 5);

        var used = allocator.UsedRanges;
        var single = Assert.Single(used);
        Assert.Equal(12u, single.StartSector);
        Assert.Equal(8u, single.SectorCount);
    }

    [Fact]
    public void MarkUsed_ZeroCount_IsNoOp()
    {
        var allocator = new SectorAllocator();
        allocator.MarkUsed(300, 0);

        Assert.Empty(allocator.UsedRanges);
        Assert.True(allocator.IsFree(300, 5));
    }

    [Fact]
    public void BoundedAllocator_FirstFitReusesGaps()
    {
        var allocator = new SectorAllocator(10, 30);
        allocator.MarkUsed(10, 5);
        allocator.MarkUsed(20, 5);

        // Gap [15, 20) is reused before the tail.
        Assert.Equal(15u, allocator.AllocateContiguous(4));
        Assert.Equal(25u, allocator.AllocateContiguous(5));
        Assert.Throws<InvalidOperationException>(() => allocator.AllocateContiguous(6));
    }

    [Fact]
    public void BoundedAllocator_ExactFitAtEnd_Succeeds()
    {
        var allocator = new SectorAllocator(10, 20);

        Assert.Equal(10u, allocator.AllocateContiguous(10));
        Assert.Empty(allocator.FreeRanges);
        Assert.Throws<InvalidOperationException>(() => allocator.AllocateContiguous(1));
    }

    [Fact]
    public void MarkUsed_BeyondTotal_Throws()
    {
        var allocator = new SectorAllocator(0, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.MarkUsed(99, 2));
        allocator.MarkUsed(99, 1);
        Assert.False(allocator.IsFree(99, 1));
    }

    [Fact]
    public void AllocateContiguous_AtAddressableEnd_ThrowsOnOverflow()
    {
        var allocator = new SectorAllocator(uint.MaxValue - 1);

        Assert.Equal(uint.MaxValue - 1, allocator.AllocateContiguous(1));
        Assert.Equal(uint.MaxValue, allocator.AllocateContiguous(1));
        Assert.Throws<InvalidOperationException>(() => allocator.AllocateContiguous(1));

        var full = new SectorAllocator(uint.MaxValue);
        Assert.Throws<InvalidOperationException>(() => full.AllocateContiguous(2));
    }

    [Fact]
    public void Ctor_InvalidBounds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectorAllocator(101, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectorAllocator(0, -1));
    }

    [Fact]
    public void FreeRanges_RequiresBoundedImage()
    {
        var allocator = new SectorAllocator();
        Assert.Throws<InvalidOperationException>(() => allocator.FreeRanges);
    }

    [Fact]
    public void UsedAndFreeRanges_TilePartition()
    {
        var allocator = new SectorAllocator(10, 30);
        allocator.MarkUsed(12, 3);
        allocator.MarkUsed(15, 5);
        allocator.MarkUsed(20, 4);

        // Adjacent marks coalesce: used = [12, 24).
        var used = allocator.UsedRanges;
        var single = Assert.Single(used);
        Assert.Equal(12u, single.StartSector);
        Assert.Equal(12u, single.SectorCount);

        var free = allocator.FreeRanges;
        Assert.Equal(2, free.Count);
        Assert.Equal(new SectorRange(10, 2), free[0]);
        Assert.Equal(new SectorRange(24, 6), free[1]);
    }

    [Fact]
    public void FromLayout_SeedsUsedAndAllocatesIntoFreeGap()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var layout = XisoReader.GetSectorLayout(iso);

        var allocator = SectorAllocator.FromLayout(layout);

        Assert.Equal(layout.TotalSectors, allocator.TotalSectors);
        Assert.Equal(layout.UsedRanges, allocator.UsedRanges);
        Assert.Equal(layout.FreeRanges, allocator.FreeRanges);

        var gap = layout.FreeRanges.First(r => r.SectorCount >= 2);
        Assert.True(allocator.IsFree(gap.StartSector, 2));

        var pos = allocator.AllocateContiguous(2);
        Assert.Equal(gap.StartSector, pos);
        Assert.False(allocator.IsFree(gap.StartSector, 2));

        // The new allocation overlaps nothing the image already uses.
        foreach (var used in layout.UsedRanges)
        {
            var usedEnd = (ulong)used.StartSector + used.SectorCount;
            Assert.True(pos + 2 <= used.StartSector || pos >= usedEnd);
        }
    }

    [Fact]
    public void FromLayout_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SectorAllocator.FromLayout(null!));
    }

    [Fact]
    public void Writer_AllocationsDoNotOverlap()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        var iso = CreateIso(src);
        var layout = XisoReader.GetSectorLayout(iso);

        // Every non-empty extent the writer placed must be pairwise disjoint —
        // the allocator-backed offset pass guarantees this by construction.
        var extents = layout.Entries.Where(e => e.SectorCount > 0).ToList();
        Assert.NotEmpty(extents);
        for (var i = 0; i < extents.Count; i++)
        {
            for (var j = i + 1; j < extents.Count; j++)
            {
                var aEnd = (ulong)extents[i].StartSector + extents[i].SectorCount;
                var bEnd = (ulong)extents[j].StartSector + extents[j].SectorCount;
                Assert.True(aEnd <= extents[j].StartSector || bEnd <= extents[i].StartSector,
                    $"Overlap: {extents[i].Path} [{extents[i].StartSector}, {aEnd}) vs " +
                    $"{extents[j].Path} [{extents[j].StartSector}, {bEnd})");
            }
        }
    }

    [Fact]
    public void CreateXiso_FixedFileTimeZero_IsDeterministic()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);

        var first = CreateIso(src, fileTime: 0);
        var second = CreateIso(src, fileTime: 0);

        Assert.Equal(0UL, XisoReader.GetFileTimeRaw(first));
        Assert.Equal(0UL, XisoReader.GetFileTimeRaw(second));

        var firstHash = SHA256.HashData(File.ReadAllBytes(first));
        var secondHash = SHA256.HashData(File.ReadAllBytes(second));
        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public void CreateXiso_DefaultFileTime_IsCurrentTime()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        var iso = CreateIso(src);

        Assert.True(XisoReader.GetFileTimeRaw(iso) > 0);
    }

    [Fact]
    public void CreateXiso_FixedFileTime_RoundTrips()
    {
        const ulong stamp = 0x01D7A3C8F1234567UL;
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        var iso = CreateIso(src, fileTime: stamp);

        Assert.Equal(stamp, XisoReader.GetFileTimeRaw(iso));
    }

    [Fact]
    public void PackFromDirectory_FixedFileTime_PassesThrough()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        var outDir = CreateTempDir("xiso_alloc_out");
        var isoPath = Path.Combine(outDir, "packed.iso");

        var rc = XisoWriter.PackFromDirectory(src, isoPath, fileTime: 0);

        Assert.Equal(0, rc);
        Assert.Equal(0UL, XisoReader.GetFileTimeRaw(isoPath));
    }

    [Fact]
    public void RemapBuildImage_FixedFileTime_IsDeterministic()
    {
        var src = CreateTempDir("xiso_alloc_src");
        PopulateMixed(src);
        Assert.True(RemapRule.TryParse("**:{0}", out var rule, out _));

        var first = Path.Combine(CreateTempDir("xiso_alloc_out"), "remap1.iso");
        var second = Path.Combine(CreateTempDir("xiso_alloc_out"), "remap2.iso");

        Assert.Equal(0, RemapFilesystem.BuildImage(src, first, [rule!], fileTime: 0));
        Assert.Equal(0, RemapFilesystem.BuildImage(src, second, [rule!], fileTime: 0));

        Assert.Equal(0UL, XisoReader.GetFileTimeRaw(first));
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(first)), SHA256.HashData(File.ReadAllBytes(second)));
    }
}
