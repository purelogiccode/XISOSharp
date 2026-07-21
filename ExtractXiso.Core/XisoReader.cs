using System.Buffers.Binary;
using ExtractXiso.DataStructures;

namespace ExtractXiso;

/// <summary>
/// Provides methods for reading, verifying, and traversing XISO disc images.
/// Supports extracting, listing, and generating AVL trees from the on-disk
/// directory structure.
/// </summary>
public static class XisoReader
{
    [ThreadStatic]
    private static byte[]? _copyBuffer;

    private static byte[] CopyBuffer => _copyBuffer ??= new byte[Constants.ReadWriteBufferSize];

    private static readonly byte[] _headerDataBytes = System.Text.Encoding.ASCII.GetBytes(Constants.HeaderData);

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

        if (!buffer.SequenceEqual(_headerDataBytes.AsSpan()))
        {
            fs.Seek((long)Constants.HeaderOffset + Constants.GlobalLseekOffset, SeekOrigin.Begin);
            ReadExact(fs, buffer);

            if (!buffer.SequenceEqual(_headerDataBytes))
            {
                fs.Seek((long)Constants.HeaderOffset + Constants.Xgd3LseekOffset, SeekOrigin.Begin);
                ReadExact(fs, buffer);

                if (!buffer.SequenceEqual(_headerDataBytes))
                {
                    fs.Seek((long)Constants.HeaderOffset + Constants.Xgd1LseekOffset, SeekOrigin.Begin);
                    ReadExact(fs, buffer);

                    if (!buffer.SequenceEqual(_headerDataBytes))
                    {
                        Logger.LogErr($"{isoName} does not appear to be a valid xbox iso image\n");
                        throw new InvalidDataException($"Invalid XISO: {isoName}");
                    }
                    else discLseek = Constants.Xgd1LseekOffset;
                }
                else discLseek = Constants.Xgd3LseekOffset;
            }
            else discLseek = Constants.GlobalLseekOffset;
        }

        Span<byte> intBuf = stackalloc byte[4];
        ReadExact(fs, intBuf);
        uint rootDirSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

        ReadExact(fs, intBuf);
        uint rootDirSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

        fs.Seek(Constants.FileTimeSize + Constants.UnusedSize, SeekOrigin.Current);
        ReadExact(fs, buffer);
        if (!buffer.SequenceEqual(_headerDataBytes))
        {
            Logger.LogErr($"{isoName} appears to be corrupt\n");
            throw new InvalidDataException($"Corrupt XISO: {isoName}");
        }

        if (rootDirSector == 0 && rootDirSize == 0)
        {
            Logger.Log($"xbox image {isoName} contains no files.\n");
            throw new ExtractErrorException(ExtractError.ErrIsoNoFiles);
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
        DirEntry? dir = inDirNode ?? node;

        dir.Left = null;
        dir.Parent = null;
        dir.AvlNode = null;
        dir.Filename = "";

        ushort lOffset = 0;
        int err = 0;

        while (true)
        {
            ReadExact(fs, shortBuf);
            ushort tmp = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

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
            ushort rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

            ReadExact(fs, intBuf);
            uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, intBuf);
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

            ReadExact(fs, byteBuf);
            byte attributes = byteBuf[0];

            ReadExact(fs, byteBuf);
            byte filenameLength = byteBuf[0];

            Span<byte> nameBuf = stackalloc byte[filenameLength];
            ReadExact(fs, nameBuf);
            string filename = System.Text.Encoding.ASCII.GetString(nameBuf);

            if (filename == "." || filename == ".." ||
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

                var left = new DirEntry();
                dir.Left = left;
                left.Parent = dir;

                fs.Seek(dirStart + (long)lOffset * Constants.DwordSize, SeekOrigin.Begin);

                DirEntry savedDir = dir.Left!;
                TraverseXiso(fs, savedDir, dirStart, path, mode, ref avlRoot, llCompat, discLseek);
            }

            dir.Left = null;

            if ((attributes & Constants.AttributeDIR) != 0)
            {
                string subPath = null!;
                if (path != null)
                {
                    subPath = path + filename + Constants.PathCharStr;
                    fs.Seek((long)startSector * Constants.SectorSize + discLseek, SeekOrigin.Begin);
                }

                if (!Logger.RemoveSystemUpdate || !filename.Contains("$SystemUpdate"))
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
                if (!Logger.RemoveSystemUpdate || !(path?.Contains("$SystemUpdate") ?? false))
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
                    int sector = (int)((fs.Position - dirStart) / Constants.SectorSize);
                    if ((long)rOffset * Constants.DwordSize / Constants.SectorSize > sector)
                        rOffset = (ushort)(sector * (Constants.SectorSize / Constants.DwordSize) +
                            (Constants.SectorSize / Constants.DwordSize));
                }

                fs.Seek(dirStart + (long)rOffset * Constants.DwordSize, SeekOrigin.Begin);

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
        if (Logger.RemoveSystemUpdate && path != null && path.Contains("$SystemUpdate"))
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
            Logger.Log($"extracting {path}{filename} (0 bytes) [100%]\r");
            Logger.Flush();
        }
        else
        {
            uint totalSize = 0;
            uint size = Math.Min(fileSize, (uint)Constants.ReadWriteBufferSize);

            do
            {
                int readSize = fs.Read(CopyBuffer, 0, (int)size);
                if (readSize < 0)
                    throw new IOException("Read error in extract_file");

                if (readSize != 0)
                {
                    outFile.Write(CopyBuffer, 0, readSize);
                }

                totalSize += (uint)readSize;
                uint percent = (uint)(totalSize * 100.0 / fileSize);
                Logger.Log($"extracting {path}{filename} ({fileSize} bytes) [{percent}%]\r");
                Logger.Flush();

                size = Math.Min(fileSize - totalSize, (uint)Constants.ReadWriteBufferSize);
            }
            while (totalSize < fileSize && size > 0);

            if (totalSize < fileSize)
            {
                Logger.Log($"\nWARNING: File {filename} is truncated. Reported size: {fileSize} bytes, read size: {totalSize} bytes!\n");
            }
        }

        Logger.Log("\n");
    }

    /// <summary>
    /// Main entry point for processing an XISO image. Verifies the image, then
    /// performs extraction, listing, or rewriting based on the specified mode.
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outIsoPath = null;
        bool repair = false;

        string filename = xisoPath;
        int len = filename.Length;

        if (mode == ExtractMode.Rewrite)
        {
            filename = filename[..^4];
            repair = true;
        }

        string name;
        int nameStart = filename.LastIndexOf(Constants.PathChar) + 1;
        name = filename[nameStart..];
        len = name.Length;

        string? shortName = null;
        if (len > 4 && string.Equals(name[^4..], ".iso", StringComparison.OrdinalIgnoreCase))
        {
            shortName = name[..^4];
        }

        if (len == 0)
        {
            Logger.LogErr($"invalid xiso image name: {xisoPath}\n");
            return 1;
        }

        string? cwd = null;
        if (mode == ExtractMode.Extract && outputPath != null)
        {
            cwd = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputPath);
            Directory.SetCurrentDirectory(outputPath);
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

        var (rootDirSect, rootDirSize, discLseek) = VerifyXiso(fs, name);

        Logger.XboxDiscLseek = discLseek;

        string isoName = shortName ?? name;

        if (mode != ExtractMode.Rewrite)
        {
            Logger.Log($"{(mode == ExtractMode.Extract ? "extracting" : "listing")} {name}:\n\n");

            if (mode == ExtractMode.Extract && outputPath == null)
            {
                Directory.CreateDirectory(isoName);
                Directory.SetCurrentDirectory(isoName);
            }
        }

        if (rootDirSect != 0 && rootDirSize != 0)
        {
            int pathLen = outputPath?.Length ?? 0;
            int addSlash = 0;
            if (outputPath != null && outputPath[^1] != Constants.PathChar)
                addSlash = 1;

            string buf = string.Concat(
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

                XisoWriter.CreateXiso(isoName, outputPath, avlRoot, fs, out outIsoPath, null, null);
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
    /// <returns>A task that completes with the result code (0 on success, non-zero on error) and the output ISO path when in rewrite mode.</returns>
    public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
        string xisoPath,
        string? outputPath,
        ExtractMode mode,
        bool llCompat = false,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            int result = DecodeXiso(xisoPath, outputPath, mode, out string? outPath, llCompat, cancellationToken);
            return (result, outPath);
        }, cancellationToken);
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
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = fs.Read(buffer[offset..]);
            if (read <= 0)
                throw new IOException($"Read error: expected {buffer.Length} bytes, got {offset}");
            offset += read;
        }
    }
}

/// <summary>
/// Exception thrown for non-fatal XISO extraction errors such as an empty ISO image.
/// The <see cref="ErrorCode"/> property identifies the specific error.
/// </summary>
public class ExtractErrorException : Exception
{
    /// <summary>The specific error code that caused this exception.</summary>
    public ExtractError ErrorCode { get; }

    /// <summary>
    /// Creates a new <see cref="ExtractErrorException"/> with the given error code.
    /// </summary>
    /// <param name="code">The <see cref="ExtractError"/> value describing the failure.</param>
    public ExtractErrorException(ExtractError code) : base($"Extract error: {code}")
    {
        ErrorCode = code;
    }
}
