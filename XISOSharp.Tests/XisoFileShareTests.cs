namespace XISOSharp.Tests;

/// <summary>
/// File-share control (2.4): <c>OpenImageStream(path, FileShare)</c> and
/// <c>XisoExplorerOptions.Share</c> must let a mount coexist with a writer —
/// a handle open for write with <see cref="FileShare.Write"/> blocks the old
/// <see cref="FileShare.Read"/> default but not <see cref="FileShare.ReadWrite"/>.
/// </summary>
[Collection("Sequential")]
public sealed class XisoFileShareTests : IDisposable
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

    private string CreateIso()
    {
        string src = CreateTempDir("xiso_share_src");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "shared image");
        File.WriteAllText(Path.Combine(src, "data.bin"), new string('y', 5000));
        string outDir = CreateTempDir("xiso_share_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    /// <summary>
    /// Holds a write-capable handle that denies other writers but shares reads:
    /// the same shape an AV scanner or sync client takes on a mounted .iso.
    /// </summary>
    private static FileStream HoldWriterHandle(string iso) =>
        new(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    [Fact]
    public void OpenImageStream_ReadWriteShare_OpensWhileWriterHoldsFile()
    {
        string iso = CreateIso();
        using FileStream writer = HoldWriterHandle(iso);

        using Stream stream = XisoReader.OpenImageStream(iso, FileShare.ReadWrite);

        Assert.True(XisoReader.GetVolumeInfo(stream, iso).IsValid);
    }

    [Fact]
    public void OpenImageStream_DefaultShare_FailsWhileWriterHoldsFile()
    {
        string iso = CreateIso();
        using FileStream writer = HoldWriterHandle(iso);

        // The 1.0.2 default stays FileShare.Read: a mounted image whose writer
        // denies sharing still blocks the open, by design.
        Assert.Throws<IOException>(() => XisoReader.OpenImageStream(iso));
    }

    [Fact]
    public void Explorer_ShareReadWrite_OpensAndReadsWhileWriterHoldsFile()
    {
        string iso = CreateIso();
        using FileStream writer = HoldWriterHandle(iso);

        using XisoExplorer explorer = new(iso, new XisoExplorerOptions
        {
            KeepOpen = true,
            Share = FileShare.ReadWrite,
        });

        Assert.Equal(2, explorer.ListChildren("/").Count);
        using Stream readme = explorer.OpenReadStream("/readme.txt");
        using MemoryStream copy = new();
        readme.CopyTo(copy);
        Assert.Equal("shared image", System.Text.Encoding.ASCII.GetString(copy.ToArray()));
    }

    [Fact]
    public void Explorer_DefaultShare_FailsWhileWriterHoldsFile()
    {
        string iso = CreateIso();
        using FileStream writer = HoldWriterHandle(iso);

        Assert.Throws<IOException>(() => new XisoExplorer(iso));
    }

    [Fact]
    public void Explorer_ShareReadWrite_StatelessOpenReadStreamWorksWhileWriterHoldsFile()
    {
        string iso = CreateIso();
        using FileStream writer = HoldWriterHandle(iso);
        using XisoExplorer explorer = new(iso, new XisoExplorerOptions { Share = FileShare.ReadWrite });

        using Stream readme = explorer.OpenReadStream("/readme.txt");
        using MemoryStream copy = new();
        readme.CopyTo(copy);
        Assert.Equal("shared image", System.Text.Encoding.ASCII.GetString(copy.ToArray()));
    }

    [Fact]
    public void Ciso_ShareReadWrite_OpensWhileWriterHoldsContainer()
    {
        string iso = CreateIso();
        string cso = Path.Combine(Path.GetDirectoryName(iso)!, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, level: 0));

        using FileStream writer = new(cso, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using Stream stream = XisoReader.OpenImageStream(cso, FileShare.ReadWrite);

        Assert.True(XisoReader.GetVolumeInfo(stream, cso).IsValid);
    }
}
