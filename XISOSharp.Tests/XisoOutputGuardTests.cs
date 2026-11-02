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

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_outguard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateDummy(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    [Fact]
    public void CompressToCso_OutputEqualsSource_Throws()
    {
        string dir = CreateTempDir();
        string src = CreateDummy(dir, "game.iso");
        IOException ex = Assert.Throws<IOException>(() => CisoWriter.CompressToCso(src, src));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void CompressToCso_SourceCollidesWithSplitPart_Throws()
    {
        string dir = CreateTempDir();
        string src = CreateDummy(dir, "game.1.cso");
        IOException ex = Assert.Throws<IOException>(() =>
            CisoWriter.CompressToCso(src, Path.Combine(dir, "game.cso"), splitBytes: 2048));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void DecompressToIso_OutputEqualsSource_Throws()
    {
        string dir = CreateTempDir();
        string src = CreateDummy(dir, "game.cso");
        IOException ex = Assert.Throws<IOException>(() => CisoReader.DecompressToIso(src, src));
        Assert.Contains("same", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(src).Length);
    }

    [Fact]
    public void RebuildRedump_OutputEqualsPart_Throws()
    {
        string dir = CreateTempDir();
        string xiso = CreateDummy(dir, "game.xiso");
        string video = CreateDummy(dir, "game.video.iso");
        IOException ex = Assert.Throws<IOException>(() =>
            XisoRedump.RebuildRedump(xiso, video, null, null, xiso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WipeFiller_OutputEqualsInput_Throws()
    {
        string dir = CreateTempDir();
        string iso = CreateDummy(dir, "game.iso");
        IOException ex = Assert.Throws<IOException>(() => XisoOperations.WipeFiller(iso, iso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4096, new FileInfo(iso).Length);
    }

    [Fact]
    public void WipeAndTrim_OutputEqualsInput_Throws()
    {
        string dir = CreateTempDir();
        string iso = CreateDummy(dir, "game.iso");
        IOException ex = Assert.Throws<IOException>(() => XisoOperations.WipeAndTrim(iso, iso, quiet: true));
        Assert.Contains("must not overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4096, new FileInfo(iso).Length);
    }

    [Fact]
    public void TrimXiso_SamePath_StillTrimsInPlace()
    {
        // In-place trim is an explicit, safe semantic (SetLength) — not refused.
        string dir = CreateTempDir();
        string src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        XisoWriter.CreateXiso(src, dir, null, null, out _, "game.iso", null);

        string iso = Path.Combine(dir, "game.iso");
        Assert.True(XisoOperations.TrimXiso(iso, iso, quiet: true));
        Assert.True(new FileInfo(iso).Length > 0);
    }
}
