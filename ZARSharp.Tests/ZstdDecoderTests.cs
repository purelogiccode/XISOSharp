using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 7 decoder gaps: multi-frame concatenation (already supported by
/// <c>DecompressFrames</c> — locked in here), skippable frames between
/// frames, strict trailing-data rejection, and the configurable window /
/// frame-content caps in <see cref="ZstdDecoderOptions"/> (large foreign
/// windows accepted up to the cap, rejected above it).
/// </summary>
public sealed class ZstdDecoderTests
{
    private static byte[] Text(int n)
    {
        const string sample = "The quick brown fox jumps over the lazy dog. Decoder gap test. ";
        var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        var outBuf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            outBuf[i] = ascii[i % ascii.Length];
        }

        return outBuf;
    }

    [Fact]
    public void ConcatenatedFramesDecodeToConcatenation()
    {
        var a = Text(1000);
        var b = Text(70000);
        var fa = new ZstdCompressor().CompressBlock(a);
        var fb = new ZstdCompressor().CompressBlock(b);

        byte[] both = [.. fa, .. fb];
        var decoded = ZstdDecompressor.Decompress(both);
        Assert.Equal([.. a, .. b], decoded);
    }

    [Fact]
    public void SkippableFrameBetweenFramesIsSkipped()
    {
        var a = Text(5000);
        var fa = new ZstdCompressor().CompressBlock(a);

        // Skippable frame: magic 0x184D2A50 LE + u32 size + payload.
        byte[] skip = [0x50, 0x2A, 0x4D, 0x18, 0x04, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];
        byte[] both = [.. fa, .. skip, .. fa];
        var decoded = ZstdDecompressor.Decompress(both);
        Assert.Equal([.. a, .. a], decoded);
    }

    [Fact]
    public void TrailingGarbageStillThrows()
    {
        var fa = new ZstdCompressor().CompressBlock(Text(100));
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress([.. fa, 0x00]));
    }

    [Fact]
    public void EmptyInputThrowsNoFrame()
    {
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress([]));
    }

    [Fact]
    public void DecompressExactWithOptionsRoundTrips()
    {
        var input = Text(4096);
        var frame = new ZstdCompressor().CompressBlock(input);
        var dst = new byte[input.Length];
        ZstdDecompressor.DecompressExact(
            frame, 0, frame.Length, dst, 0, dst.Length, new ZstdDecoderOptions());
        Assert.Equal(input, dst);
    }

    [Theory]
    [InlineData(17)] // as emitted (explicit form rebuilt below)
    [InlineData(24)] // 16 MiB foreign-style window
    [InlineData(27)] // 128 MiB foreign-style window
    [InlineData(29)] // 512 MiB = default cap, boundary accepted
    public void LargeWindowDescriptorAcceptedUpToCap(int windowLog)
    {
        // Native single-shot frames use single-segment (no window byte);
        // rebuild an explicit-window foreign frame with the same body to
        // verify the decoder accepts large declared windows up to the cap.
        var input = Text(65536);
        var frame = new ZstdCompressor().CompressBlock(input);
        Assert.Equal(0x60, frame[4]); // locks layout: single-segment, FCS 2-byte
        var body = frame[7..]; // skip magic+descriptor+FCS (no window byte)
        var patched = new byte[body.Length + 8];
        Buffer.BlockCopy(frame, 0, patched, 0, 4);
        patched[4] = 0x40; // clear single-segment, keep FCS code 1
        patched[5] = (byte)((windowLog - 10) << 3);
        patched[6] = frame[5];
        patched[7] = frame[6];
        Buffer.BlockCopy(body, 0, patched, 8, body.Length);
        Assert.Equal(input, ZstdDecompressor.Decompress(patched));
    }

    [Theory]
    [InlineData(30)] // 1 GiB window
    [InlineData(31)] // 2 GiB window
    public void WindowAboveDefaultCapRejectedUnlessRaised(int windowLog)
    {
        var input = Text(65536);
        var frame = new ZstdCompressor().CompressBlock(input);
        var body = frame[7..];
        var patched = new byte[body.Length + 8];
        Buffer.BlockCopy(frame, 0, patched, 0, 4);
        patched[4] = 0x40;
        patched[5] = (byte)((windowLog - 10) << 3);
        patched[6] = frame[5];
        patched[7] = frame[6];
        Buffer.BlockCopy(body, 0, patched, 8, body.Length);

        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(patched));
        var wide = new ZstdDecoderOptions { MaxWindowSize = 1UL << 32 };
        Assert.Equal(input, ZstdDecompressor.Decompress(patched, wide));
    }

    [Fact]
    public void OptionsCapsRejectOversizedFrames()
    {
        var frame = new ZstdCompressor().CompressBlock(Text(65536)); // 128 KiB window
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(
            frame, new ZstdDecoderOptions { MaxWindowSize = 1024 }));
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(
            frame, new ZstdDecoderOptions { MaxFrameContentSize = 16 }));
    }

    [Fact]
    public void ZeroCapsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdDecoderOptions { MaxWindowSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdDecoderOptions { MaxFrameContentSize = 0 });
    }
}