using System.Text;

namespace XISOSharp.BattleTests;

/// <summary>
/// Builds the deterministic, copyright-free corpus used by the CI battle job.
/// Every image is packed by the XISOSharp CLI from a synthetic source tree, so
/// the corpus is reproducible and contains no game data. The trees cover names
/// (Latin-1, long, spaces, mixed case), empty directories, deep nesting, many
/// entries and all sector-boundary sizes.
/// </summary>
internal static class SyntheticCorpus
{
    /// <summary>
    /// Creates the corpus under <paramref name="root"/> and returns the root.
    /// The caller owns the directory (delete it unless <c>--keep</c>).
    /// </summary>
    /// <param name="cli">Resolved XISOSharp CLI process wrapper.</param>
    /// <param name="root">Corpus root (created when missing).</param>
    /// <returns>The corpus root containing the generated <c>*.iso</c> files.</returns>
    internal static string Create(ToolProcess cli, string root)
    {
        Directory.CreateDirectory(root);

        List<(string Name, string Source)> trees =
        [
            ("basic", CreateBasic(Path.Combine(root, "src_basic"))),
            ("names", CreateNames(Path.Combine(root, "src_names"))),
            ("many", CreateMany(Path.Combine(root, "src_many"))),
            ("sizes", CreateSizes(Path.Combine(root, "src_sizes"))),
            ("deep", CreateDeep(Path.Combine(root, "src_deep"))),
        ];

        foreach ((string name, string source) in trees)
        {
            string iso = Path.Combine(root, name + ".iso");
            (int code, _, string err, _) = cli.Run("-q", "-c", source, iso);
            if (code != 0 || !File.Exists(iso))
            {
                throw new InvalidOperationException(
                    $"Synthetic corpus: CLI failed to pack '{source}' (exit {code}): {err.Trim()}");
            }
        }

        return root;
    }

    private static string CreateBasic(string source)
    {
        Directory.CreateDirectory(Path.Combine(source, "sub", "nested"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "synthetic battle corpus\n");
        File.WriteAllText(Path.Combine(source, "sub", "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(source, "sub", "nested", "b.txt"), "bravo\n");
        File.WriteAllBytes(Path.Combine(source, "sub", "binary.bin"), Pattern(4096, 7));
        return source;
    }

    private static string CreateNames(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "café.txt"), "latin-1\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(source, "naïve.txt"), "latin-1\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(source, "spaces in name.txt"), "spaces\n");
        File.WriteAllText(Path.Combine(source, "MixedCase.TXT"), "case\n");
        File.WriteAllText(Path.Combine(source, new string('n', 200) + ".txt"), "long name\n");
        return source;
    }

    private static string CreateMany(string source)
    {
        Directory.CreateDirectory(source);
        for (int d = 0; d < 8; d++)
        {
            string dir = Path.Combine(source, $"d{d:D2}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 16; f++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"file_{f:D2}.bin"), Pattern(512 + d * 17 + f, d * 100 + f));
            }
        }

        File.WriteAllText(Path.Combine(source, "manifest.txt"), "many-files corpus\n");
        return source;
    }

    private static string CreateSizes(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(source, "one.bin"), [0x42]);
        File.WriteAllBytes(Path.Combine(source, "sector.bin"), Pattern(2048, 1));
        File.WriteAllBytes(Path.Combine(source, "sector_plus_one.bin"), Pattern(2049, 2));
        File.WriteAllBytes(Path.Combine(source, "block.bin"), Pattern(65536, 3));
        File.WriteAllBytes(Path.Combine(source, "random.bin"), Pattern(262144, 4));
        return source;
    }

    private static string CreateDeep(string source)
    {
        string current = source;
        Directory.CreateDirectory(current);
        for (int i = 0; i < 16; i++)
        {
            current = Path.Combine(current, $"level{i:D2}");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "marker.txt"), $"level {i}\n");
        }

        return source;
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        random.NextBytes(data);
        return data;
    }
}
