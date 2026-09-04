using System.Diagnostics;
using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// Golden round-trips against actual Rust-produced <c>.N.cso</c> files from the
/// reference <c>xdvdfs-cli 0.8.3</c> binary (<c>ciso 0.2.1</c> split writer/reader).
/// The binary lives in the gitignored <c>References/xdvdfs-0.8.3</c> folder; tests that
/// need it silently pass when it is absent (same pattern as the <c>zarchive.exe</c> interop).
///
/// Notes on the reference tooling, verified against <c>ciso 0.2.1</c> sources:
/// <list type="bullet">
/// <item><c>unpack</c>/<c>copy-out</c> use <c>open_image_raw</c> and do NOT accept <c>.cso</c>
/// input, so the content oracle here is <c>xdvdfs md5</c> (cso-aware via
/// <c>open_image</c>), comparing <c>md5  /path</c> listings.</item>
/// <item>The reference <c>SplitOutput</c> writes each part sparsely at global stream
/// offsets (a write crossing the split point lands whole, overshooting into the next
/// part's range); <c>CisoSplitOutput</c> replicates this loop exactly.</item>
/// <item>Multi-part files cannot be consumed by stock 0.8.3 itself: its
/// <c>SplitFileReader</c> maps parts at cumulative file sizes while part data sits at
/// global offsets, so reads past the first part hit zero gaps (<c>read.rs</c> assert).
/// <c>CisoSplitInputStream</c> instead tiles parts by previous part length
/// (<c>starts[k] = parts[k-1].Length</c>), which matches the writer layout exactly.
/// Single-part files are unaffected on both sides.</item>
/// </list>
/// </summary>
[Collection("Sequential")]
public class CisoSplitInteropTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

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
                /* best effort */
            }

            try
            {
                if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Solution root not found.");
    }

    private static string XdvdfsExePath() =>
        Path.Combine(SolutionRoot(), "References", "xdvdfs-0.8.3", "xdvdfs.exe");

    private static bool ReferenceAvailable() =>
        OperatingSystem.IsWindows() && File.Exists(XdvdfsExePath());

    private static string RunXdvdfs(string arguments, string workDir)
    {
        var psi = new ProcessStartInfo(XdvdfsExePath(), arguments)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        Assert.True(proc.WaitForExit(120000), "xdvdfs.exe timed out.");
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.ExitCode == 0, $"xdvdfs.exe failed: {stderr}");
        return stdout;
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xiso_cisointerop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTempIso(string? sourceDir = null)
    {
        sourceDir ??= SourceDir;
        var outDir = CreateTempDir();
        var rc = XisoWriter.CreateXiso(sourceDir, outDir, null, null, out var outPath, null, null);
        Assert.Equal(0, rc);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
        return outPath;
    }

    private static byte[] ComputeSha256(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static Dictionary<string, string> ParseMd5Listing(string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var sep = trimmed.IndexOf("  ", StringComparison.Ordinal);
            Assert.True(sep > 0, $"unexpected md5 line: {trimmed}");
            map[trimmed[(sep + 2)..]] = trimmed[..sep];
        }

        return map;
    }

    private static string StockMd5(string imagePath, string workDir) =>
        RunXdvdfs($"md5 \"{imagePath}\"", workDir);

    /// <summary>
    /// Keeps only entries that are files on disk: directory dirtab bytes are
    /// packer-specific (entry order/padding) and legitimately differ between
    /// our packer and the reference repack.
    /// </summary>
    private static Dictionary<string, string> FilesOnly(Dictionary<string, string> listing, string sourceDir)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, hash) in listing)
        {
            string local = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(sourceDir, local)))
            {
                files[path] = hash;
            }
        }

        return files;
    }

    [Fact]
    public void RustCompressIso_OurReaderDecompressesIdenticalContent()
    {
        if (!ReferenceAvailable())
        {
            return; // reference binary not present; covered by self round-trip tests
        }

        string isoPath = CreateTempIso();
        string workDir = CreateTempDir();

        // Note: the reference SplitOutput derives part names from the file name only
        // and creates them relative to the process working directory.
        RunXdvdfs($"compress \"{isoPath}\" \"{Path.Combine(workDir, "rust.cso")}\"", workDir);

        string part1 = Path.Combine(workDir, "rust.1.cso");
        Assert.True(File.Exists(part1), "expected the reference writer to emit rust.1.cso");
        Assert.True(CisoReader.IsCso(part1));

        string decPath = Path.Combine(CreateTempDir(), "rust.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(part1, decPath));

        // The reference repacks the image before compressing, so compare content
        // listings through the same tool rather than raw ISO bytes (as dicts:
        // dirtab order may differ between packers).
        Assert.Equal(
            FilesOnly(ParseMd5Listing(StockMd5(isoPath, workDir)), SourceDir),
            FilesOnly(ParseMd5Listing(StockMd5(decPath, workDir)), SourceDir));

        using var dev = new XISOSharp.BlockDevice.CisoBlockDevice(part1);
        Assert.Equal(new FileInfo(decPath).Length, dev.Length);
    }

    [Fact]
    public void RustCompressDir_OurReaderRoundTripsContent()
    {
        if (!ReferenceAvailable())
        {
            return; // reference binary not present; covered by self round-trip tests
        }

        string srcDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        byte[] blob = Enumerable.Range(0, 200000).Select(i => (byte)((i * 2654435761u) >> 16)).ToArray();
        File.WriteAllText(Path.Combine(srcDir, "hello.txt"), "hello rust golden world");
        File.WriteAllBytes(Path.Combine(srcDir, "sub", "blob.bin"), blob);

        string workDir = CreateTempDir();
        RunXdvdfs($"compress \"{srcDir}\" \"{Path.Combine(workDir, "rustdir.cso")}\"", workDir);

        string part1 = Path.Combine(workDir, "rustdir.1.cso");
        Assert.True(File.Exists(part1), "expected the reference writer to emit rustdir.1.cso");

        string decPath = Path.Combine(CreateTempDir(), "rustdir.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(part1, decPath));

        var got = FilesOnly(ParseMd5Listing(StockMd5(decPath, workDir)), srcDir);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/hello.txt"] = Md5Hex("hello rust golden world"u8.ToArray()),
            ["/sub/blob.bin"] = Md5Hex(blob),
        };
        Assert.Equal(expected, got);
    }

    [Fact]
    public void OurSingleCso_StockReadsIdenticalContent()
    {
        if (!ReferenceAvailable())
        {
            return; // reference binary not present; covered by self round-trip tests
        }

        string isoPath = CreateTempIso();
        string workDir = CreateTempDir();
        string csoPath = Path.Combine(workDir, "single.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath, level: 9));

        Assert.Equal(
            FilesOnly(ParseMd5Listing(StockMd5(isoPath, workDir)), SourceDir),
            FilesOnly(ParseMd5Listing(StockMd5(csoPath, workDir)), SourceDir));
    }

    [Fact]
    public void OurSplitParts_FollowReferenceWriterLayout()
    {
        // Structural parity with ciso::split::SplitOutput (verified against ciso 0.2.1
        // split.rs): part names, sparse global-offset writes, and the overshoot rule
        // (a write crossing the split point lands whole, so each part's data starts
        // exactly where the previous part's file ends). Needs no reference binary.
        string isoPath = CreateTempIso();
        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ours.cso");
        const long splitBytes = 16384;
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath, level: 9, splitBytes: splitBytes));

        var parts = new List<string>();
        for (var i = 1;; i++)
        {
            var part = Path.Combine(csoDir, $"ours.{i}.cso");
            if (!File.Exists(part)) break;
            parts.Add(part);
        }

        Assert.True(parts.Count >= 2, $"expected at least 2 parts, got {parts.Count}");

        long previousLength = 0;
        foreach (var part in parts)
        {
            byte[] data = File.ReadAllBytes(part);
            var firstData = Array.FindIndex(data, b => b != 0);
            Assert.True(firstData >= 0, $"{part} has no data");
            // Data starts exactly where the previous part's file ends (sequential
            // global-offset writes into sparse part files).
            Assert.Equal(previousLength, firstData);
            previousLength = data.Length;
        }

        // The logical stream tiles contiguously: the last part ends at the stream end.
        Assert.Equal(new FileInfo(parts[^1]).Length, previousLength);

        // Content round-trips through our tiling reader (SplitFileReader parity note
        // in the class doc: stock 0.8.3 itself cannot read sparse multi-part files).
        string decPath = Path.Combine(CreateTempDir(), "ours.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(parts[0], decPath));
        Assert.True(ComputeSha256(isoPath).AsSpan().SequenceEqual(ComputeSha256(decPath)));
    }
}
