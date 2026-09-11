using System.Diagnostics;
using System.Security.Cryptography;
using XISOSharp.TestDataGenerator;
using ZArchiveSharp;

namespace XISOSharp.Tests;

/// <summary>
/// End-to-end tests for <see cref="XisoZarchive.CreateZar(string, string?, long, bool, CancellationToken, ZArchiveSharp.IZarBlockCompressor?, IProgress{ZArchiveSharp.Pipeline.ZarProgress}?)"/>:
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

    private string CreateSourceDir(Action<string> populate)
    {
        string src = CreateTempDir("xiso_zc_src");
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        string outDir = CreateTempDir("xiso_zc_iso");
        int result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out string? isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string dir)
    {
        Dictionary<string, byte[]> map = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(dir, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return map;
    }

    private static void AssertSnapshotsEqual(
        Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach ((string rel, byte[] data) in expected)
        {
            Assert.True(actual.TryGetValue(rel, out byte[]? got), $"missing {rel}");
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
        uint state = (uint)((seed * 2654435761u) + 1);
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    private static string SolutionRoot() =>
        // Centralized via TestDataLocator (BUG-TEST-006).
        TestDataLocator.GetSolutionRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Solution root not found.");

    [Fact]
    public void Convert_RoundTrip_ExtractMatchesSource()
    {
        string src = CreateSourceDir(PopulateRich);
        Dictionary<string, byte[]> expected = SnapshotFiles(src);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_rt");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        string outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_CompressedBlocks_SmallerThanRawInput()
    {
        // Highly compressible content: raw block storage would be ~input size,
        // zstd must come in well under it. This fails if the writer regresses
        // to raw-only storage.
        string src = CreateSourceDir(PopulateRich);
        long rawTotal = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_ratio");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        long zarLen = new FileInfo(zar).Length;
        Assert.True(zarLen < rawTotal / 2,
            $"ZAR {zarLen} B not < half of raw input {rawTotal} B; compression not engaged?");
    }

    [Fact]
    public void Convert_OpensInReader_NamesSizesAndHashes()
    {
        string src = CreateSourceDir(PopulateRich);
        Dictionary<string, byte[]> expected = SnapshotFiles(src);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_rd");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        using ZArchiveReader? reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach ((string rel, byte[] data) in expected)
        {
            uint h = reader.LookUp(rel);
            Assert.NotEqual(ZArchiveReader.InvalidNode, h);
            Assert.True(reader.IsFile(h));
            Assert.Equal((ulong)data.Length, reader.GetFileSize(h));
            Assert.Equal(data, reader.ReadFile(h));
        }

        // Empty directory survives the conversion.
        uint dir = reader.LookUp("sub/emptydir");
        Assert.NotEqual(ZArchiveReader.InvalidNode, dir);
        Assert.True(reader.IsDirectory(dir));
    }

    [Fact]
    public void Convert_RemoveUpdate_ExcludesSystemUpdate()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "default.xbe"), "xbe");
            Directory.CreateDirectory(Path.Combine(d, "$SystemUpdate"));
            File.WriteAllText(Path.Combine(d, "$SystemUpdate", "upd.bin"), "update");
        });
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_su");
        string zar = Path.Combine(work, "game.zar");

        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            Assert.True(XisoZarchive.CreateZar(fs, 0, zar, removeUpdate: true, quiet: true));
        }

        string outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "default.xbe")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "$SystemUpdate")));
    }

    [Fact]
    public void Convert_IsoOffset_RoundTrip()
    {
        string src = CreateSourceDir(PopulateRich);
        Dictionary<string, byte[]> expected = SnapshotFiles(src);
        string iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        string work = CreateTempDir("xiso_zc_off");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, offset, quiet: true));

        string outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_RawCompressorOption_StillValidArchive()
    {
        string src = CreateSourceDir(PopulateRich);
        Dictionary<string, byte[]> expected = SnapshotFiles(src);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_raw");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true, compressor: new ZarRawCompressor()));

        string outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }

    [Fact]
    public void Convert_HashesMatchSource_Sha256()
    {
        string src = CreateSourceDir(PopulateRich);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_hash");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        using ZArchiveReader? reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            uint h = reader.LookUp(rel);
            Assert.NotEqual(ZArchiveReader.InvalidNode, h);
            Assert.Equal(
                SHA256.HashData(File.ReadAllBytes(file)),
                SHA256.HashData(reader.ReadFile(h)));
        }
    }

    [RequiresOracleFact(OracleKind.Zarchive)]
    public void Interop_ReferenceExeExtractsOurZar()
    {
        // zarchive.exe moved with ZArchiveSharp to the sibling CSharp_ZArchiveSharp repo.
        string exe = Path.Combine(SolutionRoot(), "..", "CSharp_ZArchiveSharp", "References", "ZArchive-0.1.2",
            "zarchive.exe");
        Assert.True(File.Exists(exe), "Missing reference oracle 'Zarchive'.");

        string src = CreateSourceDir(PopulateRich);
        Dictionary<string, byte[]> expected = SnapshotFiles(src);
        string iso = CreateIso(src);
        string work = CreateTempDir("xiso_zc_exe");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        string outDir = Path.Combine(work, "exedra");
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(zar);
        psi.ArgumentList.Add(outDir);
        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort kill after timeout.
            }

            proc.WaitForExit(5000);
            Assert.Fail("zarchive.exe timed out and was killed.");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(proc.ExitCode == 0, $"zarchive.exe failed (exit {proc.ExitCode}): {stderr}{stdout}");
        AssertSnapshotsEqual(expected, SnapshotFiles(outDir));
    }
}
