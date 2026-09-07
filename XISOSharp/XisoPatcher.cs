using System.Buffers.Binary;
using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// In-place patching of files into an existing XISO image (TODO #5, xdvdfs #165).
/// Unlike the whole-image writer (<see cref="XisoWriter"/>), which rebuilds the
/// image from scratch, the patcher modifies a single file's data plus the
/// directory table(s) that reference it, leaving every other byte untouched.
/// </summary>
/// <remarks>
/// Two cases, chosen automatically per file:
/// <list type="bullet">
/// <item>Replacement fits the existing allocation: data is overwritten in place
/// (with <c>0xFF</c> tail fill) and only the parent table's entry record
/// (start sector + file size, 8 bytes) is updated.</item>
/// <item>Replacement is larger, or the file is new: a free run is allocated via
/// <see cref="SectorAllocator"/> seeded from <see cref="XisoReader.GetSectorLayout"/>,
/// and the parent table is re-serialized with <see cref="DirectoryEntryTableWriter"/>.
/// Value-only changes to ancestor tables (or the volume header, when the root
/// table moves) are applied as surgical 8-byte record patches — ancestor tables
/// never change size, so the move cascade is exactly one table deep.</item>
/// </list>
/// The image file size never changes: allocations come from free space inside
/// the image, and a clean <see cref="InvalidDataException"/> is thrown when
/// nothing fits. A <c>.old</c> backup of the pre-patch image is written first
/// (replacing any previous backup) unless disabled.
/// </remarks>
public static class XisoPatcher
{
    /// <summary>
    /// Copies a host file into an XISO image: replaces the entry at
    /// <paramref name="internalPath"/> when it exists, or adds it as a new file
    /// when only its parent directory exists.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (modified in place).</param>
    /// <param name="hostFile">Host file whose bytes become the new content.</param>
    /// <param name="internalPath">
    /// Destination path inside the image (e.g. <c>/dir/file.bin</c>);
    /// case-insensitive, <c>/</c>-separated, like the reader APIs.
    /// </param>
    /// <param name="createBackup">
    /// Write a <c>.old</c> backup of the pre-patch image first (default true).
    /// </param>
    /// <exception cref="FileNotFoundException">The host file does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="InvalidDataException">
    /// The internal path is malformed, a parent is missing, the target is a
    /// directory, the host path is a directory (only single files can be
    /// copied in), or the data/table does not fit in free space.
    /// </exception>
    public static void CopyIntoImage(string isoPath, string hostFile, string internalPath,
        bool createBackup = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(isoPath);
        ArgumentException.ThrowIfNullOrEmpty(hostFile);
        ArgumentNullException.ThrowIfNull(internalPath);
        if (Directory.Exists(hostFile))
        {
            // Directories cannot be copied in (single-file limit, TODO #22):
            // fail with the documented InvalidDataException rather than the
            // misleading FileNotFoundException that File.Exists would produce.
            throw new InvalidDataException(
                $"Cannot copy host directory '{hostFile}' into an image " +
                "(copying directories into an image is not supported).");
        }

        if (!File.Exists(hostFile))
            throw new FileNotFoundException($"Host file not found: {hostFile}", hostFile);

        var segments = SplitInternalPath(internalPath);
        var fileName = segments[^1];

        var volInfo = XisoReader.GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        var newData = File.ReadAllBytes(hostFile);
        ApplyXbeMediaPatch(fileName, newData);

        var layout = XisoReader.GetSectorLayout(isoPath);
        var canonicalParent = ResolveCanonicalParentPath(isoPath, segments, internalPath);
        var existing = XisoReader.GetEntryInfo(isoPath, internalPath);
        if (existing?.IsDirectory == true)
        {
            throw new InvalidDataException(
                $"Cannot copy into '{internalPath}': it is a directory in the image " +
                "(copying directories into an image is not supported).");
        }

        // Sibling entries for the add-new path must be read before the
        // read-write stream below opens (it takes Share.None).
        List<DirectoryEntryTableWriter.DirectoryTableEntry>? siblings = null;
        if (existing == null)
            siblings = ReadSiblingEntries(isoPath, layout, canonicalParent);

        if (createBackup)
        {
            // Keep the first backup: overwriting a previous `.old` would
            // destroy the true pre-patch original (BUG-LIB-027).
            var backupPath = isoPath + ".old";
            if (!File.Exists(backupPath))
                File.Copy(isoPath, backupPath);
        }

        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None,
                BufferSize = 65536,
            });

        if (existing != null)
        {
            ReplaceFile(fs, layout, volInfo.DiscLseek, canonicalParent, fileName, existing, newData,
                isoPath, internalPath);
        }
        else
        {
            AddFile(fs, isoPath, layout, volInfo.DiscLseek, canonicalParent, fileName, newData,
                siblings!, internalPath);
        }
    }

    private static List<DirectoryEntryTableWriter.DirectoryTableEntry> ReadSiblingEntries(
        string isoPath, SectorLayout layout, string canonicalParent)
    {
        var dirSizes = layout.Entries.Where(static e => e.IsDirectory)
            .ToDictionary(static e => e.Path, static e => e.FileSize, StringComparer.Ordinal);

        var siblings = new List<DirectoryEntryTableWriter.DirectoryTableEntry>();
        foreach (var e in XisoReader.ListDirectory(isoPath, canonicalParent))
        {
            siblings.Add(new DirectoryEntryTableWriter.DirectoryTableEntry(
                e.Name,
                e.IsDirectory,
                e.StartSector,
                e.IsDirectory ? dirSizes[JoinPath(canonicalParent, e.Name)] : e.FileSize,
                // BUG-LIB-034: keep the source attribute bits so the rewritten
                // table preserves RO/HID/SYS instead of normalizing to Archive.
                e.Attributes));
        }

        return siblings;
    }

    private static void ReplaceFile(FileStream fs, SectorLayout layout, long discLseek,
        string canonicalParent, string fileName, EntryInfo existing, byte[] newData,
        string isoPath, string internalPath)
    {
        var parent = FindDirExtent(layout, canonicalParent, internalPath);
        var oldSectors = existing.FileSize == 0
            ? 0u
            : (existing.FileSize + (Constants.SectorSize - 1)) / Constants.SectorSize;
        var need = SectorAllocator.RequiredSectors((ulong)newData.Length);
        var allocator = SectorAllocator.FromLayout(layout);

        uint target;
        if (need > oldSectors)
        {
            target = AllocateOrThrow(allocator, layout, need, (ulong)newData.Length, isoPath);
            WriteSectors(fs, discLseek, target, newData, need);
        }
        else
        {
            target = existing.StartSector;
            // need == oldSectors == 0: the old StartSector dangles (writer convention
            // for empty files) — FileSize 0 is never dereferenced, so touch nothing.
            if (oldSectors > 0)
                WriteSectors(fs, discLseek, target, newData, oldSectors);
        }

        PatchEntryRecord(fs, discLseek, parent, fileName, target, (uint)newData.Length,
            canonicalParent);

        if (need > oldSectors && oldSectors > 0)
            WipeSectors(fs, discLseek, existing.StartSector, oldSectors);
    }

    private static void AddFile(FileStream fs, string isoPath, SectorLayout layout, long discLseek,
        string canonicalParent, string fileName, byte[] newData,
        List<DirectoryEntryTableWriter.DirectoryTableEntry> siblings, string internalPath)
    {
        var parent = FindDirExtent(layout, canonicalParent, internalPath);

        var tableEntries = new List<DirectoryEntryTableWriter.DirectoryTableEntry>(siblings);

        var allocator = SectorAllocator.FromLayout(layout);
        var dataSector = AllocateOrThrow(allocator, layout,
            SectorAllocator.RequiredSectors((ulong)newData.Length), (ulong)newData.Length, isoPath);
        tableEntries.Add(new DirectoryEntryTableWriter.DirectoryTableEntry(
            fileName, false, dataSector, (uint)newData.Length));

        AvlNode? table;
        try
        {
            table = DirectoryEntryTableWriter.BuildTable(tableEntries);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(
                $"Cannot copy into '{internalPath}': {ex.Message}", ex);
        }

        var newTableSize = DirectoryEntryTableWriter.ComputeTableSize(table);
        var tableBytes = DirectoryEntryTableWriter.SerializeTable(table);
        var tableSectors = (uint)(tableBytes.Length / Constants.SectorSize);

        var moved = tableSectors > parent.SectorCount;
        var tableTarget = moved
            ? AllocateOrThrow(allocator, layout, tableSectors, (ulong)tableBytes.Length, isoPath)
            : parent.StartSector;

        if (newData.Length > 0)
        {
            WriteSectors(fs, discLseek, dataSector, newData,
                SectorAllocator.RequiredSectors((ulong)newData.Length));
        }

        var tableAbs = discLseek + ((long)tableTarget * Constants.SectorSize);
        fs.Seek(tableAbs, SeekOrigin.Begin);
        fs.Write(tableBytes, 0, tableBytes.Length);
        if (!moved)
        {
            // Same allocation, possibly fewer bytes: 0xFF-fill the stale tail.
            WipeRange(fs, tableAbs + tableBytes.Length,
                ((long)parent.SectorCount * Constants.SectorSize) - tableBytes.Length);
        }
        else if (canonicalParent.Equals("/", StringComparison.Ordinal))
        {
            PatchVolumeHeaderRoot(fs, discLseek, tableTarget, newTableSize);
        }
        else
        {
            var grandparentPath = canonicalParent[..canonicalParent.LastIndexOf('/')];
            if (grandparentPath.Length == 0)
                grandparentPath = "/";
            var grandparent = FindDirExtent(layout, grandparentPath, internalPath);
            var parentName = canonicalParent[(canonicalParent.LastIndexOf('/') + 1)..];
            var rounded = newTableSize +
                          ((Constants.SectorSize - (newTableSize % Constants.SectorSize)) % Constants.SectorSize);
            PatchEntryRecord(fs, discLseek, grandparent, parentName, tableTarget, rounded,
                grandparentPath);
        }

        if (moved && parent.SectorCount > 0)
            WipeSectors(fs, discLseek, parent.StartSector, parent.SectorCount);
    }

    private static uint AllocateOrThrow(SectorAllocator allocator, SectorLayout layout,
        uint sectors, ulong bytes, string isoPath)
    {
        try
        {
            return allocator.AllocateContiguous(sectors);
        }
        catch (InvalidOperationException ex)
        {
            ulong free = 0;
            foreach (var range in layout.FreeRanges)
                free += range.SectorCount;
            throw new InvalidDataException(
                $"Not enough free space in '{isoPath}': need {sectors} sectors " +
                $"({bytes} bytes) but only {free} sectors are free.", ex);
        }
    }

    private static FileSectorExtent FindDirExtent(SectorLayout layout, string dirPath,
        string internalPath)
    {
        foreach (var e in layout.Entries)
        {
            if (e.IsDirectory && e.Path.Equals(dirPath, StringComparison.Ordinal))
                return e;
        }

        throw new InvalidDataException($"Path not found in XISO: {internalPath}");
    }

    private static string ResolveCanonicalParentPath(string isoPath, string[] segments,
        string internalPath)
    {
        // Rebuild the parent path with on-disk casing so it matches SectorLayout
        // extent paths (which use disk case, unlike the case-insensitive lookup).
        if (segments.Length == 1)
            return "/";

        var current = "/";
        foreach (var seg in segments[..^1])
        {
            string? match = null;
            foreach (var e in XisoReader.ListDirectory(isoPath, current))
            {
                if (e.IsDirectory && string.Equals(e.Name, seg, StringComparison.OrdinalIgnoreCase))
                {
                    match = e.Name;
                    break;
                }
            }

            if (match == null)
                throw new InvalidDataException($"Path not found in XISO: {internalPath}");
            current = current.Equals("/", StringComparison.Ordinal) ? "/" + match : current + "/" + match;
        }

        return current;
    }

    private static string[] SplitInternalPath(string internalPath)
    {
        var segments = internalPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidDataException(
                $"Invalid internal path '{internalPath}': must name a file inside the image " +
                "(e.g. /dir/file.bin).");
        }

        foreach (var seg in segments)
        {
            if (seg is "." or "..")
            {
                throw new InvalidDataException(
                    $"Invalid internal path '{internalPath}': '.' and '..' are not allowed.");
            }

            if (seg.Contains('\\'))
            {
                throw new InvalidDataException(
                    $"Invalid internal path '{internalPath}': entry name '{seg}' contains " +
                    "'\\', which is not allowed in XISO directory entries.");
            }

            if (seg.Length > Constants.FilenameMaxChars)
            {
                throw new InvalidDataException(
                    $"Invalid internal path '{internalPath}': entry name '{seg}' exceeds " +
                    $"{Constants.FilenameMaxChars} characters.");
            }

            foreach (var c in seg)
            {
                if (c > 0xFF)
                {
                    throw new InvalidDataException(
                        $"Invalid internal path '{internalPath}': entry name '{seg}' is not " +
                        "Latin-1 encodable.");
                }
            }
        }

        return segments;
    }

    private static void ApplyXbeMediaPatch(string fileName, byte[] data)
    {
        // Same gate and byte edit as XisoWriter.WriteFileData: patch every occurrence
        // of the media-enable pattern so a copied-in .xbe boots like a packed one.
        if (!Logger.MediaEnable || fileName.Length < 4 ||
            !string.Equals(fileName[^4..], ".xbe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var bm = new BoyerMoore(Constants.MediaEnable);
        bm.Init();
        var pos = 0;
        while (pos < data.Length)
        {
            var found = bm.Search(data, pos, data.Length - pos);
            if (found < 0)
                break;
            data[found + Constants.MediaEnableBytePos] = Constants.MediaEnableByte;
            pos = found + Constants.MediaEnableLength;
        }
    }

    private static void PatchEntryRecord(FileStream fs, long discLseek, FileSectorExtent table,
        string entryName, uint newStartSector, uint newFileSize, string contextPath)
    {
        var tableAbs = discLseek + ((long)table.StartSector * Constants.SectorSize);
        var recordAbs = FindEntryRecordOffset(fs, tableAbs, entryName, contextPath);
        // StartSector field: record is lOffset(2) + rOffset(2) + StartSector(4) + FileSize(4) + ...
        fs.Seek(recordAbs + 4, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], newStartSector);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], newFileSize);
        fs.Write(buf);
    }

    private static void PatchVolumeHeaderRoot(FileStream fs, long discLseek, uint rootSector,
        uint rootSize)
    {
        // Same field offsets the reader uses (volume header + 20/+ 24).
        fs.Seek(Constants.HeaderOffset + discLseek + Constants.HeaderDataLength, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], rootSector);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], rootSize);
        fs.Write(buf);
    }

    private static void WriteSectors(FileStream fs, long discLseek, uint startSector, byte[] data,
        uint spanSectors)
    {
        var spanBytes = (long)spanSectors * Constants.SectorSize;
        if (data.Length > spanBytes)
            throw new ArgumentException("Data does not fit the sector span.", nameof(data));
        fs.Seek(discLseek + ((long)startSector * Constants.SectorSize), SeekOrigin.Begin);
        if (data.Length > 0)
            fs.Write(data, 0, data.Length);
        WipeRange(fs, fs.Position, spanBytes - data.Length);
    }

    private static void WipeSectors(FileStream fs, long discLseek, uint startSector, uint sectors) =>
        WipeRange(fs, discLseek + ((long)startSector * Constants.SectorSize),
            (long)sectors * Constants.SectorSize);

    private static void WipeRange(FileStream fs, long absStart, long length)
    {
        if (length <= 0)
            return;
        fs.Seek(absStart, SeekOrigin.Begin);
        var chunk = new byte[(int)Math.Min(length, 65536)];
        Array.Fill(chunk, Constants.PadByte);
        while (length > 0)
        {
            var n = (int)Math.Min(length, chunk.Length);
            fs.Write(chunk, 0, n);
            length -= n;
        }
    }

    private static long FindEntryRecordOffset(FileStream fs, long tableAbs,
        string entryName, string contextPath)
    {
        // Preorder AVL walk identical to the reader's ReadDirectoryEntries
        // (right pushed before left; same sentinels and hardening), returning the
        // absolute offset of the first case-insensitive name match — the same
        // record GetEntryInfo resolves, so the patch hits the right entry.
        var stack = new Stack<long>();
        stack.Push(0);
        var visited = new HashSet<long>();

        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            var absOffset = tableAbs + offset;
            if (absOffset < tableAbs || absOffset >= fs.Length)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': child offset {offset} (seek {absOffset}) " +
                    $"points outside the image (table at {tableAbs}, length {fs.Length}).");
            }

            if (!visited.Add(absOffset))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': directory cycle detected — entry at offset {absOffset} was already visited.");
            }

            if (visited.Count > Constants.MaxTocEntriesPerTable)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': too many entries in one directory table " +
                    $"(>{Constants.MaxTocEntriesPerTable}, possible corrupt offset chain).");
            }

            fs.Seek(absOffset, SeekOrigin.Begin);
            ReadExact(fs, shortBuf);
            var lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);
            if (lOffset == Constants.PadShort && offset == 0)
                continue;

            if (lOffset == Constants.EmptyDirectorySentinel && offset == 0)
            {
                var peekPos = fs.Position;
                var isAllZeros = false;
                try
                {
                    ReadExact(fs, headerRest);
                    isAllZeros = headerRest[0] == 0 && headerRest[1] == 0 && headerRest[2] == 0 &&
                                 headerRest[3] == 0 && headerRest[4] == 0 && headerRest[5] == 0 &&
                                 headerRest[6] == 0 && headerRest[7] == 0 && headerRest[8] == 0 &&
                                 headerRest[9] == 0 && headerRest[10] == 0 && headerRest[11] == 0;
                }
                catch
                {
                    isAllZeros = false;
                }

                fs.Seek(peekPos, SeekOrigin.Begin);
                if (isAllZeros)
                    continue;
            }

            ReadExact(fs, shortBuf);
            var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);
            ReadExact(fs, intBuf);
            ReadExact(fs, intBuf);
            ReadExact(fs, byteBuf);
            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (!string.Equals(filename, ".", StringComparison.Ordinal) &&
                !string.Equals(filename, "..", StringComparison.Ordinal) &&
                string.Equals(filename, entryName, StringComparison.OrdinalIgnoreCase))
            {
                return absOffset;
            }

            if (rOffset != 0 && rOffset != Constants.PadShort)
                stack.Push((long)rOffset * Constants.DwordSize);
            if (lOffset != 0 && lOffset != Constants.PadShort)
                stack.Push((long)lOffset * Constants.DwordSize);
        }

        throw new InvalidDataException(
            $"Cannot patch '{contextPath}': entry '{entryName}' not found in its directory table.");
    }

    private static void ReadExact(FileStream fs, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer[read..]);
            if (n <= 0)
                throw new EndOfStreamException("Unexpected end of image while reading a directory table.");
            read += n;
        }
    }

    private static string JoinPath(string dir, string name) => dir.Equals("/", StringComparison.Ordinal) ? "/" + name : dir + "/" + name;
}
