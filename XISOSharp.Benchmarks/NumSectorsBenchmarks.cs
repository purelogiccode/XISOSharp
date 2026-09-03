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
    [Benchmark]
    public static uint NumSectors_Small()
    {
        return Constants.NumSectors(1);
    }

    [Benchmark]
    public static uint NumSectors_ExactMultiple()
    {
        return Constants.NumSectors(Constants.SectorSize * 100);
    }

    [Benchmark]
    public static uint NumSectors_WithRemainder()
    {
        return Constants.NumSectors((Constants.SectorSize * 100) + 1);
    }

    [Benchmark]
    public static uint NumSectors_Large()
    {
        return Constants.NumSectors(uint.MaxValue);
    }
}