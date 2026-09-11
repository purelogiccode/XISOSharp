using ZArchiveSharp;
using ZArchiveSharp.Pipeline;

namespace XISOSharp;

/// <summary>
/// XISO → ZArchive (<c>.zar</c>) conversion — the compressed single-file layout
/// Xenia canary loads for Xbox dumps. Mirrors the ZarManager pipeline
/// (<c>../CSharp_ZARSharp/References/ZarManager-1.2.0/core.py</c>: extract ISO, pack the tree with
/// <c>zarchive.exe</c>), but streams file bytes straight from the image into
/// <see cref="ZArchiveSharp.ZArchiveWriter"/> with no intermediate directory.
/// Every 64 KiB block is compressed with the pure-C# zstd encoder (level 6 by
/// default); incompressible blocks are stored raw, which is valid per spec.
/// Output opens in <c>zarchive.exe</c> and vice versa.
/// </summary>
public static class XisoZarchive
{
    /// <summary>
    /// ZAR name-table tree node built from the XISO directory walk before streaming file data.
    /// </summary>
    private sealed class PathNode
    {
        /// <summary>Child nodes sorted case-insensitively by name.</summary>
        public readonly List<PathNode> Subnodes = [];

        /// <summary>Whether this node is a file (<c>false</c> for directories).</summary>
        public bool IsFile;

        /// <summary>Index into the shared ZAR name table.</summary>
        public int NameIndex;

        /// <summary>Partition-relative byte offset of the file data within the image.</summary>
        public long SourceOffset;

        /// <summary>File size in bytes.</summary>
        public ulong FileSize;
    }

    private static int GetOrAddName(List<string> names, Dictionary<string, int> lookup, string name)
    {
        if (lookup.TryGetValue(name, out int idx)) return idx;
        idx = names.Count;
        names.Add(name);
        lookup[name] = idx;
        return idx;
    }

    private static int CompareNodeName(string n1, string n2)
    {
        int min = Math.Min(n1.Length, n2.Length);
        for (int i = 0; i < min; i++)
        {
            char c1 = n1[i], c2 = n2[i];
            if (c1 >= 'A' && c1 <= 'Z') c1 = (char)(c1 + 32);
            if (c2 >= 'A' && c2 <= 'Z') c2 = (char)(c2 + 32);
            if (c1 != c2) return (byte)c1 - (byte)c2;
        }

        return n1.Length.CompareTo(n2.Length);
    }

    private static ushort ReadUShort(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[2];
        int total = 0;
        while (total < 2)
        {
            int n = fs.Read(buf[total..]);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static uint ReadUInt(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[4];
        int total = 0;
        while (total < 4)
        {
            int n = fs.Read(buf[total..]);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    /// <summary>Creates a ZArchive from an XISO file.</summary>
    /// <param name="isoPath">Path to the source XISO file.</param>
    /// <param name="zarPath">Destination ZAR path, or <c>null</c> to derive from <paramref name="isoPath"/>.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="compressor">
    /// Block compressor, or <c>null</c> for the default zstd level 6.
    /// Pass <c>new ZarRawCompressor()</c> to store blocks raw.
    /// </param>
    /// <param name="progress">Optional pack progress sink.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool CreateZar(string isoPath, string? zarPath = null, long isoOffset = 0, bool quiet = false,
        CancellationToken ct = default, IZarBlockCompressor? compressor = null,
        IProgress<ZarProgress>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        string outZar = zarPath ?? DeriveZarPath(isoPath);
        using FileStream isoFs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return CreateZar(isoFs, isoOffset, outZar, false, quiet, ct, compressor, progress);
    }

    private static string DeriveZarPath(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? "";
        string full = Path.GetFileName(input) ?? "archive";
        if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".redump.iso".Length];
        else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".video.iso".Length];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".iso".Length];
        else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) full = full[..^".xiso".Length];
        return Path.Combine(dir, $"{full}.zar");
    }

    /// <summary>Creates a ZArchive from an open XISO stream.</summary>
    /// <param name="isoFs">Open ISO stream positioned at the start of the file.</param>
    /// <param name="xisoOffset">Byte offset of the XISO partition.</param>
    /// <param name="zarPath">Destination ZAR path.</param>
    /// <param name="removeUpdate">When <c>true</c>, excludes the $SystemUpdate directory.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="compressor">
    /// Block compressor, or <c>null</c> for the default zstd level 6.
    /// Pass <c>new ZarRawCompressor()</c> to store blocks raw.
    /// </param>
    /// <param name="progress">Optional pack progress sink.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool CreateZar(FileStream isoFs, long xisoOffset, string zarPath, bool removeUpdate, bool quiet,
        CancellationToken ct = default, IZarBlockCompressor? compressor = null,
        IProgress<ZarProgress>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        long headerOffset = xisoOffset + Constants.HeaderOffset;
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        uint rootOffset = ReadUInt(isoFs);
        uint rootSize = ReadUInt(isoFs);

        ParseXdvdfs(isoFs, xisoOffset, (long)rootOffset * Constants.SectorSize, rootSize, removeUpdate,
            out PathNode rootNode, out List<string> names);

        if (!quiet) Logger.Log($"[INFO] Writing ZArchive to {zarPath}\n");
        try
        {
            // The XISO walk order is the pack order (directories before
            // children, siblings case-insensitively sorted); the shared
            // engine streams it exactly like WriteNode did.
            XisoPackSource source = new(isoFs, xisoOffset, rootNode, names, $"XISO:{xisoOffset}");
            ZarPipelineOptions options = new()
            {
                Compressor = compressor,
                // Historical behavior: the destination is truncated.
                CollisionPolicy = ZarCollisionPolicy.Overwrite,
                // XboxKit writes the name table in btree discovery order (its
                // names list), not pack order — seed the writer with our
                // discovery list so the archives match byte-for-byte.
                NameOrder = names,
            };
            ZarPipeline.PackSource(source, zarPath, options, progress, ct);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (!quiet) Logger.LogErr($"[ERROR] {ex.Message}\n");
            DeleteIncomplete(zarPath);
            return false;
        }
        catch
        {
            DeleteIncomplete(zarPath);
            throw;
        }

        static void DeleteIncomplete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static void ParseXdvdfs(FileStream isoFs, long isoOffset, long dirOffset, uint dirSize, bool removeUpdate,
        out PathNode rootNode, out List<string> names)
    {
        List<string> nameList = new();
        // XboxKit dedups names case-sensitively (default Dictionary); mirror it
        // so case-differing duplicates stay separate name-table entries.
        Dictionary<string, int> lookup = new(StringComparer.Ordinal);
        rootNode = new PathNode();
        ParseNode(isoFs, isoOffset, dirOffset, dirSize, 0, rootNode, nameList, lookup);
        if (removeUpdate)
        {
            rootNode.Subnodes.RemoveAll(n =>
                !n.IsFile && string.Equals(nameList[n.NameIndex], "$SystemUpdate", StringComparison.OrdinalIgnoreCase));
        }

        rootNode.Subnodes.Sort((a, b) => CompareNodeName(nameList[a.NameIndex], nameList[b.NameIndex]));
        names = nameList;
    }

    private static void ParseNode(FileStream isoFs, long isoOffset, long dirOffset, uint dirSize, long childOffset,
        PathNode parent, List<string> names, Dictionary<string, int> lookup,
        HashSet<long>? visited = null,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        if (childOffset >= dirSize) return;
        // Hardening (#16): bound the walk — a corrupt cycle previously recursed
        // until the stack overflowed instead of failing with a named error.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry: maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        visited ??= [];
        if (!visited.Add(childOffset))
        {
            throw new XisoFormatException(
                $"invalid TOC entry: directory cycle detected — table offset {childOffset} was already visited.");
        }

        if (visited.Count > Constants.MaxTocEntriesPerTable)
        {
            throw new XisoFormatException(
                "invalid TOC entry: too many entries in one directory table (possible corrupt offset chain).");
        }

        long pos = isoOffset + dirOffset + childOffset;
        isoFs.Seek(pos, SeekOrigin.Begin);
        ushort left = ReadUShort(isoFs);
        if (childOffset == 0 && IsEmptyTable(isoFs, left)) return;
        ushort right = ReadUShort(isoFs);
        uint entrySector = ReadUInt(isoFs);
        uint entrySize = ReadUInt(isoFs);
        byte attrs = (byte)isoFs.ReadByte();
        byte nameLen = (byte)isoFs.ReadByte();
        byte[] nameBytes = new byte[nameLen];
        if (nameLen > 0)
        {
            int read = 0;
            while (read < nameLen)
            {
                int n = isoFs.Read(nameBytes, read, nameLen - read);
                if (n == 0) return;
                read += n;
            }
        }

        // Xbox names are WINDOWS_1252 bytes; decode via Latin1 like every other
        // reader path (ASCII would corrupt bytes >= 0x80 into '?').
        string name = Latin1Encoding.Instance.GetString(nameBytes);
        bool isDir = (attrs & 0x10) != 0;
        long entryOffset = (long)entrySector * Constants.SectorSize;

        if (left != 0 && left != 0xFFFF)
        {
            ParseNode(isoFs, isoOffset, dirOffset, dirSize, (long)left * 4, parent, names, lookup, visited,
                depth + 1);
        }

        int nameIdx = GetOrAddName(names, lookup, name);
        PathNode node = new() { IsFile = !isDir, NameIndex = nameIdx };
        if (isDir)
        {
            ParseNode(isoFs, isoOffset, entryOffset, entrySize, 0, node, names, lookup, null, depth + 1);
            node.Subnodes.Sort((a, b) => CompareNodeName(names[a.NameIndex], names[b.NameIndex]));
        }
        else
        {
            node.SourceOffset = entryOffset;
            node.FileSize = entrySize;
        }

        parent.Subnodes.Add(node);
        if (right != 0 && right != 0xFFFF)
        {
            ParseNode(isoFs, isoOffset, dirOffset, dirSize, (long)right * 4, parent, names, lookup, visited,
                depth + 1);
        }
    }

    /// <summary>
    /// Empty directory tables are filled with 0xFF (or 0x00) — the first entry's
    /// left offset is the giveaway. Mirrors the <c>XisoReader</c> traversal guard:
    /// 0xFFFF at table start is empty; 0x0000 needs the following 12 bytes to be
    /// all zero to distinguish it from a valid entry with no left child.
    /// The stream is positioned just after <paramref name="left"/> on entry and exit.
    /// </summary>
    private static bool IsEmptyTable(FileStream isoFs, ushort left)
    {
        if (left == Constants.PadShort) return true;
        if (left != Constants.EmptyDirectorySentinel) return false;
        Span<byte> peek = stackalloc byte[12];
        int total = 0;
        while (total < 12)
        {
            int n = isoFs.Read(peek[total..]);
            if (n == 0) break;
            total += n;
        }

        isoFs.Seek(-total, SeekOrigin.Current);
        return total == 12 && peek.IndexOfAnyExcept((byte)0) < 0;
    }

    /// <summary>
    /// <see cref="IZarPackSource"/> over the parsed XDVDFS walk: entries come
    /// out in the old <c>WriteNode</c> order (directories before children), so
    /// the shared engine emits the identical call sequence and identical bytes.
    /// </summary>
    private sealed class XisoPackSource(
        FileStream isoFs,
        long xisoOffset,
        PathNode root,
        List<string> names,
        string displayPath) : IZarPackSource
    {
        private readonly PathNode _root = root;
        private readonly List<string> _names = names;
        private readonly long _xisoOffset = xisoOffset;
        private readonly FileStream _isoFs = isoFs;
        public string DisplayPath { get; } = displayPath;

        public IReadOnlyList<ZarPackEntry> Collect(CancellationToken cancellationToken = default)
        {
            List<ZarPackEntry> entries = new();
            Walk(_root, string.Empty, entries, cancellationToken);
            return entries;
        }

        private void Walk(PathNode dir, string path, List<ZarPackEntry> entries, CancellationToken ct)
        {
            foreach (PathNode child in dir.Subnodes)
            {
                ct.ThrowIfCancellationRequested();
                string childPath = path.Length == 0 ? _names[child.NameIndex] : path + "/" + _names[child.NameIndex];
                if (!child.IsFile)
                {
                    entries.Add(new ZarPackEntry { RelativePath = childPath, IsDirectory = true });
                    Walk(child, childPath, entries, ct);
                    continue;
                }

                long offset = _xisoOffset + child.SourceOffset;
                long size = (long)child.FileSize;
                entries.Add(new ZarPackEntry
                {
                    RelativePath = childPath,
                    IsDirectory = false,
                    Length = size,
                    OpenRead = () => new XisoSliceStream(_isoFs, offset, size, childPath),
                });
            }
        }
    }

    /// <summary>
    /// Read-only slice of the open image stream. Seeks the shared base
    /// stream on every read (consumed sequentially) and never closes it.
    /// A short base stream surfaces as <see cref="InvalidOperationException"/>
    /// (the old <c>Truncated file data</c> failure); real I/O faults
    /// propagate like before.
    /// </summary>
    private sealed class XisoSliceStream(FileStream baseStream, long offset, long length, string path) : Stream
    {
        private long _position;
        private readonly FileStream _baseStream = baseStream;
        private readonly long _offset = offset;
        private readonly string _path = path;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n;
            lock (_baseStream)
            {
                _baseStream.Seek(_offset + _position, SeekOrigin.Begin);
                n = _baseStream.Read(buffer[..toRead]);
            }

            if (n == 0)
            {
                throw new InvalidOperationException($"Truncated file data for {_path}");
            }

            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The image stream stays open; the owner disposes it.
        }
    }
}
