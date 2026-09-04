using System.Diagnostics;
using System.Security.Cryptography;
using ZARSharp;

namespace XISOSharp.Tests;

/// <summary>
/// End-to-end tests for <see cref="XisoZarchive.CreateZar"/> (TODO #18):
/// XISO → .zar conversion packs the image tree with real zstd blocks, so the
/// output must round-trip through <see cref="ZArchiveTool"/> and the reference
/// <c>zarchive.exe</c>, and must compress (not just store raw).
/// </summary>
[Collection("Sequential")]
public sealed class XisoZarConvertTests : IDisposable
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

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        var src = CreateTempDir("xiso_zc_src");
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        var outDir = CreateTempDir("xiso_zc_iso");
        var result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string dir)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(dir, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return map;
    }

    private static void AssertSnapshotsEqual(
        Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order().ToArray(), actual.Keys.Order().ToArray());
        foreach (var (rel, data) in expected)
        {
            Assert.True(actual.TryGetValue(rel, out var got), $"missing {rel}");
            Assert.Equal(data, got);
        }
    }

    private static void PopulateRich(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
        File.WriteAllText(Path.Combine(dir, "repeat.txt"),
            string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 4000)));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "big.bin"), PatternBytes(150000, 7));
        Directory.CreateDirectory(Path.Combine(dir, "sub", "emptydir"));
        Directory.CreateDirectory(Path.Combine(dir, "other"));
        File.WriteAllText(Path.Combine(dir, "other", "deep.txt"), new string('z', 70000));
    }

    private static byte[] PatternBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        uint state = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    private static string SolutionRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Solution root not found.");
    }

    [Fact]
    public void Convert_RoundTrip_ExtractMatchesSource()
    {
        var src = CreateSourceDir(PopulateRich);
        var expected = SnapshotFiles(src);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_rt");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        var outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_CompressedBlocks_SmallerThanRawInput()
    {
        // Highly compressible content: raw block storage would be ~input size,
        // zstd must come in well under it. This fails if the writer regresses
        // to raw-only storage.
        var src = CreateSourceDir(PopulateRich);
        long rawTotal = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_ratio");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        long zarLen = new FileInfo(zar).Length;
        Assert.True(zarLen < rawTotal / 2,
            $"ZAR {zarLen} B not < half of raw input {rawTotal} B; compression not engaged?");
    }

    [Fact]
    public void Convert_OpensInReader_NamesSizesAndHashes()
    {
        var src = CreateSourceDir(PopulateRich);
        var expected = SnapshotFiles(src);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_rd");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach (var (rel, data) in expected)
        {
            uint h = reader!.LookUp(rel);
            Assert.NotEqual(ZArchiveReader.InvalidNode, h);
            Assert.True(reader.IsFile(h));
            Assert.Equal((ulong)data.Length, reader.GetFileSize(h));
            Assert.Equal(data, reader.ReadFile(h));
        }

        // Empty directory survives the conversion.
        uint dir = reader!.LookUp("sub/emptydir");
        Assert.NotEqual(ZArchiveReader.InvalidNode, dir);
        Assert.True(reader.IsDirectory(dir));
    }

    [Fact]
    public void Convert_RemoveUpdate_ExcludesSystemUpdate()
    {
        var src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "default.xbe"), "xbe");
            Directory.CreateDirectory(Path.Combine(d, "$SystemUpdate"));
            File.WriteAllText(Path.Combine(d, "$SystemUpdate", "upd.bin"), "update");
        });
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_su");
        var zar = Path.Combine(work, "game.zar");

        using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            Assert.True(XisoZarchive.CreateZar(fs, 0, zar, removeUpdate: true, quiet: true));
        }

        var outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "default.xbe")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "$SystemUpdate")));
    }

    [Fact]
    public void Convert_IsoOffset_RoundTrip()
    {
        var src = CreateSourceDir(PopulateRich);
        var expected = SnapshotFiles(src);
        var iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        var work = CreateTempDir("xiso_zc_off");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, offset, quiet: true));

        var outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_RawCompressorOption_StillValidArchive()
    {
        var src = CreateSourceDir(PopulateRich);
        var expected = SnapshotFiles(src);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_raw");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true, compressor: new ZarRawCompressor()));

        var outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_HashesMatchSource_Sha256()
    {
        var src = CreateSourceDir(PopulateRich);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_hash");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            uint h = reader!.LookUp(rel);
            Assert.NotEqual(ZArchiveReader.InvalidNode, h);
            Assert.Equal(
                SHA256.HashData(File.ReadAllBytes(file)),
                SHA256.HashData(reader.ReadFile(h)));
        }
    }

    [Fact]
    public void Interop_ReferenceExeExtractsOurZar()
    {
        string exe = Path.Combine(SolutionRoot(), "References", "ZArchive-0.1.2", "zarchive.exe");
        if (!File.Exists(exe))
        {
            return; // reference binary not present; covered by round-trip tests
        }

        var src = CreateSourceDir(PopulateRich);
        var expected = SnapshotFiles(src);
        var iso = CreateIso(src);
        var work = CreateTempDir("xiso_zc_exe");
        var zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        var outDir = Path.Combine(work, "exedra");
        var psi = new ProcessStartInfo(exe, $"\"{zar}\" \"{outDir}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(120000);
        Assert.True(proc.ExitCode == 0, $"zarchive.exe failed: {proc.StandardOutput.ReadToEnd()}");
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }
}
