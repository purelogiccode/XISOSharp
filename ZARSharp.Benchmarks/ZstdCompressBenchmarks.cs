using BenchmarkDotNet.Attributes;
using ZARSharp.Zstd;

namespace ZARSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Benchmarks <see cref="ZstdCompressor.CompressBlock"/> single-shot frames at
/// levels 1/6/19 over text, random (raw-fallback path), hetero and multi-block
/// inputs. Levels 1 and 6 are the ZAR container's hot path (default 6);
/// level 19 pins the btultra2 binary-tree finder cost.
/// </summary>
public class ZstdCompressBenchmarks
{
    private ZstdCompressor _l1 = null!;
    private ZstdCompressor _l6 = null!;
    private ZstdCompressor _l19 = null!;
    private byte[] _text8k = null!;
    private byte[] _random8k = null!;
    private byte[] _hetero64k = null!;
    private byte[] _text200k = null!;

    /// <summary>
    /// Builds the compressors and frozen payloads once per benchmark process.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _l1 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1));
        _l6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _l19 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(19));
        _text8k = BenchmarkCorpus.CycleText(8192);
        _random8k = BenchmarkCorpus.Random(8192);
        _hetero64k = BenchmarkCorpus.Hetero64();
        _text200k = BenchmarkCorpus.CycleText(200000);
    }

    /// <summary>Level 1 over 8 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text8k()
    {
        return _l1.CompressBlock(_text8k);
    }

    /// <summary>Level 1 over 8 KiB of random bytes (exercises the raw-fallback path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Random8k()
    {
        return _l1.CompressBlock(_random8k);
    }

    /// <summary>Level 1 over the hetero 64 KiB block (single compressed block).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Hetero64k()
    {
        return _l1.CompressBlock(_hetero64k);
    }

    /// <summary>Level 1 over 200 KiB of text (multi-block frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text200k()
    {
        return _l1.CompressBlock(_text200k);
    }

    /// <summary>Level 6 over 8 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text8k()
    {
        return _l6.CompressBlock(_text8k);
    }

    /// <summary>Level 6 over 8 KiB of random bytes (exercises the raw-fallback path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Random8k()
    {
        return _l6.CompressBlock(_random8k);
    }

    /// <summary>Level 6 over the hetero 64 KiB block (the ZAR container's typical hot path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Hetero64k()
    {
        return _l6.CompressBlock(_hetero64k);
    }

    /// <summary>Level 6 over 200 KiB of text (multi-block frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text200k()
    {
        return _l6.CompressBlock(_text200k);
    }

    /// <summary>Level 19 over 8 KiB of text (btultra2 path on a small frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L19_Text8k()
    {
        return _l19.CompressBlock(_text8k);
    }

    /// <summary>Level 19 over the hetero 64 KiB block (btultra2 binary-tree finder cost).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L19_Hetero64k()
    {
        return _l19.CompressBlock(_hetero64k);
    }
}
