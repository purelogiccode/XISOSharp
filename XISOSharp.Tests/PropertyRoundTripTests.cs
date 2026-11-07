using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// LIB-04: deterministic property-style round-trips. Each case generates a
/// seeded source tree with boundary sizes (empty, one byte, one sector,
/// sector+1, padding, compression thresholds) and exercises one container.
/// The seeds are fixed so failures are reproducible.
/// </summary>
[Collection("Sequential")]
public sealed class PropertyRoundTripTests : IDisposable
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

    [Theory]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(47)]
    [InlineData(83)]
    public void PackExtract_RoundTrips(int seed)
    {
        string work = CreateTempDir();
        (string source, Dictionary<string, byte[]> expected, HashSet<string> emptyDirs) = BuildTree(work, seed);
        string iso = Path.Combine(work, "image.iso");

        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        MemoryFilesystem extracted = new();
        Assert.Equal(0, XisoReader.UnpackImage(iso, extracted));

        AssertTree(expected, emptyDirs, extracted);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(203)]
    [InlineData(307)]
    public void SplitJoin_RoundTrips(int seed)
    {
        string work = CreateTempDir();
        (string source, _, _) = BuildTree(work, seed, large: true);
        string iso = Path.Combine(work, "image.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string baseName = Path.Combine(work, "game");
        IReadOnlyList<string> parts = XisoSplitter.Split(iso, baseName, 32L * 2048);
        Assert.NotEmpty(parts);
        string joined = Path.Combine(work, "joined.iso");
        Assert.Equal(joined, XisoSplitter.Join(parts[0], joined));
        Assert.Equal(Sha256(iso), Sha256(joined));
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(11, 9)]
    [InlineData(53, 6)]
    [InlineData(53, 9)]
    public void Ciso_RoundTrips(int seed, int level)
    {
        string work = CreateTempDir();
        (string source, _, _) = BuildTree(work, seed);
        string iso = Path.Combine(work, "image.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string cso = Path.Combine(work, "image.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, level: level));
        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(cso, extracted));
        Assert.Equal(Sha256(iso), Sha256(extracted));
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(11, 9)]
    [InlineData(71, 9)]
    public void Cci_RoundTrips(int seed, int level)
    {
        string work = CreateTempDir();
        (string source, _, _) = BuildTree(work, seed);
        string iso = Path.Combine(work, "image.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string cci = Path.Combine(work, "image.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, level, splitBytes: 0, overwrite: true));
        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, XisoCci.DecompressToIso(cci, extracted, overwrite: true));
        Assert.Equal(Sha256(iso), Sha256(extracted));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(67)]
    [InlineData(149)]
    public void Zar_RoundTrips(int seed)
    {
        string work = CreateTempDir();
        (string source, Dictionary<string, byte[]> expected, HashSet<string> emptyDirs) = BuildTree(work, seed);
        string iso = Path.Combine(work, "image.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string zar = Path.Combine(work, "image.zar");
        Assert.True(XisoZarchive.CreateZar(iso, zar));
        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, XisoZarchive.ConvertZarToIso(zar, extracted, overwrite: true));

        // ZAR stores a name tree, not the raw sector image, so the rebuilt ISO
        // is not byte-identical; its contents must be.
        MemoryFilesystem roundTrip = new();
        Assert.Equal(0, XisoReader.UnpackImage(extracted, roundTrip));
        AssertTree(expected, emptyDirs, roundTrip);
    }

    private (string Source, Dictionary<string, byte[]> Files, HashSet<string> EmptyDirs) BuildTree(
        string work, int seed, bool large = false)
    {
        string source = Path.Combine(work, "src");
        Directory.CreateDirectory(source);
        Random random = new(seed);
        int[] sizes = large
            ? [0, 1, 2048, 2049, 65536, 300_000]
            : [0, 1, 2047, 2048, 2049, 4096, 65_536];
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> emptyDirs = new(StringComparer.OrdinalIgnoreCase) { "" };

        int fileCount = large ? 8 : 10;
        for (int i = 0; i < fileCount; i++)
        {
            int depth = random.Next(0, 3);
            string directory = source;
            string relative = string.Empty;
            for (int d = 0; d < depth; d++)
            {
                string name = $"d{d}_{i % 3}";
                relative = relative.Length == 0 ? name : relative + "/" + name;
                directory = Path.Combine(directory, name);
                Directory.CreateDirectory(directory);
                emptyDirs.Add(relative);
            }

            string fileName = $"f{i:D2}.bin";
            byte[] data = new byte[sizes[random.Next(sizes.Length)]];
            random.NextBytes(data);
            // Keep a zero-heavy case so compression threshold and sparse paths run.
            if (i % 4 == 0)
            {
                Array.Clear(data);
            }

            File.WriteAllBytes(Path.Combine(directory, fileName), data);
            files[relative.Length == 0 ? fileName : relative + "/" + fileName] = data;
        }

        // One explicit empty directory to exercise the empty-subdirectory sentinel.
        string empty = Path.Combine(source, "empty");
        Directory.CreateDirectory(empty);
        emptyDirs.Add("empty");
        return (source, files, emptyDirs);
    }

    private static void AssertTree(
        Dictionary<string, byte[]> expected,
        HashSet<string> emptyDirs,
        MemoryFilesystem actual)
    {
        foreach ((string path, byte[] bytes) in expected)
        {
            Assert.True(actual.FileExists(path), $"Missing extracted file: {path}");
            Assert.Equal(bytes, actual.ReadAllBytes(path));
        }

        foreach (string directory in emptyDirs)
        {
            if (directory.Length == 0)
            {
                continue;
            }

            Assert.True(actual.DirectoryExists(directory), $"Missing extracted directory: {directory}");
        }

        Assert.Equal(expected.Count, actual.FileNames.Count);
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_prop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
