using System.Diagnostics;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 5–6 acceptance: block/frame encoder properties, <c>Compress</c> API
/// semantics, checksums, multi-block frames, and native decoding of our
/// frames (python <c>compression.zstd</c>, skipped when unavailable).
/// Round-trip matrix itself lives in <c>ZstdRoundTripTests</c> (unskipped).
/// </summary>
public sealed class ZstdEncoderTests
{
    private static byte[] Text(int n, int seed = 42)
    {
        const string sample = "The quick brown fox jumps over the lazy dog. ZArchive block 64 KiB. ";
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        byte[] outBuf = new byte[n];
        for (int i = 0; i < n; i++)
        {
            outBuf[i] = ascii[(i + seed) % ascii.Length];
        }

        return outBuf;
    }

    private static byte[] Random(int n, int seed)
    {
        byte[] outBuf = new byte[n];
        new Random(seed).NextBytes(outBuf);
        return outBuf;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CompressBlock_Text64K_CompressesAndRoundTrips(int level)
    {
        byte[] input = Text(65536);
        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
        byte[] frame = compressor.CompressBlock(input);
        Assert.True(frame.Length < input.Length, $"L{level}: frame {frame.Length} not smaller than input.");
        Assert.Equal(input, ZstdDecompressor.Decompress(frame));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void CompressBlock_AllKinds_RoundTrip(int level)
    {
        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
        byte[][] inputs =
        [
            new byte[4096],
            Text(4096),
            Random(4096, 7),
            Text(65536),
            Random(1024, 9),
            [1, 2, 3, 4, 5, 6, 7, 8],
        ];
        foreach (byte[] input in inputs)
        {
            Assert.Equal(input, ZstdDecompressor.Decompress(compressor.CompressBlock(input)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1023)]
    [InlineData(4096)]
    [InlineData(32768)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)] // First multi-block frame.
    [InlineData(100000)] // Multi-block frame.
    public void CompressBlock_Sizes_RoundTrip(int size)
    {
        byte[] input = Text(size);
        var compressor = new ZstdCompressor();
        Assert.Equal(input, ZstdDecompressor.Decompress(compressor.CompressBlock(input)));

        byte[] random = Random(size, size + 1);
        Assert.Equal(random, ZstdDecompressor.Decompress(compressor.CompressBlock(random)));
    }

    [Fact]
    public void CompressBlock_Zeros_IsTiny()
    {
        byte[] frame = new ZstdCompressor().CompressBlock(new byte[65536]);
        Assert.True(frame.Length < 64, $"Zeros frame is {frame.Length} bytes.");
        Assert.Equal(new byte[65536], ZstdDecompressor.Decompress(frame));
    }

    [Fact]
    public void CompressBlock_Empty_IsValidEmptyFrame()
    {
        byte[] frame = new ZstdCompressor().CompressBlock([]);
        Assert.Equal([], ZstdDecompressor.Decompress(frame));
    }

    [Fact]
    public void Compress_SpanApi_DeclinesIncompressible()
    {
        var compressor = new ZstdCompressor();
        byte[] random = Random(65536, 1234);
        byte[] dst = new byte[ZstdCompressor.GetCompressBound(random.Length)];
        Assert.Equal(-1, compressor.Compress(random, dst));

        byte[] tiny = new byte[10];
        Assert.Equal(-1, compressor.Compress(tiny, new byte[128]));
        Assert.Equal(-1, compressor.Compress([], new byte[128]));
    }

    [Fact]
    public void Compress_SpanApi_CompressibleFitsAndDecodes()
    {
        var compressor = new ZstdCompressor();
        byte[] input = Text(65536);
        byte[] dst = new byte[ZstdCompressor.GetCompressBound(input.Length)];
        int size = compressor.Compress(input, dst);
        Assert.True(size > 0 && size < input.Length);
        Assert.Equal(input, ZstdDecompressor.Decompress(dst[..size]));

        // Too-small destination declines instead of truncating.
        Assert.Equal(-1, compressor.Compress(input, new byte[size - 1]));
    }

    [Fact]
    public void CompressBlock_Checksum_RoundTripsAndDetectsCorruption()
    {
        byte[] input = Text(8192);
        var plain = new ZstdCompressor();
        var checksummed = new ZstdCompressor(new ZstdCompressionOptions { Level = 6, ChecksumFlag = true });
        byte[] framePlain = plain.CompressBlock(input);
        byte[] frameSum = checksummed.CompressBlock(input);
        Assert.Equal(framePlain.Length + 4, frameSum.Length);
        Assert.Equal(input, ZstdDecompressor.Decompress(frameSum));

        // Corrupt the checksum → decoder rejects.
        frameSum[^1] ^= 0xFF;
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(frameSum));

        // Corrupt a payload byte → decoder rejects (or checksum catches it).
        byte[] corrupt = (byte[])checksummed.CompressBlock(input).Clone();
        corrupt[corrupt.Length / 2] ^= 0x40;
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(corrupt));
    }

    [Fact]
    public void CompressBlock_Random_IsValidViaRawBlocks()
    {
        // Incompressible data still yields a valid frame (raw blocks inside).
        byte[] random = Random(65536, 555);
        byte[] frame = new ZstdCompressor().CompressBlock(random);
        Assert.Equal(random, ZstdDecompressor.Decompress(frame));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CompressBlock_RawFirstBlock_PreservesRepeatHistory(int level)
    {
        // Regression: a raw block must not advance the frame-scoped repeat
        // history (upstream runs ZSTD_blockState_confirmRepcodesAndEntropyTables
        // only for emitted compressed blocks). Block 0 is incompressible (raw
        // fallback) but its finder still stores sequences at L3+, staging
        // history {7912,1,4}; block 1 then reuses offset 7912 as repeat
        // code 1. Before the snapshot/restore fix, L3+ resolved it against
        // the stale {1,4,8} history (first diff at source byte 73470).
        // If the encoder ever compresses block 0 outright, this input no
        // longer covers the path — find a new trigger, do not delete this.
        byte[] block0 = RawTriggerBlock();
        byte[] block1 = Random(65536, 99);
        Array.Copy(block1, 0, block1, 7912, 4096);

        uint[] rep = ZstdSeq.FreshRepeatOffsets();
        int blockSize = ZstdBlockEncoder.EncodeBlock(
            block0, level, new byte[70000], 0, 70000, lastBlock: false, rep);
        Assert.True(
            blockSize < 0 || blockSize >= block0.Length + 3,
            $"L{level}: trigger block no longer takes the raw path (size {blockSize}).");

        byte[] src = new byte[block0.Length + block1.Length];
        Array.Copy(block0, src, block0.Length);
        Array.Copy(block1, 0, src, block0.Length, block1.Length);

        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
        byte[] frame = compressor.CompressBlock(src);
        Assert.Equal(src, ZstdDecompressor.Decompress(frame));

        string? python = FindPythonWithZstd();
        if (python is not null)
        {
            Assert.Equal(src, DecodeWithNativePython(python, frame));
        }
    }

    private static byte[] RawTriggerBlock()
    {
        // Incompressible base with two planted 8-byte repeats: the finder
        // stores sequences (staging repeat history at L3+) while the frame
        // writer still falls back to raw for the block.
        var rng = new Random(1047);
        byte[] chunk = new byte[65536];
        rng.NextBytes(chunk);
        byte[] pat = new byte[8];
        rng.NextBytes(pat);
        for (int k = 0; k < 2; k++)
        {
            int at = rng.Next(0, chunk.Length - 8);
            Array.Copy(pat, 0, chunk, at, 8);
        }

        return chunk;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void NativePython_DecodesOurFrames(int level)
    {
        string? python = FindPythonWithZstd();
        if (python is null)
        {
            // Toolchain-conditional (xunit v2 has no dynamic Skip): passes
            // vacuously without python; runs fully where available.
            return;
        }

        byte[][] inputs = [Text(65536), new byte[65536], Random(5000, 11), Text(0), Text(100000)];
        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
        foreach (byte[] input in inputs)
        {
            byte[] frame = compressor.CompressBlock(input);
            Assert.Equal(input, DecodeWithNativePython(python!, frame));
        }
    }

    private static string? FindPythonWithZstd()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["python", "python3"]
            : ["python3", "python"];
        foreach (string candidate in candidates)
        {
            try
            {
                var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    ArgumentList = { "-c", "import compression.zstd" },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null)
                {
                    continue;
                }

                probe.WaitForExit(15000);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Candidate missing — try the next.
            }
        }

        return null;
    }

    private static byte[] DecodeWithNativePython(string python, byte[] frame)
    {
        string path = Path.Combine(Path.GetTempPath(), $"zarsharp-native-{Guid.NewGuid():N}.zst");
        try
        {
            File.WriteAllBytes(path, frame);
            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys,compression.zstd; sys.stdout.buffer.write(compression.zstd.decompress(open(sys.argv[1],'rb').read()))");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
            using var stdout = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(stdout);
            if (!proc.WaitForExit(60000))
            {
                proc.Kill();
                throw new TimeoutException("Native python decode timed out.");
            }

            Assert.True(proc.ExitCode == 0, $"Native python decode failed with exit {proc.ExitCode}.");
            return stdout.ToArray();
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
