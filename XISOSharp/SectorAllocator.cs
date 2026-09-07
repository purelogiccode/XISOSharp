using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Contiguous sector allocator for XISO images (xdvdfs #101, TODO #2).
/// Hands out non-overlapping sector runs, tracks every allocated region, and
/// rejects overlaps — the allocation engine behind deterministic image creation
/// and the reallocation primitive for in-place patching (TODO #5, seeded from
/// <see cref="SectorLayout"/> via <see cref="FromLayout"/>).
/// Numbering is partition-relative, matching <see cref="EntryInfo.StartSector"/>.
/// </summary>
public sealed class SectorAllocator
{
    private readonly List<(uint Start, uint Count)> _used = [];

    /// <summary>
    /// Creates an allocator whose first allocation lands at
    /// <paramref name="firstFreeSector"/> when nothing is marked used.
    /// </summary>
    /// <param name="firstFreeSector">
    /// Lowest allocatable sector. Defaults to <see cref="Constants.RootDirectorySector"/>
    /// (<c>0x108</c>), the extract-xiso layout where the volume header occupies sector 32
    /// and the root table is the first allocation (unlike xdvdfs, which starts at 33).
    /// </param>
    /// <param name="totalSectors">
    /// Optional partition size. When set, allocations are first-fit within
    /// <c>[firstFreeSector, totalSectors)</c> and <see cref="FreeRanges"/> is available;
    /// a full image throws <see cref="InvalidOperationException"/>. When <c>null</c>,
    /// the allocator bumps past the end of tracked space (creation mode).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="totalSectors"/> is negative or below
    /// <paramref name="firstFreeSector"/>.
    /// </exception>
    public SectorAllocator(uint firstFreeSector = Constants.RootDirectorySector, long? totalSectors = null)
    {
        if (totalSectors is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors), totalSectors,
                "Total sectors must be non-negative.");
        }

        if (firstFreeSector > totalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(firstFreeSector), firstFreeSector,
                "First free sector must not exceed total sectors.");
        }

        FirstFreeSector = firstFreeSector;
        TotalSectors = totalSectors;
    }

    /// <summary>Lowest allocatable sector (reserved area below it is never handed out).</summary>
    public uint FirstFreeSector { get; }

    /// <summary>Partition sector count, or <c>null</c> for unbounded (creation) mode.</summary>
    public long? TotalSectors { get; }

    /// <summary>
    /// Sector where a bump allocation would land: the end of tracked used space,
    /// or <see cref="FirstFreeSector"/> when nothing is tracked yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when tracked used space reaches past <see cref="uint.MaxValue"/>
    /// (BUG-LIB-031: previously clamped silently, hiding allocation overflow).
    /// </exception>
    public uint NextFree
    {
        get
        {
            ulong end = FirstFreeSector;
            foreach (var (start, count) in _used)
            {
                end = Math.Max(end, (ulong)start + count);
            }

            if (end > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Tracked used space ends at sector {end}, exceeding the addressable sector range.");
            }

            return (uint)end;
        }
    }

    /// <summary>Total allocated sectors across all tracked ranges.</summary>
    public ulong AllocatedSectorCount
    {
        get
        {
            ulong total = 0;
            foreach (var (_, count) in _used)
            {
                total += count;
            }

            return total;
        }
    }

    /// <summary>
    /// Sectors required to hold <paramref name="byteCount"/> bytes (ceiling division).
    /// Empty content needs 0 sectors — extract-xiso semantics (xdvdfs allocates 1).
    /// </summary>
    public static uint RequiredSectors(ulong byteCount)
    {
        if (byteCount == 0)
            return 0;

        var sectors = byteCount / Constants.SectorSize;
        if (byteCount % Constants.SectorSize != 0)
            sectors++;

        if (sectors > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"Content of {byteCount} bytes needs {sectors} sectors, exceeding the addressable range.");
        }

        return (uint)sectors;
    }

    /// <summary>
    /// Allocates a contiguous run of <paramref name="sectorCount"/> sectors and returns
    /// its first sector. First-fit at or above <see cref="FirstFreeSector"/>: gaps left by
    /// <see cref="MarkUsed"/>/seeded ranges are reused before extending past the end.
    /// A zero count returns the first free position without recording anything (mirrors
    /// the writer assigning a start sector to empty files without consuming space).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a bounded image has no fitting gap, or the run would exceed the
    /// addressable sector range.
    /// </exception>
    public uint AllocateContiguous(uint sectorCount)
    {
        var pos = FindFit(sectorCount);
        if (sectorCount != 0)
            InsertUsed(pos, sectorCount);

        return pos;
    }

    /// <summary>
    /// Allocates a contiguous run big enough for <paramref name="byteCount"/> bytes.
    /// </summary>
    public uint AllocateForBytes(ulong byteCount)
    {
        return AllocateContiguous(RequiredSectors(byteCount));
    }

    /// <summary>
    /// Records an externally placed region (volume header, existing image extents when
    /// seeding from a layout) so later allocations avoid it. Adjacent ranges coalesce.
    /// A zero count is a no-op.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the range exceeds the addressable sectors or a bounded image.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when the range overlaps a tracked region.</exception>
    public void MarkUsed(uint startSector, uint sectorCount)
    {
        if (sectorCount == 0)
            return;

        checked
        {
            var end = (ulong)startSector + sectorCount;
            if (end - 1 > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorCount), sectorCount,
                    $"Range [{startSector}, {end}) exceeds the addressable sector range.");
            }

            if (TotalSectors.HasValue && end > (ulong)TotalSectors.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(startSector), startSector,
                    $"Range [{startSector}, {end}) exceeds total sectors {TotalSectors.Value}.");
            }
        }

        foreach (var (start, count) in _used)
        {
            var existingEnd = (ulong)start + count;
            var newEnd = (ulong)startSector + sectorCount;
            if (startSector < existingEnd && start < newEnd)
            {
                throw new ArgumentException(
                    $"Range [{startSector}, {newEnd}) overlaps tracked range [{start}, {existingEnd}).",
                    nameof(startSector));
            }
        }

        InsertUsed(startSector, sectorCount);
    }

    /// <summary>
    /// Returns <c>true</c> when <c>[startSector, startSector + sectorCount)</c> is free:
    /// at or above <see cref="FirstFreeSector"/>, within bounds, and overlapping nothing
    /// tracked. A zero count is always free.
    /// </summary>
    public bool IsFree(uint startSector, uint sectorCount)
    {
        if (sectorCount == 0)
            return true;

        var end = (ulong)startSector + sectorCount;
        if (end - 1 > uint.MaxValue || startSector < FirstFreeSector)
            return false;

        if (TotalSectors.HasValue && end > (ulong)TotalSectors.Value)
            return false;

        foreach (var (start, count) in _used)
        {
            var existingEnd = (ulong)start + count;
            if (startSector < existingEnd && start < end)
                return false;
        }

        return true;
    }

    /// <summary>Tracked allocated ranges, sorted and coalesced.</summary>
    public IReadOnlyList<SectorRange> UsedRanges
    {
        get
        {
            var ranges = new List<SectorRange>(_used.Count);
            foreach (var (start, count) in _used)
            {
                ranges.Add(new SectorRange(start, count));
            }

            return ranges;
        }
    }

    /// <summary>
    /// Unallocated gaps tiling <c>[FirstFreeSector, TotalSectors)</c> with
    /// <see cref="UsedRanges"/>. Requires a bounded image.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="TotalSectors"/> is <c>null</c>.</exception>
    public IReadOnlyList<SectorRange> FreeRanges
    {
        get
        {
            if (!TotalSectors.HasValue)
            {
                throw new InvalidOperationException(
                    "Free ranges require a bounded image (TotalSectors must be set).");
            }

            var free = new List<SectorRange>();
            ulong cursor = FirstFreeSector;
            var limit = (ulong)TotalSectors.Value;
            foreach (var (start, count) in _used)
            {
                if (start > cursor)
                    free.Add(new SectorRange((uint)cursor, (uint)(start - cursor)));

                cursor = Math.Max(cursor, (ulong)start + count);
            }

            if (cursor < limit)
                free.Add(new SectorRange((uint)cursor, (uint)(limit - cursor)));

            return free;
        }
    }

    /// <summary>
    /// Seeds an allocator from an explicit layout (<see cref="XisoReader.GetSectorLayout"/>):
    /// every used range is marked, the partition size bounds the image, and later
    /// allocations first-fit into the layout's free gaps. Reallocation primitive for
    /// in-place patching (TODO #5).
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layout"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the layout is out of range or self-overlapping.</exception>
    public static SectorAllocator FromLayout(SectorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.TotalSectors < 0 || layout.TotalSectors > uint.MaxValue)
        {
            throw new ArgumentException(
                $"Layout total sectors {layout.TotalSectors} is outside the addressable range.",
                nameof(layout));
        }

        var allocator = new SectorAllocator(0, layout.TotalSectors);
        foreach (var range in layout.UsedRanges)
        {
            allocator.MarkUsed(range.StartSector, range.SectorCount);
        }

        return allocator;
    }

    private uint FindFit(uint sectorCount)
    {
        ulong cursor = FirstFreeSector;
        foreach (var (start, count) in _used)
        {
            if (start >= cursor + sectorCount)
                return (uint)cursor;

            cursor = Math.Max(cursor, (ulong)start + count);
        }

        if (TotalSectors.HasValue)
        {
            if (cursor + sectorCount > (ulong)TotalSectors.Value)
            {
                throw new InvalidOperationException(
                    $"No free run of {sectorCount} sector(s) in [{FirstFreeSector}, {TotalSectors.Value}).");
            }
        }
        else
        {
            // Unbounded (creation) mode: the tail past tracked space always fits unless
            // the run's last sector leaves the addressable range.
            var last = cursor + sectorCount - (sectorCount == 0 ? 0UL : 1UL);
            if (last > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Allocation of {sectorCount} sector(s) at {cursor} exceeds the addressable sector range.");
            }
        }

        return (uint)cursor;
    }

    private void InsertUsed(uint startSector, uint sectorCount)
    {
        _used.Add((startSector, sectorCount));
        _used.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(uint Start, uint Count)>(_used.Count);
        foreach (var (start, count) in _used)
        {
            if (merged.Count > 0)
            {
                var (lastStart, lastCount) = merged[^1];
                if (start <= (ulong)lastStart + lastCount)
                {
                    var end = Math.Max((ulong)lastStart + lastCount, (ulong)start + count);
                    merged[^1] = (lastStart, (uint)(end - lastStart));
                    continue;
                }
            }

            merged.Add((start, count));
        }

        _used.Clear();
        _used.AddRange(merged);
    }
}