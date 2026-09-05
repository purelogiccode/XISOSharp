using BenchmarkDotNet.Running;

namespace ZARSharp.Benchmarks;

/// <summary>
/// Benchmark runner entry point. Forwards command-line arguments to BenchmarkDotNet's
/// <c>BenchmarkSwitcher</c> so any benchmark in this assembly can be selected and run.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the BenchmarkDotNet switcher over this assembly.
    /// </summary>
    /// <param name="args">BenchmarkDotNet filter and option arguments.</param>
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
