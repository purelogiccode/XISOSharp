using BenchmarkDotNet.Attributes;

namespace ZARSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Benchmarks container pack (<see cref="ZArchiveWriter"/>) and extract
/// (<see cref="ZArchiveReader"/>) over four 64 KiB files into a memory stream,
/// isolating library cost from disk I/O.
/// </summary>
public class ZarArchiveBenchmarks
{
    private string[] _names = null!;
    private byte[][] _files = null!;
    private byte[] _archive = null!;

    /// <summary>
    /// Builds four varied 64 KiB inputs and packs the reference archive once.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _names = ["file0.bin", "file1.bin", "file2.bin", "file3.bin"];
        _files =
        [
            BenchmarkCorpus.CycleTextAt(65536, 0),
            BenchmarkCorpus.CycleTextAt(65536, 37),
            BenchmarkCorpus.Hetero64(),
            BenchmarkCorpus.Random(65536, 0xA41C),
        ];
        _archive = PackToArray();
    }

    private byte[] PackToArray()
    {
        using var ms = new MemoryStream();
        using (var writer = new ZArchiveWriter(ms))
        {
            for (var i = 0; i < _files.Length; i++)
            {
                writer.StartNewFile(_names[i]);
                writer.AppendData(_files[i]);
            }

            writer.Finalize();
        }

        return ms.ToArray();
    }

    /// <summary>Packs the four files into a fresh memory stream.</summary>
    /// <returns>The archive size in bytes.</returns>
    [Benchmark]
    public long Pack_4x64k()
    {
        using var ms = new MemoryStream();
        using (var writer = new ZArchiveWriter(ms))
        {
            for (var i = 0; i < _files.Length; i++)
            {
                writer.StartNewFile(_names[i]);
                writer.AppendData(_files[i]);
            }

            writer.Finalize();
        }

        return ms.Length;
    }

    /// <summary>Opens the packed archive and reads back all four files.</summary>
    /// <returns>The total decompressed size in bytes.</returns>
    [Benchmark]
    public int Extract_4x64k()
    {
        using var reader = ZArchiveReader.TryOpen(_archive)!;
        var total = 0;
        for (var i = 0; i < _names.Length; i++)
        {
            total += reader.ReadFile(reader.LookUp(_names[i])).Length;
        }

        return total;
    }
}
