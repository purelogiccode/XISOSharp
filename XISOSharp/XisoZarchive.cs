using System.Security.Cryptography;
using System.Text;

namespace XISOSharp;

/// <summary>
/// ZArchive / ZAR creation, ported from <c>References/XboxKit-0.7/LibXGD/ZArchive.cs</c>
/// and <c>References/ZArchive-0.1.2/*</c>. Uses raw (uncompressed) blocks for
/// trimmable/AOT compatibility; wire <c>ZstdSharp.Port</c> via <c>FlushBlock</c> for
/// size parity if desired (format supports both — raw fallback is valid per spec).
/// </summary>
public static class XisoZarchive
{
    private const int BlockSize = 64 * 1024;
    private const int BlocksPerRecord = 16;
    private static readonly byte[] Magic = [0x16, 0x9F, 0x52, 0xD6];
    private static readonly byte[] Version1 = [0x61, 0xBF, 0x3A, 0x01];

    private sealed class PathNode
    {
        public readonly List<PathNode> Subnodes = [];
        public bool IsFile;
        public int NameIndex;
        public long SourceOffset;
        public ulong FileSize;
        public ulong FileOffset;
        public uint NodeStartIndex;
    }

    private sealed class HashingStream(FileStream fs)
    {
        private readonly FileStream _fs = fs;
        private readonly SHA256 _sha = SHA256.Create();
        private readonly byte[] _buf = new byte[8];
        public long Position;

        public void Write(byte[] buf, int offset, int count)
        {
            _fs.Write(buf, offset, count);
            _sha.TransformBlock(buf, offset, count, null, 0);
            Position += count;
        }

        public void Write(byte b)
        {
            _buf[0] = b;
            Write(_buf, 0, 1);
        }

        public void Write(ushort v)
        {
            _buf[0] = (byte)(v >> 8);
            _buf[1] = (byte)v;
            Write(_buf, 0, 2);
        }

        public void Write(uint v)
        {
            _buf[0] = (byte)(v >> 24);
            _buf[1] = (byte)(v >> 16);
            _buf[2] = (byte)(v >> 8);
            _buf[3] = (byte)v;
            Write(_buf, 0, 4);
        }

        public void Write(ulong v)
        {
            _buf[0] = (byte)(v >> 56);
            _buf[1] = (byte)(v >> 48);
            _buf[2] = (byte)(v >> 40);
            _buf[3] = (byte)(v >> 32);
            _buf[4] = (byte)(v >> 24);
            _buf[5] = (byte)(v >> 16);
            _buf[6] = (byte)(v >> 8);
            _buf[7] = (byte)v;
            Write(_buf, 0, 8);
        }

        public byte[] FinalizeHash(byte[] lastBlock)
        {
            _sha.TransformFinalBlock(lastBlock, 0, lastBlock.Length);
            byte[] hash = _sha.Hash!;
            _sha.Dispose();
            return hash;
        }
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
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool CreateZar(string isoPath, string? zarPath = null, long isoOffset = 0, bool quiet = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string outZar = zarPath ?? DeriveZarPath(isoPath);
        using var isoFs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return CreateZar(isoFs, isoOffset, outZar, false, quiet, ct);
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
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool CreateZar(FileStream isoFs, long xisoOffset, string zarPath, bool removeUpdate, bool quiet,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long headerOffset = xisoOffset + Constants.HeaderOffset;
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        uint rootOffset = ReadUInt(isoFs);
        uint rootSize = ReadUInt(isoFs);

        ParseXdvdfs(isoFs, xisoOffset, (long)rootOffset * Constants.SectorSize, rootSize, removeUpdate,
            out var rootNode, out var names);

        if (!quiet) Logger.Log($"[INFO] Writing ZArchive to {zarPath}\n");
        using var zarFs = new FileStream(zarPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536);
        var hs = new HashingStream(zarFs);

        if (!WriteCompressedData(isoFs, xisoOffset, hs, rootNode, out var offsetRecords, ct))
            return false;
        ulong compressedDataEnd = (ulong)hs.Position;

        while (hs.Position % 8 != 0) hs.Write(0);

        ulong offsetRecordsStart = (ulong)hs.Position;
        WriteOffsetRecords(hs, offsetRecords);

        ulong nameTableStart = (ulong)hs.Position;
        WriteNameTable(hs, names, out var nameOffsets);

        ulong fileTreeStart = (ulong)hs.Position;
        WriteFileTree(hs, rootNode, nameOffsets);

        WriteFooter(zarFs, hs, compressedDataEnd, offsetRecordsStart, nameTableStart, fileTreeStart);
        return true;
    }

    private static void ParseXdvdfs(FileStream isoFs, long isoOffset, long dirOffset, uint dirSize, bool removeUpdate,
        out PathNode rootNode, out List<string> names)
    {
        var nameList = new List<string>();
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        rootNode = new PathNode();
        ParseNode(isoFs, isoOffset, dirOffset, dirSize, 0, rootNode, nameList, lookup);
        if (removeUpdate)
            rootNode.Subnodes.RemoveAll(n =>
                !n.IsFile && string.Equals(nameList[n.NameIndex], "$SystemUpdate", StringComparison.OrdinalIgnoreCase));
        rootNode.Subnodes.Sort((a, b) => CompareNodeName(nameList[a.NameIndex], nameList[b.NameIndex]));
        names = nameList;
    }

    private static void ParseNode(FileStream isoFs, long isoOffset, long dirOffset, uint dirSize, long childOffset,
        PathNode parent, List<string> names, Dictionary<string, int> lookup)
    {
        if (childOffset >= dirSize) return;
        long pos = isoOffset + dirOffset + childOffset;
        isoFs.Seek(pos, SeekOrigin.Begin);
        ushort left = ReadUShort(isoFs);
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

        string name = Encoding.ASCII.GetString(nameBytes);
        bool isDir = (attrs & 0x10) != 0;
        long entryOffset = (long)entrySector * Constants.SectorSize;

        if (left != 0 && left != 0xFFFF)
            ParseNode(isoFs, isoOffset, dirOffset, dirSize, (long)left * 4, parent, names, lookup);

        int nameIdx = GetOrAddName(names, lookup, name);
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

    private static bool WriteCompressedData(FileStream isoFs, long xisoOffset, HashingStream hs, PathNode root,
        out List<(ulong BaseOffset, ushort[] Sizes)> offsetRecords, CancellationToken ct)
    {
        offsetRecords = [];
        ushort[] sizes = new ushort[BlocksPerRecord];
        int count = 0;
        ulong recordBase = 0;
        byte[] buf = new byte[BlockSize];
        int bufPos = 0;
        ulong inputOffset = 0;

        var stack = new Stack<(PathNode node, int idx)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (PathNode dir, int i) = stack.Pop();
            while (i < dir.Subnodes.Count)
            {
                var child = dir.Subnodes[i++];
                if (!child.IsFile)
                {
                    stack.Push((dir, i));
                    dir = child;
                    i = 0;
                    continue;
                }

                child.FileOffset = inputOffset;
                isoFs.Seek(xisoOffset + child.SourceOffset, SeekOrigin.Begin);
                long remaining = (long)child.FileSize;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(BlockSize - bufPos, remaining);
                    int n = isoFs.Read(buf, bufPos, toRead);
                    if (n == 0) return false;
                    bufPos += n;
                    remaining -= n;
                    inputOffset += (ulong)n;
                    if (bufPos == BlockSize)
                    {
                        FlushBlock(hs, buf, offsetRecords, ref sizes, ref count, ref recordBase);
                        bufPos = 0;
                    }
                }
            }
        }

        if (bufPos > 0)
        {
            Array.Clear(buf, bufPos, BlockSize - bufPos);
            FlushBlock(hs, buf, offsetRecords, ref sizes, ref count, ref recordBase);
        }

        if (count > 0) offsetRecords.Add((recordBase, sizes));
        return true;
    }

    // Raw-only flush (valid per ZArchive spec). Replace body with ZstdSharp when desired:
    //   CompressedSize < BlockSize -> store compressed, else raw.
    private static void FlushBlock(HashingStream hs, byte[] data, List<(ulong, ushort[])> records, ref ushort[] sizes,
        ref int count, ref ulong recordBase)
    {
        if (count == BlocksPerRecord)
        {
            records.Add((recordBase, sizes));
            sizes = new ushort[BlocksPerRecord];
            count = 0;
        }

        if (count == 0) recordBase = (ulong)hs.Position;

        // No compression: store raw block verbatim (ZArchive readers accept this)
        hs.Write(data, 0, BlockSize);
        sizes[count++] = BlockSize - 1;
    }

    private static void WriteOffsetRecords(HashingStream hs, List<(ulong BaseOffset, ushort[] Sizes)> records)
    {
        foreach ((ulong baseOff, ushort[] sizes) in records)
        {
            hs.Write(baseOff);
            foreach (ushort s in sizes) hs.Write(s);
        }
    }

    private static void WriteNameTable(HashingStream hs, List<string> names, out uint[] offsets)
    {
        offsets = new uint[names.Count];
        uint pos = 0;
        for (int i = 0; i < names.Count; i++)
        {
            offsets[i] = pos;
            byte[] nameBytes = Encoding.UTF8.GetBytes(names[i]);
            int len = nameBytes.Length;
            if (len >= 0x80)
            {
                byte[] hdr = [(byte)((len & 0x7F) | 0x80), (byte)(len >> 7)];
                hs.Write(hdr, 0, 2);
                pos += 2;
            }
            else
            {
                hs.Write([(byte)(len & 0x7F)], 0, 1);
                pos += 1;
            }

            hs.Write(nameBytes, 0, nameBytes.Length);
            pos += (uint)nameBytes.Length;
        }
    }

    private static void WriteFileTree(HashingStream hs, PathNode root, uint[] nameOffsets)
    {
        var nodes = new List<PathNode>();
        var q = new Queue<PathNode>([root]);
        uint idx = 1;
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            nodes.Add(n);
            if (n.IsFile) continue;
            n.NodeStartIndex = idx;
            idx += (uint)n.Subnodes.Count;
            foreach (var c in n.Subnodes) q.Enqueue(c);
        }

        foreach (var n in nodes)
        {
            if (n == root) hs.Write(0x7FFFFFFF);
            else if (n.IsFile) hs.Write(0x80000000u | nameOffsets[n.NameIndex]);
            else hs.Write(nameOffsets[n.NameIndex]);

            if (n.IsFile)
            {
                hs.Write((uint)(n.FileOffset & 0xFFFFFFFF));
                hs.Write((uint)(n.FileSize & 0xFFFFFFFF));
                hs.Write((ushort)(n.FileSize >> 32));
                hs.Write((ushort)(n.FileOffset >> 32));
            }
            else
            {
                hs.Write(n.NodeStartIndex);
                hs.Write((uint)n.Subnodes.Count);
                hs.Write((uint)0);
            }
        }
    }

    private static void WriteFooter(FileStream zarFs, HashingStream hs, ulong compressedDataSize,
        ulong offsetRecordsStart, ulong nameTableStart, ulong fileTreeStart)
    {
        ulong end = (ulong)hs.Position;
        ulong totalSize = end + 144;
        using var ms = new MemoryStream(144);
        using var bw = new BinaryWriter(ms);
        WriteBe(bw, 0UL);
        WriteBe(bw, compressedDataSize);
        WriteBe(bw, offsetRecordsStart);
        WriteBe(bw, nameTableStart - offsetRecordsStart);
        WriteBe(bw, nameTableStart);
        WriteBe(bw, fileTreeStart - nameTableStart);
        WriteBe(bw, fileTreeStart);
        WriteBe(bw, end - fileTreeStart);
        WriteBe(bw, end);
        WriteBe(bw, 0UL);
        WriteBe(bw, end);
        WriteBe(bw, 0UL);
        bw.Write(new byte[32]);
        WriteBe(bw, totalSize);
        bw.Write(Version1);
        bw.Write(Magic);
        byte[] footer = ms.ToArray();
        byte[] hash = hs.FinalizeHash(footer);
        Array.Copy(hash, 0, footer, 96, 32);
        zarFs.Write(footer, 0, footer.Length);
    }

    private static void WriteBe(BinaryWriter bw, ulong v) =>
        bw.Write([
            (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16),
            (byte)(v >> 8), (byte)v
        ]);
}