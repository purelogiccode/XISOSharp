using System.Security.Cryptography;
using XISOSharp.Cli;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the per-file hash sidecar (BRF-05) across every supported
/// container, and for <c>verify-hashes</c>' MD5/SHA-1/SHA-256 detection.
/// </summary>
[Collection("Sequential")]
public sealed class XisoFileHashSidecarTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;
        foreach (string dir in _tempDirs)
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
                // best effort
            }
        }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_filehash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private (string Iso, string Source) CreateIso()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hash sidecar fixture\n");
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(source, "data.bin"),
            Enumerable.Range(0, 20_000).Select(static i => (byte)(i * 17)).ToArray());
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "alpha.txt"), "alpha\n");

        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        return (iso, source);
    }

    private static Dictionary<string, string> Map(IEnumerable<FileHashEntry> entries) =>
        entries.ToDictionary(static entry => Normalize(entry.Path), static entry => entry.HashHex,
            StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        "/" + path.Replace('\\', '/').TrimStart('/');

    private static (string Text, string Error) RunCli(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedError = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = error;
            _ = Program.Main(args);
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedError;
        }

        return (output.ToString(), error.ToString());
    }

    [Fact]
    public void ComputeFileHashSidecar_Sha1_MatchesKnownDigests()
    {
        (string iso, string source) = CreateIso();

        IReadOnlyList<FileHashEntry> entries = XisoReader.ComputeFileHashSidecar(iso);

        Assert.Equal(4, entries.Count);
        Dictionary<string, string> map = Map(entries);
        string expected = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(Path.Combine(source, "data.bin"))))
            .ToLowerInvariant();
        Assert.Equal(expected, map["/data.bin"]);
        Assert.Equal(Convert.ToHexString(SHA1.HashData([])).ToLowerInvariant(), map["/empty.bin"]);
        Assert.All(entries, static entry => Assert.Equal("sha1", entry.Algorithm));
    }

    [Fact]
    public void ComputeFileHashSidecar_AllAlgorithms()
    {
        (string iso, _) = CreateIso();

        Dictionary<string, string> sha256 = Map(XisoReader.ComputeFileHashSidecar(iso, HashAlgorithmName.SHA256));
        Dictionary<string, string> md5 = Map(XisoReader.ComputeFileHashSidecar(iso, HashAlgorithmName.MD5));

        Assert.Equal(64, sha256["/readme.txt"].Length);
        Assert.Equal(32, md5["/readme.txt"].Length);
        Assert.NotEqual(sha256["/readme.txt"], md5["/readme.txt"], StringComparer.Ordinal);
    }

    [Fact]
    public void ComputeFileHashSidecar_MatchesPetrifyHash()
    {
        (string iso, _) = CreateIso();
        string work = TempDir();
        string skeleton = Path.Combine(work, "game.skeleton.xiso");
        string hash = Path.Combine(work, "game.hash");
        Assert.True(XisoSkeleton.Petrify(iso, skeleton, hash, quiet: true));

        Dictionary<string, string> fromPetrify = File.ReadLines(hash)
            .Select(static line => line.Split(' ', 2))
            .ToDictionary(static parts => Normalize(parts[1]), static parts => parts[0].ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> fromSidecar = Map(XisoReader.ComputeFileHashSidecar(iso));

        Assert.Equal(fromPetrify.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            fromSidecar.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sidecar_CisoAndCciMatchIso()
    {
        (string iso, _) = CreateIso();
        string work = TempDir();
        string cso = Path.Combine(work, "game.cso");
        string cci = Path.Combine(work, "game.cci");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, level: 6));
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, level: 9, splitBytes: 0, overwrite: true));

        Dictionary<string, string> expected = Map(XisoReader.ComputeFileHashSidecar(iso));
        Assert.Equal(expected, Map(XisoReader.ComputeFileHashSidecar(cso)));
        Assert.Equal(expected, Map(XisoReader.ComputeFileHashSidecar(cci)));
    }

    [Fact]
    public void Sidecar_GodAndZarMatchIso()
    {
        (string iso, _) = CreateIso();
        string work = TempDir();
        string god = Path.Combine(work, "4D5307E6");
        SyntheticGodContainer.CreateFromIso(iso, god, "Hash GOD");
        string zar = Path.Combine(work, "game.zar");
        Assert.True(XisoZarchive.CreateZar(iso, zar, quiet: true));

        Dictionary<string, string> expected = Map(XisoReader.ComputeFileHashSidecar(iso));
        Assert.Equal(expected, Map(XisoReader.ComputeFileHashSidecar(god)));
        Assert.Equal(expected, Map(XisoReader.ComputeFileHashSidecar(zar)));
    }

    [Fact]
    public void Sidecar_StfsPackageHashesEveryFile()
    {
        string work = TempDir();
        byte[] xex = SyntheticXex.BuildWithXdbf();
        byte[] payload = Enumerable.Range(0, 5000).Select(static i => (byte)(i * 13)).ToArray();
        string package = Path.Combine(work, "xblig-package");
        SyntheticStfsPackage.Create(package, [
            new SyntheticStfsFile("default.xex", xex),
            new SyntheticStfsFile("data/payload.bin", payload),
        ], xblig: true);

        Dictionary<string, string> map = Map(XisoReader.ComputeFileHashSidecar(package));

        Assert.Equal(Convert.ToHexString(SHA1.HashData(xex)).ToLowerInvariant(), map["/default.xex"]);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(), map["/data/payload.bin"]);
    }

    [Fact]
    public void Cli_FileHashes_RoundTripsThroughVerifyHashes()
    {
        (string iso, _) = CreateIso();
        string sidecar = Path.Combine(TempDir(), "game.hash");

        (string text, string error) = RunCli("file-hashes", iso, "-o", sidecar);
        Assert.Contains("Wrote 4 file hash(es)", text, StringComparison.Ordinal);
        Assert.True(File.Exists(sidecar), error);

        (string verifyText, _) = RunCli("verify-hashes", iso, sidecar);
        Assert.Contains("Result: PASS", verifyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_FileHashes_DefaultPathAndJson()
    {
        (string iso, _) = CreateIso();

        (string text, _) = RunCli("file-hashes", iso, "--json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(text.Trim());
        Assert.Equal("file-hashes", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("fileCount").GetInt32());
        string expectedDefault = Path.ChangeExtension(iso, ".hash");
        Assert.Equal(expectedDefault, document.RootElement.GetProperty("output").GetString());
        Assert.True(File.Exists(expectedDefault));
    }

    [Theory]
    [InlineData("sha256", 64)]
    [InlineData("md5", 32)]
    public void Cli_FileHashes_NonDefaultAlgorithmVerifies(string algorithm, int length)
    {
        (string iso, _) = CreateIso();
        string sidecar = Path.Combine(TempDir(), $"game.{algorithm}.hash");

        _ = RunCli("file-hashes", iso, "-o", sidecar, "--algorithm", algorithm);
        string firstLine = File.ReadLines(sidecar).First();
        Assert.Equal(length, firstLine.Split(' ')[0].Length);

        (string verifyText, _) = RunCli("verify-hashes", iso, sidecar);
        Assert.Contains("Result: PASS", verifyText, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyHashes_TooShortDigest_FailsClearly()
    {
        (string iso, _) = CreateIso();
        string bad = Path.Combine(TempDir(), "bad.hash");
        File.WriteAllText(bad, "abc /readme.txt\n");

        (_, string error) = RunCli("verify-hashes", iso, bad);

        Assert.Contains("not a 32 (MD5), 40 (SHA-1)", error, StringComparison.Ordinal);
    }
}
