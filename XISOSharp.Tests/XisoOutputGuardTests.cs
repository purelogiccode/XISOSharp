namespace XISOSharp.Tests;

/// <summary>
/// Library backstop for the input==output safety guards (TODO #15, xdvdfs #36):
/// streaming writers throw instead of truncating an input they are still
/// reading. Dummy files suffice — every guard fires before any content parse.
/// </summary>
[Collection("Sequential")]
public sealed class XisoOutputGuardTests : IDisposable
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
                // ignored
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_outguard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateDummy(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    [Fact]
    public void CompressToCso_OutputEqualsSource_Throws()
    {
        var dir = CreateTempDir();
        var src = CreateDummy(dir, "game.iso");
        var ex = Assert.Throws<IOException>(() => CisoWriter.CompressToCso(src, src));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void CompressToCso_SourceCollidesWithSplitPart_Throws()
    {
        var dir = CreateTempDir();
        var src = CreateDummy(dir, "game.1.cso");
        var ex = Assert.Throws<IOException>(() =>
            CisoWriter.CompressToCso(src, Path.Combine(dir, "game.cso"), splitBytes: 2048));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void DecompressToIso_OutputEqualsSource_Throws()
    {
        var dir = CreateTempDir();
        var src = CreateDummy(dir, "game.cso");
        var ex = Assert.Throws<IOException>(() => CisoReader.DecompressToIso(src, src));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void RebuildRedump_OutputEqualsPart_Throws()
    {
        var dir = CreateTempDir();
        var xiso = CreateDummy(dir, "game.xiso");
        var video = CreateDummy(dir, "game.video.iso");
        var ex = Assert.Throws<IOException>(() =>
            XisoRedump.RebuildRedump(xiso, video, null, null, xiso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WipeFiller_OutputEqualsInput_Throws()
    {
        var dir = CreateTempDir();
        var iso = CreateDummy(dir, "game.iso");
        var ex = Assert.Throws<IOException>(() => XisoOperations.WipeFiller(iso, iso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4096, new FileInfo(iso).Length);
    }

    [Fact]
    public void WipeAndTrim_OutputEqualsInput_Throws()
    {
        var dir = CreateTempDir();
        var iso = CreateDummy(dir, "game.iso");
        var ex = Assert.Throws<IOException>(() => XisoOperations.WipeAndTrim(iso, iso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4096, new FileInfo(iso).Length);
    }

    [Fact]
    public void TrimXiso_SamePath_StillTrimsInPlace()
    {
        // In-place trim is an explicit, safe semantic (SetLength) — not refused.
        var dir = CreateTempDir();
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        XisoWriter.CreateXiso(src, dir, null, null, out _, "game.iso", null);

        var iso = Path.Combine(dir, "game.iso");
        Assert.True(XisoOperations.TrimXiso(iso, iso, quiet: true));
        Assert.True(new FileInfo(iso).Length > 0);
    }
}
