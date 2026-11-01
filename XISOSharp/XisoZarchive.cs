using System.Buffers;
using System.Security.Cryptography;
using XISOSharp.Models;
using ZArchiveSharp;
using ZArchiveSharp.Pipeline;

namespace XISOSharp;

/// <summary>
/// XISO → ZArchive (<c>.zar</c>) conversion — the compressed single-file layout
/// Xenia canary loads for Xbox dumps. Mirrors the ZarManager pipeline
/// (<c>../CSharp_ZArchiveSharp/References/ZarManager-1.2.0/core.py</c>: extract ISO, pack the tree with
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
        internal readonly List<PathNode> Subnodes = [];

        /// <summary>Whether this node is a file (<c>false</c> for directories).</summary>
        internal bool IsFile;

        /// <summary>Index into the shared ZAR name table.</summary>
        internal int NameIndex;

        /// <summary>Partition-relative byte offset of the file data within the image.</summary>
        internal long SourceOffset;

        /// <summary>File size in bytes.</summary>
        internal ulong FileSize;
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
        string full = Path.GetFileName(input);
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
        if (!quiet) Logger.Log($"[INFO] Writing ZArchive to {zarPath}\n");
        // Only a destination this call started writing may be deleted on
        // failure: a parse error before packing must leave an existing .zar
        // (which this call never touched) alone, and a pre-existing archive is
        // never deleted (mirrors CisoWriter's created-only cleanup).
        bool destinationExisted;
        try
        {
            destinationExisted = File.Exists(zarPath);
        }
        catch
        {
            destinationExisted = true;
        }

        bool packStarted = false;
        try
        {
            // Standard sector-32 descriptor, or the rebuilt sector-0 layout. Invalid
            // inputs keep the historical base so downstream reads fail as before.
            long headerOffset = XisoReader.TryFindHeaderBase(isoFs, xisoOffset, out long detectedHeader)
                ? detectedHeader
                : xisoOffset + Constants.HeaderOffset;
            isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
            uint rootOffset = ReadUInt(isoFs);
            uint rootSize = ReadUInt(isoFs);

            // Parsing stays inside the try: a corrupt table must map to the
            // documented false return, not escape as a format exception (Todo #57).
            ParseXdvdfs(isoFs, xisoOffset, (long)rootOffset * Constants.SectorSize, rootSize, removeUpdate,
                out PathNode rootNode, out List<string> names);

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
            packStarted = true;
            ZarPipeline.PackSource(source, zarPath, options, progress, ct);
            return true;
        }
        catch (XisoFormatException ex)
        {
            if (!quiet) Logger.LogErr($"[ERROR] {ex.Message}\n");
            DeleteIncomplete(zarPath);
            return false;
        }
        catch (EndOfStreamException)
        {
            if (!quiet) Logger.LogErr("[ERROR] truncated XISO: unexpected end of the image file.\n");
            DeleteIncomplete(zarPath);
            return false;
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

        void DeleteIncomplete(string path)
        {
            if (!packStarted || destinationExisted)
                return;

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

    /// <summary>
    /// Lists every entry in a ZArchive (<c>.zar</c>) name tree, parents before
    /// children and siblings in stored order.
    /// </summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <returns>Entries in archive order, with leading-<c>/</c> paths.</returns>
    /// <exception cref="ArgumentException"><paramref name="zarPath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The archive cannot be read.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed or contains an unsafe name.</exception>
    public static IReadOnlyList<ZarEntry> ListZar(string zarPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(zarPath);
        using ZArchiveReader reader = OpenZarOrThrow(zarPath);
        List<ZarEntry> entries = new();
        WalkZar(reader, ZArchiveReader.RootNode, string.Empty, entries, 0);
        return entries;
    }

    /// <summary>
    /// Computes the hash of a single file inside a ZArchive (<c>.zar</c>),
    /// mirroring <see cref="XisoReader.ComputeFileHash(string,string,HashAlgorithmName)"/>
    /// so an archive can be compared with the ISO it was packed from (for
    /// example through <see cref="XisoValidator.ValidateConversion"/>).
    /// </summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <param name="internalPath">Archive path (e.g. <c>"/subdir/file.xbe"</c>; case-insensitive).</param>
    /// <param name="algorithm">Hash algorithm to use (<see cref="HashAlgorithmName.MD5"/> or <see cref="HashAlgorithmName.SHA256"/>).</param>
    /// <returns>Hash bytes, or <c>null</c> when the file does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="zarPath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The archive cannot be read.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed, the path is a directory, or the entry is truncated.</exception>
    /// <exception cref="NotSupportedException">The hash algorithm is not supported.</exception>
    public static byte[]? ComputeFileHash(string zarPath, string internalPath, HashAlgorithmName algorithm)
    {
        ArgumentException.ThrowIfNullOrEmpty(zarPath);
        ArgumentNullException.ThrowIfNull(internalPath);

        using ZArchiveReader reader = OpenZarOrThrow(zarPath);
        if (!TryResolveZarFile(reader, internalPath, out uint node, out ulong size, out bool isDirectory))
            return null;
        if (isDirectory)
            throw new InvalidDataException($"Cannot hash a directory: {internalPath}");

        using HashAlgorithm hasher = CreateHashAlgorithm(algorithm);
        if (size == 0) return hasher.ComputeHash(Array.Empty<byte>());

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Constants.ReadWriteBufferSize);
        try
        {
            ulong consumed = 0;
            ulong remaining = size;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min((ulong)buffer.Length, remaining);
                ulong read = reader.ReadFromFile(node, consumed, buffer.AsSpan(0, toRead));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"Truncated ZArchive file data for {internalPath}: expected {size}, remaining {remaining}.");
                }

                hasher.TransformBlock(buffer, 0, (int)read, null, 0);
                consumed += read;
                remaining -= read;
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hasher.Hash;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HashAlgorithm CreateHashAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5) return MD5.Create();
        if (algorithm == HashAlgorithmName.SHA256) return SHA256.Create();
        if (algorithm == HashAlgorithmName.SHA1) return SHA1.Create();
        if (algorithm == HashAlgorithmName.SHA384) return SHA384.Create();
        if (algorithm == HashAlgorithmName.SHA512) return SHA512.Create();

        throw new NotSupportedException($"Hash algorithm '{algorithm.Name}' is not supported.");
    }

    /// <summary>
    /// Resolves an archive path (<c>/a/b/c.txt</c>) to its name-tree node.
    /// Returns <c>false</c> when any segment is missing; reports whether the
    /// final match is a directory so callers can reject hashing it.
    /// </summary>
    private static bool TryResolveZarFile(ZArchiveReader reader, string internalPath, out uint node, out ulong size,
        out bool isDirectory)
    {
        node = 0;
        size = 0;
        isDirectory = false;

        string[] segments = internalPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        uint current = ZArchiveReader.RootNode;
        for (int s = 0; s < segments.Length; s++)
        {
            uint count = reader.GetDirEntryCount(current);
            bool found = false;
            for (uint index = 0; index < count; index++)
            {
                if (!reader.TryGetDirEntry(current, index, out uint child, out ZArchiveReader.DirEntry entry))
                {
                    throw new InvalidDataException($"Invalid ZArchive directory entry under '{internalPath}'.");
                }

                if (!string.Equals(entry.Name, segments[s], StringComparison.OrdinalIgnoreCase)) continue;

                found = true;
                if (entry.IsDirectory)
                {
                    if (s == segments.Length - 1)
                    {
                        isDirectory = true;
                        return true;
                    }

                    current = child;
                    break;
                }

                // A file in the middle of the path cannot be descended into.
                if (s != segments.Length - 1) return false;

                node = child;
                size = entry.Size;
                return true;
            }

            if (!found) return false;
        }

        return false;
    }

    /// <summary>
    /// Extracts every file and directory from a ZArchive into
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <param name="outputDirectory">Destination directory (created when missing).</param>
    /// <param name="skipExisting">Skip files that already exist instead of overwriting.</param>
    /// <param name="progress">Optional progress channel (<see cref="ProgressInfoType.FileProgress"/> per chunk and <see cref="ProgressInfoType.FileAdded"/> per file).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>0</c> on success.</returns>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The archive or an output path cannot be accessed.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed or contains an unsafe entry name.</exception>
    /// <exception cref="IOException">An output file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int ExtractZar(string zarPath, string outputDirectory, bool skipExisting = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(zarPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ct.ThrowIfCancellationRequested();

        using ZArchiveReader reader = OpenZarOrThrow(zarPath);
        Directory.CreateDirectory(outputDirectory);
        ZarExtractState state = new();
        ExtractZarNode(reader, ZArchiveReader.RootNode, outputDirectory, string.Empty, skipExisting,
            progress, ct, state);
        return 0;
    }

    /// <summary>
    /// Converts a ZArchive to a plain ISO by extracting to a temporary
    /// directory and packing it with <see cref="XisoWriter.PackFromDirectory"/>.
    /// </summary>
    /// <param name="zarPath">Path to the <c>.zar</c> archive.</param>
    /// <param name="isoPath">Destination ISO path; defaults to the archive name with <c>.iso</c>.</param>
    /// <param name="overwrite">Overwrite an existing ISO (default <c>false</c>).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>0</c> on success.</returns>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="IOException">The output exists and <paramref name="overwrite"/> is <c>false</c>, or an I/O error occurred.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int ConvertZarToIso(string zarPath, string? isoPath = null, bool overwrite = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(zarPath);
        string output = isoPath ?? DeriveIsoPath(zarPath);
        if (!overwrite && File.Exists(output))
        {
            throw new IOException($"Output already exists: {output}");
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xiso_zar_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            ExtractZar(zarPath, tempDirectory, skipExisting: false, progress: null, ct);
            return XisoWriter.PackFromDirectory(tempDirectory, output, cancellationToken: ct, progress: progress);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static string DeriveIsoPath(string zarPath)
    {
        string directory = Path.GetDirectoryName(zarPath) ?? string.Empty;
        string file = Path.GetFileName(zarPath);
        string stem = file.EndsWith(".zar", StringComparison.OrdinalIgnoreCase)
            ? file[..^".zar".Length]
            : Path.GetFileNameWithoutExtension(file);
        return Path.Combine(directory, stem + ".iso");
    }

    private static ZArchiveReader OpenZarOrThrow(string zarPath)
    {
        ZArchiveReader? reader = ZArchiveReader.TryOpen(zarPath, out ZArchiveOpenFailure failure);
        if (reader is not null && failure == ZArchiveOpenFailure.None) return reader;

        throw failure switch
        {
            ZArchiveOpenFailure.FileNotFound => new FileNotFoundException(
                $"ZArchive not found: {zarPath}", zarPath),
            ZArchiveOpenFailure.AccessDenied => new UnauthorizedAccessException(
                $"Access denied reading ZArchive: {zarPath}"),
            _ => new InvalidDataException($"Invalid ZArchive '{zarPath}': {failure}."),
        };
    }

    private static void WalkZar(ZArchiveReader reader, uint node, string parent, List<ZarEntry> entries, int depth)
    {
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
            entries.Add(new ZarEntry(path, entry.IsDirectory, entry.Size));
            if (entry.IsDirectory)
            {
                WalkZar(reader, child, path, entries, depth + 1);
            }
        }
    }

    private sealed class ZarExtractState
    {
        internal int Files;
        internal long Bytes;
    }

    private static void ExtractZarNode(ZArchiveReader reader, uint node, string outputDirectory,
        string parent, bool skipExisting, IProgress<ProgressInfo>? progress, CancellationToken ct,
        ZarExtractState state)
    {
        uint count = reader.GetDirEntryCount(node);
        for (uint index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (!reader.TryGetDirEntry(node, index, out uint child, out ZArchiveReader.DirEntry entry))
            {
                throw new InvalidDataException($"Invalid ZArchive directory entry in '{parent}'.");
            }

            string relative = CombineZarPath(parent, entry.Name);
            string destination = Path.Combine(outputDirectory, relative);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                progress?.Report(new ProgressInfo(
                    ProgressInfoType.DirAdded,
                    Count: state.Files,
                    Path: "/" + relative.Replace(Path.DirectorySeparatorChar, '/'),
                    Sector: 0));
                ExtractZarNode(reader, child, outputDirectory, relative, skipExisting, progress, ct, state);
                continue;
            }

            if (skipExisting && File.Exists(destination)) continue;

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using Stream source = reader.OpenRead(child);
            using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.SequentialScan);
            CopyZarFile(source, target, destination, entry.Size, progress, ct, state);
        }
    }

    private static void CopyZarFile(Stream source, Stream destination, string destinationPath, ulong size,
        IProgress<ProgressInfo>? progress, CancellationToken ct, ZarExtractState state)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Constants.ReadWriteBufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                total += read;
                progress?.Report(new ProgressInfo(
                    ProgressInfoType.FileProgress,
                    Count: (long)size,
                    Path: destinationPath,
                    Sector: 0,
                    Size: total));
            }

            state.Files++;
            state.Bytes += total;
            progress?.Report(new ProgressInfo(
                ProgressInfoType.FileAdded,
                Count: state.Files,
                Path: destinationPath,
                Sector: 0,
                Size: total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string CombineZarPath(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0']) >= 0 ||
            Path.IsPathRooted(name) ||
            (OperatingSystem.IsWindows() && name.Contains(':')))
        {
            throw new InvalidDataException($"Unsafe ZArchive entry name: '{name}'.");
        }

        return parent.Length == 0 ? name : parent + Path.DirectorySeparatorChar + name;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
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
        ParseNode(isoFs, isoOffset, dirOffset, dirSize, 0, rootNode, nameList, lookup, isRoot: true);
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
        int depth = 0,
        bool isRoot = false)
    {
        if (dirSize == 0)
        {
            // A zero-size nested dirent is a real-world xdvdfs quirk (empty
            // directory, no table on disk) and parses as empty. A zero-size
            // root with a non-zero offset is corrupt and must not silently
            // emit an empty archive (Todo #24).
            if (isRoot && dirOffset != 0)
                throw new XisoFormatException("invalid XISO: root directory table size is zero.");
            return;
        }

        if (childOffset >= dirSize) return;
        // Hardening (#16): bound subdirectory nesting — a corrupt subdir cycle
        // throws a named error instead of recursing until the stack overflows.
        // Intra-table AVL depth is bounded by the explicit in-order stack below,
        // so a pathological left-skewed table is packed instead of being cut off
        // at MaxTocDepth (Phase 4 follow-up).
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry: maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        visited ??= [];
        List<ParseEntry> pending = [];
        long? next = childOffset;

        while (true)
        {
            if (next is long offset)
            {
                next = null;
                if (offset < dirSize)
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

                    long pos = isoOffset + dirOffset + offset;
                    isoFs.Seek(pos, SeekOrigin.Begin);
                    ushort left = ReadUShort(isoFs);
                    if (offset == 0 && IsEmptyTable(isoFs, left)) return;
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
                            if (n == 0)
                            {
                                // A truncated name used to return silently, dropping the
                                // entry and its right subtree while reporting success
                                // (Todo #58); CreateZar maps this to false.
                                throw new EndOfStreamException();
                            }

                            read += n;
                        }
                    }

                    // Xbox names are WINDOWS_1252 bytes; decode via Latin1 like every other
                    // reader path (ASCII would corrupt bytes >= 0x80 into '?').
                    string name = Latin1Encoding.Instance.GetString(nameBytes);
                    ParseEntry entry = new(right, (long)entrySector * Constants.SectorSize, entrySize,
                        (attrs & 0x10) != 0, name);

                    if (left != 0 && left != 0xFFFF)
                    {
                        // Parse the left subtree first (in-order); this node is
                        // added to its parent when the descent unwinds.
                        pending.Add(entry);
                        if (pending.Count > Constants.MaxTocStackFrames)
                        {
                            throw new XisoFormatException(
                                "invalid TOC entry: intra-table traversal stack exceeded " +
                                $"{Constants.MaxTocStackFrames} frames (possible corrupt offset chain).");
                        }

                        next = (long)left * 4;
                        continue;
                    }

                    AddParseNode(isoFs, isoOffset, parent, names, lookup, entry, depth);

                    if (right != 0 && right != 0xFFFF)
                    {
                        next = (long)right * 4;
                        continue;
                    }
                }
            }

            bool resumed = false;
            while (pending.Count > 0)
            {
                ParseEntry entry = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                AddParseNode(isoFs, isoOffset, parent, names, lookup, entry, depth);
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
    /// Adds one parsed entry to the ZAR tree: registers its name, recurses into
    /// a subdirectory table (bounded by <see cref="Constants.MaxTocDepth"/>),
    /// and appends the node to its parent.
    /// </summary>
    private static void AddParseNode(FileStream isoFs, long isoOffset, PathNode parent, List<string> names,
        Dictionary<string, int> lookup, ParseEntry entry, int depth)
    {
        int nameIdx = GetOrAddName(names, lookup, entry.Name);
        PathNode node = new() { IsFile = !entry.IsDirectory, NameIndex = nameIdx };
        if (entry.IsDirectory)
        {
            ParseNode(isoFs, isoOffset, entry.EntryOffset, entry.EntrySize, 0, node, names, lookup, null, depth + 1);
            node.Subnodes.Sort((a, b) => CompareNodeName(names[a.NameIndex], names[b.NameIndex]));
        }
        else
        {
            node.SourceOffset = entry.EntryOffset;
            node.FileSize = entry.EntrySize;
        }

        parent.Subnodes.Add(node);
    }

    /// <summary>One parsed table entry awaiting in-order ZAR tree construction.</summary>
    private readonly record struct ParseEntry(
        ushort Right,
        long EntryOffset,
        uint EntrySize,
        bool IsDirectory,
        string Name);

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

        /// <summary>Source image path shown in pack progress output.</summary>
        public string DisplayPath { get; } = displayPath;

        /// <summary>
        /// Enumerates the parsed XISO tree in <c>WriteNode</c> order (directories
        /// before children), opening each file as a bounded slice of the shared
        /// image stream.
        /// </summary>
        /// <param name="cancellationToken">Cancels the walk before the next entry is emitted.</param>
        /// <returns>The entries to pack, in emission order.</returns>
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

        /// <inheritdoc/>
        public override bool CanRead => true;

        /// <inheritdoc/>
        public override bool CanSeek => true;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length { get; } = length;

        /// <inheritdoc/>
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, Length);
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // The image stream stays open; the owner disposes it.
        }
    }
}
