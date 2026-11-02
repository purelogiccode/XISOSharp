using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

/// <summary>
/// End-to-end benchmarks: pack a synthetic tree, open/list it, hash it and
/// round-trip CISO. These cover the I/O paths the micro-benchmarks do not, so a
/// regression in the writer/reader or container codecs shows up as a trend.
/// </summary>
[MemoryDiagnoser]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class XisoEndToEndBenchmarks
{
    private string _root = string.Empty;
    private string _source = string.Empty;
    private string _iso = string.Empty;
    private string _cso = string.Empty;

    /// <summary>Creates a synthetic source tree and a packed ISO once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"xiso_bench_{Guid.NewGuid():N}");
        _source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(_source, "sub"));
        Random random = new(42);
        for (int i = 0; i < 4; i++)
        {
            byte[] data = new byte[256 * 1024];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_source, "sub", $"file{i}.bin"), data);
        }

        File.WriteAllText(Path.Combine(_source, "readme.txt"), "benchmark fixture\n");
        _iso = Path.Combine(_root, "bench.iso");
        _cso = Path.Combine(_root, "bench.cso");
        int rc = XisoWriter.PackFromDirectory(_source, _iso);
        if (rc != 0)
        {
            throw new InvalidOperationException("Could not create the benchmark ISO.");
        }

        rc = CisoWriter.CompressToCso(_iso, _cso, level: 6);
        if (rc != 0)
        {
            throw new InvalidOperationException("Could not create the benchmark CISO.");
        }
    }

    /// <summary>Removes the temporary fixture.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Packs the source tree into a fresh ISO.</summary>
    [Benchmark]
    public int Pack()
    {
        string output = Path.Combine(_root, "pack.iso");
        try
        {
            return XisoWriter.PackFromDirectory(_source, output);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>Opens the ISO and walks the full tree.</summary>
    [Benchmark]
    public int OpenAndList()
    {
        using XisoImage image = XisoImage.Open(_iso);
        return image.ListFlat().Count;
    }

    /// <summary>Hashes the logical image (CRC32/MD5/SHA-1/SHA-256/SHA3-256).</summary>
    [Benchmark]
    public XisoImageHashResult Hash()
    {
        return XisoImageHash.Compute(_iso);
    }

    /// <summary>Compresses the ISO to CISO.</summary>
    [Benchmark]
    public int CisoCompress()
    {
        string output = Path.Combine(_root, "compress.cso");
        try
        {
            return CisoWriter.CompressToCso(_iso, output, level: 6);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>Decompresses the CISO back to ISO.</summary>
    [Benchmark]
    public int CisoDecompress()
    {
        string output = Path.Combine(_root, "decompress.iso");
        try
        {
            return CisoReader.DecompressToIso(_cso, output);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
