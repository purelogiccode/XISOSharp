using System.Buffers.Binary;

namespace XISOSharp;

/// <summary>
/// File-extent discovery ported from <c>References/XboxKit-0.7/LibXGD/XDVDFS.cs</c>.
/// Provides <see cref="GetXisoRanges(string,long,bool)"/> / <see cref="MergeRanges"/> and file-entry enumeration
/// needed by filler/seed/wiped/trim/skeleton/ZAR and Redump rebuild.
/// </summary>
public static class XisoRanges
{
    private const long SectorSize = Constants.SectorSize;
    private const long HeaderOffset = Constants.HeaderOffset;

    // Magic for detection of the second volume descriptor sector (optional, not required for ranges
    // but kept for fidelity with XDVDFS header handling). Original: "XBOX_DVD_LAYOUT_TOOL_SIG"
    private static readonly byte[] Magic2 = "XBOX_DVD_LAYOUT_TOOL_SIG"u8.ToArray();

    // -----------------------------------------------------------------------
    // Low-level readers
    // -----------------------------------------------------------------------

    private static ushort ReadUShort(Stream fs)
    {
        Span<byte> buf = stackalloc byte[2];
        ReadExact(fs, buf);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static uint ReadUInt(Stream fs)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExact(fs, buf);
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static void ReadExact(Stream fs, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = fs.Read(buffer[offset..]);
            if (n == 0) throw new EndOfStreamException("Failed to read XDVDFS header");
            offset += n;
        }
    }

    // -----------------------------------------------------------------------
    // Traversal — mirrors XDVDFS.GetValidSectors exactly, with an explicit
    // in-order stack per table (Phase 4 follow-up)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Collects valid filesystem and file sectors from an XISO directory tree.
    /// Each directory table is walked in order (left subtree, entry, right
    /// siblings) with an explicit stack; only subdirectory descent recurses,
    /// bounded by <see cref="Constants.MaxTocDepth"/>.
    /// </summary>
    /// <param name="isoFs">Open ISO stream.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <param name="sysSectors">Collection to populate with filesystem (bone) sectors.</param>
    /// <param name="fileSectors">Collection to populate with file data sectors.</param>
    /// <param name="rootOffset">Byte offset of the current directory table.</param>
    /// <param name="rootSize">Byte size of the current directory table.</param>
    /// <param name="childOffset">Child offset within the directory table.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    public static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> sysSectors, List<uint> fileSectors,
        long rootOffset, uint rootSize, long childOffset, bool quiet)
    {
        _ = quiet;
        GetValidSectorsCore(isoFs, isoOffset, sysSectors, fileSectors, rootOffset, rootSize, childOffset);
    }

    /// <summary>
    /// Stream overload of <see cref="GetValidSectors(FileStream,long,List{uint},List{uint},long,uint,long,bool)"/>
    /// for container-backed images (CISO/CCI/split ISO).
    /// </summary>
    public static void GetValidSectors(Stream isoFs, long isoOffset, List<uint> sysSectors, List<uint> fileSectors,
        long rootOffset, uint rootSize, long childOffset, bool quiet)
    {
        _ = quiet;
        GetValidSectorsCore(isoFs, isoOffset, sysSectors, fileSectors, rootOffset, rootSize, childOffset);
    }

    private static void GetValidSectorsCore(Stream isoFs, long isoOffset, List<uint> sysSectors,
        List<uint> fileSectors, long rootOffset, uint rootSize, long childOffset, HashSet<long>? visited = null,
        // Threaded recursion-depth bound (#16 hardening, proven by deep-tree tests); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        // Hardening (#16): bound subdirectory nesting — a corrupt subdir cycle
        // throws a named error instead of recursing until the stack overflows.
        // Intra-table AVL depth is not counted here: the explicit stack below
        // bounds it by the per-table entry cap, so a left-skewed table is read
        // instead of being mistaken for directory nesting (Phase 4 follow-up).
        // Out-of-table offsets keep the historical silent truncate (linked-list
        // compat quirk); cycles never occur legitimately.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry: maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        if (childOffset >= rootSize) return;

        visited ??= [];
        List<SectorWalkEntry> pending = [];
        long? next = childOffset;

        while (true)
        {
            if (next is { } offset)
            {
                next = null;
                if (offset < rootSize)
                {
                    if (!visited.Add(offset))
                    {
                        throw new XisoFormatException(
                            $"invalid TOC entry: directory cycle detected — table offset {offset} was already visited.");
                    }

                    if (visited.Count > Constants.MaxTocEntriesPerTable)
                    {
                        throw new XisoFormatException(
                            "invalid TOC entry: too many entries in one directory table (possible corrupt offset chain).");
                    }

                    long cur = isoOffset + rootOffset + offset;
                    long curOffset = cur / SectorSize;
                    long curSize = (rootSize - offset + SectorSize - 1) / SectorSize;
                    for (long i = curOffset; i < curOffset + curSize; i++) sysSectors.Add((uint)i);

                    isoFs.Seek(cur, SeekOrigin.Begin);

                    ushort leftChildOffset = ReadUShort(isoFs);
                    // BUG-LIB-036: 0xFFFF marks an empty table only at the table start
                    // (mirrors TraverseXiso/ReadDirectoryEntries/CollectFileEntries which
                    // require offset == 0). Deeper nodes use it as "no left child" and
                    // must still be processed, or their right siblings get dropped.
                    if (leftChildOffset == 0xFFFF && offset == 0) return;

                    // All-zero empty-table sentinel (parity with the other walkers):
                    // without this the 14 zero bytes parse as a bogus entry.
                    if (leftChildOffset == 0 && offset == 0 && IsAllZeroEntryHeader(isoFs))
                        return;

                    ushort rightChildOffset = ReadUShort(isoFs);
                    long entryOffset = ReadUInt(isoFs) * SectorSize;
                    uint entrySize = ReadUInt(isoFs);
                    bool isDirectory = ((byte)isoFs.ReadByte() & 0x10) != 0;
                    byte nameLength = (byte)isoFs.ReadByte();
                    byte[] nameBuf = new byte[nameLength];
                    if (nameLength > 0)
                    {
                        // A truncated name must fail, not silently drop the entry and
                        // its right-sibling chain from the sector map.
                        ReadExact(isoFs, nameBuf);
                    }

                    string name = Latin1Encoding.Instance.GetString(nameBuf);
                    SectorWalkEntry entry = new(rightChildOffset, entryOffset, entrySize, isDirectory,
                        name is "." or "..");

                    if (leftChildOffset != 0 && leftChildOffset != 0xFFFF)
                    {
                        // The entry is processed after its left subtree (in-order);
                        // the explicit stack bounds the descent.
                        pending.Add(entry);
                        if (pending.Count > Constants.MaxTocStackFrames)
                        {
                            throw new XisoFormatException(
                                "invalid TOC entry: intra-table traversal stack exceeded " +
                                $"{Constants.MaxTocStackFrames} frames (possible corrupt offset chain).");
                        }

                        next = (long)leftChildOffset * 4;
                        continue;
                    }

                    AddSectorEntry(isoFs, isoOffset, sysSectors, fileSectors, entry, depth);

                    if (rightChildOffset != 0 && rightChildOffset != 0xFFFF)
                    {
                        next = (long)rightChildOffset * 4;
                        continue;
                    }
                }
            }

            // No new node: process the nearest ancestor whose left subtree is
            // complete, then descend into its right subtree when it has one.
            bool resumed = false;
            while (pending.Count > 0)
            {
                SectorWalkEntry entry = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                AddSectorEntry(isoFs, isoOffset, sysSectors, fileSectors, entry, depth);
                if (entry.Right != 0 && entry.Right != 0xFFFF)
                {
                    next = (long)entry.Right * 4;
                    resumed = true;
                    break;
                }
            }

            if (!resumed) return;
        }
    }

    /// <summary>
    /// Applies one parsed directory entry to the sector maps: a subdirectory is
    /// walked recursively (bounded by <see cref="Constants.MaxTocDepth"/>), a
    /// file contributes its data sectors, and structural <c>.</c>/<c>..</c>
    /// records contribute nothing.
    /// </summary>
    private static void AddSectorEntry(Stream isoFs, long isoOffset, List<uint> sysSectors,
        List<uint> fileSectors, SectorWalkEntry entry, int depth)
    {
        if (entry.Structural)
        {
            // Structural entry: not a real extent; children still walked.
            return;
        }

        if (entry.IsDirectory)
        {
            GetValidSectorsCore(isoFs, isoOffset, sysSectors, fileSectors, entry.EntryOffset, entry.EntrySize, 0,
                null, depth + 1);
            return;
        }

        long fileOffset = (isoOffset + entry.EntryOffset) / SectorSize;
        long fileSize = (entry.EntrySize + SectorSize - 1) / SectorSize;
        for (long i = fileOffset; i < fileOffset + fileSize; i++) fileSectors.Add((uint)i);
    }

    /// <summary>One parsed table entry awaiting in-order processing.</summary>
    private readonly record struct SectorWalkEntry(
        ushort Right,
        long EntryOffset,
        uint EntrySize,
        bool IsDirectory,
        bool Structural);

    /// <summary>
    /// Peeks the 12 bytes after a left-child offset and reports whether they are
    /// all zero (the xdvdfs all-zero empty-table sentinel). Restores the stream
    /// position; a short read counts as not-all-zero.
    /// </summary>
    private static bool IsAllZeroEntryHeader(Stream isoFs)
    {
        long peekPos = isoFs.Position;
        Span<byte> rest = stackalloc byte[12];
        bool allZeros = true;
        int read = 0;
        while (read < rest.Length)
        {
            int n = isoFs.Read(rest[read..]);
            if (n == 0)
            {
                allZeros = false;
                break;
            }

            read += n;
        }

        if (allZeros)
        {
            foreach (byte b in rest)
            {
                if (b != 0)
                {
                    allZeros = false;
                    break;
                }
            }
        }

        isoFs.Seek(peekPos, SeekOrigin.Begin);
        return allZeros;
    }

    /// <summary>
    /// Merges two sorted range lists into a single sorted, coalesced list.
    /// Ported verbatim from <c>XDVDFS.MergeRanges</c>.
    /// </summary>
    public static List<(uint Start, uint End)> MergeRanges(List<(uint Start, uint End)> a,
        List<(uint Start, uint End)> b)
    {
        List<(uint, uint)> merged = new(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
            merged.Add(a[i].Start <= b[j].Start ? a[i++] : b[j++]);
        while (i < a.Count) merged.Add(a[i++]);
        while (j < b.Count) merged.Add(b[j++]);

        if (merged.Count == 0) return merged;

        List<(uint, uint)> result = new() { merged[0] };
        for (int k = 1; k < merged.Count; k++)
        {
            (uint, uint) last = result[^1];
            // BUG-LIB-030: last.Item2 + 1 wraps to 0 when a range reaches the
            // top of the addressable space (uint.MaxValue), so every later
            // range compared false and overlapping tails never merged.
            if (last.Item2 == uint.MaxValue || merged[k].Item1 <= last.Item2 + 1)
                result[^1] = (last.Item1, Math.Max(last.Item2, merged[k].Item2));
            else
                result.Add(merged[k]);
        }

        return result;
    }

    /// <summary>
    /// Returns the set of filesystem sectors (bones) and file sectors for an XISO at
    /// <paramref name="offset"/> (byte offset of the XISO partition within the file).
    /// </summary>
    public static (List<(uint Start, uint End)> Sys, List<(uint Start, uint End)> Files) GetXisoRanges(
        FileStream isoFs, long offset, bool quiet) =>
        GetXisoRanges((Stream)isoFs, offset, quiet);

    /// <summary>
    /// Stream overload of <see cref="GetXisoRanges(FileStream,long,bool)"/> for
    /// container-backed images (CISO/CCI/split ISO) and memory streams.
    /// </summary>
    public static (List<(uint Start, uint End)> Sys, List<(uint Start, uint End)> Files) GetXisoRanges(
        Stream isoFs, long offset, bool quiet)
    {
        _ = quiet;
        List<uint> sysSectors = [];
        List<uint> fileSectors = [];
        // Standard sector-32 descriptor, or the rebuilt sector-0 layout. Invalid
        // images keep the historical base so downstream reads fail as before.
        long headerOffset = XisoReader.TryFindHeaderBase(isoFs, offset, out long detectedHeader)
            ? detectedHeader
            : offset + HeaderOffset;
        long headerOffsetSector = headerOffset / SectorSize;
        sysSectors.Add((uint)headerOffsetSector);

        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        uint rootOffset = ReadUInt(isoFs);
        uint rootSize = ReadUInt(isoFs);

        isoFs.Seek(headerOffset + SectorSize, SeekOrigin.Begin);
        Span<byte> magic = stackalloc byte[24];
        ReadExact(isoFs, magic);
        // XBOX_DVD_LAYOUT_TOOL_SIG occupies the first bytes of the second sector when present.
        bool hasMagic2 = true;
        for (int m = 0; m < Magic2.Length; m++)
        {
            if (magic[m] != Magic2[m])
            {
                hasMagic2 = false;
                break;
            }
        }

        if (hasMagic2)
            sysSectors.Add((uint)headerOffsetSector + 1);

        // Hardening (#16): validate the root pointer before walking — a corrupt
        // rootSize would otherwise pre-allocate millions of sector entries.
        long rootAbs = offset + (rootOffset * SectorSize);
        if (rootAbs < 0 || rootAbs >= isoFs.Length)
        {
            throw new XisoFormatException(
                $"invalid TOC entry: root directory sector {rootOffset} (offset {rootAbs}) points outside the image (length {isoFs.Length}).");
        }

        if (rootSize > (ulong)(isoFs.Length - rootAbs))
        {
            throw new XisoFormatException(
                $"invalid TOC entry: root directory size {rootSize} (ends at {rootAbs + rootSize}) exceeds image length {isoFs.Length}.");
        }

        GetValidSectorsCore(isoFs, offset, sysSectors, fileSectors, rootOffset * SectorSize, rootSize, 0);

        List<(uint, uint)> sysRanges = BuildRanges(sysSectors);
        List<(uint, uint)> fileRanges = BuildRanges(fileSectors);
        return (sysRanges, fileRanges);
    }

    private static List<(uint, uint)> BuildRanges(List<uint> sectors)
    {
        if (sectors.Count == 0) return [];
        List<uint> sorted = sectors.Distinct().Order().ToList();
        List<(uint, uint)> ranges = new();
        uint start = sorted[0];
        uint prev = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            uint cur = sorted[i];
            if (cur == prev + 1)
            {
                prev = cur;
            }
            else
            {
                ranges.Add((start, prev));
                start = cur;
                prev = cur;
            }
        }

        ranges.Add((start, prev));
        return ranges;
    }

    /// <summary>
    /// Returns filesystem and file ranges for an XISO at the given path and offset.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="offset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <returns>Tuple of filesystem (bone) and file sector ranges.</returns>
    public static (List<(uint Start, uint End)> Sys, List<(uint Start, uint End)> Files) GetXisoRanges(
        string isoPath, long offset = 0, bool quiet = false)
    {
        _ = quiet;
        // Container-aware open: plain ISO, split ISO, CISO and CCI all expose a
        // seekable decompressed view (Phase 1 acceptance: --ranges on CCI).
        using Stream fs = XisoImageSource.OpenRead(isoPath);
        return GetXisoRanges(fs, offset, quiet);
    }

    // -----------------------------------------------------------------------
    // File-entry enumeration — mirrors XDVDFS.GetFileEntries / CollectFileEntries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all regular file entries (not directories) with their path, byte offset
    /// and size, sorted by offset ascending (XboxKit <c>CollectFileEntries</c> order).
    /// </summary>
    public static List<(string Path, long Offset, uint Size)> GetFileEntries(FileStream isoFs, long isoOffset) =>
        GetFileEntries((Stream)isoFs, isoOffset);

    /// <summary>
    /// Stream overload of <see cref="GetFileEntries(FileStream,long)"/> for
    /// container-backed images and memory streams.
    /// </summary>
    public static List<(string Path, long Offset, uint Size)> GetFileEntries(Stream isoFs, long isoOffset)
    {
        long headerOffset = XisoReader.TryFindHeaderBase(isoFs, isoOffset, out long detectedHeader)
            ? detectedHeader
            : isoOffset + HeaderOffset;
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        uint rootOffset = ReadUInt(isoFs);
        uint rootSize = ReadUInt(isoFs);

        List<(string Path, long Offset, uint Size)> results = new();
        CollectFileEntries(isoFs, isoOffset, rootOffset * SectorSize, rootSize, 0, "", results);
        results.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return results;
    }

    /// <summary>
    /// Returns all regular file entries for an XISO at the given path and offset.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <returns>List of file entries with path, offset, and size.</returns>
    public static List<(string Path, long Offset, uint Size)> GetFileEntries(string isoPath, long isoOffset = 0)
    {
        using Stream fs = XisoImageSource.OpenRead(isoPath);
        return GetFileEntries(fs, isoOffset);
    }

    private static void CollectFileEntries(Stream isoFs, long isoOffset, long dirOffset, uint dirSize,
        long childOffset, string dirPath, List<(string Path, long Offset, uint Size)> results,
        HashSet<long>? visited = null,
        // Threaded recursion-depth bound (#16 hardening, proven by deep-tree tests); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        if (childOffset >= dirSize) return;

        // Hardening (#16): bound subdirectory nesting like GetValidSectors above;
        // intra-table AVL depth is bounded by the explicit in-order stack.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{dirPath}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        visited ??= [];
        List<FileWalkEntry> pending = [];
        long? next = childOffset;

        while (true)
        {
            if (next is { } offset)
            {
                next = null;
                if (offset < dirSize)
                {
                    if (!visited.Add(offset))
                    {
                        throw new XisoFormatException(
                            $"invalid TOC entry at '{dirPath}': directory cycle detected — table offset {offset} was already visited.");
                    }

                    if (visited.Count > Constants.MaxTocEntriesPerTable)
                    {
                        throw new XisoFormatException(
                            $"invalid TOC entry at '{dirPath}': too many entries in one directory table (possible corrupt offset chain).");
                    }

                    long pos = isoOffset + dirOffset + offset;
                    if (pos < 0 || pos >= isoFs.Length)
                    {
                        throw new XisoFormatException(
                            $"invalid TOC entry at '{dirPath}': table offset {offset} (seek {pos}) points outside the image (length {isoFs.Length}).");
                    }

                    isoFs.Seek(pos, SeekOrigin.Begin);

                    ushort leftChild = ReadUShort(isoFs);
                    // Empty-directory sentinel (all-0xFF table): without this, the 0xFF bytes
                    // parse as a garbage entry (name length 255, entry sector ~4G) that either
                    // poisons the result set or sends later seeks past EOF. Mirrors the
                    // leftChild == 0xFFFF check in GetValidSectors above and extract-xiso.
                    // BUG-LIB-036: only at the table start — deeper 0xFFFF nodes are "no left
                    // child" markers whose right siblings must still be collected.
                    if (leftChild == 0xFFFF && offset == 0)
                        return;

                    // All-zero empty-table sentinel (parity with the other walkers):
                    // without this the 14 zero bytes parse as a phantom file entry.
                    if (leftChild == 0 && offset == 0 && IsAllZeroEntryHeader(isoFs))
                        return;

                    ushort rightChild = ReadUShort(isoFs);
                    uint entrySector = ReadUInt(isoFs);
                    uint entrySize = ReadUInt(isoFs);
                    byte attributes = (byte)isoFs.ReadByte();
                    byte nameLength = (byte)isoFs.ReadByte();
                    byte[] nameBuf = new byte[nameLength];
                    if (nameLength > 0)
                    {
                        // A truncated name must fail, not silently drop the entry and its
                        // right-sibling chain (parity with GetValidSectors above).
                        int read = 0;
                        while (read < nameLength)
                        {
                            int n = isoFs.Read(nameBuf, read, nameLength - read);
                            if (n == 0)
                            {
                                throw new EndOfStreamException(
                                    $"invalid TOC entry at '{dirPath}': truncated filename (expected {nameLength} bytes, got {read}).");
                            }

                            read += n;
                        }
                    }

                    // Xbox names are WINDOWS_1252 bytes; decode via Latin1 like every other
                    // reader path (ASCII would corrupt bytes >= 0x80 into '?').
                    string name = Latin1Encoding.Instance.GetString(nameBuf);
                    bool isDirectory = (attributes & 0x10) != 0;
                    long entryOffset = entrySector * SectorSize;
                    string entryPath = dirPath.Length > 0 ? dirPath + "/" + name : name;
                    FileWalkEntry entry = new(rightChild, entryOffset, entrySize, isDirectory,
                        name is "." or "..", entryPath);

                    if (leftChild != 0 && leftChild != 0xFFFF)
                    {
                        // Process the entry after its left subtree (in-order).
                        pending.Add(entry);
                        if (pending.Count > Constants.MaxTocStackFrames)
                        {
                            throw new XisoFormatException(
                                $"invalid TOC entry at '{dirPath}': intra-table traversal stack exceeded " +
                                $"{Constants.MaxTocStackFrames} frames (possible corrupt offset chain).");
                        }

                        next = (long)leftChild * 4;
                        continue;
                    }

                    AddFileEntry(isoFs, isoOffset, results, entry, depth);

                    if (rightChild != 0 && rightChild != 0xFFFF)
                    {
                        next = (long)rightChild * 4;
                        continue;
                    }
                }
            }

            bool resumed = false;
            while (pending.Count > 0)
            {
                FileWalkEntry entry = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                AddFileEntry(isoFs, isoOffset, results, entry, depth);
                if (entry.Right != 0 && entry.Right != 0xFFFF)
                {
                    next = (long)entry.Right * 4;
                    resumed = true;
                    break;
                }
            }

            if (!resumed) return;
        }
    }

    /// <summary>
    /// Applies one parsed directory entry to the file list: a subdirectory is
    /// walked recursively (bounded by <see cref="Constants.MaxTocDepth"/>), a
    /// regular file contributes its extent, and structural <c>.</c>/<c>..</c>
    /// records contribute nothing.
    /// </summary>
    private static void AddFileEntry(Stream isoFs, long isoOffset,
        List<(string Path, long Offset, uint Size)> results, FileWalkEntry entry, int depth)
    {
        if (entry.Structural)
        {
            // Structural entry: not a real file; children still walked below.
            return;
        }

        if (entry.IsDirectory)
        {
            CollectFileEntries(isoFs, isoOffset, entry.EntryOffset, entry.EntrySize, 0, entry.Path, results, null,
                depth + 1);
            return;
        }

        results.Add((entry.Path, isoOffset + entry.EntryOffset, entry.EntrySize));
    }

    /// <summary>One parsed table entry awaiting in-order processing.</summary>
    private readonly record struct FileWalkEntry(
        ushort Right,
        long EntryOffset,
        uint EntrySize,
        bool IsDirectory,
        bool Structural,
        string Path);
}
