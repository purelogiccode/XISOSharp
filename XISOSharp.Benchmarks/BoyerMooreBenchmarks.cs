using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Benchmarks <see cref="BoyerMoore.Search(byte[])"/> over random haystacks of several
/// sizes, with the media-enable pattern planted at the end.
/// </summary>
public class BoyerMooreBenchmarks
{
    private BoyerMoore _bm = null!;
    private byte[] _haystack = null!;

    /// <summary>
    /// Gets or sets the haystack size in bytes for the current benchmark iteration.
    /// </summary>
    [Params(1024, 65536, 2097152)] public int HaystackSize;

    /// <summary>
    /// Builds the searcher and fills a random haystack with the target pattern at the tail.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _bm = new BoyerMoore(Constants.MediaEnable);
        _bm.Init();
        _haystack = new byte[HaystackSize];
        new Random(42).NextBytes(_haystack);
        _haystack[^8] = 0xE8;
        _haystack[^7] = 0xCA;
        _haystack[^6] = 0xFD;
        _haystack[^5] = 0xFF;
        _haystack[^4] = 0xFF;
        _haystack[^3] = 0x85;
        _haystack[^2] = 0xC0;
        _haystack[^1] = 0x7D;
    }

    /// <summary>
    /// Searches the prepared haystack for the media-enable pattern.
    /// </summary>
    /// <returns>The index of the match, or -1 when not found.</returns>
    [Benchmark]
    public int SearchPattern()
    {
        return _bm.Search(_haystack);
    }
}