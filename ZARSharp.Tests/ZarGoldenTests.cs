using ZARSharp.Pipeline;

namespace ZARSharp.Tests;

/// <summary>
/// Committed archive ground truth (<c>Goldens/zar/</c>):
/// <list type="bullet">
/// <item><c>native152.zar</c> was packed by the shipped <c>zarchive.exe</c>
/// (bundles libzstd 1.5.2). Our reader must extract it with no toolchain
/// present, so CI pins native-artifact interop.</item>
/// <item><c>nested.bin</c> is the tree's random member
/// (<c>random.Random(1234).randbytes(5000)</c>); the text members regenerate
/// from literals below.</item>
/// <item><c>ours_stable.zar</c> is our own pack of the same tree: a
/// byte-stability tripwire (any pack drift fails loudly and forces a
/// conscious re-baseline), not independent truth.</item>
/// </list>
/// Known version skew (see PortPlan Step 6): our pack frames match libzstd
/// 1.5.7 one-shot; the 1.5.2 bundled in <c>zarchive.exe</c> can differ on
/// multi-transition hetero blocks, so pack-identity against
/// <c>native152.zar</c> is asserted only for contents, not bytes.
/// </summary>
public sealed class ZarGoldenTests : IDisposable
{
    private const string HelloPhrase = "Hello ZArchive golden world! ";
    private const string NotesPhrase = "nested notes ";

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string GoldensDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return Path.Combine(dir, "ZARSharp.Tests", "Goldens", "zar");
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string Repeat(string phrase, int times)
    {
        var sb = new System.Text.StringBuilder(phrase.Length * times);
        for (var i = 0; i < times; i++)
        {
            sb.Append(phrase);
        }

        return sb.ToString();
    }

    /// <summary>Rebuilds the golden tree (<c>name.tmpl</c> layout) under <paramref name="root"/>.</summary>
    private static string BuildTree(string root, string dirName, byte[] nested)
    {
        var src = Directory.CreateDirectory(Path.Combine(root, dirName)).FullName;
        File.WriteAllText(Path.Combine(src, "hello.txt"), Repeat(HelloPhrase, 200));
        File.WriteAllBytes(Path.Combine(src, "empty.dat"), []);
        var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
        File.WriteAllBytes(Path.Combine(sub, "nested.bin"), nested);
        File.WriteAllText(Path.Combine(sub, "notes.txt"), Repeat(NotesPhrase, 50));
        return src;
    }

    private static void AssertTreeContents(string dest, byte[] nested)
    {
        Assert.Equal(Repeat(HelloPhrase, 200), File.ReadAllText(Path.Combine(dest, "hello.txt")));
        Assert.Equal([], File.ReadAllBytes(Path.Combine(dest, "empty.dat")));
        Assert.Equal(nested, File.ReadAllBytes(Path.Combine(dest, "sub", "nested.bin")));
        Assert.Equal(Repeat(NotesPhrase, 50), File.ReadAllText(Path.Combine(dest, "sub", "notes.txt")));
    }

    [Fact]
    public void Native152_ExtractsToExpectedTree()
    {
        var dir = GoldensDir();
        var nested = File.ReadAllBytes(Path.Combine(dir, "nested.bin"));
        var root = NewTempDir("zar_gold_ext");

        var files = ZarPipeline.Extract(
            Path.Combine(dir, "native152.zar"), Path.Combine(root, "out"));

        Assert.Equal(["empty.dat", "hello.txt", "sub/nested.bin", "sub/notes.txt"],
            files.OrderBy(f => f, StringComparer.Ordinal));
        AssertTreeContents(Path.Combine(root, "out"), nested);
    }

    [Fact]
    public void Native152_RepackRoundTrip_PreservesContents()
    {
        var dir = GoldensDir();
        var nested = File.ReadAllBytes(Path.Combine(dir, "nested.bin"));
        var root = NewTempDir("zar_gold_rt");

        var stage = Path.Combine(root, "stage");
        ZarPipeline.Extract(Path.Combine(dir, "native152.zar"), stage);
        var repacked = Path.Combine(root, "repacked.zar");
        ZarPipeline.Pack(stage, repacked);
        var final = Path.Combine(root, "final");
        var files = ZarPipeline.Extract(repacked, final);

        Assert.Equal(4, files.Count);
        AssertTreeContents(final, nested);
    }

    [Fact]
    public void OursStable_PackBytesUnchanged_AndExtracts()
    {
        var dir = GoldensDir();
        var nested = File.ReadAllBytes(Path.Combine(dir, "nested.bin"));
        var root = NewTempDir("zar_gold_stable");

        var src = BuildTree(root, "tree", nested);
        var packed = Path.Combine(root, "packed.zar");
        ZarPipeline.Pack(src, packed);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(dir, "ours_stable.zar")),
            File.ReadAllBytes(packed));

        var dest = Path.Combine(root, "out");
        var files = ZarPipeline.Extract(packed, dest);
        Assert.Equal(4, files.Count);
        AssertTreeContents(dest, nested);
    }
}
