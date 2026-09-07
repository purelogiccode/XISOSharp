using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class BoyerMooreBenchmarks
{
    private BoyerMoore _bm = null!;
    private byte[] _tailHit = null!;
    private byte[] _headHit = null!;
    private byte[] _miss = null!;

    /// <summary>
    /// Gets or sets the haystack size in bytes for the current benchmark iteration.
    /// </summary>
    [Params(1024, 65536, 2097152)] public int HaystackSize;

    /// <summary>
    /// Builds the searcher once per haystack size (tables are read-only during search).
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _bm = new BoyerMoore(Constants.MediaEnable);
        _bm.Init();
    }

    /// <summary>
    /// Rebuilds fresh haystacks before each iteration (BUG-BEN-003): reusing one
    /// buffer warms the branch predictor/cache and hides per-call cost, and a
    /// single tail-hit never exercises early-hit/miss paths.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        var rng = new Random(42 + HaystackSize);
        _tailHit = new byte[HaystackSize];
        rng.NextBytes(_tailHit);
        Constants.MediaEnable.CopyTo(_tailHit.AsSpan(_tailHit.Length - Constants.MediaEnableLength));

        _headHit = new byte[HaystackSize];
        rng.NextBytes(_headHit);
        Constants.MediaEnable.CopyTo(_headHit.AsSpan(0));

        // Miss buffer: fill with a byte absent from the pattern when possible so
        // the pattern genuinely cannot occur; fall back to verifying absence.
        _miss = new byte[HaystackSize];
        Array.Fill(_miss, (byte)0x00);
        if (Constants.MediaEnable.Contains((byte)0x00))
        {
            rng.NextBytes(_miss);
            while (ContainsPattern(_miss))
            {
                rng.NextBytes(_miss);
            }
        }
    }

    private static bool ContainsPattern(byte[] haystack)
    {
        var pattern = Constants.MediaEnable;
        for (var i = 0; i + pattern.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Searches a haystack with the pattern at the tail (worst-case scan).
    /// </summary>
    /// <returns>The index of the match.</returns>
    [Benchmark(Baseline = true)]
    public int SearchTailHit()
    {
        return _bm.Search(_tailHit);
    }

    /// <summary>
    /// Searches a haystack with the pattern at offset zero (best-case early exit).
    /// </summary>
    /// <returns>The index of the match (0).</returns>
    [Benchmark]
    public int SearchHeadHit()
    {
        return _bm.Search(_headHit);
    }

    /// <summary>
    /// Searches a haystack containing no occurrence (full-scan miss).
    /// </summary>
    /// <returns>-1 when not found.</returns>
    [Benchmark]
    public int SearchMiss()
    {
        return _bm.Search(_miss);
    }
}
