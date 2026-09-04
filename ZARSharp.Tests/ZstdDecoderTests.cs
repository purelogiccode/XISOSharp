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
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        byte[] outBuf = new byte[n];
        for (int i = 0; i < n; i++)
        {
            outBuf[i] = ascii[i % ascii.Length];
        }

        return outBuf;
    }

    [Fact]
    public void ConcatenatedFramesDecodeToConcatenation()
    {
        byte[] a = Text(1000);
        byte[] b = Text(70000);
        byte[] fa = new ZstdCompressor().CompressBlock(a);
        byte[] fb = new ZstdCompressor().CompressBlock(b);

        byte[] both = [.. fa, .. fb];
        byte[] decoded = ZstdDecompressor.Decompress(both);
        Assert.Equal([.. a, .. b], decoded);
    }

    [Fact]
    public void SkippableFrameBetweenFramesIsSkipped()
    {
        byte[] a = Text(5000);
        byte[] fa = new ZstdCompressor().CompressBlock(a);

        // Skippable frame: magic 0x184D2A50 LE + u32 size + payload.
        byte[] skip = [0x50, 0x2A, 0x4D, 0x18, 0x04, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];
        byte[] both = [.. fa, .. skip, .. fa];
        byte[] decoded = ZstdDecompressor.Decompress(both);
        Assert.Equal([.. a, .. a], decoded);
    }

    [Fact]
    public void TrailingGarbageStillThrows()
    {
        byte[] fa = new ZstdCompressor().CompressBlock(Text(100));
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
        byte[] input = Text(4096);
        byte[] frame = new ZstdCompressor().CompressBlock(input);
        byte[] dst = new byte[input.Length];
        ZstdDecompressor.DecompressExact(
            frame, 0, frame.Length, dst, 0, dst.Length, new ZstdDecoderOptions());
        Assert.Equal(input, dst);
    }

    [Theory]
    [InlineData(17)] // as emitted (128 KiB)
    [InlineData(24)] // 16 MiB foreign-style window
    [InlineData(27)] // 128 MiB foreign-style window
    [InlineData(29)] // 512 MiB = default cap, boundary accepted
    public void LargeWindowDescriptorAcceptedUpToCap(int windowLog)
    {
        // Foreign frames may declare windows far larger than their content;
        // patch our frame's window descriptor (byte 5: magic + descriptor)
        // to claim windowLog and verify content still decodes.
        // (Claiming a SMALLER window would invalidate real offsets, so the
        // sweep starts at the emitted windowLog 17.)
        byte[] input = Text(65536);
        byte[] frame = new ZstdCompressor().CompressBlock(input);
        Assert.Equal(0x38, frame[5]); // locks layout: explicit windowLog 17
        frame[5] = (byte)((windowLog - 10) << 3);
        Assert.Equal(input, ZstdDecompressor.Decompress(frame));
    }

    [Theory]
    [InlineData(30)] // 1 GiB window
    [InlineData(31)] // 2 GiB window
    public void WindowAboveDefaultCapRejectedUnlessRaised(int windowLog)
    {
        byte[] input = Text(65536);
        byte[] frame = new ZstdCompressor().CompressBlock(input);
        frame[5] = (byte)((windowLog - 10) << 3);

        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(frame));
        var wide = new ZstdDecoderOptions { MaxWindowSize = 1UL << 32 };
        Assert.Equal(input, ZstdDecompressor.Decompress(frame, wide));
    }

    [Fact]
    public void OptionsCapsRejectOversizedFrames()
    {
        byte[] frame = new ZstdCompressor().CompressBlock(Text(65536)); // 128 KiB window
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