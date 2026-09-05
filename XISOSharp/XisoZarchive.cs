using System.Text;
using ZARSharp;
using ZARSharp.Pipeline;

namespace XISOSharp;

/// <summary>
/// XISO → ZArchive (<c>.zar</c>) conversion — the compressed single-file layout
/// Xenia canary loads for Xbox dumps. Mirrors the ZarManager pipeline
/// (<c>References/ZarManager-1.2.0/core.py</c>: extract ISO, pack the tree with
/// <c>zarchive.exe</c>), but streams file bytes straight from the image into
/// <see cref="ZARSharp.ZArchiveWriter"/> with no intermediate directory.
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
        if (lookup.TryGetValue(name, out var idx)) return idx;
        idx = names.Count;
        names.Add(name);
        lookup[name] = idx;
        return idx;
    }

    private static int CompareNodeName(string n1, string n2)
    {
        var min = Math.Min(n1.Length, n2.Length);
        for (var i = 0; i < min; i++)
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
        var total = 0;
        while (total < 2)
        {
            var n = fs.Read(buf[total..]);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static uint ReadUInt(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[4];
        var total = 0;
        while (total < 4)
        {
            var n = fs.Read(buf[total..]);
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
        var outZar = zarPath ?? DeriveZarPath(isoPath);
        using var isoFs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return CreateZar(isoFs, isoOffset, outZar, false, quiet, ct, compressor, progress);
    }

    private static string DeriveZarPath(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? "";
        var full = Path.GetFileName(input) ?? "archive";
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
        var headerOffset = xisoOffset + Constants.HeaderOffset;
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = ReadUInt(isoFs);
        var rootSize = ReadUInt(isoFs);

        ParseXdvdfs(isoFs, xisoOffset, (long)rootOffset * Constants.SectorSize, rootSize, removeUpdate,
            out var rootNode, out var names);

        if (!quiet) Logger.Log($"[INFO] Writing ZArchive to {zarPath}\n");
        try
        {
            // The XISO walk order is the pack order (directories before
            // children, siblings case-insensitively sorted); the shared
            // engine streams it exactly like WriteNode did.
            var source = new XisoPackSource(isoFs, xisoOffset, rootNode, names, $"XISO:{xisoOffset}");
            var options = new ZarPipelineOptions
            {
                Compressor = compressor,
                // Historical behavior: the destination is truncated.
                CollisionPolicy = ZarCollisionPolicy.Overwrite,
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
        var nameList = new List<string>();
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
        PathNode parent, List<string> names, Dictionary<string, int> lookup)
    {
        if (childOffset >= dirSize) return;
        var pos = isoOffset + dirOffset + childOffset;
        isoFs.Seek(pos, SeekOrigin.Begin);
        var left = ReadUShort(isoFs);
        if (childOffset == 0 && IsEmptyTable(isoFs, left)) return;
        var right = ReadUShort(isoFs);
        var entrySector = ReadUInt(isoFs);
        var entrySize = ReadUInt(isoFs);
        var attrs = (byte)isoFs.ReadByte();
        var nameLen = (byte)isoFs.ReadByte();
        var nameBytes = new byte[nameLen];
        if (nameLen > 0)
        {
            var read = 0;
            while (read < nameLen)
            {
                var n = isoFs.Read(nameBytes, read, nameLen - read);
                if (n == 0) return;
                read += n;
            }
        }

        var name = Encoding.ASCII.GetString(nameBytes);
        var isDir = (attrs & 0x10) != 0;
        var entryOffset = (long)entrySector * Constants.SectorSize;

        if (left != 0 && left != 0xFFFF)
            ParseNode(isoFs, isoOffset, dirOffset, dirSize, (long)left * 4, parent, names, lookup);

        var nameIdx = GetOrAddName(names, lookup, name);
        var node = new PathNode { IsFile = !isDir, NameIndex = nameIdx };
        if (isDir)
        {
            ParseNode(isoFs, isoOffset, entryOffset, entrySize, 0, node, names, lookup);
            node.Subnodes.Sort((a, b) => CompareNodeName(names[a.NameIndex], names[b.NameIndex]));
        }
        else
        {
            node.SourceOffset = entryOffset;
            node.FileSize = entrySize;
        }

        parent.Subnodes.Add(node);
        if (right != 0 && right != 0xFFFF)
            ParseNode(isoFs, isoOffset, dirOffset, dirSize, (long)right * 4, parent, names, lookup);
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
        var total = 0;
        while (total < 12)
        {
            var n = isoFs.Read(peek[total..]);
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
        FileStream isoFs, long xisoOffset, PathNode root, List<string> names, string displayPath) : IZarPackSource
    {
        public string DisplayPath { get; } = displayPath;

        public IReadOnlyList<ZarPackEntry> Collect(CancellationToken cancellationToken = default)
        {
            var entries = new List<ZarPackEntry>();
            Walk(root, string.Empty, entries, cancellationToken);
            return entries;
        }

        private void Walk(PathNode dir, string path, List<ZarPackEntry> entries, CancellationToken ct)
        {
            foreach (var child in dir.Subnodes)
            {
                ct.ThrowIfCancellationRequested();
                var childPath = path.Length == 0 ? names[child.NameIndex] : path + "/" + names[child.NameIndex];
                if (!child.IsFile)
                {
                    entries.Add(new ZarPackEntry { RelativePath = childPath, IsDirectory = true });
                    Walk(child, childPath, entries, ct);
                    continue;
                }

                var offset = xisoOffset + child.SourceOffset;
                var size = (long)child.FileSize;
                entries.Add(new ZarPackEntry
                {
                    RelativePath = childPath,
                    IsDirectory = false,
                    Length = size,
                    OpenRead = () => new XisoSliceStream(isoFs, offset, size, childPath),
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

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n;
            lock (baseStream)
            {
                baseStream.Seek(offset + _position, SeekOrigin.Begin);
                n = baseStream.Read(buffer[..toRead]);
            }

            if (n == 0)
            {
                throw new InvalidOperationException($"Truncated file data for {path}");
            }

            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
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