using BenchmarkDotNet.Attributes;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
#pragma warning disable RCS1102
// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable once ConvertToStaticClass
/// <summary>
/// Benchmarks <see cref="Constants.NumSectors(uint)"/> for small, exact-multiple,
/// remainder, and maximum inputs.
/// </summary>
public class NumSectorsBenchmarks
#pragma warning restore RCS1102
{
    /// <summary>
    /// Measures sector rounding for a single-byte input.
    /// </summary>
    /// <returns>The sector count for one byte.</returns>
    [Benchmark]
    public static uint NumSectors_Small()
    {
        return Constants.NumSectors(1);
    }

    /// <summary>
    /// Measures sector rounding for a byte count that is an exact multiple of the sector size.
    /// </summary>
    /// <returns>The sector count for 100 sectors worth of bytes.</returns>
    [Benchmark]
    public static uint NumSectors_ExactMultiple()
    {
        return Constants.NumSectors(Constants.SectorSize * 100);
    }

    /// <summary>
    /// Measures sector rounding for a byte count with a one-byte remainder past a sector boundary.
    /// </summary>
    /// <returns>The sector count including the partial trailing sector.</returns>
    [Benchmark]
    public static uint NumSectors_WithRemainder()
    {
        return Constants.NumSectors((Constants.SectorSize * 100) + 1);
    }

    /// <summary>
    /// Measures sector rounding for the maximum <see cref="uint"/> input.
    /// </summary>
    /// <returns>The sector count for <see cref="uint.MaxValue"/> bytes.</returns>
    [Benchmark]
    public static uint NumSectors_Large()
    {
        return Constants.NumSectors(uint.MaxValue);
    }
}