using System.Security.Cryptography;

namespace ZARSharp;

/// <summary>
/// Compresses one 64 KiB block. Returns the compressed size, or -1 to store
/// the block raw (uncompressed). Raw storage is always valid per the format.
/// </summary>
public interface IZarBlockCompressor
{
    /// <summary>Compresses exactly 64 KiB from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <returns>Compressed size, or -1 to store raw.</returns>
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
}

/// <summary>Default compressor: stores every block raw (no compression).</summary>
public sealed class ZarRawCompressor : IZarBlockCompressor
{
    /// <inheritdoc/>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => -1;
}

/// <summary>
/// Pure-C# ZArchive writer. Faithful port of <c>src/zarchivewriter.cpp</c>
/// (ZArchive 0.1.2). All integers big-endian on disk; paths Windows-1252,
/// case-insensitive (A-Z folding); names deduplicated case-sensitively.
/// </summary>
public sealed class ZArchiveWriter : IDisposable
{
    private sealed class PathNode
    {
        public bool IsFile;
        public int NameIndex = -1;
        public readonly List<PathNode> Subnodes = [];
        public ulong FileOffset;
        public ulong FileSize;
        public uint NodeStartIndex;
    }

    private readonly Action<int> _newOutputFile;
    private readonly Action<byte[], int, int> _writeOutputData;
    private readonly IZarBlockCompressor _compressor;

    private readonly PathNode _rootNode = new();
    private PathNode? _currentFileNode;
    private readonly List<string> _nodeNames = [];
    private uint[] _nodeNameOffsets = [];
    private readonly Dictionary<string, uint> _nodeNameLookup = new(StringComparer.Ordinal);

    private Footer _footer;
    private readonly byte[] _currentWriteBuffer = new byte[ZArchiveCommon.CompressedBlockSize];
    private int _bufferedBytes;
    private readonly byte[] _compressionBuffer;
    private ulong _currentCompressedWriteIndex;
    private ulong _currentInputOffset;
    private ulong _numWrittenOffsetRecords;
    private readonly List<CompressionOffsetRecord> _offsetRecords = [];

    private IncrementalHash? _sha;
    private bool _finalized;
    private bool _disposed;

    /// <summary>
    /// Creates a writer with output callbacks. <paramref name="newOutputFile"/>
    /// is invoked immediately with <c>-1</c> (mirrors the C++ ctor).
    /// </summary>
    public ZArchiveWriter(
        Action<int> newOutputFile,
        Action<byte[], int, int> writeOutputData,
        IZarBlockCompressor? compressor = null)
    {
        _newOutputFile = newOutputFile ?? throw new ArgumentNullException(nameof(newOutputFile));
        _writeOutputData = writeOutputData ?? throw new ArgumentNullException(nameof(writeOutputData));
        _compressor = compressor ?? new ZarRawCompressor();
        _compressionBuffer = new byte[ZArchiveCommon.CompressedBlockSize + 1024];
        _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _newOutputFile(-1);
    }

    /// <summary>Creates a writer that appends to <paramref name="output"/>.</summary>
    public ZArchiveWriter(Stream output, IZarBlockCompressor? compressor = null)
        : this(
            _ => { },
            (buf, off, count) => output.Write(buf, off, count),
            compressor)
    {
    }

    // ------------------------------------------------------------------
    // Tree helpers
    // ------------------------------------------------------------------

    private PathNode? GetNodeByPath(string path)
    {
        PathNode current = _rootNode;
        ReadOnlySpan<char> parser = path.AsSpan();
        while (ZArchiveCommon.GetNextPathNode(ref parser, out ReadOnlySpan<char> nodeName))
        {
            PathNode? next = FindSubnodeByName(current, nodeName);
            if (next is null || next.IsFile)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private PathNode? FindSubnodeByName(PathNode parent, ReadOnlySpan<char> nodeName)
    {
        foreach (PathNode child in parent.Subnodes)
        {
            if (ZArchiveCommon.CompareNodeNameBool(_nodeNames[child.NameIndex].AsSpan(), nodeName))
            {
                return child;
            }
        }

        return null;
    }

    private uint CreateNameEntry(ReadOnlySpan<char> name)
    {
        string key = name.ToString();
        if (_nodeNameLookup.TryGetValue(key, out uint existing))
        {
            return existing;
        }

        uint index = (uint)_nodeNames.Count;
        _nodeNames.Add(key);
        _nodeNameLookup.Add(key, index);
        return index;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a new virtual file and makes it active for
    /// <see cref="AppendData(ReadOnlySpan{byte})"/>. Returns false when the parent directory does
    /// not exist or the file already exists.
    /// </summary>
    public bool StartNewFile(string path)
    {
        _currentFileNode = null;
        ReadOnlySpan<char> parser = path.AsSpan();
        ZArchiveCommon.SplitFilenameFromPath(ref parser, out ReadOnlySpan<char> filename);
        PathNode? dir = GetNodeByPath(parser.ToString());
        if (dir is null)
        {
            return false;
        }

        if (FindSubnodeByName(dir, filename) is not null)
        {
            return false;
        }

        var node = new PathNode
        {
            IsFile = true,
            NameIndex = (int)CreateNameEntry(filename),
            FileOffset = _currentInputOffset,
        };
        dir.Subnodes.Add(node);
        _currentFileNode = node;
        return true;
    }

    /// <summary>
    /// Creates a directory. Non-recursive mode creates a single level;
    /// recursive mode creates all missing ancestors (fails if a file blocks
    /// the path). Trailing slashes are ignored.
    /// </summary>
    public bool MakeDir(string path, bool recursive = false)
    {
        string trimmed = path.TrimEnd('/', '\\');
        if (!recursive)
        {
            ReadOnlySpan<char> parser = trimmed.AsSpan();
            ZArchiveCommon.SplitFilenameFromPath(ref parser, out ReadOnlySpan<char> dirName);
            PathNode? dir = GetNodeByPath(parser.ToString());
            if (dir is null)
            {
                return false;
            }

            if (FindSubnodeByName(dir, dirName) is not null)
            {
                return false;
            }

            dir.Subnodes.Add(new PathNode { IsFile = false, NameIndex = (int)CreateNameEntry(dirName) });
            return true;
        }

        PathNode current = _rootNode;
        ReadOnlySpan<char> walk = trimmed.AsSpan();
        while (ZArchiveCommon.GetNextPathNode(ref walk, out ReadOnlySpan<char> nodeName))
        {
            PathNode? next = FindSubnodeByName(current, nodeName);
            if (next is not null && next.IsFile)
            {
                return false;
            }

            if (next is null)
            {
                next = new PathNode { IsFile = false, NameIndex = (int)CreateNameEntry(nodeName) };
                current.Subnodes.Add(next);
            }

            current = next;
        }

        return true;
    }

    /// <summary>Appends data to the currently active file.</summary>
    public void AppendData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
        {
            throw new InvalidOperationException("Archive already finalized.");
        }

        int dataSize = data.Length;
        int offset = 0;
        int remaining = dataSize;
        while (remaining > 0)
        {
            int bytesToCopy = ZArchiveCommon.CompressedBlockSize - _bufferedBytes;
            if (bytesToCopy > remaining)
            {
                bytesToCopy = remaining;
            }

            if (bytesToCopy == ZArchiveCommon.CompressedBlockSize)
            {
                // Block-aligned input bypasses the staging buffer (as in C++).
                StoreBlock(data.Slice(offset, bytesToCopy));
                offset += bytesToCopy;
                remaining -= bytesToCopy;
                continue;
            }

            data.Slice(offset, bytesToCopy).CopyTo(_currentWriteBuffer.AsSpan(_bufferedBytes));
            offset += bytesToCopy;
            remaining -= bytesToCopy;
            _bufferedBytes += bytesToCopy;
            if (_bufferedBytes == ZArchiveCommon.CompressedBlockSize)
            {
                StoreBlock(_currentWriteBuffer);
                _bufferedBytes = 0;
            }
        }

        if (_currentFileNode is not null)
        {
            _currentFileNode.FileSize += (ulong)dataSize;
        }

        _currentInputOffset += (ulong)dataSize;
    }

    /// <summary>Appends data to the currently active file.</summary>
    public void AppendData(byte[] data, int offset, int count) =>
        AppendData(data.AsSpan(offset, count));

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private void OutputData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte[] copy = data.ToArray();
        _writeOutputData(copy, 0, copy.Length);
        _currentCompressedWriteIndex += (ulong)copy.Length;
        _sha?.AppendData(copy);
    }

    private ulong GetCurrentOutputOffset() => _currentCompressedWriteIndex;

    private void StoreBlock(ReadOnlySpan<byte> uncompressedData)
    {
        ulong writeOffset = GetCurrentOutputOffset();
        int outputSize = _compressor.Compress(uncompressedData, _compressionBuffer.AsSpan());
        if (outputSize < 0 || outputSize >= ZArchiveCommon.CompressedBlockSize)
        {
            // Store raw when incompressible (or when the compressor declines).
            OutputData(uncompressedData.Slice(0, ZArchiveCommon.CompressedBlockSize));
            outputSize = ZArchiveCommon.CompressedBlockSize;
        }
        else
        {
            OutputData(_compressionBuffer.AsSpan(0, outputSize));
        }

        if ((_numWrittenOffsetRecords % (ulong)ZArchiveCommon.EntriesPerOffsetRecord) == 0)
        {
            _offsetRecords.Add(new CompressionOffsetRecord(writeOffset));
        }

        CompressionOffsetRecord rec = _offsetRecords[^1];
        rec.Sizes[_numWrittenOffsetRecords % (ulong)ZArchiveCommon.EntriesPerOffsetRecord] =
            (ushort)(outputSize - 1);
        _offsetRecords[^1] = rec;
        _numWrittenOffsetRecords++;
    }

    /// <summary>
    /// Writes all sections and the footer. Pads the trailing partial block
    /// with zeros and the output to 8-byte alignment (as in C++).
    /// </summary>
    /// <remarks>
    /// Named to match the reference C++ API (<c>ZArchiveWriter::Finalize</c>);
    /// it is an ordinary method, not a finalizer.
    /// </remarks>
#pragma warning disable CS0465 // Finalize name mirrors the C++ API by design
    public void Finalize()
#pragma warning restore CS0465
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
        {
            return;
        }

        _currentFileNode = null; // padding must not grow the active file
        if (_bufferedBytes != 0)
        {
            int pad = ZArchiveCommon.CompressedBlockSize - _bufferedBytes;
            AppendData(new byte[pad]);
            _bufferedBytes = 0;
        }

        _finalized = true;

        _footer.SectionCompressedData = new OffsetInfo { Offset = 0, Size = GetCurrentOutputOffset() };
        while ((GetCurrentOutputOffset() % 8) != 0)
        {
            OutputData([0]);
        }

        WriteOffsetRecords();
        WriteNameTable();
        WriteFileTree();
        WriteMetaData();
        WriteFooter();
    }

    private void WriteOffsetRecords()
    {
        ulong start = GetCurrentOutputOffset();
        Span<byte> buf = stackalloc byte[CompressionOffsetRecord.SizeOnDisk];
        foreach (CompressionOffsetRecord rec in _offsetRecords)
        {
            rec.WriteTo(buf);
            OutputData(buf);
        }

        _footer.SectionOffsetRecords = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteNameTable()
    {
        ulong start = GetCurrentOutputOffset();
        _nodeNameOffsets = new uint[_nodeNames.Count];
        uint tableOffset = 0;
        Span<byte> header = stackalloc byte[2];
        for (int i = 0; i < _nodeNames.Count; i++)
        {
            _nodeNameOffsets[i] = tableOffset;
            byte[] nameBytes = ZArchiveCommon.Encode1252(_nodeNames[i].AsSpan());
            if (nameBytes.Length > ZArchiveCommon.MaxNameLength)
            {
                Array.Resize(ref nameBytes, ZArchiveCommon.MaxNameLength);
            }

            if (nameBytes.Length >= 0x80)
            {
                header[0] = (byte)((nameBytes.Length & 0x7F) | 0x80);
                header[1] = (byte)(nameBytes.Length >> 7);
                OutputData(header);
                tableOffset += 2;
            }
            else
            {
                header[0] = (byte)(nameBytes.Length & 0x7F);
                OutputData(header.Slice(0, 1));
                tableOffset += 1;
            }

            OutputData(nameBytes);
            tableOffset += (uint)nameBytes.Length;
        }

        _footer.SectionNames = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteFileTree()
    {
        // First pass: assign directory node ranges (BFS from root, index 0).
        var queue = new Queue<PathNode>();
        queue.Enqueue(_rootNode);
        uint currentIndex = 1; // root node is at index 0
        while (queue.Count > 0)
        {
            PathNode node = queue.Dequeue();
            if (node.IsFile)
            {
                node.NodeStartIndex = 0xFFFFFFFF;
                continue;
            }

            // Ascending sort using the reversed-sign comparator (> 0 predicate).
            // C# Comparison needs negative when x < y, i.e. -Compare(x, y).
            node.Subnodes.Sort((a, b) =>
                -ZArchiveCommon.CompareNodeName(
                    _nodeNames[a.NameIndex].AsSpan(),
                    _nodeNames[b.NameIndex].AsSpan()));
            node.NodeStartIndex = currentIndex;
            currentIndex += (uint)node.Subnodes.Count;
            foreach (PathNode child in node.Subnodes)
            {
                queue.Enqueue(child);
            }
        }

        // Second pass: serialize BFS.
        ulong start = GetCurrentOutputOffset();
        Span<byte> buf = stackalloc byte[FileDirectoryEntry.SizeOnDisk];
        var writeQueue = new Queue<PathNode>();
        writeQueue.Enqueue(_rootNode);
        while (writeQueue.Count > 0)
        {
            PathNode node = writeQueue.Dequeue();
            var tmp = new FileDirectoryEntry();
            if (ReferenceEquals(node, _rootNode))
            {
                tmp.SetTypeAndNameOffset(node.IsFile, ZArchiveCommon.RootNameOffset);
            }
            else
            {
                tmp.SetTypeAndNameOffset(node.IsFile, _nodeNameOffsets[node.NameIndex]);
            }

            if (node.IsFile)
            {
                tmp.SetFileOffset(node.FileOffset);
                tmp.SetFileSize(node.FileSize);
            }
            else
            {
                tmp.Field1 = node.NodeStartIndex;
                tmp.Field2 = (uint)node.Subnodes.Count;
                tmp.Field3 = 0;
            }

            tmp.WriteTo(buf);
            OutputData(buf);
            foreach (PathNode child in node.Subnodes)
            {
                writeQueue.Enqueue(child);
            }
        }

        _footer.SectionFileTree = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteMetaData()
    {
        ulong now = GetCurrentOutputOffset();
        _footer.SectionMetaDirectory = new OffsetInfo { Offset = now, Size = 0 };
        _footer.SectionMetaData = new OffsetInfo { Offset = now, Size = 0 };
    }

    private void WriteFooter()
    {
        _footer.Magic = Footer.KMagic;
        _footer.Version = Footer.KVersion1;
        _footer.TotalSize = GetCurrentOutputOffset() + Footer.SizeOnDisk;
        _footer.IntegrityHash = new byte[32];

        // Hash the footer with zeroed integrity bytes (mirrors C++: the
        // context is closed before the real footer is written, so the final
        // footer bytes themselves are NOT hashed).
        Span<byte> tmp = stackalloc byte[Footer.SizeOnDisk];
        _footer.WriteTo(tmp);
        _sha!.AppendData(tmp.ToArray());
        byte[] digest = _sha.GetHashAndReset();
        _sha.Dispose();
        _sha = null;

        _footer.IntegrityHash = (byte[])digest.Clone();
        _footer.WriteTo(tmp);

        // Raw write without hashing (ctx is null in C++ at this point).
        byte[] finalFooter = tmp.ToArray();
        _writeOutputData(finalFooter, 0, finalFooter.Length);
        _currentCompressedWriteIndex += (ulong)finalFooter.Length;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sha?.Dispose();
        _sha = null;
    }
}
