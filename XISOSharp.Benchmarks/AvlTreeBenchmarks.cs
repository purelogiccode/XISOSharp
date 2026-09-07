using BenchmarkDotNet.Attributes;
using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class AvlTreeBenchmarks
{
    private AvlNode? _root;
    private readonly string[] _filenames = Enumerable.Range(0, 1000).Select(static i => $"file_{i:D4}.dat").ToArray();
    private readonly string[] _extraFilenames = Enumerable.Range(0, 1000).Select(static i => $"bench_{i:D4}.dat").ToArray();

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
    /// Measures inserting 1000 additional files into the <see cref="Setup"/>-built tree
    /// (BUG-BEN-001). The new nodes hang off <c>_root</c>, so <see cref="Cleanup"/> frees
    /// everything — nothing leaks per iteration. Returns the insert count so the JIT
    /// cannot fold the loop (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public int Insert1000Files()
    {
        var inserted = 0;
        foreach (var name in _extraFilenames)
        {
            if (AvlTree.AvlInsert(ref _root, new AvlNode { Filename = name, FileSize = 4096 }) != AvlResult.AvlError)
            {
                inserted++;
            }
        }

        return inserted;
    }

    /// <summary>
    /// Measures fetching a file name known to exist in the tree.
    /// Returns the node so the lookup cannot be eliminated (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public AvlNode? FetchExistingFile()
    {
        return AvlTree.AvlFetch(_root, "file_0500.dat");
    }

    /// <summary>
    /// Measures fetching a file name known to be absent from the tree.
    /// Returns the (null) result so the lookup cannot be eliminated (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public AvlNode? FetchMissingFile()
    {
        return AvlTree.AvlFetch(_root, "nonexistent.dat");
    }

    /// <summary>
    /// Measures a prefix-order depth-first traversal of the tree.
    /// Returns the traversal result so it cannot be eliminated (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public int TraversePrefix()
    {
        return AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Prefix, 0);
    }

    /// <summary>
    /// Measures an infix-order depth-first traversal of the tree.
    /// Returns the traversal result so it cannot be eliminated (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public int TraverseInfix()
    {
        return AvlTree.AvlTraverseDepthFirst(_root, CountCallback, null, AvlTraversalMethod.Infix, 0);
    }

    /// <summary>
    /// Measures key comparisons between adjacent file names. Accumulates into a returned
    /// sum over varying keys so neither the JIT nor BenchmarkDotNet can fold the
    /// constant-operand loop (BUG-BEN-002).
    /// </summary>
    [Benchmark]
    public int CompareKeys()
    {
        var sum = 0;
        for (var i = 0; i + 1 < _filenames.Length; i++)
        {
            sum += AvlTree.AvlCompareKey(_filenames[i], _filenames[i + 1]);
        }

        return sum;
    }

    private static int CountCallback(AvlNode node, object? context, int depth)
    {
        return 0;
    }
}