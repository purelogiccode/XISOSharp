using BenchmarkDotNet.Attributes;
using XISOSharp.DataStructures;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class AvlTreeBenchmarks
{
    private AvlNode? _root;
    private readonly string[] _filenames = Enumerable.Range(0, 1000).Select(static i => $"file_{i:D4}.dat").ToArray();

    [IterationSetup]
    public void Setup()
    {
        _root = null;
        foreach (var name in _filenames)
        {
            AvlTree.AvlInsert(ref _root, new AvlNode { Filename = name, FileSize = 4096 });
        }
    }

    [IterationCleanup]
    public void Cleanup()
    {
        AvlTree.FreeTree(_root);
        _root = null;
    }

    [Benchmark]
    public void Insert1000Files()
    {
        AvlNode? root = null;
        foreach (var name in _filenames)
        {
            AvlTree.AvlInsert(ref root, new AvlNode { Filename = name, FileSize = 4096 });
        }
    }

    [Benchmark]
    public void FetchExistingFile()
    {
        AvlTree.AvlFetch(_root, "file_0500.dat");
    }

    [Benchmark]
    public void FetchMissingFile()
    {
        AvlTree.AvlFetch(_root, "nonexistent.dat");
    }

    [Benchmark]
    public void TraversePrefix()
    {
        AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Prefix, 0);
    }

    [Benchmark]
    public void TraverseInfix()
    {
        AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Infix, 0);
    }

    [Benchmark]
    public static void CompareKeys()
    {
        for (var i = 0; i < 1000; i++)
        {
            AvlTree.AvlCompareKey("file_0500.dat", "file_0501.dat");
        }
    }

    private static int CountCallback(AvlNode node, object? context, int depth) => 0;
}