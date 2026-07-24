using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class NumSectorsBenchmarks
{
    [Benchmark]
    public uint NumSectors_Small()
    {
        return Constants.NumSectors(1);
    }

    [Benchmark]
    public uint NumSectors_ExactMultiple()
    {
        return Constants.NumSectors(Constants.SectorSize * 100);
    }

    [Benchmark]
    public uint NumSectors_WithRemainder()
    {
        return Constants.NumSectors(Constants.SectorSize * 100 + 1);
    }

    [Benchmark]
    public uint NumSectors_Large()
    {
        return Constants.NumSectors(uint.MaxValue);
    }
}
