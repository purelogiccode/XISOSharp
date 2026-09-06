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
    public OffsetCalcContext(uint firstFreeSector, long prependOffset)
    {
        Allocator = new SectorAllocator(firstFreeSector);
        PrependOffset = prependOffset;
    }

    /// <summary>Allocator handing out directory-table and file-data sectors.</summary>
    public SectorAllocator Allocator { get; }

    /// <summary>Current sector number that the next allocation will receive.</summary>
    public uint CurrentSector => Allocator.NextFree;

    /// <summary>Byte offset prepended to all physical write positions (skip/prepend support).</summary>
    public long PrependOffset;
}