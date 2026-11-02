namespace XISOSharp.DataStructures;

/// <summary>
/// Context object passed through the directory-offset calculation traversal.
/// Sector positions are handed out by a <see cref="SectorAllocator"/> (TODO #2):
/// contiguous bump allocation from the root sector with overlap/overflow guards,
/// so creation-time numbering stays identical while becoming verified.
/// </summary>
internal class OffsetCalcContext
{
    /// <summary>
    /// Creates a context allocating from <paramref name="firstFreeSector"/>.
    /// </summary>
    internal OffsetCalcContext(uint firstFreeSector, long prependOffset)
    {
        Allocator = new SectorAllocator(firstFreeSector);
        PrependOffset = prependOffset;
    }

    /// <summary>
    /// Creates a context over an allocator already seeded with fixed extents
    /// (PlanToEnhance Phase 8 layout packing). Nodes present in
    /// <paramref name="fixedStarts"/> keep their requested sector; every other
    /// node is allocated first-fit around the seeded extents.
    /// </summary>
    /// <param name="allocator">Seeded allocator.</param>
    /// <param name="prependOffset">Byte offset prepended to every physical write.</param>
    /// <param name="fixedStarts">Sector requested for each fixed node (reference identity).</param>
    /// <param name="totalSectors">
    /// Partition size to preserve, or <c>null</c> to end at the last allocation.
    /// </param>
    internal OffsetCalcContext(SectorAllocator allocator, long prependOffset,
        IReadOnlyDictionary<AvlNode, uint> fixedStarts, long? totalSectors)
    {
        Allocator = allocator;
        PrependOffset = prependOffset;
        FixedStarts = fixedStarts;
        TotalSectors = totalSectors;
    }

    /// <summary>Allocator handing out directory-table and file-data sectors.</summary>
    internal SectorAllocator Allocator { get; }


    /// <summary>Byte offset prepended to all physical write positions (skip/prepend support).</summary>
    internal long PrependOffset;

    /// <summary>
    /// Sector requested for each fixed node, or <c>null</c> when no layout is
    /// active. Lookups use <see cref="AvlNode"/> reference identity.
    /// </summary>
    internal IReadOnlyDictionary<AvlNode, uint>? FixedStarts { get; }

    /// <summary>
    /// Partition sector count to preserve from a layout, or <c>null</c>.
    /// </summary>
    internal long? TotalSectors { get; }
}
