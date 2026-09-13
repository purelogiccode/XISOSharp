using XISOSharp.BlockDevice;
using XISOSharp.Models;
using ZArchiveSharp;

namespace XISOSharp.Tests;

/// <summary>
/// Rebuilt "sector 0" XISOs: the volume descriptor sits at the very start of the
/// image instead of sector 32. SimpleXisoDrive's mount path supports this layout,
/// so probe/verify/list/explorer/filetime must all accept it.
/// </summary>
[Collection("Sequential")]
public sealed class XisoRebuiltSector0Tests : IDisposable
{
    private const ulong TestFileTime = 0x01D0000000000000UL;

    private static readonly byte[] FileContent = CreatePattern(300_000);

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
                /* best effort cleanup */
            }
        }
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_sector0_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static byte[] CreatePattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) & 0xFF);
        }

        return data;
    }

    /// <summary>
    /// Packs a standard image, then derives the rebuilt sector-0 variant by copying
    /// the descriptor sector to offset 0 and clearing the standard location so the
    /// sector-0 probe is the only candidate that can match.
    /// </summary>
    private (string StandardPath, string RebuiltPath) CreateImages()
    {
        Logger.RealQuiet = true;

        string root = NewTempDir();
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "default.xbe"), FileContent);
        File.WriteAllText(Path.Combine(src, "sub", "readme.txt"), "hello rebuilt");

        string standard = Path.Combine(root, "standard.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(src, standard, fileTime: TestFileTime));

        string rebuilt = Path.Combine(root, "rebuilt.iso");
        ToSector0Layout(standard, rebuilt);
        return (standard, rebuilt);
    }

    private static void ToSector0Layout(string sourcePath, string destPath)
    {
        File.Copy(sourcePath, destPath, overwrite: true);
        using FileStream fs = new(destPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] header = new byte[Constants.SectorSize];
        fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
        fs.ReadExactly(header);
        fs.Seek(0, SeekOrigin.Begin);
        fs.Write(header);

        Array.Clear(header);
        fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
        fs.Write(header);
    }

    [Fact]
    public void GetVolumeInfo_DetectsSector0Descriptor()
    {
        (string standard, string rebuilt) = CreateImages();

        VolumeInfo standardInfo = XisoReader.GetVolumeInfo(standard);
        VolumeInfo rebuiltInfo = XisoReader.GetVolumeInfo(rebuilt);

        Assert.True(standardInfo.IsValid);
        Assert.True(rebuiltInfo.IsValid);
        Assert.Equal(Constants.HeaderOffset / Constants.SectorSize, standardInfo.DescriptorSector);
        Assert.Equal(0, rebuiltInfo.DescriptorSector);
        Assert.Equal(0, rebuiltInfo.DiscLseek);
        Assert.Equal(standardInfo.RootDirSector, rebuiltInfo.RootDirSector);
        Assert.Equal(standardInfo.RootDirSize, rebuiltInfo.RootDirSize);
        Assert.Equal(standardInfo.FileTimeRaw, rebuiltInfo.FileTimeRaw);
        Assert.Equal(standardInfo.CreationTime, rebuiltInfo.CreationTime);
    }

    [Fact]
    public void VerifyXiso_ProbesSector0_ForStreamAndBlockDevice()
    {
        (string standard, string rebuilt) = CreateImages();

        using FileStream standardStream = File.OpenRead(standard);
        (uint stdSector, uint stdSize, long stdLseek) = XisoReader.VerifyXiso(standardStream, "standard.iso");
        Assert.Equal(0, stdLseek);

        using FileStream rebuiltStream = File.OpenRead(rebuilt);
        (uint rootSector, uint rootSize, long discLseek) = XisoReader.VerifyXiso(rebuiltStream, "rebuilt.iso");
        Assert.Equal(0, discLseek);
        Assert.Equal(stdSector, rootSector);
        Assert.Equal(stdSize, rootSize);

        using FileBlockDevice dev = new(rebuilt, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        (uint devSector, uint devSize, long devLseek) = XisoReader.VerifyXiso(dev, "rebuilt.iso");
        Assert.Equal(0, devLseek);
        Assert.Equal(rootSector, devSector);
        Assert.Equal(rootSize, devSize);
    }

    [Fact]
    public void Explorer_ListsAndReadsSector0Image()
    {
        (_, string rebuilt) = CreateImages();

        using XisoExplorer explorer = new(rebuilt,
            new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });

        Assert.True(explorer.Volume.IsValid);
        Assert.Equal(0, explorer.Volume.DescriptorSector);
        Assert.Equal((long)new FileInfo(rebuilt).Length, explorer.Volume.FileLength);

        ExplorerNode? file = explorer.GetNode("/default.xbe");
        Assert.NotNull(file);
        Assert.False(file.IsDirectory);
        Assert.Equal(FileContent.Length, file.Size);

        using (Stream data = explorer.OpenReadStream(file))
        {
            byte[] read = new byte[FileContent.Length];
            int total = 0;
            while (total < read.Length)
            {
                int n = data.Read(read, total, read.Length - total);
                Assert.True(n > 0);
                total += n;
            }

            Assert.Equal(FileContent, read);
        }

        IReadOnlyList<ExplorerNode> root = explorer.ListChildren("/");
        Assert.Equal(2, root.Count);
        Assert.Contains(root, n => string.Equals(n.Name, "default.xbe", StringComparison.Ordinal));
        Assert.Contains(root, n => string.Equals(n.Name, "sub", StringComparison.Ordinal) && n.IsDirectory);

        ExplorerNode? subFile = explorer.GetNode("/sub/readme.txt");
        Assert.NotNull(subFile);
        Assert.Equal("hello rebuilt"u8.Length, subFile.Size);

        FileAttributes attrs = XisoAttributes.ToWindowsFileAttributes(file.Attributes);
        Assert.True(attrs.HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void ListDirectory_And_ReadFileBytes_WorkOnSector0Image()
    {
        (_, string rebuilt) = CreateImages();

        using FileStream fs = File.OpenRead(rebuilt);

        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(fs, "rebuilt.iso", "/");
        Assert.Equal(2, entries.Count);

        EntryInfo? file = XisoReader.GetEntryInfo(fs, "rebuilt.iso", "/default.xbe");
        Assert.NotNull(file);
        Assert.Equal((uint)FileContent.Length, file.FileSize);

        byte[] buffer = new byte[1024];
        int read = XisoReader.ReadFileBytes(fs, "rebuilt.iso", "/default.xbe", buffer, 4096);
        Assert.Equal(1024, read);
        Assert.Equal(FileContent.AsSpan(4096, 1024).ToArray(), buffer);
    }

    [Fact]
    public void GetFileTime_MatchesStandardVariant()
    {
        (string standard, string rebuilt) = CreateImages();

        Assert.Equal(TestFileTime, XisoReader.GetFileTimeRaw(standard));
        Assert.Equal(TestFileTime, XisoReader.GetFileTimeRaw(rebuilt));
        Assert.Equal(XisoReader.GetFileTime(standard), XisoReader.GetFileTime(rebuilt));
    }

    [Fact]
    public void SetFileTime_WritesDescriptorAtOffsetZero()
    {
        (_, string rebuilt) = CreateImages();

        const ulong updated = 0x01E0000000000000UL;
        XisoReader.SetFileTime(rebuilt, updated);

        Assert.Equal(updated, XisoReader.GetFileTimeRaw(rebuilt));
        Assert.Equal(updated, XisoReader.GetVolumeInfo(rebuilt).FileTimeRaw);
    }

    [Fact]
    public void OffsetBlockDevice_Probe_FindsSector0Descriptor()
    {
        (_, string rebuilt) = CreateImages();

        using FileBlockDevice inner = new(rebuilt, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using OffsetBlockDevice view = OffsetBlockDevice.Probe(inner, "rebuilt.iso");

        Assert.Equal(0, view.Offset);

        (uint rootSector, uint rootSize, long discLseek) = XisoReader.VerifyXiso(view, "rebuilt.iso");
        Assert.Equal(0, discLseek);
        Assert.True(rootSector > 0);
        Assert.True(rootSize > 0);
    }

    private static byte[] ReadExplorerFile(XisoExplorer explorer, string path)
    {
        ExplorerNode? node = explorer.GetNode(path);
        Assert.NotNull(node);
        byte[] data = new byte[(int)node.Size];
        using Stream stream = explorer.OpenReadStream(node);
        int total = 0;
        while (total < data.Length)
        {
            int n = stream.Read(data, total, data.Length - total);
            Assert.True(n > 0);
            total += n;
        }

        return data;
    }

    [Fact]
    public void GetSectorLayout_MarksDescriptorSectorUsed()
    {
        (string standard, string rebuilt) = CreateImages();

        SectorLayout standardLayout = XisoReader.GetSectorLayout(standard);
        SectorLayout rebuiltLayout = XisoReader.GetSectorLayout(rebuilt);

        Assert.Equal(Constants.HeaderOffset / Constants.SectorSize, standardLayout.Volume.DescriptorSector);
        Assert.Equal(0, rebuiltLayout.Volume.DescriptorSector);

        // The live descriptor sector must never surface as allocatable space.
        Assert.Contains(rebuiltLayout.UsedRanges, r => r.StartSector == 0 && r.SectorCount >= 1);
        Assert.DoesNotContain(rebuiltLayout.FreeRanges, r => r.StartSector == 0);
        Assert.Contains(standardLayout.UsedRanges, r =>
            r.StartSector <= Constants.HeaderOffset / Constants.SectorSize &&
            r.StartSector + r.SectorCount > Constants.HeaderOffset / Constants.SectorSize);
    }

    [Fact]
    public void CopyIn_AddFile_KeepsSector0ImageValid()
    {
        (_, string rebuilt) = CreateImages();
        byte[] added = CreatePattern(5000);
        string host = Path.Combine(NewTempDir(), "added.bin");
        File.WriteAllBytes(host, added);

        XisoReader.CopyIn(rebuilt, host, "/added.bin", createBackup: false);

        VolumeInfo vol = XisoReader.GetVolumeInfo(rebuilt);
        Assert.True(vol.IsValid);
        Assert.Equal(0, vol.DescriptorSector);
        Assert.Equal(0, vol.DiscLseek);

        using XisoExplorer explorer = new(rebuilt,
            new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });
        Assert.Equal(added, ReadExplorerFile(explorer, "/added.bin"));
        Assert.Equal(FileContent, ReadExplorerFile(explorer, "/default.xbe"));
    }

    [Fact]
    public void CopyIn_ReplaceFile_KeepsSector0ImageValid()
    {
        (_, string rebuilt) = CreateImages();
        byte[] replacement = CreatePattern(310_000);
        string host = Path.Combine(NewTempDir(), "default.xbe");
        File.WriteAllBytes(host, replacement);

        XisoReader.CopyIn(rebuilt, host, "/default.xbe", createBackup: false);

        VolumeInfo vol = XisoReader.GetVolumeInfo(rebuilt);
        Assert.True(vol.IsValid);
        Assert.Equal(0, vol.DescriptorSector);

        using XisoExplorer explorer = new(rebuilt,
            new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });
        Assert.Equal(replacement, ReadExplorerFile(explorer, "/default.xbe"));
    }

    [Fact]
    public void CopyIn_RootTableMove_UpdatesActiveSector0Header()
    {
        // 102 x 20-byte entries = 2040-byte root table (1 sector); one more entry
        // pushes it past 2048, forcing the root table to relocate. The volume
        // header at offset 0 (not the zeroed copy at 0x10000) must be updated.
        string root = NewTempDir();
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 102; i++)
            File.WriteAllBytes(Path.Combine(src, $"f{i:D3}"), []);

        string standard = Path.Combine(root, "standard.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(src, standard));
        string rebuilt = Path.Combine(root, "rebuilt.iso");
        ToSector0Layout(standard, rebuilt);

        VolumeInfo before = XisoReader.GetVolumeInfo(rebuilt);
        Assert.True(before.IsValid);

        string host = Path.Combine(NewTempDir(), "newf");
        File.WriteAllBytes(host, "0123456789"u8.ToArray());
        XisoReader.CopyIn(rebuilt, host, "/newf", createBackup: false);

        VolumeInfo after = XisoReader.GetVolumeInfo(rebuilt);
        Assert.True(after.IsValid);
        Assert.Equal(0, after.DescriptorSector);
        Assert.NotEqual(before.RootDirSector, after.RootDirSector);

        using XisoExplorer explorer = new(rebuilt,
            new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });
        Assert.Equal("0123456789"u8.ToArray(), ReadExplorerFile(explorer, "/newf"));
    }

    [Fact]
    public void XisoRangesAndZar_WorkOnSector0Image()
    {
        (string standard, string rebuilt) = CreateImages();

        (List<(uint Start, uint End)> stdSys, List<(uint Start, uint End)> stdFiles) =
            XisoRanges.GetXisoRanges(standard);
        (List<(uint Start, uint End)> sys, List<(uint Start, uint End)> files) =
            XisoRanges.GetXisoRanges(rebuilt);

        Assert.NotEmpty(files);
        Assert.Equal(stdFiles, files);
        Assert.Contains(sys, r => r.Start == 0);
        Assert.DoesNotContain(stdSys, r => r.Start == 0);
        Assert.Equal(XisoRanges.GetFileEntries(standard, 0), XisoRanges.GetFileEntries(rebuilt, 0));

        string zar = Path.Combine(NewTempDir(), "rebuilt.zar");
        Assert.True(XisoZarchive.CreateZar(rebuilt, zar, 0, quiet: true));
        using ZArchiveReader? reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        Assert.Equal((ulong)FileContent.Length, reader.GetFileSize(reader.LookUp("default.xbe")));
        Assert.Equal((ulong)"hello rebuilt"u8.Length, reader.GetFileSize(reader.LookUp("sub/readme.txt")));
    }
}
