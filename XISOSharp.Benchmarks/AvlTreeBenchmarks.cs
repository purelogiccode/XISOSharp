using BenchmarkDotNet.Attributes;
using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Benchmarks AVL directory-tree insert, fetch, traversal, and key comparison
/// over a fixed set of 1000 file names.
/// </summary>
public class AvlTreeBenchmarks
{
    private AvlNode? _root;
    private readonly string[] _filenames = Enumerable.Range(0, 1000).Select(static i => $"file_{i:D4}.dat").ToArray();

    /// <summary>
    /// Rebuilds the 1000-node tree before each iteration.
    /// </summary>
    [IterationSetup]
    public void Setup()
    {
        _root = null;
        foreach (var name in _filenames)
        {
            AvlTree.AvlInsert(ref _root, new AvlNode { Filename = name, FileSize = 4096 });
        }
    }

    /// <summary>
    /// Frees the per-iteration tree after each iteration.
    /// </summary>
    [IterationCleanup]
    public void Cleanup()
    {
        AvlTree.FreeTree(_root);
        _root = null;
    }

    /// <summary>
    /// Measures inserting 1000 files into an empty tree.
    /// </summary>
    [Benchmark]
    public void Insert1000Files()
    {
        AvlNode? root = null;
        foreach (var name in _filenames)
        {
            AvlTree.AvlInsert(ref root, new AvlNode { Filename = name, FileSize = 4096 });
        }
    }

    /// <summary>
    /// Measures fetching a file name known to exist in the tree.
    /// </summary>
    [Benchmark]
    public void FetchExistingFile()
    {
        AvlTree.AvlFetch(_root, "file_0500.dat");
    }

    /// <summary>
    /// Measures fetching a file name known to be absent from the tree.
    /// </summary>
    [Benchmark]
    public void FetchMissingFile()
    {
        AvlTree.AvlFetch(_root, "nonexistent.dat");
    }

    /// <summary>
    /// Measures a prefix-order depth-first traversal of the tree.
    /// </summary>
    [Benchmark]
    public void TraversePrefix()
    {
        AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Prefix, 0);
    }

    /// <summary>
    /// Measures an infix-order depth-first traversal of the tree.
    /// </summary>
    [Benchmark]
    public void TraverseInfix()
    {
        AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Infix, 0);
    }

    /// <summary>
    /// Measures 1000 key comparisons between two adjacent file names.
    /// </summary>
    [Benchmark]
    public static void CompareKeys()
    {
        for (var i = 0; i < 1000; i++)
        {
            AvlTree.AvlCompareKey("file_0500.dat", "file_0501.dat");
        }
    }

    private static int CountCallback(AvlNode node, object? context, int depth)
    {
        return 0;
    }
}