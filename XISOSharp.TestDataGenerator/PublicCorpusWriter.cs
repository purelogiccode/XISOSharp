using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Builds the deterministic, copyright-free public corpus used by the test
/// suite and BattleTests (DOC-02): XISO, split ISO, CISO, CCI, ZAR, GOD/SVOD,
/// XBLA/XBLIG STFS, XBE/XEX and corrupt variants. Every byte is generated from
/// synthetic source trees; no game data is included.
/// </summary>
public static class PublicCorpusWriter
{
    /// <summary>Bump when the corpus layout or generated content changes.</summary>
    private const int CorpusRevision = 1;

    private const string ManifestName = "corpus-manifest.json";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Regenerates the corpus under <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Corpus root (created when missing).</param>
    /// <param name="force">Regenerate even when a current manifest exists.</param>
    /// <returns>Human-readable descriptions of the outputs created.</returns>
    public static IReadOnlyList<string> EnsureCorpus(string root, bool force = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Directory.CreateDirectory(root);

        string manifestPath = Path.Combine(root, ManifestName);
        if (!force && IsCurrent(manifestPath))
        {
            return [];
        }

        List<string> actions = [];
        bool oldQuiet = Logger.Quiet;
        bool oldRealQuiet = Logger.RealQuiet;
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        try
        {
            string sourceRoot = Path.Combine(root, "source");
            string isoRoot = Path.Combine(root, "xiso");
            Directory.CreateDirectory(isoRoot);

            string basicSource = CreateBasicSource(Path.Combine(sourceRoot, "basic"));
            string basicIso = Path.Combine(isoRoot, "basic.iso");
            Pack(basicSource, basicIso, actions);

            string namesSource = CreateNamesSource(Path.Combine(sourceRoot, "names"));
            Pack(namesSource, Path.Combine(isoRoot, "names.iso"), actions);

            string sizesSource = CreateSizesSource(Path.Combine(sourceRoot, "sizes"));
            Pack(sizesSource, Path.Combine(isoRoot, "sizes.iso"), actions);

            string xbeSource = Path.Combine(sourceRoot, "xbe-assets");
            Directory.CreateDirectory(xbeSource);
            Directory.CreateDirectory(Path.Combine(root, "xbe"));
            byte[] xbe = SyntheticXbe.BuildWithImages();
            File.WriteAllBytes(Path.Combine(xbeSource, "default.xbe"), xbe);
            File.WriteAllBytes(Path.Combine(root, "xbe", "default.xbe"), xbe);
            Pack(xbeSource, Path.Combine(isoRoot, "xbe-assets.iso"), actions);

            string xexSource = Path.Combine(sourceRoot, "xex-title");
            Directory.CreateDirectory(xexSource);
            Directory.CreateDirectory(Path.Combine(root, "xex"));
            byte[] xex = SyntheticXex.BuildWithXdbf("Synthetic XEX", "Corpus Publisher",
                "Corpus Developer", "Action", "Synthetic corpus title");
            File.WriteAllBytes(Path.Combine(xexSource, "default.xex"), xex);
            File.WriteAllBytes(Path.Combine(root, "xex", "default.xex"), xex);
            Pack(xexSource, Path.Combine(isoRoot, "xex-title.iso"), actions);

            string splitBase = Path.Combine(root, "split", "basic");
            Directory.CreateDirectory(Path.GetDirectoryName(splitBase)!);
            IReadOnlyList<string> parts = XisoSplitter.Split(basicIso, splitBase, 128L * 1024);
            actions.AddRange(parts.Select(static p => $"wrote '{p}'"));

            string ciso = Path.Combine(root, "ciso", "basic.cso");
            Directory.CreateDirectory(Path.GetDirectoryName(ciso)!);
            Require(CisoWriter.CompressToCso(basicIso, ciso, level: 6), "CISO");
            actions.Add($"wrote '{ciso}'");

            string cci = Path.Combine(root, "cci", "basic.cci");
            Directory.CreateDirectory(Path.GetDirectoryName(cci)!);
            Require(XisoCci.CompressToCci(basicIso, cci, level: 9, splitBytes: 0, overwrite: true), "CCI");
            actions.Add($"wrote '{cci}'");

            string zar = Path.Combine(root, "zar", "basic.zar");
            Directory.CreateDirectory(Path.GetDirectoryName(zar)!);
            if (!XisoZarchive.CreateZar(basicIso, zar, quiet: true))
            {
                throw new InvalidOperationException("Corpus: ZAR creation failed.");
            }

            actions.Add($"wrote '{zar}'");

            string god = SyntheticGodContainer.CreateFromIso(basicIso,
                Path.Combine(root, "god", "4D5307E6"));
            actions.Add($"wrote '{god}' and '{god}.data'");

            string xbla = Path.Combine(root, "stfs", "xbla-package");
            Directory.CreateDirectory(Path.GetDirectoryName(xbla)!);
            SyntheticStfsPackage.Create(xbla, [
                new SyntheticStfsFile("default.xbe", xbe),
                new SyntheticStfsFile("readme.txt", Encoding.UTF8.GetBytes("synthetic XBLA corpus\n")),
                new SyntheticStfsFile("data/payload.bin", Pattern(5000, 42)),
            ], titleName: "Synthetic XBLA");
            actions.Add($"wrote '{xbla}'");

            string xblig = Path.Combine(root, "stfs", "xblig-package");
            SyntheticStfsPackage.Create(xblig, [
                new SyntheticStfsFile("default.xex", xex),
                new SyntheticStfsFile("readme.txt", Encoding.UTF8.GetBytes("synthetic XBLIG corpus\n")),
            ], xblig: true, titleName: "Synthetic XBLIG");
            actions.Add($"wrote '{xblig}'");

            string corrupt = Path.Combine(root, "corrupt");
            Directory.CreateDirectory(corrupt);
            actions.AddRange(CreateCorruptFiles(corrupt, basicIso, ciso, cci, xbla));

            List<ManifestEntry> entries = [];
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, manifestPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                entries.Add(new ManifestEntry(relative, new FileInfo(file).Length, Hash: Sha256(file)));
            }

            entries.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(new CorpusManifest(CorpusRevision, entries), ManifestOptions));
            actions.Add($"wrote '{manifestPath}'");
            return actions;
        }
        finally
        {
            Logger.Quiet = oldQuiet;
            Logger.RealQuiet = oldRealQuiet;
        }
    }

    private static bool IsCurrent(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("revision", out JsonElement revision)
                   && revision.GetInt32() == CorpusRevision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void Pack(string source, string iso, List<string> actions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iso)!);
        Require(XisoWriter.PackFromDirectory(source, iso, fileTime: 0), "XISO");
        actions.Add($"wrote '{iso}'");
    }

    private static void Require(int result, string format)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"Corpus: {format} generation failed with code {result}.");
        }
    }

    private static IEnumerable<string> CreateCorruptFiles(string root, string iso, string ciso,
        string cci, string stfs)
    {
        string truncated = Path.Combine(root, "truncated.iso");
        byte[] isoBytes = File.ReadAllBytes(iso);
        File.WriteAllBytes(truncated, isoBytes[..Math.Min(isoBytes.Length / 2, 0x40000)]);
        yield return $"wrote '{truncated}'";

        string badMagic = Path.Combine(root, "bad-magic.iso");
        File.WriteAllBytes(badMagic, new byte[1024 * 1024]);
        yield return $"wrote '{badMagic}'";

        string badCiso = Path.Combine(root, "basic.cso");
        byte[] csoBytes = File.ReadAllBytes(ciso);
        File.WriteAllBytes(badCiso, csoBytes[..(csoBytes.Length / 2)]);
        yield return $"wrote '{badCiso}'";

        string badCci = Path.Combine(root, "basic.cci");
        byte[] cciBytes = File.ReadAllBytes(cci);
        File.WriteAllBytes(badCci, cciBytes[..(cciBytes.Length / 2)]);
        yield return $"wrote '{badCci}'";

        string badStfs = Path.Combine(root, "xbla-package");
        byte[] stfsBytes = File.ReadAllBytes(stfs);
        File.WriteAllBytes(badStfs, stfsBytes[..Math.Min(stfsBytes.Length, 0xB000)]);
        yield return $"wrote '{badStfs}'";
    }

    private static string CreateBasicSource(string source)
    {
        Directory.CreateDirectory(Path.Combine(source, "sub", "nested"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "synthetic public corpus\n");
        File.WriteAllText(Path.Combine(source, "sub", "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(source, "sub", "nested", "b.txt"), "bravo\n");
        File.WriteAllBytes(Path.Combine(source, "data.bin"), Pattern(300_000, 1));
        return source;
    }

    private static string CreateNamesSource(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "café.txt"), "latin-1\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(source, "naïve.txt"), "latin-1\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(source, "spaces in name.txt"), "spaces\n");
        File.WriteAllText(Path.Combine(source, "MixedCase.TXT"), "case\n");
        File.WriteAllText(Path.Combine(source, new string('n', 200) + ".txt"), "long name\n");
        return source;
    }

    private static string CreateSizesSource(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(source, "one.bin"), [0x42]);
        File.WriteAllBytes(Path.Combine(source, "sector.bin"), Pattern(2048, 2));
        File.WriteAllBytes(Path.Combine(source, "sector_plus_one.bin"), Pattern(2049, 3));
        File.WriteAllBytes(Path.Combine(source, "block.bin"), Pattern(65536, 4));
        return source;
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local",
        Justification = "Serialized to corpus-manifest.json by System.Text.Json.")]
    private sealed record ManifestEntry(string Path, long Length, string Hash);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local",
        Justification = "Serialized to corpus-manifest.json by System.Text.Json.")]
    private sealed record CorpusManifest(int Revision, List<ManifestEntry> Files);
}
