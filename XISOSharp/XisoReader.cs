using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.DataStructures;
using XISOSharp.Interfaces;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Provides methods for reading, verifying, and traversing XISO disc images.
/// Supports extracting, listing, and generating AVL trees from the on-disk
/// directory structure.
/// </summary>
public static class XisoReader
{
    /// <summary>
    /// Rents a scratch copy buffer. Rented per operation from
    /// <see cref="ArrayPool{T}.Shared"/> (and returned cleared) instead of a
    /// <c>[ThreadStatic]</c> slot, so pooled-thread reuse and
    /// <c>Task.Run</c>-based callers never pin a 2 MB buffer per thread
    /// (BUG-LIB-010). Chunking stays identical, so progress sequences are unchanged.
    /// </summary>
    private static byte[] RentCopyBuffer() => ArrayPool<byte>.Shared.Rent(Constants.ReadWriteBufferSize);

    private static void ReturnCopyBuffer(byte[] buffer) =>
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

    private static readonly byte[] HeaderDataBytes = Encoding.ASCII.GetBytes(Constants.HeaderData);

    /// <summary>
    /// True when <paramref name="path"/> has a <c>.cso</c> extension; covers split
    /// <c>*.1.cso</c> part sets (mirroring <c>xdvdfs-cli/src/img.rs::open_image</c>).
    /// </summary>
    internal static bool IsCsoPath(string path) => Path.GetExtension(path).Equals(".cso", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens an image for reading: <c>.cso</c> paths (single or split parts) are routed
    /// through <see cref="CisoBlockDevice"/> wrapped in a <see cref="BlockDeviceStream"/>,
    /// everything else opens as a plain <see cref="FileStream"/>. Detection is by extension
    /// (mirroring <c>xdvdfs-cli/src/img.rs::open_image</c>) with a <c>CISO</c> magic sniff
    /// fallback, so renamed containers — notably the CLI rewrite flow, which appends
    /// <c>.old</c> — still resolve to the decompressed view.
    /// The caller owns the returned stream.
    /// </summary>
    public static Stream OpenImageStream(string path)
    {
        if (IsCsoPath(path) || CisoReader.IsCso(path))
            return new BlockDeviceStream(new CisoBlockDevice(path), leaveOpen: false);
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 65536
            });
    }

    /// <summary>
    /// Strips a <c>.cso</c> (or split <c>.1.cso</c>) image suffix for output naming:
    /// <c>game.cso</c> → <c>game</c>, <c>game.1.cso</c> → <c>game</c>.
    /// </summary>
    private static string StripCsoSuffix(string name)
    {
        var stem = name[..^".cso".Length];
        if (stem.EndsWith(".1", StringComparison.Ordinal))
            stem = stem[..^2];
        return stem;
    }

    /// <summary>
    /// Strips the rewrite backup suffix (legacy: last 4 chars, i.e. <c>.old</c>) plus any
    /// <c>.cso</c> image suffix, so a rewrite names its output after the game, not the container.
    /// </summary>
    private static string StripRewriteSuffix(string filename)
    {
        filename = filename.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
            ? filename[..^".old".Length]
            : filename[..^4];
        return IsCsoPath(filename) && filename.Length > 4 ? StripCsoSuffix(filename) : filename;
    }

    /// <summary>
    /// Verifies that the given stream is a valid XISO image by checking the header
    /// magic at all known disc offsets. Returns root directory metadata and the
    /// disc lseek offset used.
    /// </summary>
    /// <param name="fs">Open image stream positioned anywhere (plain file or CISO-backed).</param>
    /// <param name="isoName">Display name of the ISO (used in error messages).</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip from the start of the file before
    /// the XISO filesystem begins. When provided, the header magic is verified at
    /// <c>skipSectors * SectorSize + HeaderOffset</c> and offset probing is skipped.
    /// Use for Redump-style images where a video partition precedes the game partition.
    /// </param>
    /// <returns>
    /// Tuple containing the root directory sector index, root directory size in bytes,
    /// and the detected disc lseek offset (which includes the skip offset when provided).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="skipSectors"/> is negative.
    /// </exception>
    /// <exception cref="XisoFormatException">
    /// Thrown when no valid XISO header is found at any known offset,
    /// or when the trailing magic byte does not match.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when the file is too short to contain the expected header data
    /// at all possible offsets.
    /// </exception>
    /// <exception cref="XisoEmptyException">
    /// Thrown when the root directory sector and size are both zero (empty ISO).
    /// </exception>
    public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
        Stream fs, string isoName, int? skipSectors = null)
    {
        Span<byte> buffer = stackalloc byte[Constants.HeaderDataLength];
        long discLseek = 0;

        if (skipSectors.HasValue)
        {
            if (skipSectors.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                    "Skip sectors must be non-negative.");
            }

            discLseek = (long)skipSectors.Value * Constants.SectorSize;
            fs.Seek(Constants.HeaderOffset + discLseek, SeekOrigin.Begin);
            ReadExact(fs, buffer);

            if (!buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
            {
                Logger.LogErr(
                    $"{isoName} does not appear to be a valid xbox iso image at skip offset {discLseek} (sector {skipSectors.Value})\n");
                throw new XisoFormatException(
                    $"Invalid XISO: {isoName} — no XISO header found at sector {skipSectors.Value} (byte offset {discLseek}).");
            }
        }
        else
        {
            // Probe the header magic at every known partition base (mirrors the
            // IBlockDevice overload below and extract-xiso's verify_xiso chain:
            // plain XISO, XGD2/Redump-360, XGD3, XGD2-hybrid, XGD1). The first
            // match wins and its base becomes discLseek for all later I/O.
            long[] probes =
            [
                0, Constants.GlobalLseekOffset, Constants.Xgd3LseekOffset, Constants.Xgd2HybridLseekOffset,
                Constants.Xgd1LseekOffset
            ];
            var found = false;
            foreach (var probe in probes)
            {
                fs.Seek(Constants.HeaderOffset + probe, SeekOrigin.Begin);
                ReadExact(fs, buffer);

                if (buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
                {
                    discLseek = probe;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Logger.LogErr($"{isoName} does not appear to be a valid xbox iso image\n");
                throw new XisoFormatException($"Invalid XISO: {isoName}");
            }
        }

        Span<byte> intBuf = stackalloc byte[4];
        ReadExact(fs, intBuf);
        var rootDirSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

        ReadExact(fs, intBuf);
        var rootDirSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

        fs.Seek(Constants.FileTimeSize + Constants.UnusedSize, SeekOrigin.Current);
        ReadExact(fs, buffer);
        if (!buffer.SequenceEqual(HeaderDataBytes))
        {
            Logger.LogErr($"{isoName} appears to be corrupt\n");
            throw new XisoFormatException($"Corrupt XISO: {isoName}");
        }

        if (rootDirSector == 0 && rootDirSize == 0)
        {
            Logger.Log($"xbox image {isoName} contains no files.\n");
            throw new XisoEmptyException($"xbox image {isoName} contains no files.");
        }

        var fileLength = fs.Length;
        var totalSectors = fileLength / Constants.SectorSize;

        if (rootDirSector >= totalSectors)
        {
            Logger.LogErr($"{isoName}: root directory sector {rootDirSector} exceeds total sectors {totalSectors}\n");
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root directory sector {rootDirSector} is beyond end of image ({totalSectors} sectors).");
        }

        if (rootDirSize == 0)
        {
            Logger.LogErr($"{isoName}: root directory size is zero but sector is non-zero\n");
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root directory size is zero with non-zero sector pointer.");
        }

        var availableBytes = (totalSectors - rootDirSector) * Constants.SectorSize;
        if (rootDirSize > availableBytes)
        {
            Logger.LogErr($"{isoName}: root directory size {rootDirSize} exceeds available space {availableBytes}\n");
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root directory size {rootDirSize} bytes exceeds available space ({availableBytes} bytes from sector {rootDirSector}).");
        }

        fs.Seek(((long)rootDirSector * Constants.SectorSize) + discLseek, SeekOrigin.Begin);

        return (rootDirSector, rootDirSize, discLseek);
    }

    /// <summary>
    /// Verifies a block-device image (memory, offset-wrapped, or CISO) by probing
    /// header magic at known disc offsets. Mirrors <c>xdvdfs-core/src/blockdev.rs::OffsetWrapper::new</c>.
    /// </summary>
    /// <param name="dev">Block device to probe.</param>
    /// <param name="isoName">Display name for error messages.</param>
    /// <param name="skipSectors">Optional skip override (in 2048-byte sectors).</param>
    /// <returns>Root sector/size and disc lseek (including skip offset when provided).</returns>
    public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
        IBlockDevice dev, string isoName, int? skipSectors = null)
    {
        Span<byte> buffer = stackalloc byte[Constants.HeaderDataLength];
        Span<byte> intBuf = stackalloc byte[4];
        long discLseek = 0;

        if (skipSectors.HasValue)
        {
            if (skipSectors.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                    "Skip sectors must be non-negative.");
            }

            discLseek = (long)skipSectors.Value * Constants.SectorSize;
            if (dev.Read(Constants.HeaderOffset + discLseek, buffer) != buffer.Length)
                throw new IOException("Failed to read header");
            if (!buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
                throw new XisoFormatException($"Invalid XISO: {isoName} — no header at sector {skipSectors.Value}");
        }
        else
        {
            var ok = false;
            long[] probes =
            [
                0, Constants.GlobalLseekOffset, Constants.Xgd3LseekOffset, Constants.Xgd2HybridLseekOffset,
                Constants.Xgd1LseekOffset
            ];
            foreach (var probe in probes)
            {
                if (dev.Read(Constants.HeaderOffset + probe, buffer) != buffer.Length) continue;
                if (buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
                {
                    discLseek = probe;
                    ok = true;
                    break;
                }
            }

            if (!ok)
                throw new XisoFormatException($"Invalid XISO: {isoName}");
        }

        if (dev.Read(Constants.HeaderOffset + discLseek + Constants.HeaderDataLength, intBuf) != 4)
            throw new IOException("Failed to read root sector");
        var rootDirSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);
        if (dev.Read(Constants.HeaderOffset + discLseek + Constants.HeaderDataLength + 4, intBuf) != 4)
            throw new IOException("Failed to read root size");
        var rootDirSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

        // skip filetime + unused (8 + 0x7C8)
        Span<byte> tail = stackalloc byte[Constants.HeaderDataLength];
        if (dev.Read(
                Constants.HeaderOffset + discLseek + Constants.HeaderDataLength + 4 + 4 + Constants.FileTimeSize +
                Constants.UnusedSize, tail) != tail.Length)
        {
            throw new IOException("Failed to read trailing magic");
        }

        if (!tail.SequenceEqual(HeaderDataBytes.AsSpan()))
            throw new XisoFormatException($"Corrupt XISO: {isoName}");

        if (rootDirSector == 0 && rootDirSize == 0)
            throw new XisoEmptyException($"xbox image {isoName} contains no files.");

        var totalSectors = dev.Length / Constants.SectorSize;
        if (rootDirSector >= totalSectors)
        {
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root sector {rootDirSector} beyond end ({totalSectors} sectors).");
        }

        // BUG-LIB-037: parity with the Stream overload above — a zero root size
        // with a non-zero sector, or a root table running past end of image,
        // must fail here too instead of probing invalid later.
        if (rootDirSize == 0)
        {
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root directory size is zero with non-zero sector pointer.");
        }

        var availableBytes = (totalSectors - rootDirSector) * Constants.SectorSize;
        if (rootDirSize > availableBytes)
        {
            throw new XisoFormatException(
                $"Corrupt XISO: {isoName} — root directory size {rootDirSize} bytes exceeds available space ({availableBytes} bytes from sector {rootDirSector}).");
        }

        return (rootDirSector, rootDirSize, discLseek);
    }

    /// <summary>
    /// Audits a block-device image (memory, CISO, or offset-wrapped) with the same
    /// deep walk as <see cref="AuditXiso(string)"/>: header, optimized tag, full
    /// directory-tree traversal, sector bounds, cycles, and filenames. Never throws
    /// for corrupt content — failures surface as an invalid <see cref="AuditResult"/>.
    /// </summary>
    /// <param name="dev">Block device to audit. Left open.</param>
    /// <param name="isoName">Display name for error messages.</param>
    public static AuditResult AuditXiso(IBlockDevice dev, string isoName = "memory")
    {
        try
        {
            var (rootDirSector, _, discLseek) = VerifyXiso(dev, isoName);
            using var stream = new BlockDeviceStream(dev, leaveOpen: true);
            return AuditStream(stream, stream.Length, rootDirSector, discLseek);
        }
        catch (XisoEmptyException)
        {
            // Parity with AuditXiso(string): an empty (header-only, no files)
            // image is valid with nothing checked.
            return new AuditResult(true, 0, 0, []);
        }
        catch (Exception ex)
        {
            return new AuditResult(false, 0, 0, [ex.Message]);
        }
    }

    /// <summary>
    /// Recursively traverses the on-disk directory tree of an XISO image,
    /// building an AVL index and optionally extracting files or listing entries.
    /// </summary>
    /// <param name="fs">Image stream positioned at the start of the directory sector.</param>
    /// <param name="inDirNode">Pre-allocated directory entry node, or <c>null</c> to create one.</param>
    /// <param name="dirStart">Byte offset of the current directory sector.</param>
    /// <param name="path">Path prefix for logging and extraction.</param>
    /// <param name="mode">Operating mode (extract, list, or generate AVL tree).</param>
    /// <param name="avlRoot">Reference to the AVL root being built.</param>
    /// <param name="llCompat">If <c>true</c>, uses backwards-compatible right-offset calculation.</param>
    /// <param name="discLseek">Disc lseek offset for sector address calculation.</param>
    /// <param name="unpackOptions">
    /// Optional resume options for extract mode (skip-existing), and the
    /// <c>ContinueOnError</c> collector: per-file I/O failures are recorded
    /// and skipped instead of aborting (TODO #9, xdvdfs #187).
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">Optional channel receiving <c>FileAdded</c> per written file in extract mode.</param>
    /// <param name="visited">
    /// Absolute stream offsets of entries already seen in this directory table
    /// (TODO #16): shared across the left-subtree recursion of the same table so
    /// a corrupt cycle (extract-xiso #25, Burnout PAL) fails with a named
    /// <c>invalid TOC entry</c> error instead of hanging then OOMing. Pass
    /// <c>null</c> to start a new table (subdirectory descent gets a fresh set).
    /// </param>
    /// <param name="tableSize">
    /// Byte size of the directory table being walked (root: volume root size;
    /// subdirectory: the entry's reported size), mirroring
    /// <c>xdvdfs-core/src/layout.rs::DiskRegion::offset</c>: child offsets at or
    /// beyond it are corruption, not data. <c>long.MaxValue</c> disables the check.
    /// </param>
    /// <param name="depth">
    /// Nesting depth (subdirectory descent + intra-table AVL recursion). Genuine
    /// images stay far below <see cref="Constants.MaxTocDepth"/>; deeper means a
    /// malicious subdir cycle and throws instead of overflowing the stack.
    /// </param>
    /// <param name="filesystem">
    /// Optional destination filesystem (TODO #7). When set (extract mode), directories
    /// are created through <see cref="IFilesystem.CreateDirectory"/> at the
    /// destination-root-relative path and no process working-directory changes happen;
    /// path prefixes chain with <c>/</c>. When <c>null</c>, the legacy chdir-based
    /// extraction into the current directory runs and prefixes keep the host separator.
    /// </param>
    /// <exception cref="XisoFormatException">
    /// Thrown naming the offending path and offset when the table is
    /// structurally corrupt. Under <c>UnpackOptions.ContinueOnError</c> in
    /// extract mode the caller skips the bad subtree and records the failure
    /// instead of aborting (TODO #16 over #9).
    /// </exception>
    internal static void TraverseXiso(
        Stream fs,
        DirEntry? inDirNode,
        long dirStart,
        string? path,
        ExtractMode mode,
        ref AvlNode? avlRoot,
        bool llCompat,
        long discLseek,
        UnpackOptions? unpackOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null,
        HashSet<long>? visited = null,
        long tableSize = long.MaxValue,
        // Recursion-depth bound (#16 hardening); threaded through left-subtree and subdir recursion by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Global
        int depth = 0,
        IFilesystem? filesystem = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{path}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        visited ??= [];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        DirEntry node = new();
        var dir = inDirNode ?? node;

        dir.Left = null;
        dir.Parent = null;
        dir.AvlNode = null;
        dir.Filename = "";

        // Running table offset in DWORDs (widened to long: the old (ushort)
        // narrowing wrapped sector-padded offsets past 64K DWORDs — BUG-LIB-028).
        long lOffset = 0;

        while (true)
        {
            // Right-sibling iteration re-enters here via `continue`, bypassing the
            // method entry, so every entry — file, directory, or sibling — observes
            // cancellation (TODO #13: an interrupted unpack must stop promptly).
            cancellationToken.ThrowIfCancellationRequested();

            // Hardening (#16): bound the walk before trusting the bytes. Every entry
            // position is absolute (dirStart + table offset); revisiting one is
            // a corrupt cycle, and positions outside the table/image are corrupt
            // pointers — both previously hung then OOMed (extract-xiso #25).
            var entryPos = fs.Position;
            if (entryPos < dirStart || entryPos >= fs.Length ||
                (tableSize != long.MaxValue && entryPos >= dirStart + tableSize))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{path}': entry offset {entryPos} lies outside the directory table " +
                    $"(table at {dirStart}, size {tableSize}, image length {fs.Length}).");
            }

            if (!visited.Add(entryPos))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{path}': directory cycle detected — entry at offset {entryPos} was already visited.");
            }

            if (visited.Count > Constants.MaxTocEntriesPerTable)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{path}': too many entries in one directory table " +
                    $"(>{Constants.MaxTocEntriesPerTable}, possible corrupt offset chain).");
            }

            ReadExact(fs, shortBuf);
            var tmp = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            if (tmp == Constants.PadShort)
            {
                if (lOffset == 0)
                {
                    if (mode == ExtractMode.GenerateAvl)
                    {
                        AvlTree.AvlInsert(ref avlRoot, AvlNode.EmptySubdirectory);
                    }

                    goto end_traverse;
                }

                // Sector-pad rounding in full precision (BUG-LIB-028): bounds-check
                // before seeking so a corrupt offset fails here with its own name
                // instead of wrapping (old (ushort) cast) into a mis-walk.
                var padded = (lOffset * Constants.DwordSize) +
                             (Constants.SectorSize - ((lOffset * Constants.DwordSize) % Constants.SectorSize));
                var padSeek = dirStart + padded;
                if (padSeek < dirStart || padSeek >= fs.Length ||
                    (tableSize != long.MaxValue && padSeek >= dirStart + tableSize))
                {
                    throw new XisoFormatException(
                        $"invalid TOC entry at '{path}': padded table offset {padded} points outside the directory table " +
                        $"(table at {dirStart}, size {tableSize}, image length {fs.Length}).");
                }

                lOffset = padded;
                fs.Seek(padSeek, SeekOrigin.Begin);
                continue;
            }
            else if (tmp == Constants.EmptyDirectorySentinel)
            {
                // 0x0000 may be a valid left child offset (no left child) for a real entry,
                // or it may be the xdvdfs empty-directory sentinel (14 bytes all zeros).
                // Peek the remaining 12 bytes of the header to distinguish.
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
                {
                    if (lOffset == 0)
                    {
                        if (mode == ExtractMode.GenerateAvl)
                        {
                            AvlTree.AvlInsert(ref avlRoot, AvlNode.EmptySubdirectory);
                        }

                        goto end_traverse;
                    }

                    // Same full-precision pad rounding as above (BUG-LIB-028).
                    var paddedZero = (lOffset * Constants.DwordSize) +
                                     (Constants.SectorSize -
                                      ((lOffset * Constants.DwordSize) % Constants.SectorSize));
                    var padSeekZero = dirStart + paddedZero;
                    if (padSeekZero < dirStart || padSeekZero >= fs.Length ||
                        (tableSize != long.MaxValue && padSeekZero >= dirStart + tableSize))
                    {
                        throw new XisoFormatException(
                            $"invalid TOC entry at '{path}': padded table offset {paddedZero} points outside the directory table " +
                            $"(table at {dirStart}, size {tableSize}, image length {fs.Length}).");
                    }

                    lOffset = paddedZero;
                    fs.Seek(padSeekZero, SeekOrigin.Begin);
                    continue;
                }
                else
                {
                    lOffset = tmp;
                }
            }
            else
            {
                lOffset = tmp;
            }

            ReadExact(fs, shortBuf);
            var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            ReadExact(fs, intBuf);
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            var attributes = Constants.MaskAttributes(byteBuf[0]);

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (string.Equals(filename, ".", StringComparison.Ordinal) ||
                string.Equals(filename, "..", StringComparison.Ordinal) ||
                filename.Contains('/') || filename.Contains('\\'))
            {
                Logger.LogErr($"filename '{filename}' contains invalid character(s), aborting.\n");
                throw new XisoFormatException($"Filename '{filename}' contains invalid character(s).");
            }

            if (mode == ExtractMode.GenerateAvl)
            {
                // BUG-LIB-034: carry the masked attribute bits so a rewrite
                // re-encodes RO/HID/SYS instead of normalizing to Archive.
                var avl = new AvlNode
                {
                    Filename = filename, FileSize = fileSize, OldStartSector = startSector,
                    Attributes = attributes
                };
                dir.AvlNode = avl;

                // XISO names are case-insensitive: a tree holding both cases
                // cannot round-trip, so fail loudly instead of dropping one
                // (BUG-LIB-023). The shared empty-directory sentinel inserts
                // above stay best-effort by design (idempotent re-insert).
                if (AvlTree.AvlInsert(ref avlRoot, avl) == AvlResult.AvlError)
                {
                    throw new XisoFormatException(
                        $"invalid TOC entry at '{path}': duplicate filename '{filename}' (names are case-insensitive).");
                }
            }

            if (lOffset != 0)
            {
                llCompat = false;

                var leftSeek = dirStart + ((long)lOffset * Constants.DwordSize);
                if (leftSeek < dirStart || leftSeek >= fs.Length ||
                    (tableSize != long.MaxValue && leftSeek >= dirStart + tableSize))
                {
                    throw new XisoFormatException(
                        $"invalid TOC entry at '{path}{filename}': left child offset {lOffset} (seek {leftSeek}) " +
                        $"points outside the directory table (table at {dirStart}, size {tableSize}, image length {fs.Length}).");
                }

                var left = new DirEntry();
                dir.Left = left;
                left.Parent = dir;

                fs.Seek(leftSeek, SeekOrigin.Begin);

                // A corrupt left subtree skips just that subtree under
                // continue-on-error (the current entry and right siblings still
                // process: every sibling seek below is absolute, so recovery is
                // position-safe); otherwise the named error aborts the run.
                try
                {
                    var savedDir = dir.Left!;
                    TraverseXiso(fs, savedDir, dirStart, path, mode, ref avlRoot, llCompat, discLseek,
                        unpackOptions, cancellationToken, progress, visited, tableSize, depth + 1, filesystem);
                }
                catch (Exception ex) when (unpackOptions?.ContinueOnError == true && mode == ExtractMode.Extract &&
                                           ex is not OperationCanceledException)
                {
                    var failure = ex as ExtractFileException
                                  ?? ExtractFileException.ForToc(string.Concat(path, filename), filename, 0, 0, ex);
                    unpackOptions.RecordFailure(failure);
                    Logger.LogErr($"Error: {failure.Message}\n");
                }
            }

            dir.Left = null;
            var curpos = fs.Position;

            if ((attributes & Constants.AttributeDir) != 0)
            {
                // Destination-relative paths chain with '/' under a custom
                // filesystem; the legacy chdir walk keeps the host separator.
                var sep = filesystem != null ? "/" : Constants.PathCharStr;
                string subPath = null!;
                if (path != null)
                {
                    subPath = path + filename + sep;
                    fs.Seek(((long)startSector * Constants.SectorSize) + discLseek, SeekOrigin.Begin);
                }

                if (!Logger.RemoveSystemUpdate || !filename.Contains("$SystemUpdate", StringComparison.Ordinal))
                {
                    // Under continue-on-error an uncreatable directory records a
                    // named failure and skips its whole subtree (xdvdfs #187);
                    // otherwise the error aborts the run as before.
                    var dirOk = true;
                    if (mode == ExtractMode.Extract)
                    {
                        try
                        {
                            if (filesystem != null)
                            {
                                filesystem.CreateDirectory(string.Concat(path, filename));
                            }
                            else
                            {
                                Directory.CreateDirectory(filename);
                                Directory.SetCurrentDirectory(filename);
                            }
                        }
                        catch (Exception ex) when (unpackOptions?.ContinueOnError == true &&
                                                   ex is not OperationCanceledException)
                        {
                            var failure = ex as ExtractFileException
                                          ?? ExtractFileException.ForDirectory(string.Concat(path, filename),
                                              filesystem != null ? string.Concat(path, filename) : filename,
                                              ex);
                            unpackOptions.RecordFailure(failure);
                            Logger.LogErr($"Error: {failure.Message}\n");
                            dirOk = false;
                        }
                    }

                    if (dirOk)
                    {
                        if (mode != ExtractMode.GenerateAvl)
                        {
                            Logger.Log(
                                $"{mode switch { ExtractMode.Extract => "creating ", _ => "" }}{path}{filename}{sep} (0 bytes){mode switch { ExtractMode.Extract => " [OK]", _ => "" }}\n");
                            Logger.Flush();
                        }

                        if (fileSize > 0)
                        {
                            // Hardening (#16): validate the subdirectory pointer before
                            // descending: a corrupt start sector/size previously
                            // sent the walk into garbage (or past EOF) and hung
                            // or overflowed the stack on cycles (Burnout PAL).
                            var subStart = ((long)startSector * Constants.SectorSize) + discLseek;
                            if (subStart < 0 || subStart >= fs.Length)
                            {
                                throw new XisoFormatException(
                                    $"invalid TOC entry at '{subPath}': directory start sector {startSector} " +
                                    $"(seek {subStart}) points outside the image (length {fs.Length}).");
                            }

                            if (fileSize > (ulong)(fs.Length - subStart))
                            {
                                throw new XisoFormatException(
                                    $"invalid TOC entry at '{subPath}': directory size {fileSize} " +
                                    $"(ends at {subStart + fileSize}) exceeds image length {fs.Length}).");
                            }

                            var subdir = new DirEntry
                            {
                                Left = dir.Left,
                                Parent = null,
                                AvlNode = dir.AvlNode,
                                Filename = dir.Filename,
                                ROffset = dir.ROffset,
                                Attributes = dir.Attributes,
                                FilenameLength = dir.FilenameLength,
                                FileSize = dir.FileSize,
                                StartSector = dir.StartSector
                            };

                            // A corrupt subdirectory skips just that subtree
                            // under continue-on-error while siblings continue
                            // (sibling seeks are absolute, so recovery is
                            // position-safe); otherwise the named error aborts.
                            var subAvlRoot = mode == ExtractMode.GenerateAvl ? dir.AvlNode?.Subdirectory : null;
                            try
                            {
                                TraverseXiso(
                                    fs, subdir,
                                    subStart,
                                    subPath, mode,
                                    ref mode == ExtractMode.GenerateAvl
                                        ? ref dir.AvlNode!.Subdirectory
                                        : ref subAvlRoot,
                                    llCompat, discLseek, unpackOptions, cancellationToken, progress,
                                    null, fileSize, depth + 1, filesystem);
                            }
                            catch (Exception ex) when (unpackOptions?.ContinueOnError == true &&
                                                       mode == ExtractMode.Extract &&
                                                       ex is not OperationCanceledException)
                            {
                                var failure = ex as ExtractFileException
                                              ?? ExtractFileException.ForToc(subPath, filename, startSector,
                                                  fileSize, ex);
                                unpackOptions.RecordFailure(failure);
                                Logger.LogErr($"Error: {failure.Message}\n");
                            }
                        }

                        if (mode == ExtractMode.Extract && filesystem == null)
                        {
                            Directory.SetCurrentDirectory("..");
                        }
                    }
                }
            }
            else if (mode != ExtractMode.GenerateAvl)
            {
                if (!Logger.RemoveSystemUpdate || !(path?.Contains("$SystemUpdate", StringComparison.Ordinal) ?? false))
                {
                    // A failed file still advances the sibling chain: every entry
                    // seeks explicitly (ExtractFile seeks to its sector, siblings
                    // seek from the saved position), so recording and carrying on
                    // is position-safe. Failed files are excluded from the totals.
                    var fileOk = true;
                    if (mode == ExtractMode.Extract)
                    {
                        bool written;
                        try
                        {
                            written = ExtractFile(fs, filename, startSector, fileSize, path, discLseek,
                                unpackOptions, cancellationToken, progress, filesystem);
                        }
                        catch (Exception ex) when (unpackOptions?.ContinueOnError == true &&
                                                   ex is not OperationCanceledException)
                        {
                            var failure = ex as ExtractFileException
                                          ?? ExtractFileException.ForWrite(string.Concat(path, filename), filename,
                                              startSector, fileSize, -1, ex);
                            unpackOptions.RecordFailure(failure);
                            Logger.LogErr($"Error: {failure.Message}\n");
                            written = false;
                            fileOk = false;
                        }

                        if (written)
                        {
                            progress?.Report(new ProgressInfo(ProgressInfoType.FileAdded,
                                Path: string.Concat(path, filename).Replace('\\', '/'),
                                Sector: startSector, Size: fileSize));
                        }
                    }
                    else
                    {
                        Logger.Log($"{path!}{filename} ({fileSize} bytes)\n");
                        Logger.Flush();
                    }

                    if (fileOk)
                    {
                        Logger.RecordIsoFileWritten(fileSize);
                    }
                }
            }

            if (rOffset != 0)
            {
                if (llCompat)
                {
                    var sector = (curpos - dirStart) / Constants.SectorSize;
                    if ((long)rOffset * Constants.DwordSize / Constants.SectorSize > sector)
                    {
                        rOffset = (ushort)((sector * (Constants.SectorSize / Constants.DwordSize)) +
                                           (Constants.SectorSize / Constants.DwordSize));
                    }
                }

                var rightSeek = dirStart + ((long)rOffset * Constants.DwordSize);
                if (rightSeek < dirStart || rightSeek >= fs.Length ||
                    (tableSize != long.MaxValue && rightSeek >= dirStart + tableSize))
                {
                    throw new XisoFormatException(
                        $"invalid TOC entry at '{path}': right child offset {rOffset} (seek {rightSeek}) " +
                        $"points outside the directory table (table at {dirStart}, size {tableSize}, image length {fs.Length}).");
                }

                fs.Seek(rightSeek, SeekOrigin.Begin);

                dir.Filename = "";
                lOffset = rOffset;

                continue;
            }

            break;
        }

        end_traverse: ;
    }

    /// <summary>
    /// Extracts a single file from the XISO stream to the current working directory,
    /// reporting progress via the logger.
    /// </summary>
    /// <param name="fs">File stream positioned at the file's starting sector.</param>
    /// <param name="filename">Name of the file to create.</param>
    /// <param name="startSector">Sector index where the file data begins.</param>
    /// <param name="fileSize">Reported size of the file in bytes.</param>
    /// <param name="path">Path prefix for progress logging.</param>
    /// <param name="discLseek">Disc lseek offset for sector address calculation.</param>
    /// <param name="unpackOptions">
    /// Optional resume options; when <see cref="UnpackOptions.SkipExisting"/> is set and
    /// the file already exists with the same size, it is left untouched and logged as
    /// <c>skip: &lt;path&gt;</c> (TODO #13, xdvdfs #190).
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">
    /// Optional structured progress channel; receives a per-chunk
    /// <see cref="ProgressInfoType.FileProgress"/> event while the file copies.
    /// </param>
    /// <param name="filesystem">
    /// Optional destination filesystem (TODO #7): when set, the file is created
    /// through <see cref="IFilesystem.CreateFile"/> at the destination-root-relative
    /// path (<c>path</c> + <c>filename</c>) and the skip/post-write checks probe the
    /// same filesystem. When <c>null</c>, the file lands in the process working
    /// directory under its bare name (legacy chdir-based behavior).
    /// </param>
    /// <returns><c>true</c> when the file was written, <c>false</c> when it was skipped or excluded.</returns>
    /// <exception cref="ExtractFileException">
    /// Thrown naming the entry, its sector, and expected vs actual bytes when the
    /// destination cannot be created, the image data ends early (TODO #9, xdvdfs #187),
    /// or a write fails — replacing the old truncate-and-warn path, which also
    /// spun forever on a 0-byte read at end of image.
    /// </exception>
    internal static bool ExtractFile(
        Stream fs,
        string filename,
        uint startSector,
        uint fileSize,
        string? path,
        long discLseek,
        UnpackOptions? unpackOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null,
        IFilesystem? filesystem = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Logger.RemoveSystemUpdate && path?.Contains("$SystemUpdate", StringComparison.Ordinal) == true)
        {
            fs.Seek(((long)startSector * Constants.SectorSize) + discLseek, SeekOrigin.Begin);
            return false;
        }

        var internalPath = string.Concat(path, filename);

        // Destination in the filesystem's own path form: for a custom
        // IFilesystem that is the image-internal path ("sub/file.bin"); the
        // legacy path keeps the cwd-relative bare name.
        var dest = filesystem != null ? internalPath : filename;

        if (unpackOptions?.ShouldSkip(dest, fileSize, filesystem ?? LocalFilesystem.Instance) == true)
        {
            Logger.Log($"skip: {path}{filename} ({fileSize} bytes)\n");
            Logger.Flush();
            return false;
        }

        // Integrity pre-check: the entry's data range must lie inside the image.
        // Catches torn images and entries pointing past the end before an empty
        // destination file is created for them. A length that cannot be resolved
        // (write-only view) skips the pre-check; the copy loop still validates.
        if (fileSize > 0 && fs.CanSeek)
        {
            try
            {
                var imageLength = fs.Length;
                var dataEnd = ((long)startSector * Constants.SectorSize) + discLseek + fileSize;
                if (dataEnd > imageLength)
                {
                    throw ExtractFileException.ForTruncated(internalPath, filename, startSector, fileSize,
                        Math.Max(0, imageLength - (((long)startSector * Constants.SectorSize) + discLseek)));
                }
            }
            catch (ExtractFileException)
            {
                throw;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ObjectDisposedException)
            {
                // Length unresolvable: fall through to the copy-loop check below.
            }
        }

        Stream outFile;
        try
        {
            if (filesystem != null)
            {
                outFile = filesystem.CreateFile(dest);
            }
            else
            {
                outFile = new FileStream(
                    filename,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536
                    });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ExtractFileException.ForCreate(internalPath, dest, startSector, fileSize, ex);
        }

        uint totalSize = 0;
        try
        {
            using (outFile)
            {
                fs.Seek(((long)startSector * Constants.SectorSize) + discLseek, SeekOrigin.Begin);

                if (fileSize == 0)
                {
                    Logger.Log(
                        $"extracting {path}{filename} (0 bytes) [100%]{(Logger.Out == Console.Out && Console.IsOutputRedirected ? "\n" : "\r")}");
                    Logger.Flush();
                }
                else
                {
                    // Scenario-tuned copy (#8): small files finish in one chunk,
                    // large files stream per chunk with byte-level progress. The
                    // rented pooled buffer keeps chunking (and therefore
                    // the percent log sequence) identical to the old inline loop.
                    var progressPath = string.Concat(path, filename).Replace('\\', '/');
                    var copyBuffer = RentCopyBuffer();
                    try
                    {
                        XisoFileCopier.CopyExact(
                            fs,
                            fileSize,
                            // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
                            (buffer, count) =>
                            {
                                // Write-then-count matches the old inline loop, so
                                // ForWrite/ForTruncated carry identical byte counts.
                                outFile.Write(buffer, 0, count);
                                totalSize += (uint)count;
                            },
                            copyBuffer,
                            copied =>
                            {
                                var percent = (uint)(copied * 100.0 / fileSize);
                                Logger.Log(
                                    $"extracting {path}{filename} ({fileSize} bytes) [{percent}%]{(Logger.Out == Console.Out && Console.IsOutputRedirected ? "\n" : "\r")}");
                                Logger.Flush();
                                progress?.Report(new ProgressInfo(ProgressInfoType.FileProgress,
                                    Count: fileSize, Path: progressPath, Sector: startSector, Size: copied));
                            },
                            cancellationToken);
                    }
                    catch (TruncatedCopyException)
                    {
                        throw ExtractFileException.ForTruncated(internalPath, filename, startSector, fileSize,
                            totalSize);
                    }
                    finally
                    {
                        ReturnCopyBuffer(copyBuffer);
                    }
                }
            }

            // Post-write integrity: the bytes written must equal the reported size.
            // Catches torn writes and anything that truncated the file behind us.
            var writtenLength = filesystem != null ? filesystem.FileLength(dest) : new FileInfo(filename).Length;
            if (writtenLength != fileSize)
                throw ExtractFileException.ForTruncated(internalPath, dest, startSector, fileSize, totalSize);
        }
        catch (ExtractFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ExtractFileException.ForWrite(internalPath, filename, startSector, fileSize, totalSize, ex);
        }

        Logger.Log("\n");
        return true;
    }

    /// <summary>
    /// Rewrites (optimizes) an XISO image. The source ISO is renamed to <c>.old</c>
    /// and a new optimized ISO is created in its place.
    /// Always uses <c>llCompat=true</c> to handle linked-list-style directory entries.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file to rewrite, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="outputPath">Output directory for the rewritten ISO, or <c>null</c> for the current directory.</param>
    /// <param name="outIsoPath">Receives the path to the output ISO file.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">
    /// Custom output filename. When <c>null</c>, the original filename with <c>.iso</c> extension is used.
    /// </param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="prependSectors">
    /// Optional number of 2048-byte sectors to prepend to the output image, leaving room
    /// for a video partition. Sector numbers inside the image remain partition-relative.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives <see cref="ProgressInfo"/> events
    /// during the rewrite write phase.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Rewrite(
        string xisoPath,
        string? outputPath,
        out string? outIsoPath,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null) =>
        DecodeXiso(xisoPath, outputPath, ExtractMode.Rewrite, out outIsoPath, true, cancellationToken,
            outputName, skipSectors, prependSectors, progress);

    /// <summary>
    /// Extracts files from an XISO image to a directory.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="outputPath">Output directory, or <c>null</c> to extract to an ISO-named subdirectory.</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="options">
    /// Optional resume options; when <see cref="UnpackOptions.SkipExisting"/> is set,
    /// files already on disk with the same size are skipped (TODO #13, xdvdfs #190).
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives a <see cref="ProgressInfoType.FileAdded"/>
    /// event for each file actually written.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Extract(
        string xisoPath,
        string? outputPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null) =>
        DecodeXiso(xisoPath, outputPath, ExtractMode.Extract, out _, llCompat, cancellationToken,
            skipSectors: skipSectors, progress: progress, unpackOptions: options);

    /// <summary>
    /// Stream-based <c>Extract</c>: extracts an already-open image
    /// without taking the input path. The stream must be readable + seekable
    /// and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name for output naming and messages.</param>
    /// <param name="outputPath">Destination directory (<c>null</c> = derive from <paramref name="imageName"/>).</param>
    /// <param name="llCompat">Backwards-compatible right-offset calculation.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">Optional skip before the XISO filesystem begins.</param>
    /// <param name="options">Optional resume options (<see cref="UnpackOptions.SkipExisting"/>).</param>
    /// <param name="progress">Optional structured progress channel.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Extract(
        Stream imageStream,
        string imageName,
        string? outputPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null) =>
        DecodeXiso(imageStream, imageName, outputPath, ExtractMode.Extract, out _, llCompat,
            cancellationToken, skipSectors: skipSectors, progress: progress, unpackOptions: options);

    /// <summary>
    /// Unpacks an entire XISO image to a directory.
    /// The optimized-tag marker is probed automatically, so callers do not need to know
    /// the image layout (unlike <c>Extract</c>, which takes <c>llCompat</c>).
    /// </summary>
    /// <param name="isoPath">Path to the XISO file, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="outputPath">
    /// Destination directory. When <c>null</c>, a directory named after the ISO file
    /// (without the <c>.iso</c> extension) is created in the current directory.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="options">
    /// Optional resume options; when <see cref="UnpackOptions.SkipExisting"/> is set,
    /// files already on disk with the same size are skipped, so an interrupted unpack
    /// resumes instead of redoing completed files (TODO #13, xdvdfs #190).
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives a <see cref="ProgressInfoType.FileAdded"/>
    /// event for each file actually written.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="XisoFormatException">
    /// Thrown when the file is not a valid XISO image.
    /// </exception>
    /// <exception cref="XisoEmptyException">
    /// Thrown when the XISO image contains no files.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the input file does not exist.</exception>
    public static int UnpackImage(
        string isoPath,
        string? outputPath = null,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null)
    {
        if (skipSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                "Skip sectors must be non-negative.");
        }

        return Extract(isoPath, outputPath, !IsOptimized(isoPath, skipSectors), cancellationToken, skipSectors,
            options, progress);
    }

    /// <summary>
    /// Stream-based <see cref="UnpackImage(string, string?, CancellationToken, int?, UnpackOptions?, IProgress{ProgressInfo}?)"/>:
    /// unpacks an already-open image, probing the optimized tag from the stream.
    /// The stream must be readable + seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name for output naming and messages.</param>
    /// <param name="outputPath">Destination directory (<c>null</c> = derive from <paramref name="imageName"/>).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">Optional skip before the XISO filesystem begins.</param>
    /// <param name="options">Optional resume options (<see cref="UnpackOptions.SkipExisting"/>).</param>
    /// <param name="progress">Optional structured progress channel.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int UnpackImage(
        Stream imageStream,
        string imageName,
        string? outputPath = null,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null)
    {
        if (skipSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                "Skip sectors must be non-negative.");
        }

        return Extract(imageStream, imageName, outputPath, !IsOptimizedImage(imageStream, skipSectors),
            cancellationToken, skipSectors, options, progress);
    }

    /// <summary>
    /// Filesystem-based <see cref="UnpackImage(string, string?, CancellationToken, int?, UnpackOptions?, IProgress{ProgressInfo}?)"/>:
    /// unpacks into any <see cref="IFilesystem"/> destination — <see cref="LocalFilesystem"/>
    /// for disk, <see cref="MemoryFilesystem"/> for in-memory, or a custom store
    /// (zip archive, network upload, GUI preview; TODO #7, xdvdfs #166).
    /// Files land at the filesystem root: no ISO-named subdirectory is created and
    /// the process working directory never changes. The optimized-tag marker is
    /// probed automatically.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="filesystem">Destination filesystem receiving the unpacked tree.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="options">
    /// Optional resume options; when <see cref="UnpackOptions.SkipExisting"/> is set,
    /// files already present in <paramref name="filesystem"/> with the same size are
    /// skipped, so an interrupted unpack resumes instead of redoing completed files.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives a <see cref="ProgressInfoType.FileAdded"/>
    /// event for each file actually written plus per-chunk <see cref="ProgressInfoType.FileProgress"/>
    /// while each file copies.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filesystem"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="skipSectors"/> is negative.</exception>
    /// <exception cref="XisoFormatException">
    /// Thrown when the file is not a valid XISO image.
    /// </exception>
    /// <exception cref="XisoEmptyException">
    /// Thrown when the XISO image contains no files.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the input file does not exist.</exception>
    public static int UnpackImage(
        string isoPath,
        IFilesystem filesystem,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(filesystem);
        if (skipSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                "Skip sectors must be non-negative.");
        }

        using var fs = OpenImageStream(isoPath);
        return DecodeXisoCore(fs, isoPath, null, ExtractMode.Extract, out _,
            !IsOptimizedImage(fs, skipSectors), cancellationToken, null, skipSectors, null, progress, options,
            filesystem);
    }

    /// <summary>
    /// Stream+filesystem <see cref="UnpackImage(Stream, string, string?, CancellationToken, int?, UnpackOptions?, IProgress{ProgressInfo}?)"/>:
    /// unpacks an already-open image into any <see cref="IFilesystem"/> destination
    /// (TODO #7). The stream must be readable + seekable and is left open; files land
    /// at the filesystem root.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name for messages.</param>
    /// <param name="filesystem">Destination filesystem receiving the unpacked tree.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">Optional skip before the XISO filesystem begins.</param>
    /// <param name="options">Optional resume options (<see cref="UnpackOptions.SkipExisting"/>).</param>
    /// <param name="progress">Optional structured progress channel.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filesystem"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="imageStream"/> is not readable + seekable.
    /// </exception>
    public static int UnpackImage(
        Stream imageStream,
        string imageName,
        IFilesystem filesystem,
        CancellationToken cancellationToken = default,
        int? skipSectors = null,
        UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(filesystem);
        if (skipSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                "Skip sectors must be non-negative.");
        }

        return DecodeXisoCore(imageStream, imageName, null, ExtractMode.Extract, out _,
            !IsOptimizedImage(imageStream, skipSectors), cancellationToken, null, skipSectors, null, progress,
            options, filesystem);
    }

    /// <summary>
    /// Returns <c>true</c> when the image carries the extract-xiso optimized tag
    /// at byte offset 31337 (shifted by the skip offset when reading offset images),
    /// meaning it uses the optimized directory layout. <c>.cso</c> paths are probed
    /// through the decompressed view.
    /// </summary>
    public static bool IsOptimizedImage(string isoPath, int? skipSectors = null)
    {
        using var fs = OpenImageStream(isoPath);
        return IsOptimizedImage(fs, skipSectors);
    }

    /// <summary>
    /// Stream-based <see cref="IsOptimizedImage(string, int?)"/>: probes the
    /// optimized tag on an already-open stream and restores its position.
    /// </summary>
    /// <param name="imageStream">Open image stream; must be readable and seekable.</param>
    /// <param name="skipSectors">Optional skip applied before the tag offset.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="imageStream"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="imageStream"/> is not readable + seekable.
    /// </exception>
    public static bool IsOptimizedImage(Stream imageStream, int? skipSectors = null)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (!imageStream.CanRead || !imageStream.CanSeek)
            throw new ArgumentException("Image stream must be readable and seekable.", nameof(imageStream));

        var pos = imageStream.Position;
        try
        {
            imageStream.Seek(((long)(skipSectors ?? 0) * Constants.SectorSize) + Constants.OptimizedTagOffset,
                SeekOrigin.Begin);
            Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTagLength];
            if (imageStream.Read(tagBuf) != Constants.OptimizedTagLength)
            {
                return false;
            }

            var tag = Encoding.ASCII.GetString(tagBuf);
            return tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin],
                StringComparison.Ordinal);
        }
        finally
        {
            imageStream.Seek(pos, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the image carries the extract-xiso optimized tag
    /// at byte offset 31337 (shifted by the skip offset when reading offset images),
    /// meaning it uses the optimized directory layout.
    /// </summary>
    private static bool IsOptimized(string isoPath, int? skipSectors = null) => IsOptimizedImage(isoPath, skipSectors);

    /// <summary>
    /// Lists files in an XISO image without extracting.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int List(
        string xisoPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null) =>
        DecodeXiso(xisoPath, null, ExtractMode.List, out _, llCompat, cancellationToken,
            skipSectors: skipSectors);

    /// <summary>
    /// Stream-based <c>List</c>: lists files of an already-open image.
    /// The stream must be readable + seekable and is left open.
    /// </summary>
    public static int List(
        Stream imageStream,
        string imageName,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null) =>
        DecodeXiso(imageStream, imageName, null, ExtractMode.List, out _, llCompat,
            cancellationToken, skipSectors: skipSectors);

    /// <summary>
    /// Recursively lists all files in an XISO image in a tree format,
    /// showing full paths and sizes for each entry.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file, or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Tree(
        string xisoPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null) =>
        DecodeXiso(xisoPath, null, ExtractMode.Tree, out _, llCompat, cancellationToken,
            skipSectors: skipSectors);

    /// <summary>
    /// Stream-based <c>Tree</c>: tree-lists an already-open image.
    /// The stream must be readable + seekable and is left open.
    /// </summary>
    public static int Tree(
        Stream imageStream,
        string imageName,
        bool llCompat,
        CancellationToken cancellationToken = default,
        int? skipSectors = null) =>
        DecodeXiso(imageStream, imageName, null, ExtractMode.Tree, out _, llCompat,
            cancellationToken, skipSectors: skipSectors);

    /// <summary>
    /// Main entry point for processing an XISO image. Verifies the image, then
    /// performs extraction, listing, or rewriting based on the specified mode.
    /// Prefer using <see cref="Rewrite"/>, <c>Extract</c>, or <c>List</c>
    /// for mode-specific operations.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file (or <c>.old</c> file for rewrite mode),
    /// or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="outputPath">
    /// Output directory for extraction or rewrite output.
    /// When <c>null</c> in extract mode, a directory named after the ISO is created.
    /// </param>
    /// <param name="mode">Operating mode: extract, list, or rewrite.</param>
    /// <param name="outIsoPath">
    /// Receives the path to the output ISO file when in rewrite mode.
    /// </param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">
    /// Custom output filename for rewrite mode. When <c>null</c>, the original filename with <c>.iso</c> extension is used.
    /// Ignored in non-rewrite modes.
    /// </param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="prependSectors">
    /// Optional number of 2048-byte sectors to prepend to the output image in rewrite mode,
    /// leaving room for a video partition. Sector numbers inside the image remain
    /// partition-relative. Ignored in non-rewrite modes.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives <see cref="ProgressInfo"/> events
    /// during the rewrite write phase, and <see cref="ProgressInfoType.FileAdded"/> for
    /// each file actually written in extract mode. Ignored in list/tree modes.
    /// </param>
    /// <param name="unpackOptions">
    /// Optional resume options for extract mode; when <see cref="UnpackOptions.SkipExisting"/>
    /// is set, files already on disk with the same size are skipped (TODO #13, xdvdfs #190).
    /// Ignored in non-extract modes.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="XisoFormatException">
    /// Thrown when the file is not a valid XISO image.
    /// </exception>
    /// <exception cref="XisoEmptyException">
    /// Thrown when the XISO image contains no files.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the input file does not exist.</exception>
    public static int DecodeXiso(
        string xisoPath,
        string? outputPath,
        ExtractMode mode,
        out string? outIsoPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null,
        UnpackOptions? unpackOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fs = OpenImageStream(xisoPath);
        return DecodeXisoCore(fs, xisoPath, outputPath, mode, out outIsoPath, llCompat,
            cancellationToken, outputName, skipSectors, prependSectors, progress, unpackOptions);
    }

    /// <summary>
    /// Stream-based <c>DecodeXiso</c>: processes an already-open image
    /// (file, memory, or any readable + seekable stream) without taking the
    /// input path. The caller's stream is left open and positioned wherever
    /// the read phase ends. Rewrite mode is intentionally unavailable here —
    /// it is file-identity based (the <c>.old</c> dance).
    /// </summary>
    /// <param name="imageStream">Open image stream; must be readable and seekable.</param>
    /// <param name="imageName">
    /// Display name for output naming and messages (typically the file name,
    /// e.g. <c>game.iso</c>); extract-to-default-directory derives from it.
    /// </param>
    /// <param name="outputPath">
    /// Output directory for extraction.
    /// When <c>null</c> in extract mode, a directory named after
    /// <paramref name="imageName"/> is created.
    /// </param>
    /// <param name="mode">Operating mode: extract or list (rewrite is refused).</param>
    /// <param name="outIsoPath">Always <c>null</c> (rewrite is refused).</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">Ignored (rewrite is refused).</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the stream before the XISO
    /// filesystem begins.
    /// </param>
    /// <param name="prependSectors">Ignored (rewrite is refused).</param>
    /// <param name="progress">
    /// Optional structured progress channel; receives <see cref="ProgressInfoType.FileAdded"/>
    /// for each file actually written in extract mode. Ignored in list/tree modes.
    /// </param>
    /// <param name="unpackOptions">
    /// Optional resume options for extract mode; when <see cref="UnpackOptions.SkipExisting"/>
    /// is set, files already on disk with the same size are skipped (TODO #13, xdvdfs #190).
    /// Ignored in non-extract modes.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="imageStream"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="imageStream"/> is not readable + seekable,
    /// when <paramref name="mode"/> is rewrite, or when <paramref name="outputPath"/> is empty.
    /// </exception>
    /// <exception cref="XisoFormatException">
    /// Thrown when the stream is not a valid XISO image.
    /// </exception>
    /// <exception cref="XisoEmptyException">
    /// Thrown when the XISO image contains no files.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static int DecodeXiso(
        Stream imageStream,
        string imageName,
        string? outputPath,
        ExtractMode mode,
        out string? outIsoPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null,
        UnpackOptions? unpackOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(imageStream);
        if (!imageStream.CanRead || !imageStream.CanSeek)
            throw new ArgumentException("Image stream must be readable and seekable.", nameof(imageStream));
        if (mode == ExtractMode.Rewrite)
        {
            throw new ArgumentException("Rewrite mode requires a file path; use DecodeXiso(string, ...).",
                nameof(mode));
        }

        return DecodeXisoCore(imageStream, imageName, outputPath, mode, out outIsoPath, llCompat,
            cancellationToken, outputName, skipSectors, prependSectors, progress, unpackOptions);
    }

    /// <summary>
    /// Shared engine behind both <c>DecodeXiso</c> overloads. The path overload
    /// opens (and owns) the stream; the public stream overload validates it.
    /// The trailing <c>filesystem</c> parameter (extract mode) redirects the whole
    /// unpack into an <see cref="IFilesystem"/> destination — files land at its root
    /// via <see cref="IFilesystem.CreateFile"/>/<see cref="IFilesystem.CreateDirectory"/>
    /// with no working-directory changes; <c>null</c> keeps the legacy
    /// working-directory-based extraction.
    /// </summary>
    private static int DecodeXisoCore(
        Stream imageStream,
        string imageName,
        string? outputPath,
        ExtractMode mode,
        out string? outIsoPath,
        bool llCompat,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null,
        UnpackOptions? unpackOptions = null,
        IFilesystem? filesystem = null)
    {
        outIsoPath = null;

        // Batch scripts can pass an empty -d (`-d "%UNSET_VAR%"`): fail fast
        // with a named error instead of an IndexOutOfRangeException deep in
        // path-prefix building or a BCL ArgumentException from CreateDirectory.
        if (outputPath?.Length == 0)
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

        var filename = imageName;

        if (mode == ExtractMode.Rewrite)
        {
            filename = StripRewriteSuffix(filename);
        }

        var nameStart = filename.LastIndexOf(Constants.PathChar) + 1;
        var name = filename[nameStart..];
        var len = name.Length;

        string? shortName = null;
        switch (len)
        {
            case > 4 when string.Equals(name[^4..], ".iso", StringComparison.OrdinalIgnoreCase):
                shortName = name[..^4];
                break;
            case 0:
                Logger.LogErr($"invalid xiso image name: {imageName}\n");
                return 1;
        }

        // A .cso input names its outputs after the game, not the container.
        if (shortName == null && len > 4 && IsCsoPath(name))
            shortName = StripCsoSuffix(name);

        string? cwd = null;

        // The caller's stream stays open: extraction reads through it under the
        // destination-directory chdir below, and ownership never transfers.
        var fs = imageStream;

        (var rootDirSect, var rootDirSize, var discLseek) = VerifyXiso(fs, name, skipSectors);

        Logger.XboxDiscLseek = discLseek;

        // Change into the output directory only after the image verified successfully,
        // so an invalid image can never leave the process working directory modified.
        // A custom destination filesystem replaces the chdir entirely: its root is
        // materialized instead, then everything is created relative to it.
        if (mode == ExtractMode.Extract && outputPath != null && filesystem == null)
        {
            cwd = Directory.GetCurrentDirectory();
            try
            {
                Directory.CreateDirectory(outputPath);
                Directory.SetCurrentDirectory(outputPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.LogErr($"Error: permission denied: {outputPath}\n");
                throw new IOException($"Permission denied: {outputPath}", ex);
            }
            catch (IOException ex)
            {
                Logger.LogErr($"Error: cannot access output directory: {outputPath}: {ex.Message}\n");
                throw;
            }
        }
        else if (mode == ExtractMode.Extract && filesystem != null)
        {
            filesystem.CreateDirectory("/");
        }

        var isoName = shortName ?? name;

        // Everything below may change the process working directory (extract chdirs
        // into the destination), so it runs under try/finally: an interrupted run
        // (cancellation, disk error) must still restore the caller's directory.
        try
        {
            if (mode != ExtractMode.Rewrite)
            {
                Logger.Log($"{(mode == ExtractMode.Extract ? "extracting" : "listing")} {name}:\n\n");

                if (mode == ExtractMode.Extract && outputPath == null && filesystem == null)
                {
                    try
                    {
                        Directory.CreateDirectory(isoName);
                        // Capture the caller's directory so the finally below restores it:
                        // without this, multi-image runs (e.g. --batch) would resolve
                        // every later image relative to this ISO's subdirectory.
                        cwd ??= Directory.GetCurrentDirectory();
                        Directory.SetCurrentDirectory(isoName);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Logger.LogErr($"Error: permission denied: {isoName}\n");
                        throw new IOException($"Permission denied: {isoName}", ex);
                    }
                    catch (IOException ex)
                    {
                        Logger.LogErr($"Error: cannot create output directory: {isoName}: {ex.Message}\n");
                        throw;
                    }
                }
            }

            if (rootDirSect != 0 && rootDirSize != 0)
            {
                var addSlash = 0;
                if (outputPath != null && outputPath[^1] != Constants.PathChar)
                {
                    addSlash = 1;
                }

                var buf = string.Concat(
                    outputPath ?? "",
                    addSlash != 0 && outputPath == null ? Constants.PathCharStr : "",
                    mode != ExtractMode.List && outputPath == null ? isoName : "",
                    Constants.PathCharStr);

                if (mode == ExtractMode.Rewrite)
                {
                    fs.Seek(((long)rootDirSect * Constants.SectorSize) + discLseek, SeekOrigin.Begin);
                    AvlNode? avlRoot = null;
                    TraverseXiso(fs, null, ((long)rootDirSect * Constants.SectorSize) + discLseek,
                        buf, ExtractMode.GenerateAvl, ref avlRoot, llCompat, discLseek,
                        cancellationToken: cancellationToken, tableSize: rootDirSize);

                    XisoWriter.CreateXiso(isoName, outputPath, avlRoot, fs, out outIsoPath, outputName, null,
                        cancellationToken, prependSectors: prependSectors, progress: progress,
                        sourceDiscLseek: discLseek);
                }
                else
                {
                    fs.Seek(((long)rootDirSect * Constants.SectorSize) + discLseek, SeekOrigin.Begin);
                    AvlNode? avlRoot = null;
                    try
                    {
                        TraverseXiso(fs, null, ((long)rootDirSect * Constants.SectorSize) + discLseek,
                            filesystem != null ? "/" : buf, mode, ref avlRoot, llCompat, discLseek,
                            unpackOptions, cancellationToken, progress,
                            null, rootDirSize, 0, filesystem);
                    }
                    catch (Exception ex) when (unpackOptions?.ContinueOnError == true &&
                                               mode == ExtractMode.Extract &&
                                               ex is not OperationCanceledException)
                    {
                        // A corrupt root table still yields the end-of-run
                        // summary (TODO #16 over #9) instead of an unhandled
                        // structural failure.
                        var failure = ex as ExtractFileException
                                      ?? ExtractFileException.ForToc("/", outputPath ?? isoName, rootDirSect,
                                          rootDirSize, ex);
                        unpackOptions.RecordFailure(failure);
                        Logger.LogErr($"Error: {failure.Message}\n");
                    }

                    // A continued run that hit per-file failures still fails the
                    // run: the summary names every file (xdvdfs "Failed to unpack
                    // image"), so callers and the CLI exit code see it.
                    if (mode == ExtractMode.Extract)
                        unpackOptions?.ThrowIfFailed(name);
                }
            }

            return 0;
        }
        finally
        {
            if (cwd != null)
            {
                Directory.SetCurrentDirectory(cwd);
            }
        }
    }

    /// <summary>
    /// Asynchronously processes an XISO image. Verifies the image, then
    /// performs extraction, listing, or rewriting based on the specified mode.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file (or <c>.old</c> file for rewrite mode),
    /// or a <c>.cso</c> image (auto-detected).</param>
    /// <param name="outputPath">Output directory for extraction or rewrite output. When <c>null</c> in extract mode, a directory named after the ISO is created.</param>
    /// <param name="mode">Operating mode: extract, list, or rewrite.</param>
    /// <param name="llCompat">If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">Custom output filename for rewrite mode. When <c>null</c>, the original filename with <c>.iso</c> extension is used.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip in the source file before the XISO
    /// filesystem begins (for Redump-style images with a video partition).
    /// </param>
    /// <param name="prependSectors">
    /// Optional number of 2048-byte sectors to prepend to the output image in rewrite mode,
    /// leaving room for a video partition. Sector numbers inside the image remain
    /// partition-relative.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives <see cref="ProgressInfo"/> events
    /// during the rewrite write phase, and <see cref="ProgressInfoType.FileAdded"/> for
    /// each file actually written in extract mode.
    /// </param>
    /// <param name="unpackOptions">
    /// Optional resume options for extract mode (TODO #13, xdvdfs #190).
    /// Ignored in non-extract modes.
    /// </param>
    /// <returns>A task that completes with the result code (0 on success, non-zero on error) and the output ISO path when in rewrite mode.</returns>
    public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
        string xisoPath,
        string? outputPath,
        ExtractMode mode,
        bool llCompat = false,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null,
        UnpackOptions? unpackOptions = null) =>
        await Task.Run(() =>
        {
            var result = DecodeXiso(xisoPath, outputPath, mode, out var outPath, llCompat, cancellationToken,
                outputName, skipSectors, prependSectors, progress, unpackOptions);
            return (result, outPath);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Stream-based <c>DecodeXisoAsync</c>: runs the stream
    /// <see cref="DecodeXiso(Stream, string, string?, ExtractMode, out string?, bool, CancellationToken, string?, int?, int?, IProgress{ProgressInfo}?, UnpackOptions?)"/>
    /// overload on a thread-pool thread. The stream must be readable +
    /// seekable and is left open. Rewrite mode is refused.
    /// </summary>
    public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
        Stream imageStream,
        string imageName,
        string? outputPath,
        ExtractMode mode,
        bool llCompat = false,
        CancellationToken cancellationToken = default,
        string? outputName = null,
        int? skipSectors = null,
        int? prependSectors = null,
        IProgress<ProgressInfo>? progress = null,
        UnpackOptions? unpackOptions = null) =>
        await Task.Run(() =>
        {
            var result = DecodeXiso(imageStream, imageName, outputPath, mode, out var outPath, llCompat,
                cancellationToken, outputName, skipSectors, prependSectors, progress, unpackOptions);
            return (result, outPath);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the XISO volume descriptor and returns metadata about the image
    /// without throwing on validation errors.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>Volume information including root directory location and disc format.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public static VolumeInfo GetVolumeInfo(string isoPath)
    {
        // BUG-LIB-037: route through the CISO-aware opener like every other
        // string overload — a plain FileStream probed a .cso invalid here while
        // ListDirectory/CopyOut/ComputeFileHash saw it valid.
        using var fs = OpenImageStream(isoPath);

        return GetVolumeInfo(fs, isoPath);
    }

    /// <summary>
    /// Stream overload of <see cref="GetVolumeInfo(string)"/>: probes an already-open
    /// image (plain or CISO-backed) without throwing on validation errors. The stream
    /// must be readable + seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (unused; kept for symmetry).</param>
    /// <returns>Volume information including root directory location and disc format.</returns>
    public static VolumeInfo GetVolumeInfo(Stream imageStream, string imageName = "memory")
    {
        _ = imageName;
        var fs = imageStream;
        var fileLength = fs.Length;
        var totalSectors = fileLength / Constants.SectorSize;

        if (fileLength < Constants.HeaderOffset + Constants.HeaderDataLength)
            return new VolumeInfo(false, 0, 0, 0, fileLength, totalSectors);

        Span<byte> buffer = stackalloc byte[Constants.HeaderDataLength];
        long discLseek = 0;
        var isValid = false;

        try
        {
            fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
            ReadExact(fs, buffer);

            if (buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
            {
                isValid = true;
            }
            else
            {
                fs.Seek((long)Constants.HeaderOffset + Constants.GlobalLseekOffset, SeekOrigin.Begin);
                ReadExact(fs, buffer);
                if (buffer.SequenceEqual(HeaderDataBytes))
                {
                    discLseek = Constants.GlobalLseekOffset;
                    isValid = true;
                }
                else
                {
                    fs.Seek((long)Constants.HeaderOffset + Constants.Xgd3LseekOffset, SeekOrigin.Begin);
                    ReadExact(fs, buffer);
                    if (buffer.SequenceEqual(HeaderDataBytes))
                    {
                        discLseek = Constants.Xgd3LseekOffset;
                        isValid = true;
                    }
                    else
                    {
                        fs.Seek((long)Constants.HeaderOffset + Constants.Xgd2HybridLseekOffset, SeekOrigin.Begin);
                        ReadExact(fs, buffer);
                        if (buffer.SequenceEqual(HeaderDataBytes))
                        {
                            discLseek = Constants.Xgd2HybridLseekOffset;
                            isValid = true;
                        }
                        else
                        {
                            fs.Seek((long)Constants.HeaderOffset + Constants.Xgd1LseekOffset, SeekOrigin.Begin);
                            ReadExact(fs, buffer);
                            if (buffer.SequenceEqual(HeaderDataBytes))
                            {
                                discLseek = Constants.Xgd1LseekOffset;
                                isValid = true;
                            }
                        }
                    }
                }
            }

            if (!isValid)
                return new VolumeInfo(false, 0, 0, 0, fileLength, totalSectors);

            Span<byte> intBuf = stackalloc byte[4];
            ReadExact(fs, intBuf);
            var rootDirSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var rootDirSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            return new VolumeInfo(true, rootDirSector, rootDirSize, discLseek, fileLength, totalSectors);
        }
        catch (IOException)
        {
            return new VolumeInfo(false, 0, 0, 0, fileLength, totalSectors);
        }
    }

    /// <summary>
    /// Reads the raw 64-bit Windows FILETIME stored in an XISO image header.
    /// The FILETIME is at <c>HeaderOffset+20+4+4 (+ discLseek)</c> (8 bytes LE) and counts
    /// 100ns intervals since 1601-01-01 UTC. xdvdfs generates 0; extract-xiso writes
    /// the current time via <see cref="FileTimeHelper.WriteFileTimeNow"/>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="skipSectors">
    /// Optional number of 2048-byte sectors to skip before the XISO filesystem
    /// (for Redump-style images with a video partition).
    /// </param>
    /// <returns>Raw FILETIME value (little-endian on disk).</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static ulong GetFileTimeRaw(string isoPath, int? skipSectors = null)
    {
        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 256
            });
        var discLseek = FindDiscLseekForFileTime(fs, isoPath, skipSectors);
        Span<byte> buf = stackalloc byte[8];
        fs.Seek(Constants.HeaderOffset + discLseek + Constants.HeaderDataLength + 4 + 4, SeekOrigin.Begin);
        ReadExact(fs, buf);
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    /// <summary>
    /// Reads the XISO FILETIME as a <see cref="DateTimeOffset"/> (UTC).
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="skipSectors">Optional skip sectors for Redump images.</param>
    /// <returns>UTC time; raw 0 maps to 1601-01-01.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static DateTimeOffset GetFileTime(string isoPath, int? skipSectors = null)
    {
        var raw = GetFileTimeRaw(isoPath, skipSectors);
        return FileTimeHelper.FromFileTimeRaw(raw);
    }

    /// <summary>
    /// Block-device overload of <see cref="GetFileTimeRaw(string,int?)"/>.
    /// </summary>
    /// <param name="dev">Block device containing the XISO.</param>
    /// <param name="isoName">Display name for error messages.</param>
    /// <param name="skipSectors">Optional skip sectors.</param>
    /// <returns>Raw FILETIME.</returns>
    public static ulong GetFileTimeRaw(IBlockDevice dev, string isoName = "memory", int? skipSectors = null)
    {
        var discLseek = FindDiscLseekForFileTime(dev, isoName, skipSectors);
        Span<byte> buf = stackalloc byte[8];
        var off = Constants.HeaderOffset + discLseek + Constants.HeaderDataLength + 4 + 4;
        if (dev.Read(off, buf) != 8)
            throw new IOException("Failed to read FILETIME");
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    /// <summary>
    /// Block-device overload of <see cref="GetFileTime(string,int?)"/>.
    /// </summary>
    public static DateTimeOffset GetFileTime(IBlockDevice dev, string isoName = "memory",
        int? skipSectors = null) =>
        FileTimeHelper.FromFileTimeRaw(GetFileTimeRaw(dev, isoName, skipSectors));

    /// <summary>
    /// Overwrites the 8-byte FILETIME header field in an existing XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file (opened read-write).</param>
    /// <param name="fileTime">Raw FILETIME to write (LE on disk).</param>
    /// <param name="skipSectors">Optional skip sectors for Redump images.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on I/O errors.</exception>
    public static void SetFileTime(string isoPath, ulong fileTime, int? skipSectors = null)
    {
        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 256
            });
        var discLseek = FindDiscLseekForFileTime(fs, isoPath, skipSectors);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, fileTime);
        fs.Seek(Constants.HeaderOffset + discLseek + Constants.HeaderDataLength + 4 + 4, SeekOrigin.Begin);
        fs.Write(buf);
        fs.Flush();
    }

    /// <summary>
    /// Overwrites the 8-byte FILETIME header field with a <see cref="DateTimeOffset"/> (UTC).
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="dateTime">UTC time to write (offset normalized).</param>
    /// <param name="skipSectors">Optional skip sectors for Redump images.</param>
    public static void SetFileTime(string isoPath, DateTimeOffset dateTime, int? skipSectors = null) => SetFileTime(isoPath, FileTimeHelper.ToFileTimeRaw(dateTime), skipSectors);

    /// <summary>
    /// Probes the header magic at known disc offsets (or the skip offset when provided)
    /// and returns the detected <c>discLseek</c>, throwing if no valid header is found.
    /// Shared by <see cref="GetFileTimeRaw(string,int?)"/> and <see cref="SetFileTime(string,ulong,int?)"/>.
    /// </summary>
    private static long FindDiscLseekForFileTime(FileStream fs, string isoName, int? skipSectors)
    {
        Span<byte> buf = stackalloc byte[Constants.HeaderDataLength];
        if (skipSectors.HasValue)
        {
            if (skipSectors.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                    "Skip sectors must be non-negative.");
            }

            var discLseek = (long)skipSectors.Value * Constants.SectorSize;
            fs.Seek(Constants.HeaderOffset + discLseek, SeekOrigin.Begin);
            ReadExact(fs, buf);
            if (!buf.SequenceEqual(HeaderDataBytes.AsSpan()))
                throw new XisoFormatException($"Invalid XISO: {isoName} — no header at sector {skipSectors.Value}");
            return discLseek;
        }

        long[] probes =
        [
            0, Constants.GlobalLseekOffset, Constants.Xgd3LseekOffset, Constants.Xgd2HybridLseekOffset,
            Constants.Xgd1LseekOffset
        ];
        foreach (var probe in probes)
        {
            fs.Seek(Constants.HeaderOffset + probe, SeekOrigin.Begin);
            try
            {
                ReadExact(fs, buf);
            }
            catch
            {
                continue;
            }

            if (buf.SequenceEqual(HeaderDataBytes.AsSpan()))
                return probe;
        }

        throw new XisoFormatException($"Invalid XISO: {isoName}");
    }

    private static long FindDiscLseekForFileTime(IBlockDevice dev, string isoName, int? skipSectors)
    {
        Span<byte> buf = stackalloc byte[Constants.HeaderDataLength];
        if (skipSectors.HasValue)
        {
            if (skipSectors.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(skipSectors), skipSectors.Value,
                    "Skip sectors must be non-negative.");
            }

            var discLseek = (long)skipSectors.Value * Constants.SectorSize;
            if (dev.Read(Constants.HeaderOffset + discLseek, buf) != buf.Length ||
                !buf.SequenceEqual(HeaderDataBytes.AsSpan()))
            {
                throw new XisoFormatException($"Invalid XISO: {isoName} — no header at sector {skipSectors.Value}");
            }

            return discLseek;
        }

        long[] probes =
        [
            0, Constants.GlobalLseekOffset, Constants.Xgd3LseekOffset, Constants.Xgd2HybridLseekOffset,
            Constants.Xgd1LseekOffset
        ];
        foreach (var probe in probes)
        {
            if (dev.Read(Constants.HeaderOffset + probe, buf) != buf.Length) continue;
            if (buf.SequenceEqual(HeaderDataBytes.AsSpan()))
                return probe;
        }

        throw new XisoFormatException($"Invalid XISO: {isoName}");
    }

    /// <summary>
    /// Performs a deep integrity audit of an XISO image. Validates the header,
    /// walks the entire directory tree, checks sector bounds, detects cycles,
    /// validates filenames and attributes, and verifies the optimized tag.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file to audit.</param>
    /// <returns>An <see cref="AuditResult"/> describing the outcome.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static AuditResult AuditXiso(string isoPath)
    {
        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
        {
            return new AuditResult(false, 0, 0, ["Header magic not found at any known disc offset."]);
        }

        if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
        {
            return new AuditResult(true, 0, 0, []);
        }

        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 65536
            });
        return AuditStream(fs, fs.Length, volInfo.RootDirSector, volInfo.DiscLseek);
    }

    /// <summary>
    /// Shared deep-audit core: optimized-tag probe, root-bounds check, then the
    /// full <see cref="AuditWalk"/> tree traversal. Callers supply any seekable
    /// read stream (file or <see cref="BlockDeviceStream"/>).
    /// </summary>
    private static AuditResult AuditStream(
        Stream stream, long length, uint rootDirSector, long discLseek)
    {
        var issues = new List<string>();
        var filesChecked = 0;
        var dirsChecked = 0;

        try
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTagLength];
            ReadExact(stream, tagBuf);
            var tag = Encoding.ASCII.GetString(tagBuf);
            if (!tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin], StringComparison.Ordinal))
            {
                issues.Add("Optimized tag not found at offset 31337.");
            }
        }
        catch (IOException)
        {
            issues.Add("Could not read optimized tag (file too short).");
        }

        var rootDirStart = ((long)rootDirSector * Constants.SectorSize) + discLseek;

        if (rootDirStart >= length)
        {
            issues.Add(
                $"Root directory sector {rootDirSector} (offset {rootDirStart}) exceeds file length {length}.");
            return new AuditResult(false, 0, 0, issues);
        }

        var visited = new HashSet<long>();

        AuditWalk(stream, rootDirStart, rootDirStart, "/", length, discLseek, issues, visited, ref filesChecked,
            ref dirsChecked);

        return new AuditResult(issues.Count == 0, filesChecked, dirsChecked, issues);
    }

    private static void AuditWalk(
        Stream fs,
        long dirStart,
        long tableStart,
        string path,
        long fileLength,
        long discLseek,
        List<string> issues,
        HashSet<long> visited,
        ref int filesChecked,
        ref int dirsChecked,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        // Hardening (#16): bound the audit walk like the extract walk — a corrupt
        // offset chain reports an issue instead of looping, and a corrupt
        // subdir cycle reports instead of overflowing the stack.
        if (depth > Constants.MaxTocDepth)
        {
            issues.Add($"Invalid TOC entry at '{path}': maximum directory depth {Constants.MaxTocDepth} exceeded.");
            return;
        }

        var entriesInTable = 0;
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (true)
        {
            if (++entriesInTable > Constants.MaxTocEntriesPerTable)
            {
                issues.Add(
                    $"Invalid TOC entry at '{path}': too many entries in one directory table (possible corrupt offset chain).");
                return;
            }

            if (dirStart >= fileLength)
            {
                issues.Add($"Directory offset {dirStart} ({path}) exceeds file length {fileLength}.");
                return;
            }

            if (!visited.Add(dirStart))
            {
                issues.Add(
                    $"Cycle detected: directory entry at offset {dirStart} ({path}) was already visited on this path.");
                return;
            }

            fs.Seek(dirStart, SeekOrigin.Begin);

            ReadExact(fs, shortBuf);
            var lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            // xdvdfs semantics (mirrors GetFileEntries): 0xFFFF and the all-zero 0x0000
            // sentinel mark an empty directory table only at the table start. Deeper nodes
            // use them as "no left child" markers and must still be processed.
            if (lOffset == Constants.PadShort && dirStart == tableStart)
            {
                return;
            }

            if (lOffset == Constants.EmptyDirectorySentinel && dirStart == tableStart)
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
                {
                    return;
                }
            }

            if (lOffset != 0 && lOffset != Constants.PadShort)
            {
                var leftSeek = tableStart + ((long)lOffset * Constants.DwordSize);
                if (leftSeek >= fileLength)
                {
                    issues.Add($"Left child offset {lOffset} (seek {leftSeek}) exceeds file length in {path}.");
                }
                else
                {
                    var childVisited = new HashSet<long>(visited);
                    AuditWalk(fs, leftSeek, tableStart, path, fileLength, discLseek, issues, childVisited,
                        ref filesChecked,
                        ref dirsChecked, depth + 1);
                }
            }

            fs.Seek(dirStart + 2, SeekOrigin.Begin);
            ReadExact(fs, shortBuf);
            var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            ReadExact(fs, intBuf);
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            var rawAttributes = byteBuf[0];

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (filename.Contains('/') || filename.Contains('\\'))
            {
                issues.Add($"Filename '{filename}' contains path separator in {path}.");
            }

            var sectorOffset = ((long)startSector * Constants.SectorSize) + discLseek;
            if (sectorOffset >= fileLength)
            {
                issues.Add(
                    $"Sector {startSector} (offset {sectorOffset}) for '{path}{filename}' exceeds file length {fileLength}.");
            }

            if ((rawAttributes & Constants.AttributeReservedMask) != 0)
            {
                issues.Add($"Reserved attribute bits set in '{path}{filename}': 0x{rawAttributes:X2}.");
            }

            var attributes = Constants.MaskAttributes(rawAttributes);
            var isDir = (attributes & Constants.AttributeDir) != 0;

            if (isDir)
            {
                dirsChecked++;

                if (fileSize > 0 && sectorOffset < fileLength)
                {
                    var endOffset = sectorOffset + fileSize;
                    if (endOffset > fileLength)
                    {
                        issues.Add(
                            $"Directory '{path}{filename}' size {fileSize} (ends at {endOffset}) exceeds file length {fileLength}.");
                    }

                    AuditWalk(fs, sectorOffset, sectorOffset, path + filename + "/", fileLength, discLseek, issues,
                        new HashSet<long>(), ref filesChecked, ref dirsChecked, depth + 1);
                }
            }
            else
            {
                filesChecked++;
            }

            if (rOffset != 0 && rOffset != Constants.PadShort)
            {
                var rightSeek = tableStart + ((long)rOffset * Constants.DwordSize);
                if (rightSeek >= fileLength)
                {
                    issues.Add($"Right child offset {rOffset} (seek {rightSeek}) exceeds file length in {path}.");
                    break;
                }

                dirStart = rightSeek;
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// Returns the names of all entries in the specified directory within an XISO image,
    /// without recursing into subdirectories.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">
    /// Path within the ISO to list (e.g. <c>"/"</c> for root, <c>"/subdir"</c> for a subdirectory).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>The entry names, or an empty list if the directory is empty.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="InvalidDataException">Thrown when the path does not exist in the ISO.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static IReadOnlyList<string> ListDirectoryFlat(string isoPath, string internalPath = "/") => ListDirectory(isoPath, internalPath).Select(static e => e.Name).ToArray();

    /// <summary>
    /// Returns metadata about all entries in the specified directory within an XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">
    /// Path within the ISO to list (e.g. <c>"/"</c> for root, <c>"/subdir"</c> for a subdirectory).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>List of directory entries, or empty if the directory is empty.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="InvalidDataException">Thrown when the path does not exist in the ISO.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static IReadOnlyList<EntryInfo> ListDirectory(string isoPath, string internalPath = "/")
    {
        using var fs = OpenImageStream(isoPath);
        return ListDirectory(fs, isoPath, internalPath);
    }

    /// <summary>
    /// Stream overload of <see cref="ListDirectory(string, string)"/> over an
    /// already-open image (plain or CISO-backed). The stream must be readable +
    /// seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">
    /// Path within the ISO to list (e.g. <c>"/"</c> for root, <c>"/subdir"</c> for a subdirectory).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>List of directory entries, or empty if the directory is empty.</returns>
    /// <exception cref="XisoFormatException">Thrown when the image is not a valid XISO image.</exception>
    /// <exception cref="InvalidDataException">Thrown when the path does not exist in the image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static IReadOnlyList<EntryInfo> ListDirectory(Stream imageStream, string imageName,
        string internalPath = "/")
    {
        var fs = imageStream;
        var isoPath = imageName;
        var volInfo = GetVolumeInfo(fs);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
            return Array.Empty<EntryInfo>();

        var dirStart = ((long)volInfo.RootDirSector * Constants.SectorSize) + volInfo.DiscLseek;

        // Navigate to the target directory if not root
        if (!string.Equals(internalPath, "/", StringComparison.Ordinal))
        {
            var segments = internalPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var entries = ReadDirectoryEntries(fs, dirStart, internalPath);
                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase) && e.IsDirectory);

                if (match == null)
                    throw new InvalidDataException($"Path not found: {internalPath}");

                dirStart = ((long)match.StartSector * Constants.SectorSize) + volInfo.DiscLseek;
            }
        }

        return ReadDirectoryEntries(fs, dirStart, internalPath);
    }

    /// <summary>
    /// Returns metadata about a specific file or directory entry within an XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">Path within the ISO (e.g. <c>"/subdir/file.xbe"</c>).</param>
    /// <returns>Entry information, or <c>null</c> if the path does not exist.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the ISO is invalid.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static EntryInfo? GetEntryInfo(string isoPath, string internalPath)
    {
        using var fs = OpenImageStream(isoPath);
        return GetEntryInfo(fs, isoPath, internalPath);
    }

    /// <summary>
    /// Stream overload of <see cref="GetEntryInfo(string, string)"/> over an
    /// already-open image (plain or CISO-backed). The stream must be readable +
    /// seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">Path within the image (e.g. <c>"/subdir/file.xbe"</c>).</param>
    /// <returns>Entry information, or <c>null</c> if the path does not exist.</returns>
    /// <exception cref="InvalidDataException">Thrown when the image is invalid.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static EntryInfo? GetEntryInfo(Stream imageStream, string imageName, string internalPath)
    {
        if (string.IsNullOrEmpty(internalPath) || string.Equals(internalPath, "/", StringComparison.Ordinal))
            return null;

        var segments = internalPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var dirPath = segments.Length > 1
            ? "/" + string.Join("/", segments[..^1])
            : "/";

        var entryName = segments[^1];

        var entries = ListDirectory(imageStream, imageName, dirPath);
        return entries.FirstOrDefault(e =>
            string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the explicit sector layout of an XISO image (xdvdfs #49): every file
    /// and directory table mapped to its sector range, plus merged allocated ranges
    /// and the free gaps between them. All sectors are partition-relative (the same
    /// numbering as <see cref="EntryInfo.StartSector"/>); the free ranges are the
    /// allocator input for in-place patching (TODO #5).
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>
    /// A <see cref="SectorLayout"/> with per-file/table extents sorted by start
    /// sector, merged used ranges (volume header + tables + file data), free gaps
    /// tiling <c>[0, TotalSectors)</c>, and the partition sector count.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">
    /// Thrown when the ISO is not a valid XISO image, or when an entry's extent
    /// points outside the image or the tables form a cycle (TODO #16 hardening).
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static SectorLayout GetSectorLayout(string isoPath)
    {
        // BUG-LIB-037: same CISO-aware routing as GetVolumeInfo above.
        using var fs = OpenImageStream(isoPath);

        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        var fileLength = fs.Length;
        var discLseek = volInfo.DiscLseek;
        var totalSectors = (fileLength - discLseek) / Constants.SectorSize;

        // Partition-relative sector of the volume descriptor (always sector 32).
        const uint headerSector = (uint)(Constants.HeaderOffset / Constants.SectorSize);
        var used = new List<SectorRange> { new(headerSector, 1) };
        var entries = new List<FileSectorExtent>();

        if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
        {
            return new SectorLayout(volInfo, Array.Empty<FileSectorExtent>(),
                MergeSectorRanges(used, totalSectors),
                ComplementSectorRanges(MergeSectorRanges(used, totalSectors), totalSectors),
                totalSectors);
        }

        // Iterative preorder walk over directory tables. ReadDirectoryEntries is
        // cycle-safe within one table (TODO #16); the visited set + depth cap here
        // bound the cross-table walk (a corrupt subdir pointing back at an ancestor).
        var visitedTables = new HashSet<long>();
        var stack = new Stack<(long TableStart, uint TableSize, string DirPath, int Depth)>();
        stack.Push((((long)volInfo.RootDirSector * Constants.SectorSize) + discLseek,
            volInfo.RootDirSize, "/", 0));

        while (stack.Count > 0)
        {
            var (tableStart, tableSize, dirPath, depth) = stack.Pop();
            if (depth > Constants.MaxTocDepth)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{dirPath}': maximum directory depth {Constants.MaxTocDepth} " +
                    "exceeded (possible directory cycle).");
            }

            if (!visitedTables.Add(tableStart))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{dirPath}': directory cycle detected — table at offset " +
                    $"{tableStart} was already visited.");
            }

            CheckTableBounds(fs, fileLength, tableStart, tableSize, dirPath);

            var tableSector = (uint)((tableStart - discLseek) / Constants.SectorSize);
            var tableSectorCount = (tableSize + Constants.SectorSize - 1) / Constants.SectorSize;
            entries.Add(new FileSectorExtent(dirPath, true, tableSector, tableSectorCount, tableSize));
            if (tableSectorCount > 0)
                used.Add(new SectorRange(tableSector, tableSectorCount));

            if (tableSize == 0)
                continue;

            foreach (var (name, isDir, sector, size) in ReadRawEntries(fs, tableStart, dirPath))
            {
                var entryPath = dirPath.Equals("/", StringComparison.Ordinal)
                    ? "/" + name
                    : dirPath + "/" + name;
                if (isDir)
                {
                    var subStart = ((long)sector * Constants.SectorSize) + discLseek;
                    CheckTableBounds(fs, fileLength, subStart, size, entryPath);
                    stack.Push((subStart, size, entryPath, depth + 1));
                }
                else
                {
                    var entry = new EntryInfo(name, false, sector, size, 0, 0, 0);
                    CheckFileBounds(fs, fileLength, discLseek, entry, entryPath);
                    var sectorCount = size == 0
                        ? 0u
                        : (size + Constants.SectorSize - 1) / Constants.SectorSize;
                    entries.Add(new FileSectorExtent(entryPath, false, sector, sectorCount, size));
                    if (sectorCount > 0)
                        used.Add(new SectorRange(sector, sectorCount));
                }
            }
        }

        entries.Sort(static (a, b) =>
        {
            var c = a.StartSector.CompareTo(b.StartSector);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        });

        var usedRanges = MergeSectorRanges(used, totalSectors);
        var freeRanges = ComplementSectorRanges(usedRanges, totalSectors);
        return new SectorLayout(volInfo, entries, usedRanges, freeRanges, totalSectors);
    }

    /// <summary>
    /// Reads one directory table's raw entries — like <see cref="ReadDirectoryEntries"/>
    /// but keeping the on-disk data size for directories (which <c>EntryInfo</c>
    /// zeroes out). Same traversal, sentinel, and hardening rules; keep in sync.
    /// </summary>
    private static List<(string Name, bool IsDir, uint Sector, uint Size)> ReadRawEntries(
        Stream fs, long dirStart, string contextPath)
    {
        var raw = new List<(string Name, bool IsDir, uint Sector, uint Size)>();
        var stack = new Stack<long>();
        stack.Push(0);

        // Hardening (#16): same bounds as ReadDirectoryEntries — visited set plus
        // per-table entry cap so a corrupt cycle fails fast with a named error.
        var visited = new HashSet<long>();

        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            var absOffset = dirStart + offset;
            if (absOffset < dirStart || absOffset >= fs.Length)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': child offset {offset} (seek {absOffset}) " +
                    $"points outside the image (table at {dirStart}, length {fs.Length}).");
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

            // Empty-directory sentinels (0xFF- or 0x00-filled table), as in ReadDirectoryEntries.
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
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            var attributes = Constants.MaskAttributes(byteBuf[0]);

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (string.Equals(filename, ".", StringComparison.Ordinal) ||
                string.Equals(filename, "..", StringComparison.Ordinal))
            {
                if (rOffset != 0 && rOffset != Constants.PadShort)
                    stack.Push((long)rOffset * Constants.DwordSize);
                if (lOffset != 0 && lOffset != Constants.PadShort)
                    stack.Push((long)lOffset * Constants.DwordSize);
                continue;
            }

            raw.Add((filename, (attributes & Constants.AttributeDir) != 0, startSector, dataSize));

            if (rOffset != 0 && rOffset != Constants.PadShort)
                stack.Push((long)rOffset * Constants.DwordSize);

            if (lOffset != 0 && lOffset != Constants.PadShort)
                stack.Push((long)lOffset * Constants.DwordSize);
        }

        return raw;
    }

    private static void CheckTableBounds(Stream fs, long fileLength, long tableStart, uint tableSize,
        string dirPath)
    {
        _ = fs;
        if (tableStart < 0 || tableStart >= fileLength ||
            tableSize > (ulong)fileLength || tableStart > fileLength - tableSize)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{dirPath}': directory table at offset {tableStart} " +
                $"(size {tableSize}) points outside the image (length {fileLength}).");
        }
    }

    private static void CheckFileBounds(Stream fs, long fileLength, long discLseek, EntryInfo entry,
        string entryPath)
    {
        _ = fs;
        if (entry.FileSize == 0)
            return;
        var dataStart = discLseek + ((long)entry.StartSector * Constants.SectorSize);
        var dataEnd = dataStart + entry.FileSize;
        if (dataStart < 0 || dataEnd > fileLength)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{entryPath}': file extent at sector {entry.StartSector} " +
                $"(size {entry.FileSize}) points outside the image (length {fileLength}).");
        }
    }

    private static IReadOnlyList<SectorRange> MergeSectorRanges(List<SectorRange> used, long totalSectors)
    {
        var clamped = new List<SectorRange>(used.Count);
        foreach (var r in used)
        {
            if (r.SectorCount == 0 || r.StartSector >= totalSectors)
                continue;
            var count = Math.Min(r.SectorCount, totalSectors - r.StartSector);
            clamped.Add(r with { SectorCount = (uint)count });
        }

        clamped.Sort(static (a, b) => a.StartSector.CompareTo(b.StartSector));
        var merged = new List<SectorRange>(clamped.Count);
        foreach (var r in clamped)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var lastEnd = (long)last.StartSector + last.SectorCount;
                if (r.StartSector <= lastEnd)
                {
                    var end = Math.Max(lastEnd, (long)r.StartSector + r.SectorCount);
                    merged[^1] = last with { SectorCount = (uint)(end - last.StartSector) };
                    continue;
                }
            }

            merged.Add(r);
        }

        return merged;
    }

    private static IReadOnlyList<SectorRange> ComplementSectorRanges(IReadOnlyList<SectorRange> used,
        long totalSectors)
    {
        var free = new List<SectorRange>();
        long cursor = 0;
        foreach (var r in used)
        {
            if (r.StartSector > cursor)
                free.Add(new SectorRange((uint)cursor, (uint)(r.StartSector - cursor)));
            cursor = Math.Max(cursor, (long)r.StartSector + r.SectorCount);
        }

        if (cursor < totalSectors)
            free.Add(new SectorRange((uint)cursor, (uint)(totalSectors - cursor)));
        return free;
    }

    /// <summary>
    /// Copies a single file or directory from an XISO image to the local filesystem.
    /// If the path points to a file, it is extracted to <paramref name="destPath"/>.
    /// If the path points to a directory, all its contents are recursively extracted.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">Path within the ISO (e.g. <c>"/subdir/file.xbe"</c>).</param>
    /// <param name="destPath">Destination path on the local filesystem.</param>
    /// <param name="options">
    /// Optional resume options; when <see cref="UnpackOptions.SkipExisting"/> is set,
    /// destinations already holding a same-size file are left untouched and logged as
    /// <c>skip: &lt;path&gt;</c> (TODO #13, xdvdfs #190).
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">
    /// Optional structured progress channel; receives a per-chunk
    /// <see cref="ProgressInfoType.FileProgress"/> event for each file copied.
    /// </param>
    /// <exception cref="FileNotFoundException">Thrown when the ISO file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the internal path does not exist.</exception>
    /// <exception cref="ExtractFileException">
    /// Thrown naming the entry, its sector, and expected vs actual bytes on
    /// destination or data failures (TODO #9, xdvdfs #187); under
    /// <see cref="UnpackOptions.ContinueOnError"/> a directory copy collects
    /// per-file failures and throws the <see cref="ExtractError.ErrExtractFailed"/>
    /// summary instead.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static void CopyOut(string isoPath, string internalPath, string destPath,
        UnpackOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        using var fs = OpenImageStream(isoPath);
        CopyOut(fs, isoPath, internalPath, destPath, options, cancellationToken, progress);
    }

    /// <summary>
    /// Stream overload of
    /// <see cref="CopyOut(string, string, string, UnpackOptions?, CancellationToken, IProgress{ProgressInfo}?)"/>
    /// over an already-open image (plain or CISO-backed). The stream must be
    /// readable + seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">Source path within the image.</param>
    /// <param name="destPath">Destination path on the local filesystem.</param>
    /// <param name="options">Optional resume options (see <see cref="UnpackOptions"/>).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">Optional structured progress channel.</param>
    /// <exception cref="InvalidDataException">Thrown when the internal path does not exist.</exception>
    /// <exception cref="ExtractFileException">
    /// Thrown naming the entry, its sector, and expected vs actual bytes on
    /// destination or data failures.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static void CopyOut(Stream imageStream, string imageName, string internalPath, string destPath,
        UnpackOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isoPath = imageName;
        var fs = imageStream;
        var entry = GetEntryInfo(fs, isoPath, internalPath);
        if (entry == null)
            throw new InvalidDataException($"Path not found in XISO: {internalPath}");

        var volInfo = GetVolumeInfo(fs);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        if (entry.IsDirectory)
        {
            CopyOutDirectory(fs, isoPath, internalPath, destPath, volInfo, options, cancellationToken,
                progress: progress);
            options?.ThrowIfFailed(isoPath);
        }
        else
        {
            CopyOutFile(fs, entry, internalPath, destPath, volInfo, options, cancellationToken,
                progress: progress);
        }
    }

    /// <summary>
    /// Copies a host file into an XISO image: replaces the entry at
    /// <paramref name="internalPath"/> when it exists, or adds it as a new file
    /// when only its parent directory exists (TODO #5, xdvdfs #165).
    /// The image is modified in place; a <c>.old</c> backup of the pre-patch
    /// image is written first unless <paramref name="createBackup"/> is false.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (modified in place).</param>
    /// <param name="hostFile">Host file whose bytes become the new content.</param>
    /// <param name="internalPath">
    /// Destination path inside the image (e.g. <c>/dir/file.bin</c>);
    /// case-insensitive, <c>/</c>-separated.
    /// </param>
    /// <param name="createBackup">Write a <c>.old</c> backup first (default true).</param>
    /// <exception cref="FileNotFoundException">The host file does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="InvalidDataException">
    /// The internal path is malformed, a parent is missing, the target is a
    /// directory, or the data/table does not fit in free space.
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static void CopyIn(string isoPath, string hostFile, string internalPath,
        bool createBackup = true) =>
        XisoPatcher.CopyIntoImage(isoPath, hostFile, internalPath, createBackup);

    /// <summary>
    /// Repairs the class-C issues of an XISO image in place (TODO #26, Phase 1;
    /// facade over <see cref="XisoRepairer.RepairInPlace"/>): reserved attribute
    /// bits, a missing optimized tag, and path separators in filenames.
    /// The image is modified in place; a <c>.old</c> backup of the pre-repair
    /// image is written first unless <paramref name="createBackup"/> is false.
    /// Truncation, structural, and refused classes are reported, never patched.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (modified in place).</param>
    /// <param name="createBackup">Write a <c>.old</c> backup first (default true).</param>
    /// <param name="dryRun">Preview the fixes without changing anything (default false).</param>
    /// <returns>
    /// The applied (or would-be) fixes plus the post-repair audit issues.
    /// </returns>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="InvalidDataException">
    /// The image is a CISO container or a split part; neither is patch-stable.
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static RepairResult Repair(string isoPath, bool createBackup = true, bool dryRun = false) => XisoRepairer.RepairInPlace(isoPath, createBackup, dryRun);

    /// <summary>
    /// Rebuilds a readable image from a corrupt one (TODO #26, Phase 2;
    /// facade over <see cref="XisoSalvager.Salvage"/>): carries every entry
    /// reachable without tripping a truncation or structural gate and repacks
    /// them through the <c>CreateXiso</c> pipeline into a fresh plain
    /// <c>.iso</c>. The source is only read, never modified; CISO input is
    /// allowed (reads go through the decompressed view).
    /// </summary>
    /// <param name="sourcePath">Path of the corrupt image (plain or CISO).</param>
    /// <param name="outputPath">
    /// Destination for the rebuilt image (<c>null</c> = same directory,
    /// source stem plus <c>.salvaged.iso</c>). An existing file is
    /// overwritten; a missing parent directory is created.
    /// </param>
    /// <returns>Carried paths, dropped lines, output path, and re-audit issues.</returns>
    /// <exception cref="FileNotFoundException">The source file does not exist.</exception>
    /// <exception cref="XisoFormatException">
    /// Not a valid XISO image, or the tree root itself is unreachable.
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static SalvageResult Salvage(string sourcePath, string? outputPath = null) => XisoSalvager.Salvage(sourcePath, outputPath);

    /// <summary>
    /// Splits an XISO image into sector-aligned parts of at most
    /// <paramref name="partSizeBytes"/> bytes (TODO #17, xdvdfs #97; facade
    /// over <see cref="XisoSplitter.Split"/>).
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c>).</param>
    /// <param name="outputBase">Base path for the parts (<c>game</c> → <c>game.1.iso</c>, …).</param>
    /// <param name="partSizeBytes">Maximum part size in bytes (≥ one sector).</param>
    /// <param name="cancellationToken">Cancels the copy (partial parts removed).</param>
    /// <param name="progress">Optional <see cref="ProgressInfoType.FileProgress"/> reports.</param>
    /// <returns>Paths of the parts written, in join order.</returns>
    public static IReadOnlyList<string> SplitXiso(string isoPath, string outputBase, long partSizeBytes,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null) =>
        XisoSplitter.Split(isoPath, outputBase, partSizeBytes, cancellationToken, progress);

    /// <summary>
    /// Splits an XISO image into two sector-aligned halves (TODO #17, xdvdfs
    /// #97; facade over <see cref="XisoSplitter.SplitHalves"/>).
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c>).</param>
    /// <param name="outputBase">Base path for the parts (<c>game</c> → <c>game.1.iso</c>, …).</param>
    /// <param name="cancellationToken">Cancels the copy (partial parts removed).</param>
    /// <param name="progress">Optional <see cref="ProgressInfoType.FileProgress"/> reports.</param>
    /// <returns>Paths of the parts written, in join order.</returns>
    public static IReadOnlyList<string> SplitXisoHalves(string isoPath, string outputBase,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null) =>
        XisoSplitter.SplitHalves(isoPath, outputBase, cancellationToken, progress);

    /// <summary>
    /// Reassembles a split image into <paramref name="outputPath"/> (TODO #17,
    /// xdvdfs #97; facade over <see cref="XisoSplitter.Join"/>).
    /// </summary>
    /// <param name="firstPartPath">Path of the first split part (<c>*.1.iso</c>).</param>
    /// <param name="outputPath">Destination for the reassembled image.</param>
    /// <param name="cancellationToken">Cancels the copy (partial output removed).</param>
    /// <param name="progress">Optional <see cref="ProgressInfoType.FileProgress"/> reports.</param>
    /// <returns><paramref name="outputPath"/>.</returns>
    public static string JoinSplitXiso(string firstPartPath, string outputPath,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null) =>
        XisoSplitter.Join(firstPartPath, outputPath, cancellationToken, progress);

    private static void CopyOutFile(Stream fs, EntryInfo entry, string internalPath, string destPath,
        VolumeInfo volInfo, UnpackOptions? options = null, CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.ShouldSkip(destPath, entry.FileSize) == true)
        {
            Logger.Log($"skip: {destPath} ({entry.FileSize} bytes)\n");
            Logger.Flush();
            return;
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        FileStream outFile;
        try
        {
            outFile = new FileStream(
                destPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ExtractFileException.ForCreate(internalPath, destPath, entry.StartSector, entry.FileSize, ex);
        }

        try
        {
            using (outFile)
            {
                fs.Seek(((long)entry.StartSector * Constants.SectorSize) + volInfo.DiscLseek, SeekOrigin.Begin);

                // Shared copier (#8): rents a pooled buffer instead of
                // allocating 2 MB per file, and reports byte-level progress.
                var totalRead = 0L;
                var copyBuffer = RentCopyBuffer();
                try
                {
                    XisoFileCopier.CopyExact(
                        fs,
                        entry.FileSize,
                        // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
                        (buffer, count) =>
                        {
                            outFile.Write(buffer, 0, count);
                            totalRead += count;
                        },
                        copyBuffer,
                        copied => progress?.Report(new ProgressInfo(ProgressInfoType.FileProgress,
                            Count: entry.FileSize,
                            Path: internalPath.Replace('\\', '/'),
                            Sector: entry.StartSector, Size: copied)),
                        cancellationToken);
                }
                catch (TruncatedCopyException)
                {
                    throw ExtractFileException.ForTruncated(internalPath, destPath, entry.StartSector,
                        entry.FileSize, totalRead);
                }
                finally
                {
                    ReturnCopyBuffer(copyBuffer);
                }
            }

            if (new FileInfo(destPath).Length != entry.FileSize)
            {
                throw ExtractFileException.ForTruncated(internalPath, destPath, entry.StartSector,
                    entry.FileSize, new FileInfo(destPath).Length);
            }
        }
        catch (ExtractFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ExtractFileException.ForWrite(internalPath, destPath, entry.StartSector, entry.FileSize, -1, ex);
        }
    }

    private static void CopyOutDirectory(Stream fs, string imageName, string internalPath, string destPath,
        VolumeInfo volInfo, UnpackOptions? options = null, CancellationToken cancellationToken = default,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0, IProgress<ProgressInfo>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Hardening (#16): a subdirectory cycle (table pointing back at an ancestor)
        // would otherwise recurse until the stack overflows.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{internalPath}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        Directory.CreateDirectory(destPath);

        var entries = ListDirectory(fs, imageName, internalPath);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryDestPath = Path.Combine(destPath, entry.Name);

            // Belt over ReadDirectoryEntries' separator rejection (BUG-LIB-021):
            // a hostile name must never resolve outside the destination, even
            // if a future reader relaxes the check above.
            var entryDestFull = Path.GetFullPath(entryDestPath);
            var destFull = Path.GetFullPath(destPath);
            if (!XisoPaths.AreSamePath(entryDestFull, destFull) &&
                !XisoPaths.IsWithinDirectory(entryDestFull, destFull))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{internalPath}': entry '{entry.Name}' escapes the destination directory.");
            }

            var entryInternalPath = internalPath.TrimEnd('/') + "/" + entry.Name;

            try
            {
                if (entry.IsDirectory)
                {
                    CopyOutDirectory(fs, imageName, entryInternalPath, entryDestPath, volInfo, options,
                        cancellationToken, depth + 1, progress);
                }
                else
                {
                    CopyOutFile(fs, entry, entryInternalPath, entryDestPath, volInfo, options, cancellationToken,
                        progress);
                }
            }
            catch (Exception ex) when (options?.ContinueOnError == true && ex is not OperationCanceledException)
            {
                var failure = ex as ExtractFileException
                              ?? ExtractFileException.ForWrite(entryInternalPath, entryDestPath, entry.StartSector,
                                  entry.FileSize, -1, ex);
                options.RecordFailure(failure);
                Logger.LogErr($"Error: {failure.Message}\n");
            }
        }
    }

    /// <summary>
    /// Computes the hash of a single file within an XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">Path within the ISO (e.g. <c>"/subdir/file.xbe"</c>).</param>
    /// <param name="algorithm">Hash algorithm to use (<see cref="HashAlgorithmName.MD5"/> or <see cref="HashAlgorithmName.SHA256"/>).</param>
    /// <returns>Hash bytes, or <c>null</c> if the file does not exist.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the ISO file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the ISO is invalid or path is a directory.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static byte[]? ComputeFileHash(string isoPath, string internalPath, HashAlgorithmName algorithm)
    {
        using var fs = OpenImageStream(isoPath);
        return ComputeFileHash(fs, isoPath, internalPath, algorithm);
    }

    /// <summary>
    /// Stream overload of <see cref="ComputeFileHash(string, string, HashAlgorithmName)"/>
    /// over an already-open image (plain or CISO-backed). The stream must be
    /// readable + seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">File path within the image.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <returns>Hash bytes, or <c>null</c> if the file does not exist.</returns>
    /// <exception cref="InvalidDataException">Thrown when the image is invalid or path is a directory.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static byte[]? ComputeFileHash(Stream imageStream, string imageName, string internalPath,
        HashAlgorithmName algorithm)
    {
        var isoPath = imageName;
        var fs = imageStream;
        var entry = GetEntryInfo(fs, isoPath, internalPath);
        if (entry == null)
            return null;

        if (entry.IsDirectory)
            throw new InvalidDataException($"Cannot hash a directory: {internalPath}");

        using var hasher = CreateHashAlgorithm(algorithm);

        if (entry.FileSize == 0)
        {
            return hasher.ComputeHash(Array.Empty<byte>());
        }

        var volInfo = GetVolumeInfo(fs);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        fs.Seek(((long)entry.StartSector * Constants.SectorSize) + volInfo.DiscLseek, SeekOrigin.Begin);

        // Shared copier (#8): same bytes, same truncation error, no per-call buffer.
        var hashBuffer = RentCopyBuffer();
        try
        {
            XisoFileCopier.CopyExact(
                fs,
                entry.FileSize,
                // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
                (buffer, count) => hasher.TransformBlock(buffer, 0, count, buffer, 0),
                hashBuffer);
        }
        catch (TruncatedCopyException)
        {
            throw new IOException($"Unexpected end of file data at sector {entry.StartSector}");
        }
        finally
        {
            ReturnCopyBuffer(hashBuffer);
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hasher.Hash;
    }

    private static HashAlgorithm CreateHashAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5)
            return MD5.Create();
        if (algorithm == HashAlgorithmName.SHA256)
            return SHA256.Create();
        if (algorithm == HashAlgorithmName.SHA1)
            return SHA1.Create();
        if (algorithm == HashAlgorithmName.SHA384)
            return SHA384.Create();
        if (algorithm == HashAlgorithmName.SHA512)
            return SHA512.Create();

        throw new NotSupportedException($"Hash algorithm '{algorithm.Name}' is not supported.");
    }

    /// <summary>
    /// Computes hashes for all files in a directory (or the entire image) within an XISO.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">Path within the ISO (e.g. <c>"/"</c> for root, <c>"/subdir"</c> for a subdirectory).</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <returns>List of (path, hash) tuples for all files.</returns>
    public static IReadOnlyList<(string Path, byte[] Hash)> ComputeDirectoryHashes(
        string isoPath, string internalPath, HashAlgorithmName algorithm)
    {
        var results = new List<(string Path, byte[] Hash)>();
        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid || volInfo.RootDirSector == 0)
            return results;

        CollectHashes(isoPath, internalPath, algorithm, results);
        return results;
    }

    // XEX2 optional-header keys (see xenia's xex2_info.h).
    private const uint XexKeyFileFormatInfo = 0x000003FF;
    private const uint XexKeyEntryPoint = 0x00010100;
    private const uint XexKeyImageBaseAddress = 0x00010201;
    private const uint XexKeyExecutionInfo = 0x00040006;

    /// <summary>Maximum number of XEX optional-header entries accepted.</summary>
    private const uint XexMaxHeaderCount = 64;

    /// <summary>Maximum number of header bytes read from the executable (retail headers are 0x4000).</summary>
    private const int XexHeaderReadLimit = 0x8000;

    /// <summary>
    /// Parses the Xbox 360 XEX2 header of an executable file inside an XISO image.
    /// All fields are read big-endian per the XEX2 specification.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">
    /// Path of the <c>.xex</c> file within the ISO (e.g. <c>"/default.xex"</c>).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>
    /// The parsed <see cref="XexInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XEX2 executable.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the ISO file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static XexInfo? GetXexInfo(string isoPath, string internalPath)
    {
        using var fs = OpenImageStream(isoPath);
        return GetXexInfo(fs, isoPath, internalPath);
    }

    /// <summary>
    /// Stream overload of <see cref="GetXexInfo(string, string)"/> over an
    /// already-open image (plain or CISO-backed). The stream must be readable +
    /// seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">Path of the <c>.xex</c> file within the image.</param>
    /// <returns>
    /// The parsed <see cref="XexInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XEX2 executable.
    /// </returns>
    /// <exception cref="XisoFormatException">Thrown when the image is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static XexInfo? GetXexInfo(Stream imageStream, string imageName, string internalPath)
    {
        var isoPath = imageName;
        var fs = imageStream;
        var entry = GetEntryInfo(fs, isoPath, internalPath);
        if (entry?.IsDirectory != false || entry.FileSize < 0x18)
            return null;

        var volInfo = GetVolumeInfo(fs);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        fs.Seek(((long)entry.StartSector * Constants.SectorSize) + volInfo.DiscLseek, SeekOrigin.Begin);

        var header = new byte[Math.Min(entry.FileSize, XexHeaderReadLimit)];
        fs.ReadExactly(header);

        return ParseXexHeader(header);
    }

    private static XexInfo? ParseXexHeader(byte[] header)
    {
        // Magic: 'XEX2'
        if (header.Length < 0x18 ||
            header[0] != (byte)'X' || header[1] != (byte)'E' || header[2] != (byte)'X' || header[3] != (byte)'2')
        {
            return null;
        }

        var moduleFlags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x04));
        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x08));
        var securityOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        var headerCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x14));

        if (headerCount > XexMaxHeaderCount || 0x18 + (headerCount * 8) > header.Length)
            return null;

        uint entryPoint = 0;
        uint imageBaseAddress = 0;
        uint executionOffset = 0;
        uint formatOffset = 0;

        for (var i = 0; i < headerCount; i++)
        {
            var offset = 0x18 + (i * 8);
            var key = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(offset));
            var value = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(offset + 4));

            switch (key)
            {
                case XexKeyEntryPoint:
                    entryPoint = value;
                    break;
                case XexKeyImageBaseAddress:
                    imageBaseAddress = value;
                    break;
                case XexKeyExecutionInfo:
                    executionOffset = value;
                    break;
                case XexKeyFileFormatInfo:
                    formatOffset = value;
                    break;
            }
        }

        // Security info: image size @+4, load address @+0x110, region @+0x178, media types @+0x17C.
        // Long arithmetic keeps the bounds check overflow-safe for malformed headers.
        uint imageSize = 0;
        uint loadAddress = 0;
        uint region = 0;
        uint allowedMediaTypes = 0;
        if ((long)securityOffset + 0x180 <= header.Length)
        {
            imageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)securityOffset + 4));
            loadAddress = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)securityOffset + 0x110));
            region = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)securityOffset + 0x178));
            allowedMediaTypes = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)securityOffset + 0x17C));
        }

        // Execution info (0x18 bytes): media id, version, base version, title id,
        // platform, executable table, disc number, disc count, savegame id.
        uint mediaId = 0;
        uint titleId = 0;
        uint version = 0;
        byte platform = 0;
        byte discNumber = 0;
        byte discCount = 0;
        if (executionOffset != 0 && (long)executionOffset + 0x18 <= header.Length)
        {
            mediaId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)executionOffset));
            version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)executionOffset + 4));
            titleId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan((int)executionOffset + 0x0C));
            platform = header[(int)executionOffset + 0x10];
            discNumber = header[(int)executionOffset + 0x12];
            discCount = header[(int)executionOffset + 0x13];
        }

        // File format info: info size @0, encryption type (u16) @+4, compression type (u16) @+6.
        ushort encryptionType = 0;
        ushort compressionType = 0;
        if (formatOffset != 0 && (long)formatOffset + 8 <= header.Length)
        {
            encryptionType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan((int)formatOffset + 4));
            compressionType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan((int)formatOffset + 6));
        }

        return new XexInfo(
            moduleFlags,
            headerSize,
            entryPoint,
            imageBaseAddress,
            imageSize,
            loadAddress,
            region,
            allowedMediaTypes,
            mediaId,
            titleId,
            version,
            platform,
            discNumber,
            discCount,
            encryptionType,
            compressionType);
    }

    /// <summary>Certificate size in bytes (retail XBE certificates are always 464).</summary>
    private const uint XbeCertSize = 0x1D0;

    /// <summary>Maximum number of header bytes read from the executable.</summary>
    private const int XbeHeaderReadLimit = 0x8000;

    /// <summary>
    /// Parses the original-Xbox XBEH header + certificate of an executable file
    /// inside an XISO image. All fields are read little-endian per the XBE
    /// specification (see <c>xbe.h</c> in Cxbx-Reloaded).
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">
    /// Path of the <c>.xbe</c> file within the ISO (e.g. <c>"/default.xbe"</c>).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>
    /// The parsed <see cref="XbeInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XBEH executable.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the ISO file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static XbeInfo? GetXbeInfo(string isoPath, string internalPath)
    {
        using var fs = OpenImageStream(isoPath);
        return GetXbeInfo(fs, isoPath, internalPath);
    }

    /// <summary>
    /// Stream overload of <see cref="GetXbeInfo(string, string)"/> over an
    /// already-open image (plain or CISO-backed). The stream must be readable +
    /// seekable and is left open.
    /// </summary>
    /// <param name="imageStream">Open image stream.</param>
    /// <param name="imageName">Display name of the image (used in error messages).</param>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the image.</param>
    /// <returns>
    /// The parsed <see cref="XbeInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XBEH executable.
    /// </returns>
    /// <exception cref="XisoFormatException">Thrown when the image is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static XbeInfo? GetXbeInfo(Stream imageStream, string imageName, string internalPath)
    {
        var isoPath = imageName;
        var fs = imageStream;
        var entry = GetEntryInfo(fs, isoPath, internalPath);
        if (entry?.IsDirectory != false || entry.FileSize < 0x12C)
            return null;

        var volInfo = GetVolumeInfo(fs);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        fs.Seek(((long)entry.StartSector * Constants.SectorSize) + volInfo.DiscLseek, SeekOrigin.Begin);

        var header = new byte[Math.Min(entry.FileSize, XbeHeaderReadLimit)];
        fs.ReadExactly(header);

        return ParseXbeHeader(header);
    }

    private static XbeInfo? ParseXbeHeader(byte[] header)
    {
        // Magic: 'XBEH'
        if (header.Length < 0x12C ||
            header[0] != (byte)'X' || header[1] != (byte)'B' || header[2] != (byte)'E' || header[3] != (byte)'H')
        {
            return null;
        }

        var baseAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x104));
        var certAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x118));
        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x11C));
        var initFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x124));
        var entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x128));

        // The certificate address is a load pointer: file offset = address - base.
        // Long arithmetic keeps the bounds check overflow-safe for malformed headers.
        var certOffset = (long)certAddress - baseAddress;
        if (certOffset < 0 || certOffset + XbeCertSize > header.Length)
            return null;

        var cert = header.AsSpan((int)certOffset);
        var certSize = BinaryPrimitives.ReadUInt32LittleEndian(cert);
        if (certSize != XbeCertSize)
            return null;

        var certTimeDate = BinaryPrimitives.ReadUInt32LittleEndian(cert[4..]);
        var titleId = BinaryPrimitives.ReadUInt32LittleEndian(cert[8..]);
        var titleName = Encoding.Unicode.GetString(cert.Slice(0x0C, 80));
        var nul = titleName.IndexOf('\0');
        if (nul >= 0)
            titleName = titleName[..nul];

        var alternateTitleIds = new uint[16];
        for (var i = 0; i < 16; i++)
            alternateTitleIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(cert.Slice(0x5C + (i * 4)));

        var allowedMedia = BinaryPrimitives.ReadUInt32LittleEndian(cert[0x9C..]);
        var gameRegion = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA0..]);
        var gameRatings = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA4..]);
        var diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA8..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xAC..]);

        return new XbeInfo(
            baseAddress,
            entryPoint,
            sectionCount,
            initFlags,
            certSize,
            certTimeDate,
            titleId,
            titleName,
            alternateTitleIds,
            allowedMedia,
            gameRegion,
            gameRatings,
            diskNumber,
            version);
    }

    private static void CollectHashes(
        string isoPath,
        string currentPath,
        HashAlgorithmName algorithm,
        List<(string Path, byte[] Hash)> results,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        // Hardening (#16): bound subdirectory descent like CopyOutDirectory above.
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{currentPath}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        var entries = ListDirectory(isoPath, currentPath);

        foreach (var entry in entries)
        {
            var fullPath = currentPath.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                CollectHashes(isoPath, fullPath, algorithm, results, depth + 1);
            }
            else
            {
                var hash = ComputeFileHash(isoPath, fullPath, algorithm);
                if (hash != null)
                    results.Add((fullPath, hash));
            }
        }
    }

    /// <summary>
    /// Reads all directory entries from a directory table at the given offset
    /// by performing an iterative preorder traversal of the AVL tree.
    /// </summary>
    /// <param name="fs">Open image stream.</param>
    /// <param name="dirStart">Absolute byte offset of the directory table.</param>
    /// <param name="contextPath">Image-internal path being listed, for error messages.</param>
    /// <exception cref="XisoFormatException">
    /// Thrown naming <paramref name="contextPath"/> and the offending offset
    /// when the table is structurally corrupt (TODO #16): a cycle, a child
    /// offset outside the image, or an absurd entry count — previously an
    /// infinite loop growing <c>entries</c> until OOM.
    /// </exception>
    private static List<EntryInfo> ReadDirectoryEntries(Stream fs, long dirStart, string contextPath)
    {
        var entries = new List<EntryInfo>();
        var stack = new Stack<long>();
        stack.Push(0); // Start at offset 0

        // Hardening (#16): every pushed offset is visited at most once — a corrupt
        // cycle (or DAG-shaped offsets fanning out exponentially) fails fast
        // with a named error instead of looping until OOM.
        var visited = new HashSet<long>();

        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            var absOffset = dirStart + offset;
            if (absOffset < dirStart || absOffset >= fs.Length)
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': child offset {offset} (seek {absOffset}) " +
                    $"points outside the image (table at {dirStart}, length {fs.Length}).");
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

            // Empty directory — xdvdfs fills with 0xFF or 0x00 (14 bytes all same). Original
            // code only handled 0xFFFF; 0x0000 needs a 14-byte check to distinguish a valid
            // entry whose left child offset is 0 (no left child) from a truly empty table.
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

            // Read right offset
            ReadExact(fs, shortBuf);
            var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            ReadExact(fs, intBuf);
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            var attributes = Constants.MaskAttributes(byteBuf[0]);

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            // Parity with TraverseXiso (BUG-LIB-021): separator-bearing names
            // abort the walk instead of flowing into Path.Combine, where they
            // would create directories outside the destination.
            if (filename.Contains('/') || filename.Contains('\\'))
            {
                throw new XisoFormatException(
                    $"invalid TOC entry at '{contextPath}': filename '{filename}' contains a path separator.");
            }

            // Skip "." and ".." entries
            if (string.Equals(filename, ".", StringComparison.Ordinal) ||
                string.Equals(filename, "..", StringComparison.Ordinal))
            {
                // Still need to traverse children
                if (rOffset != 0 && rOffset != Constants.PadShort)
                    stack.Push((long)rOffset * Constants.DwordSize);
                if (lOffset != 0 && lOffset != Constants.PadShort)
                    stack.Push((long)lOffset * Constants.DwordSize);
                continue;
            }

            var isDir = (attributes & Constants.AttributeDir) != 0;

            entries.Add(new EntryInfo(
                filename,
                isDir,
                startSector,
                isDir ? 0u : fileSize,
                attributes,
                lOffset,
                rOffset));

            // Push children onto stack (right first so left is processed first - preorder)
            if (rOffset != 0 && rOffset != Constants.PadShort)
                stack.Push((long)rOffset * Constants.DwordSize);

            if (lOffset != 0 && lOffset != Constants.PadShort)
                stack.Push((long)lOffset * Constants.DwordSize);
        }

        return entries;
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream,
    /// retrying until the buffer is full or EOF is reached.
    /// </summary>
    /// <exception cref="IOException">
    /// Thrown when EOF is reached before the buffer is fully populated.
    /// </exception>
    private static void ReadExact(Stream fs, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = fs.Read(buffer[offset..]);
            if (read <= 0)
                throw new IOException($"Read error: expected {buffer.Length} bytes, got {offset}");

            offset += read;
        }
    }
}

// ExtractErrorException is defined in ExtractErrorException.cs
