using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 9 corruption tests. Core property: a flipped / truncated byte must
/// either throw <see cref="ZstdException"/> or decode byte-identical to the
/// original — never silent corruption, never a hang. Every decode runs under
/// a 10 s guard (the decoder is fully bounds-checked, so the guard only ever
/// fires on a regression).
///
/// Assertion strength by region (all sound):
/// - Frame header bytes (magic / descriptor / window / FCS) and content
///   checksum bytes: every flip MUST throw (these bytes are always
///   load-bearing; descriptor flips set the reserved bit or change framing).
/// - All other bytes of CHECKSUMMED frames (Huffman tables, FSE NCounts,
///   sequence bitstreams, literal payloads, incl. zero padding bits):
///   throw-or-identical. Any content change breaks the checksum, so silent
///   divergence is impossible; true no-ops (padding bits) decode identical.
///   (Checksumless frames get no body sweep: payload flips there legitimately
///   decode to different content — no integrity protection exists.)
/// - Truncations (any proper prefix): MUST throw (a proper prefix cannot
///   contain the final last-block with matching FCS).
/// Goldens use seeded randomized text so frames are KB-sized with rich
/// entropy tables (a cycled 66 B phrase would collapse to ~80 B and exercise
/// nothing).
/// </summary>
public sealed class ZstdCorruptionTests
{
    private static byte[] MakeText(int n, int seed)
    {
        const string sample = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. ";
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        var rng = new Random(seed);
        byte[] buf = new byte[n];
        for (int i = 0; i < n; i++)
        {
            buf[i] = ascii[(i + rng.Next(ascii.Length)) % ascii.Length];
        }

        return buf;
    }

    private static readonly byte[] TextInput = MakeText(65536, 0xC04E);
    private static readonly byte[] TextFrame =
        new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(TextInput);

    private static readonly byte[] SmallInput = MakeText(2048, 0x5EEE);
    private static readonly byte[] ChecksumFrame =
        new ZstdCompressor(new ZstdCompressionOptions { Level = 6, ChecksumFlag = true })
            .CompressBlock(SmallInput);

    private static readonly byte[] BigChecksumFrame =
        new ZstdCompressor(new ZstdCompressionOptions { Level = 6, ChecksumFlag = true })
            .CompressBlock(TextInput);

    private static readonly byte[] BigInput = MakeText(100000, 0xB16);
    private static readonly byte[] BigFrame = new ZstdCompressor().CompressBlock(BigInput);

    /// <summary>
    /// Decodes under a hang guard. Returns (threw, output-or-null).
    /// </summary>
    private static (bool Threw, byte[]? Output) GuardedDecode(byte[] data)
    {
        Exception? captured = null;
        byte[]? output = null;
        var task = Task.Run(() =>
        {
            try
            {
                output = ZstdDecompressor.Decompress(data);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        bool finished = task.Wait(TimeSpan.FromSeconds(10));
        Assert.True(finished, $"decode hung on {data.Length} B corrupted input");
        return (captured is not null || output is null, output);
    }

    private static void AssertThrowOrIdentical(byte[] corrupted, byte[] original, string where)
    {
        (bool threw, byte[]? output) = GuardedDecode(corrupted);
        if (!threw)
        {
            Assert.True(
                output!.AsSpan().SequenceEqual(original),
                $"silent corruption at {where}");
        }
    }

    private static void AssertThrows(byte[] corrupted, string where)
    {
        (bool threw, _) = GuardedDecode(corrupted);
        Assert.True(threw, $"corruption at {where} decoded without error");
    }

    private static byte[] Flipped(byte[] frame, int pos)
    {
        byte[] corrupted = (byte[])frame.Clone();
        corrupted[pos] ^= 0xFF;
        return corrupted;
    }

    [Fact]
    public void GoldensAreRichFrames()
    {
        // Guards the test design: sweeps are meaningless on degenerate frames.
        Assert.True(TextFrame.Length > 4096, $"text frame too small: {TextFrame.Length}");
        Assert.True(ChecksumFrame.Length > 256, $"checksum frame too small: {ChecksumFrame.Length}");
        Assert.True(BigFrame.Length > TextFrame.Length, "expected a multi-block big frame");
    }

    [Fact]
    public void ChecksumFrameSweepThrowOrIdentical()
    {
        for (int pos = 0; pos < ChecksumFrame.Length; pos++)
        {
            AssertThrowOrIdentical(Flipped(ChecksumFrame, pos), SmallInput, $"checksum frame byte {pos}");
        }
    }

    [Fact]
    public void ChecksumFrameHeaderAndChecksumBytesStrictlyThrow()
    {
        // Magic (0-3), descriptor (4), window (5), FCS (6-7): always load-bearing.
        for (int pos = 0; pos <= 7; pos++)
        {
            AssertThrows(Flipped(ChecksumFrame, pos), $"checksum frame header byte {pos}");
        }

        // Content checksum tail: any flip breaks the integrity check.
        for (int pos = ChecksumFrame.Length - 4; pos < ChecksumFrame.Length; pos++)
        {
            AssertThrows(Flipped(ChecksumFrame, pos), $"checksum byte {pos}");
        }
    }

    [Fact]
    public void BigChecksumFrameSampledSweepThrowOrIdentical()
    {
        // 64 KiB checksummed frame: every sampled flip either breaks framing
        // (throws) or changes content (checksum mismatch throws) or is a true
        // no-op such as a padding bit (identical). Payload flips CANNOT slip
        // through silently — that is the checksum's job. (The same sweep on a
        // checksunless frame would be unsound: payload flips legitimately
        // decode to different content with no integrity protection.)
        for (int pos = 0; pos < BigChecksumFrame.Length; pos += 16)
        {
            AssertThrowOrIdentical(Flipped(BigChecksumFrame, pos), TextInput, $"big checksum frame byte {pos}");
        }
    }

    [Fact]
    public void PlainFrameHeaderBytesStrictlyThrow()
    {
        // Magic flips can never parse; window flip (0x38^0xFF) blows the cap;
        // FCS flips break the content-size check. (Descriptor excluded: an
        // fcsFlag change can still decode identically — redundant field.)
        foreach (int pos in new[] { 0, 1, 2, 3, 5, 6, 7 })
        {
            AssertThrows(Flipped(TextFrame, pos), $"text frame header byte {pos}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(1000)]
    public void TruncationAlwaysThrows(int length)
    {
        Assert.True(length < TextFrame.Length, "test bug: not a proper prefix");
        AssertThrows(TextFrame[..length], $"truncation to {length} B");
    }

    [Fact]
    public void TruncatedTailsAlwaysThrow()
    {
        foreach (int length in new[] { TextFrame.Length - 5, TextFrame.Length - 1 })
        {
            AssertThrows(TextFrame[..length], $"tail truncation to {length} B");
        }
    }

    [Fact]
    public void TruncatedMultiBlockFrameThrows()
    {
        foreach (int length in new[] { 7, 1000, BigFrame.Length / 2, BigFrame.Length - 1 })
        {
            AssertThrows(BigFrame[..length], $"multi-block truncation to {length} B");
        }

        Assert.Equal(BigInput, ZstdDecompressor.Decompress(BigFrame));
    }

    [Fact]
    public void ChecksumMismatchThrows()
    {
        Assert.Equal(SmallInput, ZstdDecompressor.Decompress(ChecksumFrame));
        AssertThrows(Flipped(ChecksumFrame, ChecksumFrame.Length - 1), "checksum last byte");
    }

    [Fact]
    public void EmptyFrameCorruptionThrows()
    {
        byte[] empty = new ZstdCompressor().CompressBlock([]);
        Assert.Empty(ZstdDecompressor.Decompress(empty));
        AssertThrows(Flipped(empty, 0), "empty-frame magic");
    }
}
