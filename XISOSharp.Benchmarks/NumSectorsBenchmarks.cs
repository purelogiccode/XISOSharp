using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
#pragma warning disable RCS1102
// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable once ConvertToStaticClass
public class NumSectorsBenchmarks
#pragma warning restore RCS1102
{
    /// <summary>
    /// Gets or sets the input byte count. Instance state (not a compile-time
    /// constant) so the JIT cannot constant-fold the call away (BUG-BEN-004).
    /// </summary>
    [Params(1u, 2048u, 204800u, 204801u, uint.MaxValue)]
    public uint ByteCount { get; set; }

    private uint[] _batch = null!;

    /// <summary>
    /// Builds a batch of varying inputs around <see cref="ByteCount"/> so the
    /// loop benchmark measures real arithmetic, not one folded constant.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _batch = new uint[256];
        for (var i = 0; i < _batch.Length; i++)
        {
            _batch[i] = unchecked(ByteCount + (uint)i);
        }
    }

    /// <summary>
    /// Measures sector rounding for the parameterized input.
    /// Instance field input defeats constant folding; the returned value
    /// defeats dead-code elimination.
    /// </summary>
    /// <returns>The sector count.</returns>
    [Benchmark(Baseline = true)]
    public uint NumSectors_Param() => Constants.NumSectors(ByteCount);

    /// <summary>
    /// Measures sector rounding over a 256-entry batch with varying inputs and
    /// accumulates the sum, lifting the single-op cost above timer resolution
    /// (BUG-BEN-004: one integer op is pure call overhead otherwise).
    /// </summary>
    /// <returns>Sum of sector counts (prevents folding/elimination).</returns>
    [Benchmark]
    public uint NumSectors_Batch256()
    {
        var sum = 0u;
        var batch = _batch;
        for (var i = 0; i < batch.Length; i++)
        {
            sum += Constants.NumSectors(batch[i]);
        }

        return sum;
    }
}
