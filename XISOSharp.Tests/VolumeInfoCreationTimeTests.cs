using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Volume descriptor FILETIME surfacing (2.3) and descriptor-sector reporting
/// (3.2): <see cref="XisoReader.GetVolumeInfo(string)"/> populates
/// <c>CreationTime</c>/<c>FileTimeRaw</c>/<c>DescriptorSector</c> from the same
/// probe used for validity, agreeing with the dedicated
/// <see cref="XisoReader.GetFileTime(string, int?)"/> read.
/// </summary>
[Collection("Sequential")]
public sealed class VolumeInfoCreationTimeTests : IDisposable
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

    private string CreateIso(ulong? fileTime = null)
    {
        string src = CreateTempDir("xiso_vol_src");
        File.WriteAllText(Path.Combine(src, "hello.txt"), "hello world");
        File.WriteAllText(Path.Combine(src, "data.bin"), "12345");
        string outDir = CreateTempDir("xiso_vol_iso");
        int rc = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null,
            fileTime: fileTime);
        Assert.Equal(0, rc);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void GetVolumeInfo_AgreesWithGetFileTime()
    {
        string iso = CreateIso();

        VolumeInfo info = XisoReader.GetVolumeInfo(iso);

        Assert.True(info.IsValid);
        Assert.Equal(XisoReader.GetFileTimeRaw(iso), info.FileTimeRaw);
        Assert.Equal(XisoReader.GetFileTime(iso), info.CreationTime);
    }

    [Fact]
    public void GetVolumeInfo_DeterministicZero_Is1601()
    {
        string iso = CreateIso(fileTime: 0);

        VolumeInfo info = XisoReader.GetVolumeInfo(iso);

        Assert.Equal(0UL, info.FileTimeRaw);
        Assert.Equal(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), info.CreationTime);
    }

    [Fact]
    public void GetVolumeInfo_KnownTime_MatchesRoundTrip()
    {
        DateTimeOffset target = new(2021, 12, 31, 23, 59, 59, TimeSpan.Zero);
        string iso = CreateIso(fileTime: FileTimeHelper.ToFileTimeRaw(target));

        VolumeInfo info = XisoReader.GetVolumeInfo(iso);

        Assert.Equal(FileTimeHelper.ToFileTimeRaw(target), info.FileTimeRaw);
        Assert.Equal(target, info.CreationTime);
    }

    [Fact]
    public void GetVolumeInfo_InvalidImage_TimeNullAndSectorMinusOne()
    {
        string work = CreateTempDir("xiso_vol_bad");
        string garbage = Path.Combine(work, "garbage.iso");
        File.WriteAllBytes(garbage, new byte[64 * 1024]);

        VolumeInfo info = XisoReader.GetVolumeInfo(garbage);

        Assert.False(info.IsValid);
        Assert.Null(info.CreationTime);
        Assert.Equal(0UL, info.FileTimeRaw);
        Assert.Equal(-1, info.DescriptorSector);
    }

    [Fact]
    public void GetVolumeInfo_GarbageFileTime_ClampsWithoutThrowing()
    {
        string iso = CreateIso(fileTime: 0);
        long offset = Constants.HeaderOffset + Constants.HeaderDataLength + 4 + 4;
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Span<byte> garbage = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(garbage, ulong.MaxValue);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(garbage);
        }

        VolumeInfo info = XisoReader.GetVolumeInfo(iso);

        Assert.True(info.IsValid); // the descriptor itself is still intact
        Assert.Equal(ulong.MaxValue, info.FileTimeRaw);
        Assert.NotNull(info.CreationTime);
        Assert.Equal(DateTimeOffset.MaxValue.Year, info.CreationTime!.Value.Year);
    }

    [Fact]
    public void DescriptorSector_StandardImage_Is32()
    {
        string iso = CreateIso();

        VolumeInfo info = XisoReader.GetVolumeInfo(iso);
        VolumeInfo explorerVolume = new XisoExplorer(iso).Volume;

        Assert.Equal(32, info.DescriptorSector);
        Assert.Equal(32, explorerVolume.DescriptorSector);
    }

    [Fact]
    public void DescriptorSector_PartitionRelative_MatchesKnownOffsets()
    {
        // Every known layout keeps the descriptor at partition-relative sector
        // 32 (HeaderOffset / 2048), regardless of the absolute disc lseek.
        string iso = CreateIso();
        Assert.Equal(Constants.HeaderOffset / Constants.SectorSize,
            XisoReader.GetVolumeInfo(iso).DescriptorSector);
    }
}
