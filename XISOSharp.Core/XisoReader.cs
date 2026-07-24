using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.DataStructures;

namespace XISOSharp;

/// <summary>
/// Provides methods for reading, verifying, and traversing XISO disc images.
/// Supports extracting, listing, and generating AVL trees from the on-disk
/// directory structure.
/// </summary>
public static class XisoReader
{
    [ThreadStatic] private static byte[]? _copyBuffer;

    private static byte[] CopyBuffer => _copyBuffer ??= new byte[Constants.ReadWriteBufferSize];

    private static readonly byte[] HeaderDataBytes = Encoding.ASCII.GetBytes(Constants.HeaderData);

    /// <summary>
    /// Verifies that the given stream is a valid XISO image by checking the header
    /// magic at all known disc offsets. Returns root directory metadata and the
    /// disc lseek offset used.
    /// </summary>
    /// <param name="fs">Open file stream positioned anywhere.</param>
    /// <param name="isoName">Display name of the ISO (used in error messages).</param>
    /// <returns>
    /// Tuple containing the root directory sector index, root directory size in bytes,
    /// and the detected disc lseek offset.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when no valid XISO header is found at any known offset,
    /// or when the trailing magic byte does not match.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when the file is too short to contain the expected header data
    /// at all possible offsets.
    /// </exception>
    /// <exception cref="ExtractErrorException">
    /// Thrown when the root directory sector and size are both zero (empty ISO).
    /// </exception>
    public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
        FileStream fs, string isoName)
    {
        Span<byte> buffer = stackalloc byte[Constants.HeaderDataLength];
        long discLseek = 0;

        fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);

        ReadExact(fs, buffer);

        if (!buffer.SequenceEqual(HeaderDataBytes.AsSpan()))
        {
            fs.Seek((long)Constants.HeaderOffset + Constants.GlobalLseekOffset, SeekOrigin.Begin);
            ReadExact(fs, buffer);

            if (!buffer.SequenceEqual(HeaderDataBytes))
            {
                fs.Seek((long)Constants.HeaderOffset + Constants.Xgd3LseekOffset, SeekOrigin.Begin);
                ReadExact(fs, buffer);

                if (!buffer.SequenceEqual(HeaderDataBytes))
                {
                    fs.Seek((long)Constants.HeaderOffset + Constants.Xgd1LseekOffset, SeekOrigin.Begin);
                    ReadExact(fs, buffer);

                    if (!buffer.SequenceEqual(HeaderDataBytes))
                    {
                        Logger.LogErr($"{isoName} does not appear to be a valid xbox iso image\n");
                        throw new InvalidDataException($"Invalid XISO: {isoName}");
                    }
                    else
                    {
                        discLseek = Constants.Xgd1LseekOffset;
                    }
                }
                else
                {
                    discLseek = Constants.Xgd3LseekOffset;
                }
            }
            else
            {
                discLseek = Constants.GlobalLseekOffset;
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
            throw new InvalidDataException($"Corrupt XISO: {isoName}");
        }

        if (rootDirSector == 0 && rootDirSize == 0)
        {
            Logger.Log($"xbox image {isoName} contains no files.\n");
            throw new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        }

        var fileLength = fs.Length;
        var totalSectors = fileLength / Constants.SectorSize;

        if (rootDirSector >= totalSectors)
        {
            Logger.LogErr($"{isoName}: root directory sector {rootDirSector} exceeds total sectors {totalSectors}\n");
            throw new InvalidDataException(
                $"Corrupt XISO: {isoName} — root directory sector {rootDirSector} is beyond end of image ({totalSectors} sectors).");
        }

        if (rootDirSize == 0)
        {
            Logger.LogErr($"{isoName}: root directory size is zero but sector is non-zero\n");
            throw new InvalidDataException(
                $"Corrupt XISO: {isoName} — root directory size is zero with non-zero sector pointer.");
        }

        var availableBytes = (totalSectors - rootDirSector) * (long)Constants.SectorSize;
        if (rootDirSize > availableBytes)
        {
            Logger.LogErr($"{isoName}: root directory size {rootDirSize} exceeds available space {availableBytes}\n");
            throw new InvalidDataException(
                $"Corrupt XISO: {isoName} — root directory size {rootDirSize} bytes exceeds available space ({availableBytes} bytes from sector {rootDirSector}).");
        }

        fs.Seek((long)rootDirSector * Constants.SectorSize + discLseek, SeekOrigin.Begin);

        return (rootDirSector, rootDirSize, discLseek);
    }

    /// <summary>
    /// Recursively traverses the on-disk directory tree of an XISO image,
    /// building an AVL index and optionally extracting files or listing entries.
    /// </summary>
    /// <param name="fs">File stream positioned at the start of the directory sector.</param>
    /// <param name="inDirNode">Pre-allocated directory entry node, or <c>null</c> to create one.</param>
    /// <param name="dirStart">Byte offset of the current directory sector.</param>
    /// <param name="path">Path prefix for logging and extraction.</param>
    /// <param name="mode">Operating mode (extract, list, or generate AVL tree).</param>
    /// <param name="avlRoot">Reference to the AVL root being built.</param>
    /// <param name="llCompat">If <c>true</c>, uses backwards-compatible right-offset calculation.</param>
    /// <param name="discLseek">Disc lseek offset for sector address calculation.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    internal static int TraverseXiso(
        FileStream fs,
        DirEntry? inDirNode,
        long dirStart,
        string? path,
        ExtractMode mode,
        ref AvlNode? avlRoot,
        bool llCompat,
        long discLseek)
    {
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> byteBuf = stackalloc byte[1];

        DirEntry node = new();
        var dir = inDirNode ?? node;

        dir.Left = null;
        dir.Parent = null;
        dir.AvlNode = null;
        dir.Filename = "";

        ushort lOffset = 0;
        const int err = 0;

        while (true)
        {
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

                lOffset = (ushort)(lOffset * Constants.DwordSize +
                    (Constants.SectorSize - (lOffset * Constants.DwordSize) % Constants.SectorSize));
                fs.Seek(dirStart + lOffset, SeekOrigin.Begin);
                continue;
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
            var attributes = byteBuf[0];

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (string.Equals(filename, ".", StringComparison.Ordinal) || string.Equals(filename, "..", StringComparison.Ordinal) ||
                filename.Contains('/') || filename.Contains('\\'))
            {
                Logger.LogErr($"filename '{filename}' contains invalid character(s), aborting.\n");
                throw new InvalidOperationException($"Filename '{filename}' contains invalid character(s).");
            }

            if (mode == ExtractMode.GenerateAvl)
            {
                var avl = new AvlNode
                {
                    Filename = filename,
                    FileSize = fileSize,
                    OldStartSector = startSector
                };
                dir.AvlNode = avl;
                AvlTree.AvlInsert(ref avlRoot, avl);
            }

            if (lOffset != 0)
            {
                llCompat = false;

                var leftSeek = dirStart + (long)lOffset * Constants.DwordSize;
                if (leftSeek >= fs.Length)
                {
                    Logger.LogErr($"warning: left offset {lOffset} (seek {leftSeek}) exceeds file length {fs.Length}, truncating directory.\n");
                    goto end_traverse;
                }

                var left = new DirEntry();
                dir.Left = left;
                left.Parent = dir;

                fs.Seek(leftSeek, SeekOrigin.Begin);

                var savedDir = dir.Left!;
                TraverseXiso(fs, savedDir, dirStart, path, mode, ref avlRoot, llCompat, discLseek);
            }

            dir.Left = null;
            var curpos = fs.Position;

            if ((attributes & Constants.AttributeDir) != 0)
            {
                string subPath = null!;
                if (path != null)
                {
                    subPath = path + filename + Constants.PathCharStr;
                    fs.Seek((long)startSector * Constants.SectorSize + discLseek, SeekOrigin.Begin);
                }

                if (!Logger.RemoveSystemUpdate || !filename.Contains("$SystemUpdate", StringComparison.Ordinal))
                {
                    if (mode == ExtractMode.Extract)
                    {
                        Directory.CreateDirectory(filename);
                        Directory.SetCurrentDirectory(filename);
                    }

                    if (mode != ExtractMode.GenerateAvl)
                    {
                        Logger.Log($"{mode switch { ExtractMode.Extract => "creating ", _ => "" }}{path}{filename}{Constants.PathCharStr} (0 bytes){mode switch { ExtractMode.Extract => " [OK]", _ => "" }}\n");
                        Logger.Flush();
                    }

                    if (fileSize > 0)
                    {
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

                        var subAvlRoot = mode == ExtractMode.GenerateAvl ? dir.AvlNode?.Subdirectory : null;
                        TraverseXiso(
                            fs, subdir,
                            (long)startSector * Constants.SectorSize + discLseek,
                            subPath, mode,
                            ref (mode == ExtractMode.GenerateAvl ? ref dir.AvlNode!.Subdirectory : ref subAvlRoot)!,
                            llCompat, discLseek);
                    }

                    if (mode == ExtractMode.Extract)
                    {
                        Directory.SetCurrentDirectory("..");
                    }
                }
            }
            else if (mode != ExtractMode.GenerateAvl)
            {
                if (!Logger.RemoveSystemUpdate || !(path?.Contains("$SystemUpdate", StringComparison.Ordinal) ?? false))
                {
                    if (mode == ExtractMode.Extract)
                    {
                        ExtractFile(fs, filename, startSector, fileSize, path, discLseek);
                    }
                    else
                    {
                        Logger.Log($"{path!}{filename} ({fileSize} bytes)\n");
                        Logger.Flush();
                    }

                    Logger.TotalFiles++;
                    Logger.TotalFilesAllIsos++;
                    Logger.TotalBytes += fileSize;
                    Logger.TotalBytesAllIsos += fileSize;
                }
            }

            if (rOffset != 0)
            {
                if (llCompat)
                {
                    var sector = (curpos - dirStart) / Constants.SectorSize;
                    if ((long)rOffset * Constants.DwordSize / Constants.SectorSize > sector)
                    {
                        rOffset = (ushort)(sector * (Constants.SectorSize / Constants.DwordSize) +
                                           (Constants.SectorSize / Constants.DwordSize));
                    }
                }

                var rightSeek = dirStart + (long)rOffset * Constants.DwordSize;
                if (rightSeek >= fs.Length)
                {
                    Logger.LogErr($"warning: right offset {rOffset} (seek {rightSeek}) exceeds file length {fs.Length}, truncating directory.\n");
                    break;
                }

                fs.Seek(rightSeek, SeekOrigin.Begin);

                dir.Filename = "";
                lOffset = rOffset;

                continue;
            }

            break;
        }

        end_traverse:
        return err;
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
    /// <exception cref="IOException">Thrown on read or write errors.</exception>
    internal static void ExtractFile(
        FileStream fs,
        string filename,
        uint startSector,
        uint fileSize,
        string? path,
        long discLseek)
    {
        if (Logger.RemoveSystemUpdate && path != null && path.Contains("$SystemUpdate", StringComparison.Ordinal))
        {
            fs.Seek((long)startSector * Constants.SectorSize + discLseek, SeekOrigin.Begin);
            return;
        }

        using var outFile = new FileStream(
            filename,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 65536
            });

        fs.Seek((long)startSector * Constants.SectorSize + discLseek, SeekOrigin.Begin);

        if (fileSize == 0)
        {
            Logger.Log($"extracting {path}{filename} (0 bytes) [100%]{(Logger.Out == Console.Out && Console.IsOutputRedirected ? "\n" : "\r")}");
            Logger.Flush();
        }
        else
        {
            uint totalSize = 0;
            var size = Math.Min(fileSize, Constants.ReadWriteBufferSize);

            do
            {
                var readSize = fs.Read(CopyBuffer, 0, (int)size);
                if (readSize < 0)
                    throw new IOException("Read error in extract_file");

                if (readSize != 0)
                {
                    outFile.Write(CopyBuffer, 0, readSize);
                }

                totalSize += (uint)readSize;
                var percent = (uint)(totalSize * 100.0 / fileSize);
                Logger.Log($"extracting {path}{filename} ({fileSize} bytes) [{percent}%]{(Logger.Out == Console.Out && Console.IsOutputRedirected ? "\n" : "\r")}");
                Logger.Flush();

                size = Math.Min(fileSize - totalSize, Constants.ReadWriteBufferSize);
            } while (totalSize < fileSize && size > 0);

            if (totalSize < fileSize)
            {
                Logger.Log($"\nWARNING: File {filename} is truncated. Reported size: {fileSize} bytes, read size: {totalSize} bytes!\n");
            }
        }

        Logger.Log("\n");
    }

    /// <summary>
    /// Rewrites (optimizes) an XISO image. The source ISO is renamed to <c>.old</c>
    /// and a new optimized ISO is created in its place.
    /// Always uses <c>llCompat=true</c> to handle linked-list-style directory entries.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file to rewrite.</param>
    /// <param name="outputPath">Output directory for the rewritten ISO, or <c>null</c> for the current directory.</param>
    /// <param name="outIsoPath">Receives the path to the output ISO file.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">
    /// Custom output filename. When <c>null</c>, the original filename with <c>.iso</c> extension is used.
    /// </param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Rewrite(
        string xisoPath,
        string? outputPath,
        out string? outIsoPath,
        CancellationToken cancellationToken = default,
        string? outputName = null)
    {
        return DecodeXiso(xisoPath, outputPath, ExtractMode.Rewrite, out outIsoPath, true, cancellationToken, outputName);
    }

    /// <summary>
    /// Extracts files from an XISO image to a directory.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file.</param>
    /// <param name="outputPath">Output directory, or <c>null</c> to extract to an ISO-named subdirectory.</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Extract(
        string xisoPath,
        string? outputPath,
        bool llCompat,
        CancellationToken cancellationToken = default)
    {
        return DecodeXiso(xisoPath, outputPath, ExtractMode.Extract, out _, llCompat, cancellationToken);
    }

    /// <summary>
    /// Lists files in an XISO image without extracting.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file.</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int List(
        string xisoPath,
        bool llCompat,
        CancellationToken cancellationToken = default)
    {
        return DecodeXiso(xisoPath, null, ExtractMode.List, out _, llCompat, cancellationToken);
    }

    /// <summary>
    /// Recursively lists all files in an XISO image in a tree format,
    /// showing full paths and sizes for each entry.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file.</param>
    /// <param name="llCompat">
    /// If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.
    /// Pass <c>false</c> for already-optimized ISOs.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int Tree(
        string xisoPath,
        bool llCompat,
        CancellationToken cancellationToken = default)
    {
        return DecodeXiso(xisoPath, null, ExtractMode.Tree, out _, llCompat, cancellationToken);
    }

    /// <summary>
    /// Main entry point for processing an XISO image. Verifies the image, then
    /// performs extraction, listing, or rewriting based on the specified mode.
    /// Prefer using <see cref="Rewrite"/>, <see cref="Extract"/>, or <see cref="List"/>
    /// for mode-specific operations.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file (or <c>.old</c> file for rewrite mode).</param>
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
    /// <returns>0 on success, non-zero on error.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file is not a valid XISO image.
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
        string? outputName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outIsoPath = null;
        var repair = false;

        var filename = xisoPath;

        if (mode == ExtractMode.Rewrite)
        {
            filename = filename[..^4];
            repair = true;
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
                Logger.LogErr($"invalid xiso image name: {xisoPath}\n");
                return 1;
        }

        string? cwd = null;
        if (mode == ExtractMode.Extract && outputPath != null)
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

        using var fs = new FileStream(
            xisoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });

        (uint rootDirSect, uint rootDirSize, long discLseek) = VerifyXiso(fs, name);

        Logger.XboxDiscLseek = discLseek;

        var isoName = shortName ?? name;

        if (mode != ExtractMode.Rewrite)
        {
            Logger.Log($"{(mode == ExtractMode.Extract ? "extracting" : "listing")} {name}:\n\n");

            if (mode == ExtractMode.Extract && outputPath == null)
            {
                try
                {
                    Directory.CreateDirectory(isoName);
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
                fs.Seek((long)rootDirSect * Constants.SectorSize + discLseek, SeekOrigin.Begin);
                AvlNode? avlRoot = null;
                TraverseXiso(fs, null, (long)rootDirSect * Constants.SectorSize + discLseek,
                    buf, ExtractMode.GenerateAvl, ref avlRoot, llCompat, discLseek);

                XisoWriter.CreateXiso(isoName, outputPath, avlRoot, fs, out outIsoPath, outputName, null);
            }
            else
            {
                fs.Seek((long)rootDirSect * Constants.SectorSize + discLseek, SeekOrigin.Begin);
                AvlNode? avlRoot = null;
                TraverseXiso(fs, null, (long)rootDirSect * Constants.SectorSize + discLseek,
                    buf, mode, ref avlRoot, llCompat, discLseek);
            }
        }

        if (shortName != null)
        {
        }

        if (cwd != null)
        {
            Directory.SetCurrentDirectory(cwd);
        }

        if (repair)
        {
        }

        return 0;
    }

    /// <summary>
    /// Asynchronously processes an XISO image. Verifies the image, then
    /// performs extraction, listing, or rewriting based on the specified mode.
    /// </summary>
    /// <param name="xisoPath">Path to the XISO file (or <c>.old</c> file for rewrite mode).</param>
    /// <param name="outputPath">Output directory for extraction or rewrite output. When <c>null</c> in extract mode, a directory named after the ISO is created.</param>
    /// <param name="mode">Operating mode: extract, list, or rewrite.</param>
    /// <param name="llCompat">If <c>true</c>, use backwards-compatible (non-optimized) right-offset calculation.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="outputName">Custom output filename for rewrite mode. When <c>null</c>, the original filename with <c>.iso</c> extension is used.</param>
    /// <returns>A task that completes with the result code (0 on success, non-zero on error) and the output ISO path when in rewrite mode.</returns>
    public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
        string xisoPath,
        string? outputPath,
        ExtractMode mode,
        bool llCompat = false,
        CancellationToken cancellationToken = default,
        string? outputName = null)
    {
        return await Task.Run(() =>
        {
            var result = DecodeXiso(xisoPath, outputPath, mode, out var outPath, llCompat, cancellationToken, outputName);
            return (result, outPath);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the XISO volume descriptor and returns metadata about the image
    /// without throwing on validation errors.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>Volume information including root directory location and disc format.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public static VolumeInfo GetVolumeInfo(string isoPath)
    {
        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 256
            });

        var fileLength = fs.Length;
        var totalSectors = fileLength / Constants.SectorSize;

        if (fileLength < Constants.HeaderOffset + Constants.HeaderDataLength)
            return new VolumeInfo(false, 0, 0, 0, fileLength, totalSectors);

        Span<byte> buffer = stackalloc byte[Constants.HeaderDataLength];
        long discLseek = 0;
        bool isValid = false;

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
        var issues = new List<string>();
        var filesChecked = 0;
        var dirsChecked = 0;

        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
        {
            issues.Add("Header magic not found at any known disc offset.");
            return new AuditResult(false, 0, 0, issues);
        }

        if (volInfo.RootDirSector == 0 && volInfo.RootDirSize == 0)
        {
            return new AuditResult(true, 0, 0, issues);
        }

        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });

        try
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTagLength];
            ReadExact(fs, tagBuf);
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

        var fileLength = fs.Length;
        var discLseek = volInfo.DiscLseek;
        var rootDirStart = (long)volInfo.RootDirSector * Constants.SectorSize + discLseek;

        if (rootDirStart >= fileLength)
        {
            issues.Add($"Root directory sector {volInfo.RootDirSector} (offset {rootDirStart}) exceeds file length {fileLength}.");
            return new AuditResult(false, 0, 0, issues);
        }

        var visited = new HashSet<long>();

        AuditWalk(fs, rootDirStart, "/", fileLength, discLseek, issues, visited, ref filesChecked, ref dirsChecked);

        return new AuditResult(issues.Count == 0, filesChecked, dirsChecked, issues);
    }

    private static void AuditWalk(
        FileStream fs,
        long dirStart,
        string path,
        long fileLength,
        long discLseek,
        List<string> issues,
        HashSet<long> visited,
        ref int filesChecked,
        ref int dirsChecked)
    {
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];

        while (true)
        {
            if (dirStart >= fileLength)
            {
                issues.Add($"Directory offset {dirStart} ({path}) exceeds file length {fileLength}.");
                return;
            }

            if (!visited.Add(dirStart))
            {
                issues.Add($"Cycle detected: directory entry at offset {dirStart} ({path}) was already visited on this path.");
                return;
            }

            fs.Seek(dirStart, SeekOrigin.Begin);

            ReadExact(fs, shortBuf);
            var lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            if (lOffset == Constants.PadShort)
            {
                return;
            }

            if (lOffset != 0)
            {
                var leftSeek = dirStart + (long)lOffset * Constants.DwordSize;
                if (leftSeek >= fileLength)
                {
                    issues.Add($"Left child offset {lOffset} (seek {leftSeek}) exceeds file length in {path}.");
                }
                else
                {
                    var childVisited = new HashSet<long>(visited);
                    AuditWalk(fs, leftSeek, path, fileLength, discLseek, issues, childVisited, ref filesChecked, ref dirsChecked);
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
            var attributes = byteBuf[0];

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

            if (filename.Contains('/') || filename.Contains('\\'))
            {
                issues.Add($"Filename '{filename}' contains path separator in {path}.");
            }

            var sectorOffset = (long)startSector * Constants.SectorSize + discLseek;
            if (sectorOffset >= fileLength)
            {
                issues.Add($"Sector {startSector} (offset {sectorOffset}) for '{path}{filename}' exceeds file length {fileLength}.");
            }

            if ((attributes & 0x48) != 0)
            {
                issues.Add($"Reserved attribute bits set in '{path}{filename}': 0x{attributes:X2}.");
            }

            var isDir = (attributes & Constants.AttributeDir) != 0;

            if (isDir)
            {
                dirsChecked++;

                if (fileSize > 0 && sectorOffset < fileLength)
                {
                    var endOffset = sectorOffset + fileSize;
                    if (endOffset > fileLength)
                    {
                        issues.Add($"Directory '{path}{filename}' size {fileSize} (ends at {endOffset}) exceeds file length {fileLength}.");
                    }

                    var subDirStart = sectorOffset;
                    AuditWalk(fs, subDirStart, path + filename + "/", fileLength, discLseek, issues, new HashSet<long>(), ref filesChecked, ref dirsChecked);
                }
            }
            else
            {
                filesChecked++;
            }

            if (rOffset != 0)
            {
                var rightSeek = dirStart + (long)rOffset * Constants.DwordSize;
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
    /// Returns metadata about all entries in the specified directory within an XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">
    /// Path within the ISO to list (e.g. <c>"/"</c> for root, <c>"/subdir"</c> for a subdirectory).
    /// Use forward slashes as separators.
    /// </param>
    /// <returns>List of directory entries, or empty if the directory is empty.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the ISO is invalid.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static IReadOnlyList<EntryInfo> ListDirectory(string isoPath, string internalPath = "/")
    {
        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });

        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new InvalidDataException($"Not a valid XISO: {isoPath}");

        if (volInfo.RootDirSector == 0 && volInfo.RootDirSize == 0)
            return Array.Empty<EntryInfo>();

        var dirStart = (long)volInfo.RootDirSector * Constants.SectorSize + volInfo.DiscLseek;

        // Navigate to the target directory if not root
        if (!string.Equals(internalPath, "/", StringComparison.Ordinal))
        {
            var segments = internalPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var entries = ReadDirectoryEntries(fs, dirStart);
                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase) && e.IsDirectory);

                if (match == null)
                    throw new InvalidDataException($"Path not found: {internalPath}");

                dirStart = (long)match.StartSector * Constants.SectorSize + volInfo.DiscLseek;
            }
        }

        return ReadDirectoryEntries(fs, dirStart);
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
        if (string.IsNullOrEmpty(internalPath) || string.Equals(internalPath, "/", StringComparison.Ordinal))
            return null;

        var segments = internalPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var dirPath = segments.Length > 1
            ? "/" + string.Join("/", segments[..^1])
            : "/";

        var entryName = segments[^1];

        var entries = ListDirectory(isoPath, dirPath);
        return entries.FirstOrDefault(e =>
            string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies a single file or directory from an XISO image to the local filesystem.
    /// If the path points to a file, it is extracted to <paramref name="destPath"/>.
    /// If the path points to a directory, all its contents are recursively extracted.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="internalPath">Path within the ISO (e.g. <c>"/subdir/file.xbe"</c>).</param>
    /// <param name="destPath">Destination path on the local filesystem.</param>
    /// <exception cref="FileNotFoundException">Thrown when the ISO file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the internal path does not exist.</exception>
    /// <exception cref="IOException">Thrown on read or write errors.</exception>
    public static void CopyOut(string isoPath, string internalPath, string destPath)
    {
        var entry = GetEntryInfo(isoPath, internalPath);
        if (entry == null)
            throw new InvalidDataException($"Path not found in XISO: {internalPath}");

        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new InvalidDataException($"Not a valid XISO: {isoPath}");

        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });

        if (entry.IsDirectory)
        {
            CopyOutDirectory(fs, isoPath, internalPath, destPath, volInfo);
        }
        else
        {
            CopyOutFile(fs, entry, destPath, volInfo);
        }
    }

    private static void CopyOutFile(FileStream fs, EntryInfo entry, string destPath, VolumeInfo volInfo)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        using var outFile = new FileStream(
            destPath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 65536
            });

        fs.Seek((long)entry.StartSector * Constants.SectorSize + volInfo.DiscLseek, SeekOrigin.Begin);

        uint remaining = entry.FileSize;
        var buffer = new byte[Constants.ReadWriteBufferSize];

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, Constants.ReadWriteBufferSize);
            var read = fs.Read(buffer, 0, toRead);
            if (read <= 0)
                throw new IOException($"Unexpected end of file data for {entry.Name}");

            outFile.Write(buffer, 0, read);
            remaining -= (uint)read;
        }
    }

    private static void CopyOutDirectory(FileStream fs, string isoPath, string internalPath, string destPath, VolumeInfo volInfo)
    {
        Directory.CreateDirectory(destPath);

        var entries = ListDirectory(isoPath, internalPath);

        foreach (var entry in entries)
        {
            var entryDestPath = Path.Combine(destPath, entry.Name);
            var entryInternalPath = internalPath.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                CopyOutDirectory(fs, isoPath, entryInternalPath, entryDestPath, volInfo);
            }
            else
            {
                CopyOutFile(fs, entry, entryDestPath, volInfo);
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
        var entry = GetEntryInfo(isoPath, internalPath);
        if (entry == null)
            return null;

        if (entry.IsDirectory)
            throw new InvalidDataException($"Cannot hash a directory: {internalPath}");

        using var hasher = CreateHashAlgorithm(algorithm);

        if (entry.FileSize == 0)
        {
            return hasher.ComputeHash(Array.Empty<byte>());
        }

        var volInfo = GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new InvalidDataException($"Not a valid XISO: {isoPath}");

        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });

        fs.Seek((long)entry.StartSector * Constants.SectorSize + volInfo.DiscLseek, SeekOrigin.Begin);

        var buffer = new byte[Constants.ReadWriteBufferSize];
        uint remaining = entry.FileSize;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, Constants.ReadWriteBufferSize);
            var read = fs.Read(buffer, 0, toRead);
            if (read <= 0)
                throw new IOException($"Unexpected end of file data at sector {entry.StartSector}");

            hasher.TransformBlock(buffer, 0, read, buffer, 0);
            remaining -= (uint)read;
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

        CollectHashes(isoPath, internalPath, algorithm, volInfo, results);
        return results;
    }

    private static void CollectHashes(
        string isoPath,
        string currentPath,
        HashAlgorithmName algorithm,
        VolumeInfo volInfo,
        List<(string Path, byte[] Hash)> results)
    {
        var entries = ListDirectory(isoPath, currentPath);

        foreach (var entry in entries)
        {
            var fullPath = currentPath.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                CollectHashes(isoPath, fullPath, algorithm, volInfo, results);
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
    private static List<EntryInfo> ReadDirectoryEntries(FileStream fs, long dirStart)
    {
        var entries = new List<EntryInfo>();
        var stack = new Stack<long>();
        stack.Push(0); // Start at offset 0

        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            fs.Seek(dirStart + offset, SeekOrigin.Begin);

            ReadExact(fs, shortBuf);
            var lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            // Empty directory or end of entries
            if (lOffset == Constants.PadShort && offset == 0)
                continue;

            // Read right offset
            ReadExact(fs, shortBuf);
            var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            ReadExact(fs, intBuf);
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            var attributes = byteBuf[0];

            ReadExact(fs, byteBuf);
            var filenameLength = byteBuf[0];

            var nameBuf = new byte[filenameLength];
            ReadExact(fs, nameBuf);
            var filename = Latin1Encoding.Instance.GetString(nameBuf);

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
    private static void ReadExact(FileStream fs, Span<byte> buffer)
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
