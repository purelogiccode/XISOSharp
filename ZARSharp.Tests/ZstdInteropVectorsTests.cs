using System.Diagnostics;
using ZARSharp;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 0 interop corpus: the existing C# decoder must decompress every golden
/// frame, and writer output must open in <c>zarchive.exe</c> (extends the pattern
/// in <c>ZArchiveSharpTests</c> with boundary sizes, CP1252 names and trees).
/// </summary>
public sealed class ZstdInteropVectorsTests
{
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

    private static string ZArchiveExePath() =>
        Path.Combine(SolutionRoot(), "References", "ZArchive-0.1.2", "zarchive.exe");

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RunExe(string input, string output)
    {
        var psi = new ProcessStartInfo(ZArchiveExePath(), $"\"{input}\" \"{output}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(120000);
        Assert.True(proc.ExitCode == 0, $"zarchive.exe failed: {proc.StandardOutput.ReadToEnd()}");
    }

    private static byte[] Pattern(int n, int seed)
    {
        byte[] data = new byte[n];
        uint state = (uint)((seed * 2654435761u) + 1);
        for (int i = 0; i < n; i++)
        {
            state = (state * 1664525) + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    private static byte[] RawBlockFrame(byte[] content)
    {
        var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD };
        if (content.Length < 256)
        {
            frame.Add(0x20);
            frame.Add((byte)content.Length);
        }
        else
        {
            frame.Add(0x60);
            int v = content.Length - 256;
            frame.Add((byte)(v & 0xFF));
            frame.Add((byte)(v >> 8));
        }

        uint header = (uint)((content.Length << 3) | (0 << 1) | 1);
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)((header >> 8) & 0xFF));
        frame.Add((byte)((header >> 16) & 0xFF));
        frame.AddRange(content);
        return [.. frame];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    public void Decoder_GoldenRawFrames(int size)
    {
        byte[] content = Pattern(size, size + 7);
        Assert.Equal(content, ZstdDecompressor.Decompress(RawBlockFrame(content)));
    }

    [Fact]
    public void Decoder_CompressorBoundFormula()
    {
        // ZSTD_compressBound(65536) = 65536 + 512 + ((131072-65536)>>11) = 65824.
        Assert.Equal(65824, ZstdCompressor.GetCompressBound(65536));
        Assert.Equal(65824, ZstdCompressor.GetCompressBound(ZArchiveCommon.CompressedBlockSize));
    }

    [Fact]
    public void Interop_BoundarySizes_BothWays()
    {
        if (!File.Exists(ZArchiveExePath()))
        {
            return;
        }

        string tmp = NewTempDir("boundary");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(indir);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["empty.bin"] = [],
                ["one.bin"] = [0x42],
                ["exact64k.bin"] = Pattern(65536, 1),
                ["plus1.bin"] = Pattern(65537, 2), // exercises zero-padding
                ["zeros.bin"] = new byte[70000],
            };
            var rnd = new Random(99);
            byte[] incompressible = new byte[70000];
            rnd.NextBytes(incompressible);
            files["random.bin"] = incompressible;

            foreach (var (rel, data) in files)
            {
                File.WriteAllBytes(Path.Combine(indir, rel), data);
            }

            // exe -> ZARSharp.
            string refZar = Path.Combine(tmp, "ref.zar");
            RunExe(indir, refZar);
            using (var reader = ZArchiveReader.TryOpen(refZar))
            {
                Assert.NotNull(reader);
                foreach (var (rel, data) in files)
                {
                    if (data.Length == 0)
                    {
                        Assert.Equal(0ul, reader!.GetFileSize(reader.LookUp(rel)));
                        continue;
                    }

                    Assert.Equal(data, reader!.ReadFile(reader.LookUp(rel)));
                }
            }

            // ZARSharp -> exe.
            string ourZar = Path.Combine(tmp, "ours.zar");
            ZArchiveTool.Pack(indir, ourZar);
            string outdir = Path.Combine(tmp, "out");
            RunExe(ourZar, outdir);
            foreach (var (rel, data) in files)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outdir, rel)));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Interop_TreesAndCp1252Names_BothWays()
    {
        if (!File.Exists(ZArchiveExePath()))
        {
            return;
        }

        string tmp = NewTempDir("trees");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(Path.Combine(indir, "docs", "sub"));
            File.WriteAllBytes(Path.Combine(indir, "docs", "readme.txt"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(indir, "docs", "sub", "data.bin"), Pattern(1000, 3));
            File.WriteAllBytes(Path.Combine(indir, "caf\u00e9.txt"), [4, 5]); // é = 0xE9 in CP1252
            File.WriteAllBytes(Path.Combine(indir, "na\u00efve.bin"), Pattern(70000, 4)); // ï multi-block

            string refZar = Path.Combine(tmp, "ref.zar");
            RunExe(indir, refZar);
            using (var reader = ZArchiveReader.TryOpen(refZar))
            {
                Assert.NotNull(reader);
                Assert.Equal([1, 2, 3], reader!.ReadFile(reader.LookUp("docs/readme.txt")));
                Assert.Equal(Pattern(1000, 3), reader.ReadFile(reader.LookUp("docs/sub/data.bin")));
            }

            string ourZar = Path.Combine(tmp, "ours.zar");
            ZArchiveTool.Pack(indir, ourZar);
            string outdir = Path.Combine(tmp, "out");
            RunExe(ourZar, outdir);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(outdir, "docs", "readme.txt")));
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch
            {
            }
        }
    }
}