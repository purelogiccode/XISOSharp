using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Read-only access to a single-file Xbox 360 STFS package (XBLA or XBLIG):
/// the file table, per-file block chains and the file data behind the STFS
/// hash tables. The package file itself starts with the <c>XContent</c>
/// header; there is no <c>.data</c> directory (that layout is GOD/SVOD).
/// </summary>
/// <remarks>
/// <para>
/// The reader follows the STFS volume descriptor at <c>0x379</c>: file table
/// block count/number, read-only vs writable hash-table layout, and the
/// allocated block count. Logical data blocks are mapped to file offsets with
/// the standard <c>0xAA</c>-block level-0 / <c>0x70E4</c>-block level-1 hash
/// interleave; hash tables are never exposed as file data.
/// </para>
/// <para>
/// Writable-format packages (two hash tables per level) select the active
/// table through the volume-descriptor root bit and the parent hash entry's
/// active-index bit, matching the on-disk format. Reads are serialized on an
/// internal lock, so one instance can be shared by concurrent readers. The
/// package is read-only; there is no write API.
/// </para>
/// </remarks>
public sealed class StfsPackage : IDisposable
{
    private const int BlockSize = 0x1000;
    private const int HeaderReadSize = 0x971A;
    private const int HeaderSizeOffset = 0x340;
    private const int ContentTypeOffset = XContentHeaderReader.ContentTypeOffset;
    private const int TitleIdOffset = XContentHeaderReader.TitleIdOffset;
    private const int DiscNumberOffset = XContentHeaderReader.DiscNumberOffset;
    private const int DiscCountOffset = XContentHeaderReader.DiscCountOffset;
    private const int TitleNameOffset = XContentHeaderReader.TitleNameOffset;
    private const int TitleNameLength = XContentHeaderReader.TitleNameLength;
    private const int VolumeDescriptorOffset = 0x379;
    private const int FlagsOffset = VolumeDescriptorOffset + 0x02;
    private const int FileTableBlockCountOffset = VolumeDescriptorOffset + 0x03;
    private const int FileTableBlockNumberOffset = VolumeDescriptorOffset + 0x05;
    private const int TotalBlockCountOffset = VolumeDescriptorOffset + 0x1C;
    private const int FreeBlockCountOffset = VolumeDescriptorOffset + 0x20;
    private const int VolumeTypeOffset = 0x3A9;
    private const int DataFileCountOffset = 0x39D;
    private const int DirectoryEntrySize = 0x40;
    private const int EntriesPerBlock = BlockSize / DirectoryEntrySize;
    private const int HashEntrySize = 0x18;
    private const int EntriesPerHashTable = 0xAA;
    private const int BlocksPerHashLevel1 = 0x70E4;
    private const uint EndOfChain = 0xFFFFFF;
    private const uint VolumeTypeStfs = 0;
    private const int MaxHashTableCacheEntries = 16;
    private const int MaxEntries = Constants.MaxTocEntriesPerTable;

    private readonly FileStream _stream;
    private readonly string _packagePath;
    private readonly long _firstBlockOffset;
    private readonly uint _totalBlockCount;
    private readonly uint _freeBlockCount;
    private readonly ushort _fileTableBlockCount;
    private readonly uint _fileTableBlockNumber;
    private readonly bool _readOnlyFormat;
    private readonly bool _rootActiveIndex;
    private readonly int _blocksPerHashTable;
    private readonly uint _blockStep0;
    private readonly uint _blockStep1;
    private readonly int _topLevel;
    private readonly List<StfsEntry> _entries;
    private readonly IReadOnlyList<StfsEntry> _entriesView;
    private readonly Dictionary<string, StfsEntry> _byPath;
    private readonly Dictionary<long, byte[]> _hashTableCache = new();
#pragma warning disable MA0158 // object keeps net8 compat (System.Threading.Lock is net9+)
    private readonly object _sync = new();
#pragma warning restore MA0158
    private bool _disposed;

    private StfsPackage(FileStream stream, string packagePath)
    {
        _stream = stream;
        _packagePath = Path.GetFullPath(packagePath);

        if (stream.Length < HeaderReadSize)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' is too small ({stream.Length} bytes).");
        }

        byte[] header = new byte[HeaderReadSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!IsStfsHeader(header))
        {
            throw new InvalidDataException(
                $"Not an XBLA/XBLIG STFS package: '{packagePath}'.");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(HeaderSizeOffset));
        if (headerSize == 0 || headerSize > stream.Length)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' has an invalid header size ({headerSize}).");
        }

        _firstBlockOffset = ((long)headerSize + (BlockSize - 1)) & ~(long)(BlockSize - 1);
        if (_firstBlockOffset < HeaderReadSize || _firstBlockOffset + BlockSize > stream.Length)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' has no room for its hash tables.");
        }

        byte flags = header[FlagsOffset];
        _readOnlyFormat = (flags & 0x01) != 0;
        _rootActiveIndex = (flags & 0x02) != 0;
        _blocksPerHashTable = _readOnlyFormat ? 1 : 2;
        _blockStep0 = (uint)(EntriesPerHashTable + _blocksPerHashTable);
        _blockStep1 = (uint)(BlocksPerHashLevel1 + ((EntriesPerHashTable + 1) * _blocksPerHashTable));

        _totalBlockCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(TotalBlockCountOffset));
        _freeBlockCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(FreeBlockCountOffset));
        _fileTableBlockCount = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(FileTableBlockCountOffset));
        _fileTableBlockNumber = ReadUInt24LittleEndian(header, FileTableBlockNumberOffset);

        if (_totalBlockCount == 0 || _totalBlockCount > EndOfChain)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' has an invalid block count ({_totalBlockCount}).");
        }

        if (_fileTableBlockCount == 0 || _fileTableBlockCount > _totalBlockCount ||
            _fileTableBlockNumber >= _totalBlockCount)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' has an invalid file table location.");
        }

        uint dataFileCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(DataFileCountOffset));
        if (dataFileCount > 1)
        {
            throw new InvalidDataException(
                $"STFS package '{packagePath}' is a multi-file container; use the GOD/SVOD reader.");
        }

        _topLevel = _totalBlockCount <= EntriesPerHashTable
            ? 0
            : _totalBlockCount <= BlocksPerHashLevel1
                ? 1
                : 2;

        _entries = ReadEntries();
        _entriesView = _entries.AsReadOnly();
        _byPath = new Dictionary<string, StfsEntry>(_entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (StfsEntry entry in _entries)
        {
            if (!_byPath.TryAdd(entry.Path, entry))
            {
                throw new InvalidDataException(
                    $"STFS package '{packagePath}' contains duplicate entry '{entry.Path}'.");
            }
        }

        uint contentType = XContentHeaderReader.ReadContentType(header);
        StfsPackageKind kind = contentType == XContentHeaderReader.ContentTypeXbla
            ? StfsPackageKind.Xbla
            : StfsPackageKind.Xblig;
        uint titleId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(TitleIdOffset));
        string titleName = XContentHeaderReader.SanitizeTitleName(
            XContentHeaderReader.DecodeTitleName(header.AsSpan(TitleNameOffset, TitleNameLength)));
        if (titleName.Length == 0)
        {
            titleName = titleId.ToString("X8", CultureInfo.InvariantCulture);
        }

        int fileCount = 0;
        int directoryCount = 0;
        long totalFileSize = 0;
        foreach (StfsEntry entry in _entries)
        {
            if (entry.IsDirectory)
            {
                directoryCount++;
            }
            else
            {
                fileCount++;
                totalFileSize += entry.Size;
            }
        }

        Info = new StfsPackageInfo(
            _packagePath,
            kind,
            titleName,
            titleId,
            $"0x{titleId:X8}",
            header[DiscNumberOffset],
            header[DiscCountOffset],
            contentType,
            stream.Length,
            headerSize,
            _firstBlockOffset,
            _totalBlockCount,
            _freeBlockCount,
            _fileTableBlockCount,
            _fileTableBlockNumber,
            _readOnlyFormat,
            fileCount,
            directoryCount,
            totalFileSize);
    }

    /// <summary>Package metadata and STFS volume layout.</summary>
    public StfsPackageInfo Info { get; }

    /// <summary>Every file and directory in the package, in file-table order.</summary>
    public IReadOnlyList<StfsEntry> Entries => _entriesView;

    /// <summary>
    /// Returns true when <paramref name="path"/> is a readable single-file STFS
    /// package with an XBLA or XBLIG content type. This is a cheap header probe;
    /// use <see cref="Open"/> for full validation.
    /// </summary>
    /// <param name="path">Path to the package file.</param>
    public static bool IsStfsPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0x400, FileOptions.RandomAccess);
            if (stream.Length < VolumeTypeOffset + sizeof(uint))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[VolumeTypeOffset + sizeof(uint)];
            stream.ReadExactly(header);
            return IsStfsHeader(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Opens an XBLA/XBLIG STFS package for reading.</summary>
    /// <param name="path">Path to the package file.</param>
    /// <param name="share">File sharing mode for the package file.</param>
    /// <returns>An open, disposable package reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The package does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid XBLA/XBLIG STFS package.</exception>
    /// <exception cref="UnauthorizedAccessException">The package cannot be opened.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public static StfsPackage Open(string path, FileShare share = FileShare.Read)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = share,
                BufferSize = 1 << 16,
                Options = FileOptions.RandomAccess,
            });

        try
        {
            return new StfsPackage(stream, path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to open an XBLA/XBLIG STFS package. Returns <c>false</c> for a
    /// missing, unreadable or invalid package instead of throwing.
    /// </summary>
    /// <param name="path">Path to the package file.</param>
    /// <param name="package">The open package when the method returns <c>true</c>.</param>
    public static bool TryOpen(string path, [NotNullWhen(true)] out StfsPackage? package)
    {
        package = null;
        try
        {
            package = Open(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds an entry by relative path. Accepts <c>/</c> or <c>\</c> separators,
    /// ignores a leading separator, and compares case-insensitively (the Xbox
    /// 360 file system is case-insensitive).
    /// </summary>
    /// <param name="path">Relative path inside the package.</param>
    /// <returns>The matching entry, or <c>null</c> when it does not exist.</returns>
    public StfsEntry? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        return _byPath.TryGetValue(normalized, out StfsEntry? entry) ? entry : null;
    }

    /// <summary>
    /// Reads a file into a new byte array. Files larger than
    /// <see cref="int.MaxValue"/> are rejected; use <see cref="CopyTo(StfsEntry,Stream)"/>
    /// for large files.
    /// </summary>
    /// <param name="entry">A file entry returned by <see cref="Entries"/> or <see cref="Find"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The entry is a directory or does not belong to this package.</exception>
    /// <exception cref="IOException">The file is too large or its data is truncated.</exception>
    /// <exception cref="ObjectDisposedException">The package has been disposed.</exception>
    public byte[] ReadAllBytes(StfsEntry entry)
    {
        StfsEntry resolved = ResolveEntry(entry);
        if (resolved.IsDirectory)
        {
            throw new ArgumentException($"'{resolved.Path}' is a directory.", nameof(entry));
        }

        if (resolved.Size > int.MaxValue)
        {
            throw new IOException(
                $"STFS file '{resolved.Path}' is too large for ReadAllBytes ({resolved.Size} bytes).");
        }

        byte[] data = new byte[resolved.Size];
        using MemoryStream destination = new(data);
        CopyTo(resolved, destination);
        return data;
    }

    /// <summary>
    /// Copies one file's data to <paramref name="destination"/> without loading
    /// the whole file into memory.
    /// </summary>
    /// <param name="entry">A file entry returned by <see cref="Entries"/> or <see cref="Find"/>.</param>
    /// <param name="destination">Writable destination stream.</param>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The entry is a directory or does not belong to this package.</exception>
    /// <exception cref="InvalidDataException">The file's block chain or data is truncated.</exception>
    /// <exception cref="ObjectDisposedException">The package has been disposed.</exception>
    public void CopyTo(StfsEntry entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        StfsEntry resolved = ResolveEntry(entry);
        if (resolved.IsDirectory)
        {
            throw new ArgumentException($"'{resolved.Path}' is a directory.", nameof(entry));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReadFileChunksCore(resolved, (buffer, offset, count) => destination.Write(buffer, offset, count));
        }
    }

    /// <summary>
    /// Extracts every file and directory into <paramref name="outputDirectory"/>.
    /// Unsafe entry names are rejected before anything is written outside the
    /// destination.
    /// </summary>
    /// <param name="outputDirectory">Destination directory (created when missing).</param>
    /// <param name="skipExisting">Skip files that already exist instead of overwriting them.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files written.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe or its data is truncated.</exception>
    /// <exception cref="IOException">A destination file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public int ExtractTo(string outputDirectory, bool skipExisting = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        ExtractTo(outputDirectory, ExtractNamePolicy.Reject, skipExisting, progress, ct);

    /// <summary>
    /// Extracts every file and directory into <paramref name="outputDirectory"/>
    /// using <paramref name="namePolicy"/> for entry names that are unsafe on
    /// the host (path separators, drive-relative colons, trailing dots/spaces,
    /// DOS device names).
    /// </summary>
    /// <param name="outputDirectory">Destination directory (created when missing).</param>
    /// <param name="namePolicy">
    /// <see cref="ExtractNamePolicy.Reject"/> (fail fast),
    /// <see cref="ExtractNamePolicy.Sanitize"/> (replace unsafe characters and
    /// disambiguate collisions) or <see cref="ExtractNamePolicy.Skip"/> (omit
    /// the entry and a skipped directory's subtree).
    /// </param>
    /// <param name="skipExisting">Skip files that already exist instead of overwriting them.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files written.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe or its data is truncated.</exception>
    /// <exception cref="IOException">A destination file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public int ExtractTo(string outputDirectory, ExtractNamePolicy namePolicy,
        bool skipExisting = false, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);
        string root = Path.GetFullPath(outputDirectory);

        // Entries are parent-first (the file table validates parent < child),
        // so a parent's resolved path is always available before its children.
        Dictionary<string, string> resolvedPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = string.Empty,
        };
        Dictionary<string, HashSet<string>> usedNamesByDirectory = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> skippedDirectories = new(StringComparer.OrdinalIgnoreCase);

        int written = 0;
        foreach (StfsEntry entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            string parentOriginal = GetParentPath(entry.Path);
            if (skippedDirectories.Contains(parentOriginal))
            {
                if (entry.IsDirectory)
                {
                    skippedDirectories.Add(entry.Path);
                }

                continue;
            }

            if (!resolvedPaths.TryGetValue(parentOriginal, out string? parentResolved))
            {
                throw new InvalidDataException(
                    $"STFS entry '{entry.Path}' has no resolved parent directory.");
            }

            if (!usedNamesByDirectory.TryGetValue(parentOriginal, out HashSet<string>? usedNames))
            {
                usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedNamesByDirectory[parentOriginal] = usedNames;
            }

            bool accepted;
            string resolvedName;
            bool renamed;
            try
            {
                accepted = XisoEntryNames.TryResolveExtractionName(entry.Name, namePolicy, usedNames,
                    out resolvedName, out renamed);
            }
            catch (XisoFormatException ex)
            {
                // STFS callers have always seen InvalidDataException for an
                // unsafe entry name; keep that public contract.
                throw new InvalidDataException(ex.Message, ex);
            }

            if (!accepted)
            {
                if (entry.IsDirectory)
                {
                    skippedDirectories.Add(entry.Path);
                }

                Logger.LogErr($"warning: skipped unsafe STFS entry '{entry.Path}'\n");
                continue;
            }

            string relative = parentResolved.Length == 0
                ? resolvedName
                : parentResolved + "/" + resolvedName;
            if (renamed)
            {
                Logger.LogErr($"warning: renamed unsafe STFS entry '{entry.Path}' to '{relative}'\n");
            }

            resolvedPaths[entry.Path] = relative;
            string destination = Path.GetFullPath(Path.Combine(
                root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string relativeCheck = Path.GetRelativePath(root, destination);
            if (Path.IsPathRooted(relativeCheck) ||
                string.Equals(relativeCheck, "..", StringComparison.Ordinal) ||
                relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"STFS entry '{entry.Path}' escapes the extraction directory.");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                progress?.Report(new ProgressInfo(
                    ProgressInfoType.DirAdded,
                    Path: "/" + relative,
                    Sector: entry.StartBlock));
                continue;
            }

            if (skipExisting && File.Exists(destination))
            {
                continue;
            }

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                using FileStream target = new(
                    destination, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 20, FileOptions.SequentialScan);
                target.SetLength(entry.Size);
                CopyTo(entry, target);
            }
            catch
            {
                TryDeleteFile(destination);
                throw;
            }

            if (entry.Modified is { } modified)
            {
                TrySetLastWriteTime(destination, modified);
            }

            written++;
            progress?.Report(new ProgressInfo(
                ProgressInfoType.FileAdded,
                Path: "/" + relative,
                Sector: entry.StartBlock,
                Size: entry.Size));
        }

        return written;
    }

    /// <summary>Returns the parent portion of an STFS path (<c>""</c> at the root).</summary>
    private static string GetParentPath(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hashTableCache.Clear();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Streams one file's bytes to an internal sink. Used by the checksum
    /// walker so large files are never buffered in full.
    /// </summary>
    internal void ReadFileChunks(StfsEntry entry, Action<byte[], int, int> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        StfsEntry resolved = ResolveEntry(entry);
        if (resolved.IsDirectory)
        {
            throw new ArgumentException($"'{resolved.Path}' is a directory.", nameof(entry));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReadFileChunksCore(resolved, sink);
        }
    }

    private void ReadFileChunksCore(StfsEntry entry, Action<byte[], int, int> sink)
    {
        long remaining = entry.Size;
        if (remaining == 0)
        {
            return;
        }

        byte[] buffer = new byte[BlockSize];
        uint block = entry.StartBlock;
        uint blockCount = entry.BlockCount;
        for (uint index = 0; index < blockCount && remaining > 0; index++)
        {
            if (block >= _totalBlockCount)
            {
                throw new InvalidDataException(
                    $"STFS file '{entry.Path}' references block {block} outside the package.");
            }

            int toRead = (int)Math.Min(BlockSize, remaining);
            ReadBlockBytes(block, buffer.AsSpan(0, toRead));
            sink(buffer, 0, toRead);
            remaining -= toRead;

            if (index + 1 < blockCount)
            {
                block = NextBlock(block);
                if (block == EndOfChain)
                {
                    throw new InvalidDataException(
                        $"STFS file '{entry.Path}' ends before its declared size.");
                }
            }
        }

        if (remaining != 0)
        {
            throw new InvalidDataException(
                $"STFS file '{entry.Path}' is truncated ({remaining} bytes missing).");
        }
    }

    private StfsEntry ResolveEntry(StfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StfsEntry? known = Find(entry.Path);
        return known ?? throw new ArgumentException(
            $"Entry '{entry.Path}' is not part of STFS package '{_packagePath}'.", nameof(entry));
    }

    private List<StfsEntry> ReadEntries()
    {
        List<RawEntry> raw = new();
        uint block = _fileTableBlockNumber;
        bool terminated = false;

        for (int blockIndex = 0; blockIndex < _fileTableBlockCount; blockIndex++)
        {
            byte[] table = ReadBlockBytes(BlockToOffset(block));
            for (int entryIndex = 0; entryIndex < EntriesPerBlock; entryIndex++)
            {
                int offset = entryIndex * DirectoryEntrySize;
                byte flags = table[offset + 0x28];
                int nameLength = flags & 0x3F;
                if (nameLength == 0)
                {
                    terminated = true;
                    break;
                }

                if (nameLength > 0x28)
                {
                    throw new InvalidDataException(
                        $"STFS file table entry has an invalid name length ({nameLength}).");
                }

                string name = Encoding.Latin1.GetString(table.AsSpan(offset, nameLength));
                if (name.IndexOf('\0') >= 0 ||
                    name is "." or ".." ||
                    XisoEntryNames.IsStructurallyInvalidEntryName(name))
                {
                    throw new InvalidDataException($"Unsafe STFS entry name: '{name}'.");
                }

                uint validBlocks = ReadUInt24LittleEndian(table, offset + 0x29);
                uint allocatedBlocks = ReadUInt24LittleEndian(table, offset + 0x2C);
                uint startBlock = ReadUInt24LittleEndian(table, offset + 0x2F);
                ushort parentIndex = BinaryPrimitives.ReadUInt16BigEndian(
                    table.AsSpan(offset + 0x32));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset + 0x34));
                ushort createDate = BinaryPrimitives.ReadUInt16BigEndian(
                    table.AsSpan(offset + 0x38));
                ushort createTime = BinaryPrimitives.ReadUInt16BigEndian(
                    table.AsSpan(offset + 0x3A));
                ushort modifiedDate = BinaryPrimitives.ReadUInt16BigEndian(
                    table.AsSpan(offset + 0x3C));
                ushort modifiedTime = BinaryPrimitives.ReadUInt16BigEndian(
                    table.AsSpan(offset + 0x3E));

                bool isDirectory = (flags & 0x80) != 0;
                bool contiguous = (flags & 0x40) != 0;
                uint blockCount = isDirectory ? 0 : Math.Max(validBlocks, allocatedBlocks);
                if (!isDirectory && size > 0)
                {
                    uint requiredBlocks = (uint)(((ulong)size + BlockSize - 1) / BlockSize);
                    if (blockCount == 0)
                    {
                        blockCount = requiredBlocks;
                    }

                    if (blockCount < requiredBlocks)
                    {
                        throw new InvalidDataException(
                            $"STFS file '{name}' declares {size} bytes in {blockCount} blocks.");
                    }
                }

                if (!isDirectory && blockCount > 0)
                {
                    if (startBlock >= _totalBlockCount ||
                        (ulong)startBlock + blockCount > _totalBlockCount)
                    {
                        throw new InvalidDataException(
                            $"STFS file '{name}' points outside the package block range.");
                    }
                }

                raw.Add(new RawEntry(
                    name,
                    isDirectory,
                    size,
                    startBlock,
                    blockCount,
                    contiguous,
                    parentIndex,
                    DecodeFatTimestamp(createDate, createTime),
                    DecodeFatTimestamp(modifiedDate, modifiedTime)));

                if (raw.Count > MaxEntries)
                {
                    throw new InvalidDataException(
                        $"STFS package has more than {MaxEntries} file table entries.");
                }
            }

            if (terminated || blockIndex + 1 >= _fileTableBlockCount)
            {
                break;
            }

            block = NextBlock(block);
            if (block == EndOfChain || block >= _totalBlockCount)
            {
                throw new InvalidDataException(
                    "STFS file table block chain ends before the declared block count.");
            }
        }

        List<StfsEntry> entries = new(raw.Count);
        string[] paths = new string[raw.Count];
        for (int index = 0; index < raw.Count; index++)
        {
            RawEntry entry = raw[index];
            if (entry.ParentIndex != 0xFFFF && entry.ParentIndex >= index)
            {
                throw new InvalidDataException(
                    $"STFS entry '{entry.Name}' has an invalid parent index ({entry.ParentIndex}).");
            }

            if (entry.ParentIndex != 0xFFFF && !raw[entry.ParentIndex].IsDirectory)
            {
                throw new InvalidDataException(
                    $"STFS entry '{entry.Name}' has a non-directory parent.");
            }

            int depth = 0;
            for (int parent = entry.ParentIndex; parent != 0xFFFF; parent = raw[parent].ParentIndex)
            {
                if (++depth > Constants.MaxTocDepth)
                {
                    throw new InvalidDataException(
                        $"STFS directory depth exceeds {Constants.MaxTocDepth} at '{entry.Name}'.");
                }
            }

            string path = entry.ParentIndex == 0xFFFF
                ? entry.Name
                : paths[entry.ParentIndex] + "/" + entry.Name;
            paths[index] = path;
            entries.Add(new StfsEntry(
                path,
                entry.Name,
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Size,
                entry.StartBlock,
                entry.BlockCount,
                entry.IsContiguous,
                entry.Created,
                entry.Modified));
        }

        return entries;
    }

    private static bool IsStfsHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < VolumeTypeOffset + sizeof(uint) || !XContentHeaderReader.IsXContent(header))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(header[VolumeTypeOffset..]) != VolumeTypeStfs)
        {
            return false;
        }

        uint contentType = XContentHeaderReader.ReadContentType(header);
        return contentType is XContentHeaderReader.ContentTypeXbla or
            XContentHeaderReader.ContentTypeXblig or
            XContentHeaderReader.ContentTypeCommunityGame;
    }

    private long BlockToOffset(uint block)
    {
        ulong baseSize = EntriesPerHashTable;
        ulong mapped = block;
        for (int level = 0; level < 3; level++)
        {
            mapped += ((block + baseSize) / baseSize) * (ulong)_blocksPerHashTable;
            if (block < baseSize)
            {
                break;
            }

            baseSize *= EntriesPerHashTable;
        }

        long offset = _firstBlockOffset + checked((long)mapped * BlockSize);
        if (offset < 0 || offset > _stream.Length - BlockSize)
        {
            throw new InvalidDataException($"STFS block {block} points outside the package.");
        }

        return offset;
    }

    private uint NextBlock(uint block)
    {
        if (block >= _totalBlockCount)
        {
            throw new InvalidDataException($"STFS block {block} is outside the package block range.");
        }

        long tableOffset = HashTableOffset(block, 0);
        byte[] table = GetHashTable(tableOffset);
        int index = (int)(block % EntriesPerHashTable);
        uint info = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan((index * HashEntrySize) + 0x14));
        return info & EndOfChain;
    }

    private long HashTableOffset(uint block, int level)
    {
        uint blockNumber = HashTableBlockNumber(block, level);
        long baseOffset = _firstBlockOffset + ((long)blockNumber * BlockSize);
        if (baseOffset < 0 || baseOffset > _stream.Length - BlockSize)
        {
            throw new InvalidDataException(
                $"STFS hash table for block {block} (level {level}) points outside the package.");
        }

        long offset;
        if (_readOnlyFormat || level >= _topLevel)
        {
            offset = baseOffset + (_rootActiveIndex ? BlockSize : 0);
        }
        else
        {
            long parentOffset = HashTableOffset(block, level + 1);
            int parentIndex = level switch
            {
                0 => (int)((block / EntriesPerHashTable) % EntriesPerHashTable),
                1 => (int)((block / BlocksPerHashLevel1) % EntriesPerHashTable),
                _ => 0,
            };
            uint parentInfo = BinaryPrimitives.ReadUInt32BigEndian(
                ReadHashTableEntry(parentOffset, parentIndex).AsSpan(0x14));
            offset = baseOffset + ((parentInfo & 0x40000000) != 0 ? BlockSize : 0);
        }

        if (offset > _stream.Length - BlockSize)
        {
            throw new InvalidDataException(
                $"STFS hash table for block {block} (level {level}) points outside the package.");
        }

        return offset;
    }

    private uint HashTableBlockNumber(uint block, int level)
    {
        if (level == 0)
        {
            if (block < EntriesPerHashTable)
            {
                return 0;
            }

            uint number = ((block / EntriesPerHashTable) * _blockStep0) +
                          (((block / BlocksPerHashLevel1) + 1) * (uint)_blocksPerHashTable);
            if (block >= BlocksPerHashLevel1)
            {
                number += (uint)_blocksPerHashTable;
            }

            return number;
        }

        if (level == 1)
        {
            return block < BlocksPerHashLevel1
                ? _blockStep0
                : ((block / BlocksPerHashLevel1) * _blockStep1) + (uint)_blocksPerHashTable;
        }

        return _blockStep1;
    }

    private byte[] ReadHashTableEntry(long tableOffset, int index)
    {
        byte[] table = GetHashTable(tableOffset);
        return table.AsSpan(index * HashEntrySize, HashEntrySize).ToArray();
    }

    private byte[] GetHashTable(long offset)
    {
        if (_hashTableCache.TryGetValue(offset, out byte[]? cached))
        {
            return cached;
        }

        byte[] table = ReadBlockBytes(offset);
        if (_hashTableCache.Count >= MaxHashTableCacheEntries)
        {
            _hashTableCache.Clear();
        }

        _hashTableCache[offset] = table;
        return table;
    }

    private byte[] ReadBlockBytes(long offset)
    {
        byte[] buffer = new byte[BlockSize];
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private void ReadBlockBytes(uint block, Span<byte> destination)
    {
        _stream.Position = BlockToOffset(block);
        _stream.ReadExactly(destination);
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16));

    private static DateTimeOffset? DecodeFatTimestamp(ushort date, ushort time)
    {
        if (date == 0 && time == 0)
        {
            return null;
        }

        int year = ((date & 0xFE00) >> 9) + 1980;
        int month = (date & 0x01E0) >> 5;
        int day = date & 0x001F;
        int hour = (time & 0xF800) >> 11;
        int minute = (time & 0x07E0) >> 5;
        int second = (time & 0x001F) << 1;
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: keep the original extraction failure.
        }
    }

    private static void TrySetLastWriteTime(string path, DateTimeOffset modified)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            // Timestamps are metadata; a filesystem that rejects them must not
            // fail an otherwise successful extraction.
        }
    }

    private sealed record RawEntry(
        string Name,
        bool IsDirectory,
        uint Size,
        uint StartBlock,
        uint BlockCount,
        bool IsContiguous,
        ushort ParentIndex,
        DateTimeOffset? Created,
        DateTimeOffset? Modified);
}
