using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Resolves an <see cref="XisoLayoutSpec"/> against a source AVL tree
/// (PlanToEnhance Phase 8): validates every fixed extent, assigns the matching
/// nodes their requested sectors, and seeds a <see cref="SectorAllocator"/> so
/// unplaced entries are allocated first-fit around the fixed ones.
/// Internal implementation detail of <see cref="XisoWriter"/>.
/// </summary>
internal sealed class LayoutPlanner
{
    /// <summary>
    /// First sector a layout may place data at: sector 32 is the XISO volume
    /// descriptor and sectors 16-17 carry the ECMA-119 descriptor pair, so
    /// everything below sector 33 is reserved.
    /// </summary>
    private const uint FirstLayoutSector = 33;

    private readonly XisoLayoutSpec _spec;
    private readonly Dictionary<string, XisoLayoutEntry> _entriesByPath;
    private readonly Dictionary<AvlNode, uint> _fixedStarts = new();
    private readonly HashSet<string> _matchedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(uint Start, uint Count, string Path)> _reserved = new();

    /// <summary>
    /// Creates a planner for <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">Layout to apply.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spec"/> is <c>null</c>.</exception>
    internal LayoutPlanner(XisoLayoutSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _spec = spec;
        if (spec.TotalSectors is long total &&
            (total < Constants.RootDirectorySector || total > uint.MaxValue))
        {
            throw new InvalidDataException(
                $"Sector layout total {total} is outside the addressable XISO range " +
                $"[{Constants.RootDirectorySector}, {uint.MaxValue}].");
        }

        _entriesByPath = new Dictionary<string, XisoLayoutEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (XisoLayoutEntry entry in spec.Entries)
        {
            // Parse-time duplicate checks make this unreachable for public
            // callers; keep the guard for hand-built specs.
            if (!_entriesByPath.TryAdd(entry.Path, entry))
            {
                throw new InvalidDataException(
                    $"Sector layout contains duplicate path '{entry.Path}'.");
            }
        }
    }

    /// <summary>
    /// Allocator seeded with every fixed extent; unplaced entries are handed
    /// out first-fit from <see cref="Constants.RootDirectorySector"/>.
    /// </summary>
    internal SectorAllocator Allocator { get; private set; } = null!;

    /// <summary>Sector requested for each matched node (reference identity).</summary>
    internal IReadOnlyDictionary<AvlNode, uint> FixedStarts => _fixedStarts;

    /// <summary>Partition sector count from the layout, or <c>null</c>.</summary>
    internal long? TotalSectors => _spec.TotalSectors;

    /// <summary>
    /// Matches the layout against <paramref name="root"/> and seeds
    /// <see cref="Allocator"/>. Must run after directory requirements (table
    /// sizes) are computed and before directory offsets are assigned.
    /// </summary>
    /// <param name="root">Synthetic image root whose children are the image root table.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when a fixed extent is out of range, overlaps another extent or
    /// the system area, is smaller than the source entry needs, or names a path
    /// missing from the source tree in the strict text form.
    /// </exception>
    internal void Plan(AvlNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        PlanNode(root, "/", isDirectory: true);

        if (root.Subdirectory != null && !ReferenceEquals(root.Subdirectory, AvlNode.EmptySubdirectory))
        {
            Stack<(AvlNode Node, string ParentPath)> pending = new();
            pending.Push((root.Subdirectory, "/"));
            while (pending.Count > 0)
            {
                (AvlNode node, string parentPath) = pending.Pop();
                string path = string.Equals(parentPath, "/", StringComparison.Ordinal)
                    ? "/" + node.Filename
                    : parentPath + "/" + node.Filename;
                bool isDirectory = node.Subdirectory != null;

                PlanNode(node, path, isDirectory);

                if (node.Left != null)
                    pending.Push((node.Left, parentPath));
                if (node.Right != null)
                    pending.Push((node.Right, parentPath));
                if (isDirectory && !ReferenceEquals(node.Subdirectory, AvlNode.EmptySubdirectory))
                {
                    pending.Push((node.Subdirectory!, path));
                }
            }
        }

        // A JSON layout may describe more files than the source tree (for
        // example when packing a subset). Reserve those extents so unplaced
        // files cannot reuse bytes the caller asked to keep free. The compact
        // text form is strict instead: a declared path that does not exist is
        // almost always a typo, so fail before writing anything.
        foreach (XisoLayoutEntry entry in _spec.Entries)
        {
            if (_matchedPaths.Contains(entry.Path))
                continue;

            if (_spec.StrictPaths)
            {
                throw new InvalidDataException(
                    $"Sector layout entry '{entry.Path}' was not found in the source tree.");
            }

            if (entry.SectorCount is uint count && count > 0)
            {
                AddReserved(entry.StartSector, count, entry.Path);
                Logger.LogErr(
                    $"warning: sector layout entry '{entry.Path}' is not in the source tree; reserving " +
                    $"{count} sector(s) at {entry.StartSector}.\n");
            }
        }

        // Validate and seed in one pass: a large layout has thousands of fixed
        // extents, so the bulk path avoids re-sorting the used-range list per
        // entry (the per-range MarkUsed path is meant for small patch sets).
        _reserved.Add((0, FirstLayoutSector, "<system area>"));
        _reserved.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        List<SectorRange> seeded = new(_reserved.Count);
        for (int i = 0; i < _reserved.Count; i++)
        {
            (uint start, uint count, string path) = _reserved[i];
            if (i > 0)
            {
                (uint previousStart, uint previousCount, string previousPath) = _reserved[i - 1];
                ulong previousEnd = (ulong)previousStart + previousCount;
                if (start < previousEnd)
                {
                    throw new InvalidDataException(
                        $"Sector layout entry '{path}' at [{start}, {(ulong)start + count}) overlaps " +
                        $"'{previousPath}' at [{previousStart}, {previousEnd}).");
                }
            }

            seeded.Add(new SectorRange(start, count));
        }

        Allocator = new SectorAllocator(Constants.RootDirectorySector, _spec.TotalSectors);
        Allocator.SeedUsedRanges(seeded);
    }

    private void PlanNode(AvlNode node, string path, bool isDirectory)
    {
        if (!_entriesByPath.TryGetValue(path, out XisoLayoutEntry? entry))
        {
            // Not every source entry needs a fixed extent: the rest are
            // allocated first-fit. Strict mode only requires the reverse —
            // every declared path must exist (checked after the walk).
            return;
        }

        _matchedPaths.Add(path);

        if (entry.IsDirectory is bool declaredDirectory && declaredDirectory != isDirectory)
        {
            throw new InvalidDataException(
                $"Sector layout entry '{path}' is a " +
                $"{(declaredDirectory ? "directory" : "file")} but the source tree has a " +
                $"{(isDirectory ? "directory" : "file")}.");
        }

        uint required = RequiredSectors(node, isDirectory);
        uint reserved = required;
        if (entry.SectorCount is uint declaredCount)
        {
            if (declaredCount < required)
            {
                throw new InvalidDataException(
                    $"Sector layout entry '{path}' reserves {declaredCount} sector(s) at " +
                    $"{entry.StartSector}, but the source entry needs {required}.");
            }

            reserved = declaredCount;
        }

        if (reserved > 0)
        {
            AddReserved(entry.StartSector, reserved, path);
        }
        else if (entry.StartSector < FirstLayoutSector)
        {
            throw new InvalidDataException(
                $"Sector layout entry '{path}' starts at sector {entry.StartSector}, inside the " +
                $"reserved system area [0, {FirstLayoutSector}).");
        }

        _fixedStarts[node] = entry.StartSector;
    }

    private static uint RequiredSectors(AvlNode node, bool isDirectory)
    {
        if (!isDirectory)
            return SectorAllocator.RequiredSectors(node.FileSize);

        // An empty directory still occupies one table sector (the writer emits
        // a 0xFF-filled sentinel sector); never let it collapse to zero.
        uint sectors = SectorAllocator.RequiredSectors(node.FileSize);
        return sectors == 0 ? 1u : sectors;
    }

    private void AddReserved(uint startSector, uint sectorCount, string path)
    {
        if (startSector < FirstLayoutSector)
        {
            throw new InvalidDataException(
                $"Sector layout entry '{path}' starts at sector {startSector}, inside the " +
                $"reserved system area [0, {FirstLayoutSector}).");
        }

        ulong end = (ulong)startSector + sectorCount;
        if (end - 1 > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"Sector layout entry '{path}' at [{startSector}, {end}) is out of range: it exceeds " +
                "the addressable sector space.");
        }

        if (_spec.TotalSectors is long total && end > (ulong)total)
        {
            throw new InvalidDataException(
                $"Sector layout entry '{path}' at [{startSector}, {end}) is out of range: the layout " +
                $"declares {total} total sector(s).");
        }

        _reserved.Add((startSector, sectorCount, path));
    }
}
