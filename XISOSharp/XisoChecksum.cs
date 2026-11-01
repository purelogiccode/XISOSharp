using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.Interfaces;
using ZArchiveSharp;

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
    /// Plain ISO, CISO/CSO, CCI and split part sets are auto-detected by
    /// <see cref="XisoImageSource"/> and read through the decompressed view.</param>
    /// <param name="skipSectors">Optional skip sectors for Redump game partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte SHA3-256 digest.</returns>
    public static byte[] ComputeImageChecksum(string isoPath, int? skipSectors = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using IBlockDevice dev = XisoImageSource.OpenBlockDevice(isoPath);
        return ComputeImageChecksum(dev, Path.GetFileName(isoPath), skipSectors, ct);
    }

    /// <summary>Computes checksum from an open stream with known disc name (for error reporting).</summary>
    public static byte[] ComputeImageChecksum(FileStream fs, string isoName, int? skipSectors = null,
        CancellationToken ct = default)
    {
        using FileBlockDevice dev = new(fs, leaveOpen: true);
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
        (uint rootSector, uint rootSize, long discLseek) = XisoReader.VerifyXiso(dev, isoName, skipSectors);

        long dirStart = ((long)rootSector * Constants.SectorSize) + discLseek;

        // Collect entries as xdvdfs does: file_tree returns (parentDirString, node)
        // where path = parent + "/" + name, including both files and directories.
        SortedDictionary<string, (bool IsDir, long Offset, uint Size)> map = new(StringComparer.Ordinal);

        CollectFileTree(dev, dirStart, rootSize, discLseek, "", map, ct);

        // SHA3-256 over sorted map. Prefer the BCL (native CNG/OpenSSL speed)
        // when the OS supports it; otherwise use the pure-managed fallback
        // (Windows 10 CNG and OpenSSL 1.x throw PlatformNotSupportedException).
        // Both are NIST FIPS 202, so digests are identical everywhere.
        using IncrementalHash? nativeHasher = SHA3_256.IsSupported
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256)
            : null;
        using Sha3256? managedHasher = nativeHasher == null ? new Sha3256() : null;

        foreach (KeyValuePair<string, (bool IsDir, long Offset, uint Size)> kv in map)
        {
            ct.ThrowIfCancellationRequested();
            string path = kv.Key; // already "/name" or "/dir/file"
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            if (nativeHasher != null)
            {
                nativeHasher.AppendData(pathBytes);
            }
            else
            {
                managedHasher!.AppendData(pathBytes);
            }

            (bool IsDir, long Offset, uint Size) entry = kv.Value;
            if (!entry.IsDir && entry.Size > 0)
            {
                // Stream file data without loading all at once (avoid read_data_all)
                long fileOffset = entry.Offset;
                long consumed = 0;
                long remaining = entry.Size;
                byte[] buf = new byte[Constants.ReadWriteBufferSize];
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int n = dev.Read(fileOffset + consumed, buf.AsSpan(0, toRead));
                    if (n == 0) break;
                    if (nativeHasher != null)
                    {
                        nativeHasher.AppendData(buf, 0, n);
                    }
                    else
                    {
                        managedHasher!.AppendData(buf, 0, n);
                    }

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

        return nativeHasher != null ? nativeHasher.GetHashAndReset() : managedHasher!.GetHashAndReset();
    }

    /// <summary>Returns the hex (lowercase) representation of the checksum.</summary>
    public static string ComputeImageChecksumHex(string isoPath, int? skipSectors = null,
        CancellationToken ct = default) =>
        Convert.ToHexString(ComputeImageChecksum(isoPath, skipSectors, ct)).ToLowerInvariant();

    /// <summary>
    /// Computes the same deterministic SHA3-256 tree checksum for a ZArchive
    /// (<c>.zar</c>) that <see cref="ComputeImageChecksum(string,int?,CancellationToken)"/>
    /// computes for an ISO: sorted <c>/path</c> bytes plus file data. Equivalent
    /// trees therefore hash equally across ISO and ZAR.
    /// </summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte SHA3-256 digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="zarPath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed.</exception>
    public static byte[] ComputeZarChecksum(string zarPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(zarPath);
        ct.ThrowIfCancellationRequested();

        using ZArchiveReader reader = ZArchiveReader.TryOpen(zarPath, out ZArchiveOpenFailure failure)
                                      ?? throw failure switch
                                      {
                                          ZArchiveOpenFailure.FileNotFound => new FileNotFoundException(
                                              $"ZArchive not found: {zarPath}", zarPath),
                                          ZArchiveOpenFailure.AccessDenied => new UnauthorizedAccessException(
                                              $"Access denied reading ZArchive: {zarPath}"),
                                          _ => new InvalidDataException(
                                              $"Invalid ZArchive '{zarPath}': {failure}."),
                                      };

        SortedDictionary<string, (bool IsDir, uint Node, ulong Size)> map = new(StringComparer.Ordinal);
        CollectZarTree(reader, ZArchiveReader.RootNode, string.Empty, map, ct);

        using IncrementalHash? nativeHasher = SHA3_256.IsSupported
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256)
            : null;
        using Sha3256? managedHasher = nativeHasher == null ? new Sha3256() : null;
        byte[] buffer = new byte[Constants.ReadWriteBufferSize];

        foreach (KeyValuePair<string, (bool IsDir, uint Node, ulong Size)> kv in map)
        {
            ct.ThrowIfCancellationRequested();
            byte[] pathBytes = Encoding.UTF8.GetBytes(kv.Key);
            if (nativeHasher != null) nativeHasher.AppendData(pathBytes);
            else managedHasher!.AppendData(pathBytes);

            (bool isDir, uint node, ulong size) = kv.Value;
            if (isDir || size == 0) continue;

            ulong consumed = 0;
            ulong remaining = size;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min((ulong)buffer.Length, remaining);
                ulong read = reader.ReadFromFile(node, consumed, buffer.AsSpan(0, toRead));
                if (read == 0)
                {
                    throw new IOException(
                        $"Truncated ZArchive file data for {kv.Key}: expected {size}, remaining {remaining}");
                }

                if (nativeHasher != null) nativeHasher.AppendData(buffer, 0, (int)read);
                else managedHasher!.AppendData(buffer, 0, (int)read);

                consumed += read;
                remaining -= read;
            }
        }

        return nativeHasher != null ? nativeHasher.GetHashAndReset() : managedHasher!.GetHashAndReset();
    }

    /// <summary>Returns the lowercase hex representation of <see cref="ComputeZarChecksum"/>.</summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <param name="ct">Cancellation token.</param>
    public static string ComputeZarChecksumHex(string zarPath, CancellationToken ct = default) =>
        Convert.ToHexString(ComputeZarChecksum(zarPath, ct)).ToLowerInvariant();

    // -----------------------------------------------------------------------
    // File-tree collection — mirrors xdvdfs read.rs file_tree + walk_dirent_tree
    // -----------------------------------------------------------------------

    private static void CollectZarTree(ZArchiveReader reader, uint node, string parent,
        SortedDictionary<string, (bool IsDir, uint Node, ulong Size)> map, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > Constants.MaxTocDepth)
        {
            throw new InvalidDataException(
                $"ZArchive directory depth exceeds {Constants.MaxTocDepth} at '{parent}'.");
        }

        uint count = reader.GetDirEntryCount(node);
        for (uint index = 0; index < count; index++)
        {
            if (!reader.TryGetDirEntry(node, index, out uint child, out ZArchiveReader.DirEntry entry))
            {
                throw new InvalidDataException($"Invalid ZArchive directory entry in '{parent}'.");
            }

            string path = parent + "/" + entry.Name;
            map[path] = (entry.IsDirectory, child, entry.Size);
            if (entry.IsDirectory)
            {
                CollectZarTree(reader, child, path, map, ct, depth + 1);
            }
        }
    }

    private static void CollectFileTree(IBlockDevice dev, long dirStart, uint dirSize, long discLseek,
        string parent, SortedDictionary<string, (bool IsDir, long Offset, uint Size)> map, CancellationToken ct,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        // Hardening (#16): bound subdirectory descent — a corrupt subdir cycle
        // previously recursed until the stack overflowed.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{parent}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        // Gather immediate children of this directory table
        List<DirEnt> children = WalkDirentTree(dev, dirStart, dirSize);

        // For each child, insert into map and recurse if directory
        foreach (DirEnt child in children)
        {
            string path = parent.Length == 0 ? "/" + child.Name : parent + "/" + child.Name;
            bool isDir = child.IsDirectory;
            long fileOffset = ((long)child.StartSector * Constants.SectorSize) + discLseek;

            // xdvdfs inserts (parent, node) where path = format!("{}/{}", parent, name)
            // For root, parent="" => path="/name"
            map[path] = (isDir, fileOffset, child.Size);

            if (isDir && child.Size > 0)
            {
                long subDirStart = fileOffset;
                uint subDirSize = child.Size;
                CollectFileTree(dev, subDirStart, subDirSize, discLseek, path, map, ct, depth + 1);
            }
        }
    }

    /// <summary>
    /// Simplified directory entry collected during the checksum file-tree walk.
    /// </summary>
    private sealed class DirEnt
    {
        /// <summary>File or directory name.</summary>
        internal string Name = "";

        /// <summary>Partition-relative start sector of the entry's data or subdirectory table.</summary>
        internal uint StartSector;

        /// <summary>File size or subdirectory table size in bytes.</summary>
        internal uint Size;

        /// <summary>Whether the entry is a directory.</summary>
        internal bool IsDirectory;
    }

    private static List<DirEnt> WalkDirentTree(IBlockDevice dev, long dirStart, uint dirSize)
    {
        List<DirEnt> result = new();
        if (dirSize == 0) return result;

        // Stack of offsets within the directory table (like xdvdfs walk_dirent_tree)
        Stack<uint> stack = new();
        stack.Push(0);

        // Hardening (#16): every pushed offset is visited at most once — a corrupt
        // cycle previously looped until the result list exhausted memory.
        HashSet<uint> visited = new();

        while (stack.Count > 0)
        {
            uint top = stack.Pop();
            long offset = dirStart + top;
            // Bounds check: ensure we don't read beyond dir table
            if (top >= dirSize) continue;
            if (!visited.Add(top))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry: directory cycle detected — table offset {top} was already visited.");
            }

            if (visited.Count > Constants.MaxTocEntriesPerTable)
            {
                throw new XisoFormatException(
                    "invalid TOC entry: too many entries in one directory table (possible corrupt offset chain).");
            }

            DirentNodeRaw? opt = ReadDirent(dev, offset);
            if (opt == null) continue; // empty directory sentinel

            DirentNodeRaw node = opt;

            // Push children using the same logic as xdvdfs: left then right (stack LIFO)
            ushort left = node.LeftOffset;
            if (left != 0 && left != 0xFFFF)
                stack.Push((uint)left * 4);

            ushort right = node.RightOffset;
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

    /// <summary>
    /// Raw on-disk directory entry parsed from a directory table during the checksum walk.
    /// </summary>
    private sealed class DirentNodeRaw
    {
        /// <summary>Left-child offset in DWORDs within the directory table (0 or 0xFFFF if none).</summary>
        internal ushort LeftOffset;

        /// <summary>Right-child offset in DWORDs within the directory table (0 or 0xFFFF if none).</summary>
        internal ushort RightOffset;

        /// <summary>Partition-relative start sector of the entry's data or subdirectory table.</summary>
        internal uint StartSector;

        /// <summary>File size or subdirectory table size in bytes.</summary>
        internal uint Size;

        /// <summary>Raw attribute byte with reserved bits masked out.</summary>
        internal byte Attributes;

        /// <summary>Decoded entry name.</summary>
        internal string Name = "";

        /// <summary>Whether the entry is a directory (derived from <see cref="Attributes"/>).</summary>
        internal bool IsDirectory => (Attributes & Constants.AttributeDir) != 0;
    }

    private static DirentNodeRaw? ReadDirent(IBlockDevice dev, long offset)
    {
        Span<byte> hdr = stackalloc byte[14];
        if (dev.Read(offset, hdr) != hdr.Length) return null;

        // Check empty directory sentinel (14 bytes all 0xFF or all 0x00)
        bool allFf = true, allZero = true;
        for (int i = 0; i < 14; i++)
        {
            if (hdr[i] != 0xFF) allFf = false;
            if (hdr[i] != 0x00) allZero = false;
            if (!allFf && !allZero) break;
        }

        if (allFf || allZero) return null;

        ushort left = BinaryPrimitives.ReadUInt16LittleEndian(hdr[..2]);
        ushort right = BinaryPrimitives.ReadUInt16LittleEndian(hdr[2..4]);
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..12]);
        byte attrs = Constants.MaskAttributes(hdr[12]);
        byte nameLen = hdr[13];

        if (nameLen == 0) return null; // shouldn't happen, but treat as empty

        byte[] nameBuf = new byte[nameLen];
        if (dev.Read(offset + 14, nameBuf) != nameLen) return null;

        // Xbox uses Windows-1252; xdvdfs uses encoding_rs WINDOWS_1252.
        // Latin1Encoding covers the same range for test vectors (ASCII).
        string name = Latin1Encoding.Instance.GetString(nameBuf);

        return new DirentNodeRaw
        {
            LeftOffset = left,
            RightOffset = right,
            StartSector = sector,
            Size = size,
            Attributes = attrs,
            Name = name
        };
    }
}
