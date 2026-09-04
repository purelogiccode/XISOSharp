using ZARSharp.Zstd;

namespace ZARSharp;

/// <summary>
/// Pure-C# ZArchive reader. Faithful port of <c>src/zarchivereader.cpp</c>
/// (ZArchive 0.1.2): same open-validation chain (returns null on any
/// failure, never throws), 4 MiB LRU block cache, case-insensitive lookup,
/// and the 0.1.2 long-name quirk (see <see cref="GetName"/>).
/// Thread-safe for concurrent reads (single lock, like the C++ mutex).
/// </summary>
public sealed class ZArchiveReader : IDisposable
{
    /// <summary>Node handle returned when a path is not found.</summary>
    public const uint InvalidNode = ZArchiveCommon.InvalidNode;

    /// <summary>Directory entry (mirrors <c>ZArchiveReader::DirEntry</c>).</summary>
    public readonly struct DirEntry
    {
        /// <summary>Entry name (Windows-1252 decoded).</summary>
        public readonly string Name;

        /// <summary>True for files.</summary>
        public readonly bool IsFile;

        /// <summary>True for directories.</summary>
        public readonly bool IsDirectory;

        /// <summary>File size (valid for files only; 0 for directories).</summary>
        public readonly ulong Size;

        /// <summary>Creates an entry.</summary>
        public DirEntry(string name, bool isFile, ulong size)
        {
            Name = name;
            IsFile = isFile;
            IsDirectory = !isFile;
            Size = size;
        }
    }

    private sealed class CacheBlock
    {
        public readonly byte[] Data = new byte[ZArchiveCommon.CompressedBlockSize];
        public ulong BlockIndex = ulong.MaxValue;
    }

#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _mutex = new();
#else
    private readonly object _mutex = new();
#endif
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    private readonly CompressionOffsetRecord[] _offsetRecords;
    private readonly byte[] _nameTable;
    private readonly FileDirectoryEntry[] _fileTree;
    private readonly ulong _compressedDataOffset;
    private readonly ulong _compressedDataSize;
    private readonly ulong _blockCount;

    private readonly LinkedList<CacheBlock> _lruChain = new();
    private readonly Dictionary<ulong, LinkedListNode<CacheBlock>> _blockLookup = [];
    private readonly byte[] _blockDecompressionBuffer = new byte[ZArchiveCommon.CompressedBlockSize];
    private bool _disposed;

    private ZArchiveReader(
        Stream stream, bool leaveOpen,
        CompressionOffsetRecord[] offsetRecords, byte[] nameTable, FileDirectoryEntry[] fileTree,
        ulong compressedDataOffset, ulong compressedDataSize)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _offsetRecords = offsetRecords;
        _nameTable = nameTable;
        _fileTree = fileTree;
        _compressedDataOffset = compressedDataOffset;
        _compressedDataSize = compressedDataSize;
        _blockCount = (ulong)offsetRecords.Length * (ulong)ZArchiveCommon.EntriesPerOffsetRecord;

        // 4 MiB LRU cache = 64 x 64 KiB blocks.
        for (int i = 0; i < 64; i++)
        {
            _lruChain.AddLast(new CacheBlock());
        }
    }

    // ------------------------------------------------------------------
    // Opening (null on any failure, no exceptions escape)
    // ------------------------------------------------------------------

    /// <summary>Opens an archive from a file. Returns null when invalid.</summary>
    public static ZArchiveReader? TryOpen(string path)
    {
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, false);
            var reader = TryOpen(fs, leaveOpen: false);
            if (reader is null)
            {
                fs.Dispose();
            }

            return reader;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens an archive from a seekable stream. Returns null when invalid.</summary>
    public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen = false)
    {
        try
        {
            if (stream is null || !stream.CanRead || !stream.CanSeek)
            {
                return null;
            }

            ulong fileSize = (ulong)stream.Length;
            if (fileSize <= (ulong)Footer.SizeOnDisk)
            {
                return null;
            }

            byte[] footerBytes = new byte[Footer.SizeOnDisk];
            if (!TryReadAt(stream, (long)(fileSize - (ulong)Footer.SizeOnDisk), footerBytes, 0, footerBytes.Length))
            {
                return null;
            }

            Footer footer = Footer.ReadFrom(footerBytes);
            if (footer.Magic != Footer.KMagic ||
                footer.Version != Footer.KVersion1 ||
                footer.TotalSize != fileSize)
            {
                return null;
            }

            if (!footer.SectionCompressedData.IsWithinValidRange(fileSize) ||
                !footer.SectionOffsetRecords.IsWithinValidRange(fileSize) ||
                !footer.SectionNames.IsWithinValidRange(fileSize) ||
                !footer.SectionFileTree.IsWithinValidRange(fileSize) ||
                !footer.SectionMetaDirectory.IsWithinValidRange(fileSize) ||
                !footer.SectionMetaData.IsWithinValidRange(fileSize))
            {
                return null;
            }

            if (footer.SectionOffsetRecords.Size > 0xFFFFFFFFUL ||
                footer.SectionNames.Size > 0x7FFFFFFFUL ||
                footer.SectionFileTree.Size > 0xFFFFFFFFUL)
            {
                return null;
            }

            // Offset records (must be a whole, non-empty count).
            if (footer.SectionOffsetRecords.Size % (ulong)CompressionOffsetRecord.SizeOnDisk != 0)
            {
                return null;
            }

            long numOffsetRecords =
                (long)(footer.SectionOffsetRecords.Size / (ulong)CompressionOffsetRecord.SizeOnDisk);
            if (numOffsetRecords == 0)
            {
                return null;
            }

            if (footer.SectionOffsetRecords.Size > int.MaxValue)
            {
                return null;
            }

            byte[] offsetBytes = new byte[(int)footer.SectionOffsetRecords.Size];
            if (!TryReadAt(stream, (long)footer.SectionOffsetRecords.Offset, offsetBytes, 0, offsetBytes.Length))
            {
                return null;
            }

            var offsetRecords = new CompressionOffsetRecord[numOffsetRecords];
            for (long i = 0; i < numOffsetRecords; i++)
            {
                offsetRecords[i] = CompressionOffsetRecord.ReadFrom(
                    offsetBytes.AsSpan((int)(i * CompressionOffsetRecord.SizeOnDisk)));
            }

            // Name table.
            if (footer.SectionNames.Size > int.MaxValue)
            {
                return null;
            }

            byte[] nameTable = new byte[(int)footer.SectionNames.Size];
            if (nameTable.Length > 0 &&
                !TryReadAt(stream, (long)footer.SectionNames.Offset, nameTable, 0, nameTable.Length))
            {
                return null;
            }

            // File tree (must be a whole, non-empty count).
            if (footer.SectionFileTree.Size % (ulong)FileDirectoryEntry.SizeOnDisk != 0)
            {
                return null;
            }

            long numEntries = (long)(footer.SectionFileTree.Size / (ulong)FileDirectoryEntry.SizeOnDisk);
            if (numEntries == 0 || numEntries > int.MaxValue)
            {
                return null;
            }

            if (footer.SectionFileTree.Size > int.MaxValue)
            {
                return null;
            }

            byte[] treeBytes = new byte[(int)footer.SectionFileTree.Size];
            if (!TryReadAt(stream, (long)footer.SectionFileTree.Offset, treeBytes, 0, treeBytes.Length))
            {
                return null;
            }

            var fileTree = new FileDirectoryEntry[numEntries];
            for (long i = 0; i < numEntries; i++)
            {
                fileTree[i] = FileDirectoryEntry.ReadFrom(
                    treeBytes.AsSpan((int)(i * FileDirectoryEntry.SizeOnDisk)));
            }

            // Verify root: first entry must be a directory with an empty name.
            if (fileTree[0].IsFile)
            {
                return null;
            }

            if (GetName(nameTable, fileTree[0].NameOffset).Length != 0)
            {
                return null;
            }

            return new ZArchiveReader(
                stream, leaveOpen, offsetRecords, nameTable, fileTree,
                footer.SectionCompressedData.Offset, footer.SectionCompressedData.Size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens an archive from a byte array. Returns null when invalid.</summary>
    public static ZArchiveReader? TryOpen(byte[] data)
    {
        if (data is null)
        {
            return null;
        }

        // The reader keeps the stream; a read-only MemoryStream avoids copies.
        return TryOpen(new MemoryStream(data, writable: false), leaveOpen: false);
    }

    private static bool TryReadAt(Stream stream, long offset, byte[] buffer, int bufferOffset, int count)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, bufferOffset + total, count - total);
                if (read == 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Names (with the 0.1.2 extended-length quirk)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decodes a name-table entry. Ports the 0.1.2 quirk exactly: in the
    /// extended 2-byte-length branch the length is computed from
    /// <c>nameTable[nameOffset]</c> (the FIRST header byte) again instead of
    /// <c>nameTable[nameOffset + 1]</c>. Names of ≥ 0x80 chars therefore
    /// decode to "" (upstream bug, preserved for byte parity).
    /// Returns "" on any out-of-range input.
    /// </summary>
    public static string GetName(byte[] nameTable, uint nameOffset)
    {
        if (nameOffset == ZArchiveCommon.RootNameOffset || nameOffset >= (uint)nameTable.Length)
        {
            return string.Empty;
        }

        int offset = (int)nameOffset;
        int nameLength = nameTable[offset] & 0x7F;
        if ((nameTable[offset] & 0x80) != 0)
        {
            // Extended 2-byte length (with the upstream quirk).
            if (offset + 1 >= nameTable.Length)
            {
                return string.Empty;
            }

            nameLength |= nameTable[offset] << 7; // quirk: first byte again
            offset += 2;
        }
        else
        {
            offset += 1;
        }

        if (nameLength < 0 || offset + nameLength > nameTable.Length)
        {
            return string.Empty;
        }

        return ZArchiveCommon.Decode1252(nameTable.AsSpan(offset, nameLength));
    }

    /// <summary>Raw (Windows-1252) name bytes, or null when out of range.</summary>
    public static byte[]? GetNameRaw(byte[] nameTable, uint nameOffset, out int length)
    {
        length = 0;
        if (nameOffset == ZArchiveCommon.RootNameOffset || nameOffset >= (uint)nameTable.Length)
        {
            return null;
        }

        int offset = (int)nameOffset;
        int nameLength = nameTable[offset] & 0x7F;
        if ((nameTable[offset] & 0x80) != 0)
        {
            if (offset + 1 >= nameTable.Length)
            {
                return null;
            }

            nameLength |= nameTable[offset] << 7; // quirk: first byte again
            offset += 2;
        }
        else
        {
            offset += 1;
        }

        if (nameLength < 0 || offset + nameLength > nameTable.Length)
        {
            return null;
        }

        length = nameLength;
        return nameTable.AsSpan(offset, nameLength).ToArray();
    }

    // ------------------------------------------------------------------
    // Lookup & directory/file operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="path"/> to a node handle, or
    /// <see cref="InvalidNode"/> when not found.
    /// </summary>
    /// <remarks>
    /// <paramref name="allowFile"/>/<paramref name="allowDirectory"/> are
    /// accepted for API compatibility but ignored, exactly like the
    /// reference C++ (which never reads them).
    /// </remarks>
    public uint LookUp(string path, bool allowFile = true, bool allowDirectory = true)
    {
        _ = allowFile;
        _ = allowDirectory;
        if (path is null)
        {
            return InvalidNode;
        }

        // Byte-faithful walk: encode the path as Windows-1252 (separators
        // are ASCII and survive the codec) and compare raw name bytes.
        byte[] pathBytes = ZArchiveCommon.Encode1252(path.AsSpan());
        int pos = 0;
        uint currentNode = 0;
        while (true)
        {
            // Skip leading separators.
            while (pos < pathBytes.Length && (pathBytes[pos] == (byte)'/' || pathBytes[pos] == (byte)'\\'))
            {
                pos++;
            }

            if (pos >= pathBytes.Length)
            {
                return currentNode; // end of path
            }

            int nodeStart = pos;
            while (pos < pathBytes.Length && pathBytes[pos] != (byte)'/' && pathBytes[pos] != (byte)'\\')
            {
                pos++;
            }

            ReadOnlySpan<byte> nodeName = pathBytes.AsSpan(nodeStart, pos - nodeStart);
            if (currentNode >= (uint)_fileTree.Length)
            {
                return InvalidNode;
            }

            FileDirectoryEntry entry = _fileTree[currentNode];
            if (entry.IsFile)
            {
                return InvalidNode; // trying to iterate a file
            }

            uint index = entry.NodeStartIndex;
            uint endIndex = entry.NodeStartIndex + entry.Count;
            uint match = InvalidNode;
            while (index < endIndex)
            {
                if (index >= (uint)_fileTree.Length)
                {
                    return InvalidNode;
                }

                FileDirectoryEntry child = _fileTree[index];
                byte[]? childName = GetNameRaw(_nameTable, child.NameOffset, out int childLen);
                if (childName is not null &&
                    ZArchiveCommon.CompareNodeNameBool(nodeName, childName.AsSpan(0, childLen)))
                {
                    match = index;
                    break;
                }

                index++;
            }

            if (match == InvalidNode)
            {
                return InvalidNode;
            }

            currentNode = match;
        }
    }

    /// <summary>True when <paramref name="node"/> is a directory.</summary>
    public bool IsDirectory(uint node) =>
        node < (uint)_fileTree.Length && !_fileTree[node].IsFile;

    /// <summary>True when <paramref name="node"/> is a file.</summary>
    public bool IsFile(uint node) =>
        node < (uint)_fileTree.Length && _fileTree[node].IsFile;

    /// <summary>Child count (0 for files and invalid handles).</summary>
    public uint GetDirEntryCount(uint node)
    {
        if (node >= (uint)_fileTree.Length || _fileTree[node].IsFile)
        {
            return 0;
        }

        return _fileTree[node].Count;
    }

    /// <summary>Reads a directory entry. Returns false when invalid.</summary>
    public bool GetDirEntry(uint node, uint index, out DirEntry entry)
    {
        entry = default;
        if (node >= (uint)_fileTree.Length || _fileTree[node].IsFile)
        {
            return false;
        }

        FileDirectoryEntry dir = _fileTree[node];
        if (index >= dir.Count)
        {
            return false;
        }

        uint childIndex = dir.NodeStartIndex + index;
        if (childIndex >= (uint)_fileTree.Length)
        {
            return false;
        }

        FileDirectoryEntry child = _fileTree[childIndex];
        string name = GetName(_nameTable, child.NameOffset);
        if (name.Length == 0)
        {
            return false; // bad name (also rejects the ≥0x80-char quirk names)
        }

        entry = new DirEntry(name, child.IsFile, child.IsFile ? child.GetFileSize() : 0);
        return true;
    }

    /// <summary>File size (0 for directories and invalid handles).</summary>
    public ulong GetFileSize(uint node)
    {
        if (node >= (uint)_fileTree.Length || !_fileTree[node].IsFile)
        {
            return 0;
        }

        return _fileTree[node].GetFileSize();
    }

    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes from <paramref name="node"/>
    /// at <paramref name="offset"/> (clamped to the file size). Returns the
    /// number of bytes read. Thread-safe.
    /// </summary>
    public ulong ReadFromFile(uint node, ulong offset, Span<byte> buffer)
    {
        if (node >= (uint)_fileTree.Length)
        {
            return 0;
        }

        lock (_mutex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FileDirectoryEntry file = _fileTree[node];
            if (!file.IsFile)
            {
                return 0;
            }

            ulong fileOffset = file.GetFileOffset();
            ulong fileSize = file.GetFileSize();
            if (offset >= fileSize)
            {
                return 0;
            }

            ulong bytesToRead = Math.Min((ulong)buffer.Length, fileSize - offset);
            ulong rawReadOffset = fileOffset + offset;
            ulong remaining = bytesToRead;
            int bufferPos = 0;
            while (remaining > 0)
            {
                ulong blockIndex = rawReadOffset / (ulong)ZArchiveCommon.CompressedBlockSize;
                uint blockOffset = (uint)(rawReadOffset % (ulong)ZArchiveCommon.CompressedBlockSize);
                uint step = (uint)Math.Min(remaining, (ulong)ZArchiveCommon.CompressedBlockSize - blockOffset);
                CacheBlock? block = GetCachedBlock(blockIndex);
                if (block is null)
                {
                    return 0;
                }

                block.Data.AsSpan((int)blockOffset, (int)step).CopyTo(buffer.Slice(bufferPos, (int)step));
                rawReadOffset += step;
                remaining -= step;
                bufferPos += (int)step;
            }

            return bytesToRead;
        }
    }

    /// <summary>Reads a whole file into a new array (empty when invalid).</summary>
    public byte[] ReadFile(uint node)
    {
        ulong size = GetFileSize(node);
        if (size > int.MaxValue)
        {
            throw new InvalidOperationException("File too large to read into memory.");
        }

        byte[] buffer = new byte[(int)size];
        ulong read = ReadFromFile(node, 0, buffer);
        if (read != size)
        {
            throw new IOException("Failed to read file from archive.");
        }

        return buffer;
    }

    // ------------------------------------------------------------------
    // Block cache
    // ------------------------------------------------------------------

    private CacheBlock? GetCachedBlock(ulong blockIndex)
    {
        if (_blockLookup.TryGetValue(blockIndex, out LinkedListNode<CacheBlock>? node))
        {
            MarkBlockAsMRU(node);
            return node.Value;
        }

        if (blockIndex >= _blockCount)
        {
            return null;
        }

        LinkedListNode<CacheBlock> recycled = _lruChain.First!;
        _blockLookup.Remove(recycled.Value.BlockIndex);
        recycled.Value.BlockIndex = blockIndex;
        _blockLookup[blockIndex] = recycled;
        MarkBlockAsMRU(recycled);
        if (!LoadBlock(recycled.Value))
        {
            _blockLookup.Remove(blockIndex);
            recycled.Value.BlockIndex = ulong.MaxValue;
            return null;
        }

        return recycled.Value;
    }

    private void MarkBlockAsMRU(LinkedListNode<CacheBlock> node)
    {
        if (node.List is null || _lruChain.Last == node)
        {
            return; // already MRU
        }

        _lruChain.Remove(node);
        _lruChain.AddLast(node);
    }

    private bool LoadBlock(CacheBlock block)
    {
        ulong recordIndex = block.BlockIndex / (ulong)ZArchiveCommon.EntriesPerOffsetRecord;
        ulong recordSubIndex = block.BlockIndex % (ulong)ZArchiveCommon.EntriesPerOffsetRecord;
        if (recordIndex >= (ulong)_offsetRecords.Length)
        {
            return false;
        }

        CompressionOffsetRecord record = _offsetRecords[recordIndex];
        ulong offset = record.BaseOffset;
        for (ulong i = 0; i < recordSubIndex; i++)
        {
            offset += (ulong)record.Sizes[i] + 1;
        }

        uint compressedSize = (uint)record.Sizes[recordSubIndex] + 1;
        if (offset + compressedSize > _compressedDataSize)
        {
            return false;
        }

        ulong fileOffset = _compressedDataOffset + offset;
        if (compressedSize == (uint)ZArchiveCommon.CompressedBlockSize)
        {
            // Raw block: read directly.
            return TryReadAt(_stream, (long)fileOffset, block.Data, 0, block.Data.Length);
        }

        if (!TryReadAt(_stream, (long)fileOffset, _blockDecompressionBuffer, 0, (int)compressedSize))
        {
            return false;
        }

        try
        {
            byte[] src = new byte[compressedSize];
            Array.Copy(_blockDecompressionBuffer, src, (int)compressedSize);
            ZstdDecompressor.DecompressExact(src, 0, (int)compressedSize, block.Data, 0, block.Data.Length);
            return true;
        }
        catch (ZstdException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_mutex)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}