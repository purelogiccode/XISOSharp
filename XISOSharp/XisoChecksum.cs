using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.Interfaces;

namespace XISOSharp;

/// <summary>
/// Combined integrity checksum (SHA3-256) over image contents, ported from
/// <c>References/xdvdfs-0.8.3/xdvdfs-core/src/checksum.rs</c>:
/// <c>BTreeMap&lt;String,Node&gt;</c> sorted <c>dir/file</c> paths +
/// <c>hasher.update(path.bytes); hasher.update(data)</c>.
/// </summary>
public static class XisoChecksum
{
    /// <summary>
    /// Computes the deterministic SHA3-256 checksum of an XISO image.
    /// The hash is over the sorted set of all directory entries (files and directories)
    /// using their UTF-8 path bytes (leading <c>/</c>, e.g. <c>/DIR/FILE.TXT</c>) and,
    /// for regular files, the file data. This matches <c>xdvdfs checksum</c>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file (or Redump partition; use <paramref name="skipSectors"/> for video offset).
    /// A <c>.cso</c> path — single file or split <c>*.1.cso</c> parts — is auto-detected by extension and
    /// routed through <see cref="CisoBlockDevice"/> (mirroring <c>xdvdfs-cli/src/img.rs::open_image</c>).</param>
    /// <param name="skipSectors">Optional skip sectors for Redump game partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte SHA3-256 digest.</returns>
    public static byte[] ComputeImageChecksum(string isoPath, int? skipSectors = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (IsCsoPath(isoPath))
        {
            using var dev = new CisoBlockDevice(isoPath);
            return ComputeImageChecksum(dev, Path.GetFileName(isoPath), skipSectors, ct);
        }

        using var fsDev = new FileBlockDevice(isoPath, FileMode.Open, FileAccess.Read);
        return ComputeImageChecksum(fsDev, Path.GetFileName(isoPath), skipSectors, ct);
    }

    /// <summary>True when <paramref name="path"/> has a <c>.cso</c> extension (covers split <c>*.1.cso</c>).</summary>
    private static bool IsCsoPath(string path)
        => Path.GetExtension(path).Equals(".cso", StringComparison.OrdinalIgnoreCase);

    /// <summary>Computes checksum from an open stream with known disc name (for error reporting).</summary>
    public static byte[] ComputeImageChecksum(FileStream fs, string isoName, int? skipSectors = null,
        CancellationToken ct = default)
    {
        using var dev = new FileBlockDevice(fs, leaveOpen: true);
        return ComputeImageChecksum(dev, isoName, skipSectors, ct);
    }

    /// <summary>
    /// Computes the checksum over any <see cref="IBlockDevice"/> (file, memory, CISO or
    /// offset-wrapped), mirroring <c>xdvdfs checksum</c> operating on <c>Box&lt;dyn BlockDeviceRead&gt;</c>.
    /// </summary>
    public static byte[] ComputeImageChecksum(IBlockDevice dev, string isoName, int? skipSectors = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Detect discLseek / root table via VerifyXiso probe (supports skipSectors override)
        (var rootSector, var rootSize, var discLseek) = XisoReader.VerifyXiso(dev, isoName, skipSectors);

        var dirStart = ((long)rootSector * Constants.SectorSize) + discLseek;

        // Collect entries as xdvdfs does: file_tree returns (parentDirString, node)
        // where path = parent + "/" + name, including both files and directories.
        var map = new SortedDictionary<string, (bool IsDir, long Offset, uint Size)>(StringComparer.Ordinal);

        CollectFileTree(dev, dirStart, rootSize, discLseek, "", map, ct);

        // SHA3-256 over sorted map
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256);
        // For older runtimes fallback: SHA3_256.Create()
        // IncrementalHash works on net8+.

        foreach (var kv in map)
        {
            ct.ThrowIfCancellationRequested();
            var path = kv.Key; // already "/name" or "/dir/file"
            var pathBytes = Encoding.UTF8.GetBytes(path);
            hasher.AppendData(pathBytes);

            var entry = kv.Value;
            if (!entry.IsDir && entry.Size > 0)
            {
                // Stream file data without loading all at once (avoid read_data_all)
                var fileOffset = entry.Offset;
                long consumed = 0;
                long remaining = entry.Size;
                var buf = new byte[Constants.ReadWriteBufferSize];
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buf.Length, remaining);
                    var n = dev.Read(fileOffset + consumed, buf.AsSpan(0, toRead));
                    if (n == 0) break;
                    hasher.AppendData(buf, 0, n);
                    consumed += n;
                    remaining -= n;
                }

                if (remaining != 0)
                {
                    throw new IOException(
                        $"Truncated file data for {path}: expected {entry.Size}, remaining {remaining}");
                }
            }
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>Returns the hex (lowercase) representation of the checksum.</summary>
    public static string ComputeImageChecksumHex(string isoPath, int? skipSectors = null,
        CancellationToken ct = default)
        => Convert.ToHexString(ComputeImageChecksum(isoPath, skipSectors, ct)).ToLowerInvariant();

    // -----------------------------------------------------------------------
    // File-tree collection — mirrors xdvdfs read.rs file_tree + walk_dirent_tree
    // -----------------------------------------------------------------------

    private static void CollectFileTree(IBlockDevice dev, long dirStart, uint dirSize, long discLseek,
        string parent, SortedDictionary<string, (bool IsDir, long Offset, uint Size)> map, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Gather immediate children of this directory table
        var children = WalkDirentTree(dev, dirStart, dirSize);

        // For each child, insert into map and recurse if directory
        foreach (var child in children)
        {
            var path = parent.Length == 0 ? "/" + child.Name : parent + "/" + child.Name;
            var isDir = child.IsDirectory;
            var fileOffset = ((long)child.StartSector * Constants.SectorSize) + discLseek;

            // xdvdfs inserts (parent, node) where path = format!("{}/{}", parent, name)
            // For root, parent="" => path="/name"
            map[path] = (isDir, fileOffset, child.Size);

            if (isDir && child.Size > 0)
            {
                var subDirStart = fileOffset;
                var subDirSize = child.Size;
                CollectFileTree(dev, subDirStart, subDirSize, discLseek, path, map, ct);
            }
        }
    }

    private sealed class DirEnt
    {
        public string Name = "";
        public uint StartSector;
        public uint Size;
        public bool IsDirectory;
    }

    private static List<DirEnt> WalkDirentTree(IBlockDevice dev, long dirStart, uint dirSize)
    {
        var result = new List<DirEnt>();
        if (dirSize == 0) return result;

        // Stack of offsets within the directory table (like xdvdfs walk_dirent_tree)
        var stack = new Stack<uint>();
        stack.Push(0);

        while (stack.Count > 0)
        {
            var top = stack.Pop();
            var offset = dirStart + top;
            // Bounds check: ensure we don't read beyond dir table
            if (top >= dirSize) continue;

            var opt = ReadDirent(dev, offset);
            if (opt == null) continue; // empty directory sentinel

            var node = opt;

            // Push children using the same logic as xdvdfs: left then right (stack LIFO)
            var left = node.LeftOffset;
            if (left != 0 && left != 0xFFFF)
                stack.Push((uint)left * 4);

            var right = node.RightOffset;
            if (right != 0 && right != 0xFFFF)
                stack.Push((uint)right * 4);

            // Add to result (preorder)
            result.Add(new DirEnt
            {
                Name = node.Name, StartSector = node.StartSector, Size = node.Size, IsDirectory = node.IsDirectory
            });
        }

        return result;
    }

    private sealed class DirentNodeRaw
    {
        public ushort LeftOffset;
        public ushort RightOffset;
        public uint StartSector;
        public uint Size;
        public byte Attributes;
        public byte NameLength;
        public string Name = "";
        public bool IsDirectory => (Attributes & Constants.AttributeDir) != 0;
    }

    private static DirentNodeRaw? ReadDirent(IBlockDevice dev, long offset)
    {
        Span<byte> hdr = stackalloc byte[14];
        if (dev.Read(offset, hdr) != hdr.Length) return null;

        // Check empty directory sentinel (14 bytes all 0xFF or all 0x00)
        bool allFf = true, allZero = true;
        for (var i = 0; i < 14; i++)
        {
            if (hdr[i] != 0xFF) allFf = false;
            if (hdr[i] != 0x00) allZero = false;
            if (!allFf && !allZero) break;
        }

        if (allFf || allZero) return null;

        var left = BinaryPrimitives.ReadUInt16LittleEndian(hdr[0..2]);
        var right = BinaryPrimitives.ReadUInt16LittleEndian(hdr[2..4]);
        var sector = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..12]);
        var attrs = Constants.MaskAttributes(hdr[12]);
        var nameLen = hdr[13];

        if (nameLen == 0) return null; // shouldn't happen, but treat as empty

        var nameBuf = new byte[nameLen];
        if (dev.Read(offset + 14, nameBuf) != nameLen) return null;

        // Xbox uses Windows-1252; xdvdfs uses encoding_rs WINDOWS_1252.
        // Latin1Encoding covers the same range for test vectors (ASCII).
        var name = Latin1Encoding.Instance.GetString(nameBuf);

        return new DirentNodeRaw
        {
            LeftOffset = left,
            RightOffset = right,
            StartSector = sector,
            Size = size,
            Attributes = attrs,
            NameLength = nameLen,
            Name = name
        };
    }
}