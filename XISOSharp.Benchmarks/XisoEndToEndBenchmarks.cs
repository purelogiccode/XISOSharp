using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

/// <summary>
/// End-to-end benchmarks: pack a synthetic tree, open/list/extract it, hash it,
/// round-trip CISO and CCI, and read a larger image. These cover the I/O paths
/// the micro-benchmarks do not, so a regression in the writer/reader or
/// container codecs shows up as a trend. Run the whole set with
/// <c>--filter "*EndToEnd*"</c>.
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
    private string _cci = string.Empty;
    private string _largeIso = string.Empty;
    private int _extractCounter;

    /// <summary>Creates a synthetic source tree and the derived images once.</summary>
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
        _cci = Path.Combine(_root, "bench.cci");
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

        rc = XisoCci.CompressToCci(_iso, _cci, level: 9, splitBytes: 0, overwrite: true);
        if (rc != 0)
        {
            throw new InvalidOperationException("Could not create the benchmark CCI.");
        }

        // A larger image for sequential/random read throughput. The payload is
        // zero-heavy so setup stays fast and compression-friendly.
        string largeSource = Path.Combine(_root, "large-src");
        Directory.CreateDirectory(largeSource);
        using (FileStream large = new(Path.Combine(largeSource, "large.bin"),
                   FileMode.Create, FileAccess.Write, FileShare.None))
        {
            large.SetLength(64L * 1024 * 1024);
            large.Position = 0;
            large.Write([1, 2, 3, 4]);
        }

        File.WriteAllText(Path.Combine(largeSource, "readme.txt"), "large fixture\n");
        _largeIso = Path.Combine(_root, "large.iso");
        rc = XisoWriter.PackFromDirectory(largeSource, _largeIso);
        if (rc != 0)
        {
            throw new InvalidOperationException("Could not create the large benchmark ISO.");
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

    /// <summary>Extracts the ISO tree to disk (sequential unpack path).</summary>
    [Benchmark]
    public int Extract()
    {
        string output = Path.Combine(_root, $"extract-{Interlocked.Increment(ref _extractCounter)}");
        try
        {
            return XisoReader.UnpackImage(_iso, output);
        }
        finally
        {
            try
            {
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>Compresses the ISO to CCI at level 9 (single part).</summary>
    [Benchmark]
    public int CciCompress()
    {
        string output = Path.Combine(_root, "compress.cci");
        try
        {
            return XisoCci.CompressToCci(_iso, output, level: 9, splitBytes: 0, overwrite: true);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>Decompresses the CCI back to ISO.</summary>
    [Benchmark]
    public int CciDecompress()
    {
        string output = Path.Combine(_root, "cci-decompress.iso");
        try
        {
            return XisoCci.DecompressToIso(_cci, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// Random 4 KiB reads from the 64 MiB image's large file, exercising the
    /// image facade and block-device read path.
    /// </summary>
    [Benchmark]
    public long LargeImageRandomRead()
    {
        using XisoImage image = XisoImage.Open(_largeIso);
        byte[] buffer = new byte[4096];
        Random random = new(1234);
        long total = 0;
        const long maxOffset = (64L * 1024 * 1024) - 4096;
        for (int i = 0; i < 512; i++)
        {
            total += image.ReadFile("/large.bin", buffer, random.NextInt64(0, maxOffset));
        }

        return total;
    }
}
