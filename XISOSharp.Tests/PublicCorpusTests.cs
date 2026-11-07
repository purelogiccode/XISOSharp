using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// DOC-02: the public corpus generator must produce every advertised container
/// deterministically, and each generated fixture must open through the same
/// readers the CLI uses. Corrupt fixtures must fail cleanly.
/// </summary>
[Collection("Sequential")]
public sealed class PublicCorpusTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void Corpus_ContainsEveryAdvertisedFixture()
    {
        string root = CreateTempDir();
        IReadOnlyList<string> actions = PublicCorpusWriter.EnsureCorpus(root, force: true);

        Assert.NotEmpty(actions);
        string[] expected =
        [
            "xiso/basic.iso",
            "xiso/names.iso",
            "xiso/sizes.iso",
            "xiso/xbe-assets.iso",
            "xiso/xex-title.iso",
            "split/basic.1.iso",
            "ciso/basic.cso",
            "cci/basic.cci",
            "zar/basic.zar",
            "god/4D5307E6",
            "stfs/xbla-package",
            "stfs/xblig-package",
            "xbe/default.xbe",
            "xex/default.xex",
            "corrupt/truncated.iso",
            "corrupt/bad-magic.iso",
            "corrupt/basic.cso",
            "corrupt/basic.cci",
            "corrupt/xbla-package",
            "corpus-manifest.json",
        ];
        foreach (string relative in expected)
        {
            Assert.True(File.Exists(Path.Combine(root, relative)), $"Missing corpus fixture: {relative}");
        }

        Assert.True(Directory.Exists(Path.Combine(root, "god", "4D5307E6.data")));

        // Re-running without --force reuses the manifest.
        Assert.Empty(PublicCorpusWriter.EnsureCorpus(root));
    }

    [Fact]
    public void Corpus_EveryContainerOpensAndLists()
    {
        string root = PrepareCorpus();
        string xisoRoot = Path.Combine(root, "xiso");

        foreach (string name in new[] { "basic.iso", "names.iso", "sizes.iso" })
        {
            using XisoImage image = XisoImage.Open(Path.Combine(xisoRoot, name));
            Assert.NotEmpty(image.ListFlat());
        }

        using (XisoImage split = XisoImage.Open(Path.Combine(root, "split", "basic.1.iso")))
        {
            Assert.NotEmpty(split.ListFlat());
        }

        using (XisoImage ciso = XisoImage.Open(Path.Combine(root, "ciso", "basic.cso")))
        {
            Assert.NotEmpty(ciso.ListFlat());
        }

        using (XisoImage cci = XisoImage.Open(Path.Combine(root, "cci", "basic.cci")))
        {
            Assert.NotEmpty(cci.ListFlat());
        }

        Assert.NotEmpty(XisoZarchive.ListZar(Path.Combine(root, "zar", "basic.zar")));

        using (XisoImage god = XisoImage.Open(Path.Combine(root, "god", "4D5307E6")))
        {
            Assert.NotEmpty(god.ListFlat());
        }

        Assert.True(StfsPackage.TryOpen(Path.Combine(root, "stfs", "xbla-package"), out StfsPackage? xbla));
        using (xbla)
        {
            Assert.NotNull(xbla);
            Assert.Equal(StfsPackageKind.Xbla, xbla.Info.Kind);
            Assert.NotEmpty(xbla.Entries);
        }

        Assert.True(StfsPackage.TryOpen(Path.Combine(root, "stfs", "xblig-package"), out StfsPackage? xblig));
        using (xblig)
        {
            Assert.NotNull(xblig);
            Assert.Equal(StfsPackageKind.Xblig, xblig.Info.Kind);
        }
    }

    [Fact]
    public void Corpus_TitleMetadataAndImages()
    {
        string root = PrepareCorpus();
        string xisoRoot = Path.Combine(root, "xiso");

        TitleInfo? xbe = XisoReader.GetTitleInfo(Path.Combine(xisoRoot, "xbe-assets.iso"));
        Assert.NotNull(xbe);
        Assert.Equal("Synthetic XBE", xbe.TitleName);
        Assert.Equal(0x4D530004u, xbe.TitleId);

        TitleInfo? xex = XisoReader.GetTitleInfo(Path.Combine(xisoRoot, "xex-title.iso"));
        Assert.NotNull(xex);
        Assert.Equal(TitleKind.Xex, xex.Kind);
        Assert.Equal("Synthetic XEX", xex.TitleName);
        Assert.Equal("Corpus Publisher", xex.Xex?.Publisher);

        XbeImage? logo = XbeAssets.GetLogo(Path.Combine(xisoRoot, "xbe-assets.iso"), "/default.xbe");
        XbeImage? title = XbeAssets.GetTitleImage(Path.Combine(xisoRoot, "xbe-assets.iso"), "/default.xbe");
        XbeImage? save = XbeAssets.GetSaveImage(Path.Combine(xisoRoot, "xbe-assets.iso"), "/default.xbe");
        Assert.NotNull(logo?.Png);
        Assert.NotNull(title?.Png);
        Assert.NotNull(save?.Png);

        Assert.True(GodHeader.TryReadTitle(Path.Combine(root, "god", "4D5307E6"), out GodTitleInfo? god));
        Assert.NotNull(god);
        Assert.Equal("Synthetic GOD", god.TitleName);
    }

    [Fact]
    public void Corpus_CorruptFixturesFailCleanly()
    {
        string root = PrepareCorpus();
        string corrupt = Path.Combine(root, "corrupt");

        foreach (string name in new[] { "truncated.iso", "bad-magic.iso", "basic.cso", "basic.cci" })
        {
            Exception? error = Record.Exception(() =>
            {
                using XisoImage image = XisoImage.Open(Path.Combine(corrupt, name));
                _ = image.ListFlat();
            });
            Assert.True(error is not null, $"{name} opened and listed without an error");
        }

        Assert.False(StfsPackage.TryOpen(Path.Combine(corrupt, "xbla-package"), out _));
    }

    private string PrepareCorpus()
    {
        string root = CreateTempDir();
        PublicCorpusWriter.EnsureCorpus(root, force: true);
        return root;
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_corpus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
