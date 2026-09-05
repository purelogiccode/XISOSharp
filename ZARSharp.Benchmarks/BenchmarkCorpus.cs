namespace ZARSharp.Benchmarks;

/// <summary>
/// Deterministic benchmark payloads mirroring the committed golden corpus
/// (<c>ZARSharp.Tests/Goldens/zstd/</c>): phrase-cycle text, seeded random
/// bytes, and the hetero mix (16 KiB text + 8 KiB random + 8 KiB text, zero
/// padded to 64 KiB). Fixed seeds only — never <c>HashCode.Combine</c>.
/// Buffers are built once and frozen; the compressor/reader never mutate
/// their inputs.
/// </summary>
internal static class BenchmarkCorpus
{
    private static readonly byte[] Phrase =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. "u8.ToArray();

    /// <summary>Cycles the phrase buffer to exactly <paramref name="n"/> bytes.</summary>
    public static byte[] CycleText(int n)
    {
        return CycleTextAt(n, 0);
    }

    /// <summary>Cycles the phrase buffer with a start <paramref name="offset"/> for per-file variety.</summary>
    public static byte[] CycleTextAt(int n, int offset)
    {
        var buf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            buf[i] = Phrase[(offset + i) % Phrase.Length];
        }

        return buf;
    }

    /// <summary>Deterministic pseudo-random bytes from a fixed <paramref name="seed"/>.</summary>
    public static byte[] Random(int n, int seed = 0x60D6)
    {
        var buf = new byte[n];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>Hetero 64 KiB block: 16 KiB text + 8 KiB random + 8 KiB text, zero padded.</summary>
    public static byte[] Hetero64()
    {
        var buf = new byte[65536];
        CycleTextAt(16384, 0).CopyTo(buf, 0);
        Random(8192).CopyTo(buf, 16384);
        CycleTextAt(8192, 17).CopyTo(buf, 24576);
        return buf;
    }
}
