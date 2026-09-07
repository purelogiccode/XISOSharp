using System.Buffers.Binary;
using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Builds and serializes single XISO directory entry tables (TODO #3).
/// This is the shared table-writing primitive used by the whole-image writer
/// (<see cref="XisoWriter"/>) and by the in-place patcher (<see cref="XisoPatcher"/>,
/// TODO #5): one code path encodes records, so a table rewritten in place is
/// byte-identical in format to a table written during image creation.
/// </summary>
/// <remarks>
/// Record layout (all little-endian, matching extract-xiso 2.7.1): left-child
/// offset (<c>u16</c>, in DWORDs), right-child offset (<c>u16</c>, in DWORDs),
/// start sector (<c>u32</c>), file size (<c>u32</c>, sector-rounded for
/// directories), attributes (<c>u8</c>), filename length (<c>u8</c>), filename
/// (Latin-1). Records are DWORD-aligned, never straddle a sector boundary, and
/// gaps plus the sector tail are filled with <see cref="Constants.PadByte"/>
/// (<c>0xFF</c>). An empty directory is a single <c>0xFF</c> sector.
/// </remarks>
public static class DirectoryEntryTableWriter
{
    /// <summary>
    /// A single directory entry to place in a table: file or subdirectory.
    /// </summary>
    /// <param name="Name">Entry filename (no path separators, 1–255 Latin-1 chars).</param>
    /// <param name="IsDirectory">True for a subdirectory entry.</param>
    /// <param name="StartSector">
    /// Partition-relative first sector of the file data or subdirectory table.
    /// </param>
    /// <param name="FileSize">
    /// File size in bytes, or subdirectory table size in bytes (sector-rounded
    /// on disk; <see cref="Constants.SectorSize"/> for an empty directory).
    /// </param>
    /// <param name="Attributes">
    /// On-disk attribute byte (masked); 0 (default) means unspecified and the
    /// encoder falls back to directory/archive. Pass the source entry's
    /// attributes when rewriting so RO/HID/SYS survive (BUG-LIB-034).
    /// </param>
    public sealed record DirectoryTableEntry(
        string Name,
        bool IsDirectory,
        uint StartSector,
        uint FileSize,
        byte Attributes = 0);

    /// <summary>
    /// Builds a balanced AVL tree from directory entries. Entries are inserted
    /// in ordinal filename order so the tree shape (and therefore the on-disk
    /// byte layout) is deterministic for a given entry set, matching the
    /// whole-image writer convention.
    /// </summary>
    /// <param name="entries">Entries to place in the table.</param>
    /// <returns>
    /// Root of the AVL tree (<c>null</c> when <paramref name="entries"/> is empty).
    /// Subdirectory nodes carry an opaque non-null <c>Subdirectory</c> marker:
    /// the marker is never traversed — it only selects directory attributes and
    /// sector-rounded sizing during encoding.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A name is invalid (empty, too long, contains a separator) or duplicated
    /// (case-insensitive).
    /// </exception>
    public static AvlNode? BuildTable(IEnumerable<DirectoryTableEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        AvlNode? root = null;
        foreach (DirectoryTableEntry entry in entries.OrderBy(static e => e.Name, StringComparer.Ordinal))
        {
            ValidateName(entry.Name);
            AvlNode node = new()
            {
                Filename = entry.Name,
                // Opaque marker: never traversed, never serialized as a table.
                // Only checked for null (file vs directory) by the encoder.
                Subdirectory = entry.IsDirectory ? new AvlNode() : null,
                StartSector = entry.StartSector,
                FileSize = entry.FileSize,
                Attributes = entry.Attributes,
            };
            if (AvlTree.AvlInsert(ref root, node) == AvlResult.AvlError)
            {
                throw new InvalidOperationException(
                    $"Duplicate directory entry name '{entry.Name}' (names are case-insensitive).");
            }
        }

        return root;
    }

    /// <summary>
    /// Computes the on-disk size of one directory entry and assigns its
    /// <see cref="AvlNode.Offset"/> within the table, DWORD-aligning and
    /// avoiding sector-boundary straddles exactly like the whole-image writer.
    /// </summary>
    /// <param name="node">Node whose entry size is being calculated.</param>
    /// <param name="size">Running table size in bytes; updated in place.</param>
    public static void PlaceEntry(AvlNode node, ref uint size)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateName(node.Filename);

        // BUG-LIB-035: size in bytes, not chars — Filename.Length undercounts
        // names outside ASCII. Latin-1 is 1:1, but the byte count is the
        // contract EncodeEntry below allocates against.
        uint length = (uint)(Constants.FilenameOffset + Latin1Encoding.Instance.GetByteCount(node.Filename));
        length += (Constants.DwordSize - (length % Constants.DwordSize)) % Constants.DwordSize;

        if (NumSectors(size + length) > NumSectors(size))
        {
            size += (Constants.SectorSize - (size % Constants.SectorSize)) % Constants.SectorSize;
        }

        node.Offset = size;
        size += length;
    }

    /// <summary>
    /// Assigns <see cref="AvlNode.Offset"/> for every node of one table and
    /// returns the unrounded table size in bytes.
    /// </summary>
    /// <param name="tableRoot">
    /// Root of the table's AVL tree (<c>null</c> or
    /// <see cref="AvlNode.EmptySubdirectory"/> for an empty directory).
    /// </param>
    /// <returns>
    /// Table byte size (<see cref="Constants.SectorSize"/> for an empty directory).
    /// </returns>
    public static uint ComputeTableSize(AvlNode? tableRoot)
    {
        if (tableRoot == null || ReferenceEquals(tableRoot, AvlNode.EmptySubdirectory))
            return Constants.SectorSize;

        TableSizeAccumulator acc = new();
        AvlTree.AvlTraverseDepthFirst(tableRoot, static (node, ctx, _) =>
        {
            PlaceEntry(node, ref ((TableSizeAccumulator)ctx!).Size);
            return 0;
        }, acc, AvlTraversalMethod.Prefix, 0);
        return acc.Size;
    }

    /// <summary>
    /// Encodes one directory entry record (header + filename, no padding).
    /// Byte-identical to what the whole-image writer emits for the same node.
    /// </summary>
    /// <param name="node">Node to encode (must have <c>Left</c>/<c>Right</c> placed).</param>
    /// <returns>The 14+N byte record.</returns>
    /// <exception cref="InvalidOperationException">The filename is invalid.</exception>
    /// <exception cref="ArgumentException">The filename is not Latin-1 encodable.</exception>
    public static byte[] EncodeEntry(AvlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateName(node.Filename);

        byte[] nameBytes = Latin1Encoding.Instance.GetBytes(node.Filename);

        uint fileSizeForEntry = node.FileSize;
        if (node.Subdirectory != null)
        {
            fileSizeForEntry +=
                (Constants.SectorSize - (node.FileSize % Constants.SectorSize)) % Constants.SectorSize;
        }

        // BUG-LIB-034: preserve the source attribute bits (RO/HID/SYS) instead
        // of normalizing every file to Archive. Nodes built without attributes
        // (fresh pack) carry 0 and keep the historical directory/archive default.
        byte attributes = node.Attributes != 0
            ? node.Attributes
            : node.Subdirectory != null
                ? Constants.AttributeDir
                : Constants.AttributeArc;
        ushort lOffset = (ushort)(node.Left != null ? node.Left.Offset / Constants.DwordSize : 0);
        ushort rOffset = (ushort)(node.Right != null ? node.Right.Offset / Constants.DwordSize : 0);

        byte[] record = new byte[Constants.FilenameOffset + nameBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), lOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2, 2), rOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), node.StartSector);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8, 4), fileSizeForEntry);
        record[12] = attributes;
        record[13] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, record, Constants.FilenameOffset, nameBytes.Length);
        return record;
    }

    /// <summary>
    /// Serializes one directory table to a sector-padded, <c>0xFF</c>-filled
    /// buffer, assigning entry offsets first. An empty table serializes to a
    /// single <c>0xFF</c> sector, matching the whole-image writer.
    /// </summary>
    /// <param name="tableRoot">
    /// Root of the table's AVL tree (<c>null</c> or
    /// <see cref="AvlNode.EmptySubdirectory"/> for an empty directory).
    /// </param>
    /// <returns>
    /// Table bytes: records at their offsets, gaps and sector tail filled with
    /// <see cref="Constants.PadByte"/>, total length a multiple of
    /// <see cref="Constants.SectorSize"/>.
    /// </returns>
    public static byte[] SerializeTable(AvlNode? tableRoot)
    {
        if (tableRoot == null || ReferenceEquals(tableRoot, AvlNode.EmptySubdirectory))
        {
            byte[] empty = new byte[Constants.SectorSize];
            Array.Fill(empty, Constants.PadByte);
            return empty;
        }

        uint tableSize = ComputeTableSize(tableRoot);
        byte[] buffer = new byte[SectorAllocator.RequiredSectors(tableSize) * Constants.SectorSize];
        Array.Fill(buffer, Constants.PadByte);
        AvlTree.AvlTraverseDepthFirst(tableRoot, static (node, ctx, _) =>
        {
            byte[] record = EncodeEntry(node);
            Buffer.BlockCopy(record, 0, (byte[])ctx!, (int)node.Offset, record.Length);
            return 0;
        }, buffer, AvlTraversalMethod.Prefix, 0);
        return buffer;
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > Constants.FilenameMaxChars)
        {
            throw new InvalidOperationException(
                $"Invalid directory entry name '{name}': must be 1-{Constants.FilenameMaxChars} characters.");
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"Filename '{name}' contains path separator characters ('/' or '\\') which are not allowed in XISO directory entries.");
        }

        // BUG-LIB-035: fail fast on names the Latin-1 record can never hold.
        // Without this, sizing (chars) and encoding (bytes) disagree and the
        // throw surfaces mid-write as a generic ArgumentException (err=1).
        try
        {
            _ = Latin1Encoding.Instance.GetByteCount(name);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Invalid directory entry name '{name}': contains characters outside the Latin-1 range.",
                ex);
        }
    }

    private static uint NumSectors(uint size) => (size / Constants.SectorSize) + (size % Constants.SectorSize != 0 ? 1u : 0u);

    private sealed class TableSizeAccumulator
    {
        public uint Size;
    }
}
