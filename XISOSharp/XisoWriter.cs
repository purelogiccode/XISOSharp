using System.Buffers.Binary;
using System.Text;
using XISOSharp.DataStructures;

namespace XISOSharp;

/// <summary>
/// Provides methods for creating and rewriting XISO disc images from
/// a local file system or an existing AVL tree built from a source ISO.
/// </summary>
public static class XisoWriter
{
    [ThreadStatic] private static BoyerMoore? _bm;

    /// <summary>
    /// Creates or rewrites an XISO image. When <paramref name="inRoot"/> is <c>null</c>,
    /// builds an AVL tree from the local file system and creates a new ISO.
    /// Otherwise, rewrites the ISO using the pre-built AVL tree and source stream.
    /// </summary>
    /// <param name="rootDirectory">
    /// Source directory for creation, or base name for rewrite mode.
    /// </param>
    /// <param name="outputDirectory">
    /// Directory where the output ISO file is written.
    /// When <c>null</c>, the current working directory is used.
    /// </param>
    /// <param name="inRoot">
    /// Pre-built AVL tree root. When <c>null</c>, the tree is generated from the file system.
    /// </param>
    /// <param name="sourceStream">
    /// Source ISO stream for reading file data in rewrite mode;
    /// <c>null</c> when creating from a file system.
    /// </param>
    /// <param name="outIsoPath">
    /// Receives the full path of the created output ISO file.
    /// </param>
    /// <param name="inName">
    /// Explicit output filename. When <c>null</c>, the directory name plus <c>.iso</c> is used.
    /// </param>
    /// <param name="progressCallback">
    /// Optional callback invoked with (<c>currentBytes</c>, <c>totalBytes</c>) during write.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="prependSectors">
    /// Optional number of 2048-byte sectors to prepend to the output image before the
    /// XISO filesystem begins, leaving room for a video partition (e.g. for Redump-style
    /// reconstruction). Sector numbers stored in directory entries remain partition-relative;
    /// only the physical file positions shift by <c>prependSectors * SectorSize</c>.
    /// </param>
    /// <param name="excludePatterns">
    /// Optional glob patterns of files and directories to omit from the image when creating
    /// from a file system. Paths are matched relative to <paramref name="rootDirectory"/>
    /// using <c>/</c> separators (see <see cref="GlobMatcher"/> for the supported syntax).
    /// Excluded directories are not recursed into. Ignored in rewrite mode.
    /// When <see cref="Logger.RemoveSystemUpdate"/> is set, the pattern
    /// <c>**/$SystemUpdate/**</c> is implicitly added.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel. Receives <see cref="ProgressInfo"/> events:
    /// <see cref="ProgressInfoType.FileCount"/> and <see cref="ProgressInfoType.DirCount"/>
    /// before writing starts, <see cref="ProgressInfoType.DirAdded"/> /
    /// <see cref="ProgressInfoType.FileAdded"/> as entries are written, and
    /// <see cref="ProgressInfoType.FinishedPacking"/> when the image is complete.
    /// </param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int CreateXiso(
        string rootDirectory,
        string? outputDirectory,
        AvlNode? inRoot,
        Stream? sourceStream,
        out string? outIsoPath,
        string? inName,
        ProgressCallback? progressCallback,
        CancellationToken cancellationToken = default,
        int? prependSectors = null,
        IReadOnlyList<string>? excludePatterns = null,
        IProgress<ProgressInfo>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outIsoPath = null;
        var err = 0;

        if (prependSectors is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prependSectors), prependSectors.Value,
                "Prepend sectors must be non-negative.");
        }

        var prependOffset = (long)(prependSectors ?? 0) * Constants.SectorSize;

        Logger.TotalBytes = Logger.TotalFiles = 0;

        var cwd = Directory.GetCurrentDirectory();

        // Capture full source path before chdir for #55 validation (relative paths resolve against original CWD).
        string? fullSourceForValidation = null;
        if (inRoot == null)
        {
            try
            {
                fullSourceForValidation = Path.IsPathRooted(rootDirectory)
                    ? Path.GetFullPath(rootDirectory)
                    : Path.GetFullPath(Path.Combine(cwd, rootDirectory));
                fullSourceForValidation = fullSourceForValidation.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (fullSourceForValidation.Length == 0)
                    fullSourceForValidation = cwd;
            }
            catch
            {
                fullSourceForValidation = null;
            }
        }

        string isoName;
        string isoDir;

        if (inRoot == null)
        {
            Directory.SetCurrentDirectory(rootDirectory);

            var dir = rootDirectory;
            var last = dir.Length - 1;
            if (last >= 0 && dir[last] == Path.DirectorySeparatorChar)
            {
                dir = dir[..last];
            }

            var slashPos = dir.LastIndexOf(Constants.PathChar) + 1;
            isoDir = dir[slashPos..];
            isoName = inName ?? isoDir;
        }
        else
        {
            isoDir = rootDirectory;
            isoName = rootDirectory;
        }

        if (string.IsNullOrEmpty(isoDir))
        {
            isoDir = Constants.PathCharStr;
        }

        outputDirectory ??= cwd;

        var outLen = outputDirectory.Length;
        if (outLen > 0 && outputDirectory[outLen - 1] == Constants.PathChar)
        {
            outputDirectory = outputDirectory[..--outLen];
        }

        if (string.IsNullOrEmpty(isoName))
        {
            isoName = "root";
        }
        else if (OperatingSystem.IsWindows() && isoName.Length > 1 && isoName[1] == ':')
        {
            isoName = isoName[1..];
        }

        var xisoPath = Path.Combine(outputDirectory, isoName + (inName != null ? "" : ".iso"));

        // #55 — Cannot generate ISO when output path equals input directory.
        if (inRoot == null && fullSourceForValidation != null)
        {
            try
            {
                var fullOutput = Path.IsPathRooted(xisoPath)
                    ? Path.GetFullPath(xisoPath)
                    : Path.GetFullPath(Path.Combine(cwd, xisoPath));
                ValidateOutputNotColliding(fullSourceForValidation, fullOutput);
            }
            catch (ArgumentException)
            {
                // Ensure CWD is restored before throwing (cleanup label expects original CWD).
                try
                {
                    Directory.SetCurrentDirectory(cwd);
                }
                catch
                {
                    // ignored
                }

                throw;
            }
        }

        Logger.Log($"{(inRoot != null ? "rewriting" : "\ncreating")} {isoName}{(inName != null ? "" : ".iso")}:\n\n");

        var root = new AvlNode { Filename = isoDir, StartSector = Constants.RootDirectorySector };

        Logger.TotalBytes = Logger.TotalFiles = 0;

        if (inRoot != null)
        {
            root.Subdirectory = inRoot;
            AvlTree.AvlTraverseDepthFirst(inRoot, CalculateTotalFilesAndBytes, null,
                AvlTraversalMethod.Prefix, 0);
        }
        else
        {
            var n = 0;
            var filesSkipped = 0;
            Logger.Log("generating avl tree from filesystem: ");
            Logger.Flush();

            // The -s flag (skip $SystemUpdate) becomes an implicit exclude pattern.
            var effectivePatterns = excludePatterns;
            if (Logger.RemoveSystemUpdate)
            {
                var patterns = new List<string> { "**/$SystemUpdate/**" };
                if (excludePatterns != null)
                {
                    patterns.AddRange(excludePatterns);
                }

                effectivePatterns = patterns;
            }

            err = GenerateAvlTreeLocal(ref root.Subdirectory, ref n, ref filesSkipped, effectivePatterns);

            for (var i = 0; i < n; i++) Logger.Log("\b");
            for (var i = 0; i < n; i++) Logger.Log(" ");
            for (var i = 0; i < n; i++) Logger.Log("\b");

            Logger.Log($"{(err != 0 ? "failed!" : "[OK]")}\n\n");

            if (filesSkipped > 0)
            {
                Logger.LogErr($"warning: {filesSkipped} file(s)/director(y/ies) skipped due to access errors.\n");
            }
        }

        if (err != 0) goto cleanup;

        cancellationToken.ThrowIfCancellationRequested();

        if (progress != null)
        {
            (var fileCount, var dirCount) = CountTreeEntries(root.Subdirectory);
            progress.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: fileCount));
            progress.Report(new ProgressInfo(ProgressInfoType.DirCount, Count: dirCount));
        }

        progressCallback?.Invoke(0, Logger.TotalBytes);
        var finalTotal = Logger.TotalBytes;
        Logger.TotalBytes = Logger.TotalFiles = 0;

        var startSector = root.StartSector;

        AvlTree.AvlTraverseDepthFirst(root, CalculateDirectoryRequirements, null,
            AvlTraversalMethod.Prefix, 0);

        var offsetCtx = new OffsetCalcContext { CurrentSector = startSector, PrependOffset = prependOffset };
        AvlTree.AvlTraverseDepthFirst(root, static (n, c, _) =>
        {
            CalculateDirectoryOffsets(n, (OffsetCalcContext)c!);
            return 0;
        }, offsetCtx, AvlTraversalMethod.Prefix, 0);

        var bufSize = Math.Max(Constants.ReadWriteBufferSize, Constants.HeaderOffset);
        var buf = new byte[bufSize];

        try
        {
            using var xisoFs = new FileStream(xisoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 65536
                });

            outIsoPath = xisoPath;

            if (prependOffset > 0)
            {
                // Extend the file with zero-filled space for the video partition / header area.
                xisoFs.SetLength(prependOffset);
                xisoFs.Seek(prependOffset, SeekOrigin.Begin);
            }

            Array.Clear(buf, 0, Constants.HeaderOffset);
            xisoFs.Write(buf, 0, Constants.HeaderOffset);

            var magicBytes = Encoding.ASCII.GetBytes(Constants.HeaderData);
            xisoFs.Write(magicBytes, 0, Constants.HeaderDataLength);

            Span<byte> leBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(leBuf, root.StartSector);
            xisoFs.Write(leBuf);

            BinaryPrimitives.WriteUInt32LittleEndian(leBuf, root.FileSize);
            xisoFs.Write(leBuf);

            if (inRoot != null && sourceStream != null)
            {
                sourceStream.Seek(Constants.HeaderOffset + Constants.HeaderDataLength +
                                  Constants.SectorOffsetSize + Constants.DirTableSize + Logger.XboxDiscLseek,
                    SeekOrigin.Begin);
                Span<byte> ftBuf = stackalloc byte[8];
                sourceStream.ReadExactly(ftBuf);
                xisoFs.Write(ftBuf);
                ftBuf.Clear();
            }
            else
            {
                Span<byte> ftBuf = stackalloc byte[8];
                FileTimeHelper.WriteFileTimeNow(ftBuf);
                xisoFs.Write(ftBuf);
            }

            Span<byte> unused = stackalloc byte[Constants.UnusedSize];
            unused.Clear();
            xisoFs.Write(unused);

            xisoFs.Write(magicBytes, 0, Constants.HeaderDataLength);

            if (inRoot == null)
            {
                Directory.SetCurrentDirectory("..");
            }

            root.Filename = isoDir;

            xisoFs.Seek(prependOffset + ((long)root.StartSector * Constants.SectorSize), SeekOrigin.Begin);

            var wtContext = new WriteTreeContext
            {
                XisoStream = xisoFs,
                Path = null,
                SourceStream = sourceStream,
                ProgressCallback = progressCallback,
                StructuredProgress = progress,
                FinalBytes = finalTotal,
                CancellationToken = cancellationToken,
                PrependOffset = prependOffset
            };

            AvlTree.AvlTraverseDepthFirst(root, WriteTreeCallback, wtContext,
                AvlTraversalMethod.Prefix, 0);

            var pos = xisoFs.Seek(0, SeekOrigin.End);
            var pad = ((Constants.FileModulus - (pos % Constants.FileModulus)) % Constants.FileModulus);
            if (pad > 0)
            {
                Array.Clear(buf, 0, (int)pad);
                xisoFs.Write(buf, 0, (int)pad);
            }

            var totalSectors = (pos + pad) / Constants.SectorSize;
            if (totalSectors > uint.MaxValue)
            {
                throw new XisoFileTooLargeException(isoName, pos + pad);
            }

            WriteVolumeDescriptors(xisoFs, (uint)totalSectors, prependOffset);

            xisoFs.Seek(prependOffset + Constants.OptimizedTagOffset, SeekOrigin.Begin);
            var tagBytes = Encoding.ASCII.GetBytes(Constants.OptimizedTag);
            xisoFs.Write(tagBytes, 0, Constants.OptimizedTagLength);

            if (inRoot == null)
            {
                Logger.Log(
                    $"\nsucessfully created {isoName}{(inName != null ? "" : ".iso")} ({Logger.TotalFiles} files totalling {Logger.TotalBytes} bytes added)\n");
            }

            progress?.Report(new ProgressInfo(ProgressInfoType.FinishedPacking));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogErr($"Error: permission denied: {xisoPath}\n{ex.Message}\n");
            err = 1;
        }
        catch (IOException ex)
        {
            Logger.LogErr($"Error: cannot write to {xisoPath}: {ex.Message}\n");
            err = 1;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"{ex.Message}\n");
            err = 1;
        }

        cleanup:
        if (root.Subdirectory != null && !ReferenceEquals(root.Subdirectory, AvlNode.EmptySubdirectory))
        {
            AvlTree.FreeTree(root.Subdirectory);
        }

        Directory.SetCurrentDirectory(cwd);

        return err;
    }

    /// <summary>
    /// Callback for writing directory table entries during the tree-write phase.
    /// Handles subdirectory recursion, file data writing, and directory record emission.
    /// </summary>
    private static int WriteTreeCallback(AvlNode avl, object? context, int depth)
    {
        var ctx = (WriteTreeContext)context!;

        if (avl.Subdirectory != null)
        {
            var subCtx = new WriteTreeContext
            {
                XisoStream = ctx.XisoStream,
                SourceStream = ctx.SourceStream,
                ProgressCallback = ctx.ProgressCallback,
                StructuredProgress = ctx.StructuredProgress,
                FinalBytes = ctx.FinalBytes,
                Path = ctx.Path != null
                    ? ctx.Path + avl.Filename + Constants.PathCharStr
                    : Constants.PathCharStr,
                PrependOffset = ctx.PrependOffset
            };

            ctx.StructuredProgress?.Report(new ProgressInfo(
                ProgressInfoType.DirAdded,
                Path: ToInternalPath(subCtx.Path),
                Sector: avl.StartSector));

            Logger.Log($"adding {subCtx.Path} (0 bytes) [OK]\n");

            if (!ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
            {
                if (ctx.SourceStream == null && !ctx.IsRemap)
                {
                    Directory.SetCurrentDirectory(avl.Filename);
                }

                // Propagate remap flag to child context
                subCtx.IsRemap = ctx.IsRemap;

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, WriteFileCallback, subCtx,
                    AvlTraversalMethod.Prefix, 0);

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, WriteTreeCallback, subCtx,
                    AvlTraversalMethod.Prefix, 0);

                var xisoFs = (FileStream)ctx.XisoStream;
                xisoFs.Seek(ctx.PrependOffset + ((long)avl.StartSector * Constants.SectorSize), SeekOrigin.Begin);
                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, WriteDirectoryCallback, xisoFs,
                    AvlTraversalMethod.Prefix, 0);

                var pos = xisoFs.Seek(0, SeekOrigin.Current);
                var pad = (Constants.SectorSize - (pos % Constants.SectorSize)) % Constants.SectorSize;
                if (pad > 0)
                {
                    Span<byte> padBuf = stackalloc byte[(int)pad];
                    padBuf.Fill(Constants.PadByte);
                    xisoFs.Write(padBuf);
                }

                if (ctx.SourceStream == null && !ctx.IsRemap)
                {
                    Directory.SetCurrentDirectory("..");
                }
            }
            else
            {
                var xisoFs = (FileStream)ctx.XisoStream;
                xisoFs.Seek(ctx.PrependOffset + ((long)avl.StartSector * Constants.SectorSize), SeekOrigin.Begin);
                Span<byte> emptySector = stackalloc byte[Constants.SectorSize];
                emptySector.Fill(Constants.PadByte);
                xisoFs.Write(emptySector);
            }
        }

        return 0;
    }

    /// <summary>
    /// Callback invoked during tree traversal to write file data for leaf nodes.
    /// </summary>
    private static int WriteFileCallback(AvlNode avl, object? context, int depth)
    {
        var ctx = (WriteTreeContext)context!;
        if (avl.Subdirectory == null)
            WriteFileData(avl, ctx);
        return 0;
    }

    /// <summary>
    /// Callback invoked during tree traversal to write the on-disk directory entry
    /// for a single node (file or subdirectory).
    /// </summary>
    private static int WriteDirectoryCallback(AvlNode avl, object? context, int depth)
    {
        var fs = (FileStream)context!;

        var fileSizeForEntry = avl.FileSize;
        if (avl.Subdirectory != null)
        {
            fileSizeForEntry += (Constants.SectorSize - (avl.FileSize % Constants.SectorSize)) % Constants.SectorSize;
        }

        if (avl.Filename.Contains('/') || avl.Filename.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"Filename '{avl.Filename}' contains path separator characters ('/' or '\\') which are not allowed in XISO directory entries.");
        }

        var attributes = avl.Subdirectory != null ? Constants.AttributeDir : Constants.AttributeArc;
        var length = (byte)avl.Filename.Length;

        var lOffset = (ushort)(avl.Left != null ? avl.Left.Offset / Constants.DwordSize : 0);
        var rOffset = (ushort)(avl.Right != null ? avl.Right.Offset / Constants.DwordSize : 0);

        Span<byte> leBuf = stackalloc byte[4];

        var pos = fs.Seek(0, SeekOrigin.Current);
        var targetPos = avl.Offset + avl.DirStart;
        var pad = targetPos - pos;
        if (pad > 0)
        {
            Span<byte> padBuf = stackalloc byte[(int)pad];
            padBuf.Fill(Constants.PadByte);
            fs.Write(padBuf);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(leBuf[..2], lOffset);
        fs.Write(leBuf[..2]);
        BinaryPrimitives.WriteUInt16LittleEndian(leBuf[..2], rOffset);
        fs.Write(leBuf[..2]);
        BinaryPrimitives.WriteUInt32LittleEndian(leBuf, avl.StartSector);
        fs.Write(leBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(leBuf, fileSizeForEntry);
        fs.Write(leBuf);
        fs.WriteByte(attributes);
        fs.WriteByte(length);

        var nameBytes = Latin1Encoding.Instance.GetBytes(avl.Filename);
        fs.Write(nameBytes, 0, length);

        return 0;
    }

    /// <summary>
    /// Reads file data from the source stream or local file, optionally performing
    /// media-enable patching on <c>.xbe</c> files, and writes it to the output XISO stream.
    /// </summary>
    private static void WriteFileData(AvlNode avl, WriteTreeContext ctx)
    {
        var xisoFs = (FileStream)ctx.XisoStream;
        xisoFs.Seek(ctx.PrependOffset + ((long)avl.StartSector * Constants.SectorSize), SeekOrigin.Begin);

        var bufSize = Math.Max(Constants.SectorSize, Constants.ReadWriteBufferSize) + 1;
        var buf = new byte[bufSize + 1];

        Stream srcStream;
        if (ctx.SourceStream == null)
        {
            var hostPath = avl.HostPath ?? avl.Filename;
            // For remap, HostPath is absolute; for normal, it's bare filename with CWD already set.
            srcStream = new FileStream(hostPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 65536
                });
        }
        else
        {
            srcStream = ctx.SourceStream;
            srcStream.Seek(((long)avl.OldStartSector * Constants.SectorSize) + Logger.XboxDiscLseek,
                SeekOrigin.Begin);
        }

        using (ctx.SourceStream == null ? srcStream : null)
        {
            Logger.Log($"adding {ctx.Path}{avl.Filename} ({avl.FileSize} bytes) ");
            Logger.Flush();

            var written = 0;
            var bytes = avl.FileSize;

            while (bytes > 0)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(bytes, (uint)(bufSize - written));
                var n = srcStream.Read(buf, written, toRead);
                if (n <= 0) break;

                bytes -= (uint)n;

                if (Logger.MediaEnable &&
                    avl.Filename.Length >= 4 &&
                    string.Equals(avl.Filename[^4..], ".xbe", StringComparison.OrdinalIgnoreCase))
                {
                    _bm ??= new BoyerMoore(Constants.MediaEnable);
                    _bm.Init();

                    buf[written + n] = 0;
                    var searchEnd = written + n;
                    var searchPos = 0;
                    while (searchPos < searchEnd)
                    {
                        var found = _bm.Search(buf, searchPos, searchEnd - searchPos);
                        if (found < 0) break;

                        buf[found + Constants.MediaEnableBytePos] = Constants.MediaEnableByte;
                        searchPos = found + Constants.MediaEnableLength;
                    }

                    if (bytes > 0)
                    {
                        const int overlap = Constants.MediaEnableLength - 1;
                        xisoFs.Write(buf, 0, written + n - overlap);
                        Array.Copy(buf, written + n - overlap, buf, 0, overlap);
                        written = overlap;
                    }
                    else
                    {
                        xisoFs.Write(buf, 0, written + n);
                        written = 0;
                    }
                }
                else
                {
                    xisoFs.Write(buf, 0, written + n);
                    written = 0;
                }
            }

            var originalSize = avl.FileSize;
            avl.FileSize -= bytes;

            var padding = (Constants.SectorSize - (avl.FileSize % Constants.SectorSize)) % Constants.SectorSize;
            if (padding > 0)
            {
                Span<byte> padBuf = stackalloc byte[(int)padding];
                padBuf.Fill(Constants.PadByte);
                xisoFs.Write(padBuf);
            }

            Logger.Log("[OK]\n");

            if (originalSize != avl.FileSize)
            {
                Logger.LogErr(
                    $"WARNING: File {avl.Filename} is truncated. Reported size: {originalSize} bytes, wrote size: {avl.FileSize} bytes!\n");
            }

            Logger.TotalFiles++;
            Logger.TotalBytes += avl.FileSize;
            ctx.ProgressCallback?.Invoke(Logger.TotalBytes, ctx.FinalBytes);
            ctx.StructuredProgress?.Report(new ProgressInfo(
                ProgressInfoType.FileAdded,
                Path: ToInternalPath(ctx.Path + avl.Filename),
                Sector: avl.StartSector,
                Size: avl.FileSize));
        }
    }

    /// <summary>
    /// Recursively descends into the current working directory, building an AVL tree
    /// of all files and subdirectories found. Updates the progress display as it goes.
    /// Inaccessible entries are skipped with a warning rather than aborting.
    /// Entries matching an exclude pattern are skipped silently.
    /// </summary>
    /// <param name="outRoot">Receives the root of the generated AVL tree.</param>
    /// <param name="ioN">Running count of filename characters for progress display.</param>
    /// <param name="filesSkipped">Running count of entries skipped due to access errors.</param>
    /// <param name="excludePatterns">
    /// Optional glob patterns of files and directories to omit. Paths are matched relative
    /// to the source root with <c>/</c> separators; matching directories are not recursed into.
    /// </param>
    /// <param name="relativePath">Relative path of the current directory within the source root.</param>
    /// <returns>0 on success.</returns>
    internal static int GenerateAvlTreeLocal(
        ref AvlNode? outRoot,
        ref int ioN,
        ref int filesSkipped,
        IReadOnlyList<string>? excludePatterns = null,
        string relativePath = "")
    {
        GlobMatcher? matcher = excludePatterns is { Count: > 0 } ? new GlobMatcher(excludePatterns) : null;
        return GenerateAvlTreeLocalCore(ref outRoot, ref ioN, ref filesSkipped, matcher, relativePath);
    }

    private static int GenerateAvlTreeLocalCore(
        ref AvlNode? outRoot,
        ref int ioN,
        ref int filesSkipped,
        GlobMatcher? matcher,
        string relativePath)
    {
        var entries = Directory.GetFileSystemEntries(".");
        var emptyDir = true;

        foreach (var entryPath in entries)
        {
            var entryName = Path.GetFileName(entryPath);

            if (entryName is "." or "..")
                continue;

            var entryRelPath = relativePath.Length == 0 ? entryName : relativePath + "/" + entryName;

            if (matcher?.IsMatch(entryRelPath) == true)
            {
                continue;
            }

            try
            {
                for (var i = ioN; i > 0; i--) Logger.Log("\b");
                Logger.Log(entryName);
                var nameLen = entryName.Length;
                for (var j = nameLen; j < ioN; j++) Logger.Log(" ");
                for (var j = nameLen; j < ioN; j++) Logger.Log("\b");
                ioN = nameLen;
                Logger.Flush();

                var attr = File.GetAttributes(entryPath);
                var avl = new AvlNode { Filename = entryName };

                if ((attr & FileAttributes.Directory) != FileAttributes.None)
                {
                    emptyDir = false;
                    var prevDir = Directory.GetCurrentDirectory();
                    Directory.SetCurrentDirectory(entryName);

                    GenerateAvlTreeLocalCore(ref avl.Subdirectory, ref ioN, ref filesSkipped, matcher, entryRelPath);

                    Directory.SetCurrentDirectory(prevDir);
                }
                else
                {
                    emptyDir = false;
                    var fi = new FileInfo(entryPath);
                    if (fi.Length > uint.MaxValue)
                    {
                        throw new XisoFileTooLargeException(entryName, fi.Length);
                    }

                    avl.FileSize = (uint)fi.Length;
                    Logger.TotalBytes += avl.FileSize;
                    Logger.TotalFiles++;
                }

                AvlTree.AvlInsert(ref outRoot, avl);
            }
            catch (UnauthorizedAccessException)
            {
                Logger.LogErr($"warning: permission denied: {entryName}, skipping.\n");
                filesSkipped++;
            }
            catch (PathTooLongException)
            {
                Logger.LogErr($"warning: path too long: {entryName}, skipping.\n");
                filesSkipped++;
            }
            catch (DirectoryNotFoundException)
            {
                Logger.LogErr($"warning: directory not found: {entryName}, skipping.\n");
                filesSkipped++;
            }
            catch (IOException ex)
            {
                Logger.LogErr($"warning: I/O error on {entryName}: {ex.Message}, skipping.\n");
                filesSkipped++;
            }
        }

        if (emptyDir)
        {
            outRoot = AvlNode.EmptySubdirectory;
        }

        return 0;
    }

    /// <summary>
    /// Packs a local directory into an XISO image with a 1:1 mapping of all entries.
    /// Convenience wrapper around <see cref="CreateXiso"/> that takes a single output
    /// ISO path instead of separate directory/name arguments.
    /// </summary>
    /// <param name="sourceDirectory">Source directory whose contents are packed.</param>
    /// <param name="outputIsoPath">Full path of the ISO to create (may include a directory).</param>
    /// <param name="excludePatterns">
    /// Optional glob patterns of files/directories to omit (see <see cref="GlobMatcher"/>).
    /// </param>
    /// <param name="progressCallback">Optional byte-progress callback.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">
    /// Optional structured progress channel (<see cref="ProgressInfo"/> events).
    /// </param>
    /// <returns>0 on success, 1 on error.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="outputIsoPath"/> is null or empty.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the source directory does not exist.</exception>
    /// <exception cref="XisoFileTooLargeException">Thrown when a source file exceeds ~4 GB.</exception>
    public static int PackFromDirectory(
        string sourceDirectory,
        string outputIsoPath,
        IReadOnlyList<string>? excludePatterns = null,
        ProgressCallback? progressCallback = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputIsoPath);

        var fullOutput = Path.GetFullPath(outputIsoPath);
        var fullSource = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        ValidateOutputNotColliding(fullSource, fullOutput);

        var outputDirectory = Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory();
        var inName = Path.GetFileName(fullOutput);

        Directory.CreateDirectory(outputDirectory);

        return CreateXiso(sourceDirectory, outputDirectory, null, null, out _, inName,
            progressCallback, cancellationToken, excludePatterns: excludePatterns, progress: progress);
    }

    /// <summary>
    /// Asynchronously packs a local directory into an XISO image with a 1:1 mapping.
    /// </summary>
    /// <param name="sourceDirectory">Source directory whose contents are packed.</param>
    /// <param name="outputIsoPath">Full path of the ISO to create (may include a directory).</param>
    /// <param name="excludePatterns">Optional glob patterns of files/directories to omit.</param>
    /// <param name="progressCallback">Optional byte-progress callback.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">Optional structured progress channel.</param>
    /// <returns>A task that completes with 0 on success, 1 on error.</returns>
    public static async Task<int> PackFromDirectoryAsync(
        string sourceDirectory,
        string outputIsoPath,
        IReadOnlyList<string>? excludePatterns = null,
        ProgressCallback? progressCallback = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        return await Task.Run(() => PackFromDirectory(
                sourceDirectory, outputIsoPath, excludePatterns, progressCallback, cancellationToken, progress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an XISO from a pre-built remap AVL tree (used by <c>build-image</c>).
    /// The tree is expected to have <see cref="AvlNode.HostPath"/> set for file nodes.
    /// </summary>
    internal static int CreateFromRemapTree(AvlNode? remapRoot, string outputIsoPath, string? volumeName = null,
        IProgress<ProgressInfo>? progress = null, ProgressCallback? progressCallback = null,
        CancellationToken cancellationToken = default, int? prependSectors = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputIsoPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullOutput = Path.GetFullPath(outputIsoPath);
        var outputDir = Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory();
        var outFileName = Path.GetFileName(fullOutput);
        Directory.CreateDirectory(outputDir);
        var xisoPath = Path.Combine(outputDir, outFileName);

        var isoName = volumeName ?? Path.GetFileNameWithoutExtension(outFileName);
        if (string.IsNullOrEmpty(isoName)) isoName = "IMAGE";
        var isoDir = isoName;

        var xisoSettingsName = isoName;
        // Build synthetic root
        var root = new AvlNode
        {
            Filename = isoDir,
            StartSector = Constants.RootDirectorySector,
            Subdirectory = remapRoot ?? AvlNode.EmptySubdirectory
        };

        // Compute totals from remap tree for progress
        long totalBytes = 0;
        var totalFiles = 0;

        SumFiles(root.Subdirectory);
        Logger.TotalFiles = totalFiles;
        Logger.TotalBytes = (uint)Math.Min(totalBytes, uint.MaxValue);

        if (progress != null)
        {
            (var fc, var dc) = CountTreeEntries(root.Subdirectory);
            progress.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: fc));
            progress.Report(new ProgressInfo(ProgressInfoType.DirCount, Count: dc));
        }

        progressCallback?.Invoke(0, totalBytes);
        var finalTotal = totalBytes;
        Logger.TotalBytes = Logger.TotalFiles = 0;

        var prependOffset = (long)(prependSectors ?? 0) * Constants.SectorSize;
        if (prependOffset < 0) throw new ArgumentOutOfRangeException(nameof(prependSectors));

        var err = 0;
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            // Directory layout
            AvlTree.AvlTraverseDepthFirst(root, CalculateDirectoryRequirements, null, AvlTraversalMethod.Prefix, 0);
            var offsetCtx = new OffsetCalcContext { CurrentSector = root.StartSector, PrependOffset = prependOffset };
            AvlTree.AvlTraverseDepthFirst(root, static (n, c, _) =>
            {
                CalculateDirectoryOffsets(n, (OffsetCalcContext)c!);
                return 0;
            }, offsetCtx, AvlTraversalMethod.Prefix, 0);

            var bufSize = Math.Max(Constants.ReadWriteBufferSize, Constants.HeaderOffset);
            var buf = new byte[bufSize];

            using var xisoFs = new FileStream(xisoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 65536
                });
            if (prependOffset > 0)
            {
                xisoFs.SetLength(prependOffset);
                xisoFs.Seek(prependOffset, SeekOrigin.Begin);
            }

            Array.Clear(buf, 0, Constants.HeaderOffset);
            xisoFs.Write(buf, 0, Constants.HeaderOffset);
            var magicBytes = Encoding.ASCII.GetBytes(Constants.HeaderData);
            xisoFs.Write(magicBytes, 0, Constants.HeaderDataLength);
            Span<byte> leBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(leBuf, root.StartSector);
            xisoFs.Write(leBuf);
            BinaryPrimitives.WriteUInt32LittleEndian(leBuf, root.FileSize);
            xisoFs.Write(leBuf);
            Span<byte> ftBuf = stackalloc byte[8];
            FileTimeHelper.WriteFileTimeNow(ftBuf);
            xisoFs.Write(ftBuf);
            Span<byte> unused = stackalloc byte[Constants.UnusedSize];
            unused.Clear();
            xisoFs.Write(unused);
            xisoFs.Write(magicBytes, 0, Constants.HeaderDataLength);

            xisoFs.Seek(prependOffset + ((long)root.StartSector * Constants.SectorSize), SeekOrigin.Begin);

            var wtContext = new WriteTreeContext
            {
                XisoStream = xisoFs,
                Path = null,
                SourceStream = null,
                ProgressCallback = progressCallback,
                StructuredProgress = progress,
                FinalBytes = finalTotal,
                CancellationToken = cancellationToken,
                PrependOffset = prependOffset,
                IsRemap = true
            };

            AvlTree.AvlTraverseDepthFirst(root, WriteTreeCallback, wtContext, AvlTraversalMethod.Prefix, 0);

            var pos = xisoFs.Seek(0, SeekOrigin.End);
            var pad = ((Constants.FileModulus - (pos % Constants.FileModulus)) % Constants.FileModulus);
            if (pad > 0)
            {
                Array.Clear(buf, 0, (int)pad);
                xisoFs.Write(buf, 0, (int)pad);
            }

            var totalSectors = (pos + pad) / Constants.SectorSize;
            if (totalSectors > uint.MaxValue) throw new XisoFileTooLargeException(xisoSettingsName, pos + pad);
            WriteVolumeDescriptors(xisoFs, (uint)totalSectors, prependOffset);
            xisoFs.Seek(prependOffset + Constants.OptimizedTagOffset, SeekOrigin.Begin);
            var tagBytes = Encoding.ASCII.GetBytes(Constants.OptimizedTag);
            xisoFs.Write(tagBytes, 0, Constants.OptimizedTagLength);
            Logger.Log($"\nsucessfully created {xisoSettingsName} ({totalFiles} files)\n");
            progress?.Report(new ProgressInfo(ProgressInfoType.FinishedPacking));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"{ex.Message}\n");
            err = 1;
        }
        finally
        {
            if (root.Subdirectory != null && !ReferenceEquals(root.Subdirectory, AvlNode.EmptySubdirectory))
                AvlTree.FreeTree(root.Subdirectory);
            try
            {
                Directory.SetCurrentDirectory(cwd);
            }
            catch
            {
                // ignored
            }
        }

        return err;

        void SumFiles(AvlNode? n)
        {
            while (true)
            {
                if (n == null || ReferenceEquals(n, AvlNode.EmptySubdirectory)) return;
                if (n.Subdirectory == null)
                {
                    totalFiles++;
                    totalBytes += n.FileSize;
                }
                else if (!ReferenceEquals(n.Subdirectory, AvlNode.EmptySubdirectory))
                {
                    SumFiles(n.Subdirectory);
                }

                SumFiles(n.Left);
                n = n.Right;
            }
        }
    }

    /// <summary>
    /// Recursively counts the file and directory nodes in a generated AVL tree.
    /// The root node itself is not counted; the empty-directory sentinel contributes nothing
    /// (it represents the absence of entries, not an entry).
    /// </summary>
    /// <param name="root">Root of the tree to count (may be <c>null</c>).</param>
    /// <returns>The number of file nodes and directory nodes.</returns>
    private static (int Files, int Dirs) CountTreeEntries(AvlNode? root)
    {
        var files = 0;
        var dirs = 0;
        CountCore(root);
        return (files, dirs);

        void CountCore(AvlNode? node)
        {
            while (true)
            {
                // The sentinel represents "no entries" (e.g. an empty source directory);
                // it is not itself an entry.
                if (node == null || ReferenceEquals(node, AvlNode.EmptySubdirectory)) return;

                if (node.Subdirectory != null)
                {
                    dirs++;

                    if (!ReferenceEquals(node.Subdirectory, AvlNode.EmptySubdirectory))
                    {
                        CountCore(node.Subdirectory);
                    }
                }
                else
                {
                    files++;
                }

                CountCore(node.Left);
                node = node.Right;
            }
        }
    }

    /// <summary>
    /// Converts a platform-separator path (e.g. <c>"\\subdir\\"</c>) into the internal
    /// forward-slash form used by progress events (e.g. <c>"/subdir"</c>; the root is <c>"/"</c>).
    /// </summary>
    private static string ToInternalPath(string? path)
    {
        var p = (path ?? "").Replace(Constants.PathChar, '/');
        if (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p.Length == 0 ? "/" : p;
    }

    /// <summary>
    /// Traversal callback that accumulates total file count and byte count
    /// from an AVL tree. Used to compute the final byte count before writing.
    /// </summary>
    /// <param name="avl">Current node being visited.</param>
    /// <param name="context">Not used.</param>
    /// <param name="depth">Not used.</param>
    /// <returns>Always 0.</returns>
    internal static int CalculateTotalFilesAndBytes(AvlNode avl, object? context, int depth)
    {
        if (avl.Subdirectory != null && !ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
        {
            AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, CalculateTotalFilesAndBytes, null,
                AvlTraversalMethod.Prefix, 0);
        }
        else if (avl.Subdirectory == null)
        {
            Logger.TotalFiles++;
            Logger.TotalBytes += avl.FileSize;
        }

        return 0;
    }

    /// <summary>
    /// Traversal callback that calculates the directory table size requirements
    /// for each subdirectory node, recursively computing <see cref="AvlNode.FileSize"/>
    /// for directory nodes.
    /// </summary>
    /// <param name="avl">Current node being visited.</param>
    /// <param name="context">Not used.</param>
    /// <param name="depth">Not used.</param>
    /// <returns>Always 0.</returns>
    internal static int CalculateDirectoryRequirements(AvlNode avl, object? context, int depth)
    {
        if (avl.Subdirectory != null)
        {
            if (!ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
            {
                avl.FileSize = 0;
                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, (n, _, _) =>
                {
                    CalculateDirectorySize(n, ref avl.FileSize);
                    return 0;
                }, null, AvlTraversalMethod.Prefix, 0);

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, CalculateDirectoryRequirements, null,
                    AvlTraversalMethod.Prefix, 0);
            }
            else
            {
                avl.FileSize = Constants.SectorSize;
            }
        }

        return 0;
    }

    /// <summary>
    /// Computes the on-disk size of an individual directory entry (file or subdirectory)
    /// and assigns its <see cref="AvlNode.Offset"/> within the directory table.
    /// </summary>
    /// <param name="avl">Node whose entry size is being calculated.</param>
    /// <param name="outSize">Running total size of the directory table; updated in place.</param>
    internal static void CalculateDirectorySize(AvlNode avl, ref uint outSize)
    {
        var length = (uint)(Constants.FilenameOffset + avl.Filename.Length);
        length += (Constants.DwordSize - (length % Constants.DwordSize)) % Constants.DwordSize;

        if (NumSectors(outSize + length) > NumSectors(outSize))
        {
            outSize += (Constants.SectorSize - (outSize % Constants.SectorSize)) % Constants.SectorSize;
        }

        avl.Offset = outSize;
        outSize += length;
    }

    /// <summary>Local helper for sector count calculation (ceiling division).</summary>
    private static uint NumSectors(uint size)
    {
        return (size / Constants.SectorSize) + (size % Constants.SectorSize != 0 ? 1u : 0u);
    }

    /// <summary>
    /// Traversal callback that assigns sector positions to directory entries
    /// and delegates file position assignment to <see cref="WriteDirStartAndFilePositions"/>.
    /// </summary>
    /// <param name="avl">Current node being visited.</param>
    /// <param name="ctx">Context tracking the current sector counter.</param>
    internal static void CalculateDirectoryOffsets(AvlNode avl, OffsetCalcContext ctx)
    {
        if (avl.Subdirectory != null)
        {
            if (ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
            {
                avl.StartSector = ctx.CurrentSector;
                ctx.CurrentSector++;
            }
            else
            {
                avl.StartSector = ctx.CurrentSector;
                var dirStart = ctx.PrependOffset + ((long)avl.StartSector * Constants.SectorSize);
                ctx.CurrentSector += NumSectors(avl.FileSize);

                var wdsafp = new WdsafpContext { CurrentSector = ctx.CurrentSector, DirStart = dirStart };

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, static (n, c, _) =>
                {
                    WriteDirStartAndFilePositions(n, (WdsafpContext)c!);
                    return 0;
                }, wdsafp, AvlTraversalMethod.Prefix, 0);

                ctx.CurrentSector = wdsafp.CurrentSector;

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, static (n, c, _) =>
                {
                    CalculateDirectoryOffsets(n, (OffsetCalcContext)c!);
                    return 0;
                }, ctx, AvlTraversalMethod.Prefix, 0);
            }
        }
    }

    /// <summary>
    /// Assigns the directory start offset and starting sector to each file node
    /// in preparation for writing.
    /// </summary>
    /// <param name="avl">Current node.</param>
    /// <param name="ctx">Context carrying the directory start and current sector.</param>
    internal static void WriteDirStartAndFilePositions(AvlNode avl, WdsafpContext ctx)
    {
        avl.DirStart = ctx.DirStart;

        if (avl.Subdirectory == null)
        {
            avl.StartSector = ctx.CurrentSector;
            ctx.CurrentSector += NumSectors(avl.FileSize);
        }
    }

    /// <summary>
    /// Writes the ECMA-119 primary volume descriptor set at the end of the XISO image.
    /// This includes the data area identifier, volume space size, volume set size,
    /// volume set identifier, and creation date fields.
    /// </summary>
    /// <param name="fs">File stream positioned at the data area start offset.</param>
    /// <param name="totalSectors">Total number of sectors in the image.</param>
    /// <param name="prependOffset">Byte offset prepended to all physical positions (skip/prepend support).</param>
    internal static void WriteVolumeDescriptors(FileStream fs, uint totalSectors, long prependOffset = 0)
    {
        fs.Seek(prependOffset + Constants.Ecma119DataAreaStart, SeekOrigin.Begin);
        fs.WriteByte(0x01);
        fs.Write("CD001"u8);
        fs.WriteByte(0x01);

        fs.Seek(prependOffset + Constants.Ecma119VolumeSpaceSize, SeekOrigin.Begin);
        Span<byte> leBuf = stackalloc byte[4];
        Span<byte> beBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(leBuf, totalSectors);
        fs.Write(leBuf);
        BinaryPrimitives.WriteUInt32BigEndian(beBuf, totalSectors);
        fs.Write(beBuf);

        fs.Seek(prependOffset + Constants.Ecma119VolumeSetSize, SeekOrigin.Begin);
        byte[] volumeSetSize = [0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x08, 0x08, 0x00];
        fs.Write(volumeSetSize, 0, 12);

        fs.Seek(prependOffset + Constants.Ecma119VolumeSetIdentifier, SeekOrigin.Begin);
        const int spacesSize = Constants.Ecma119VolumeCreationDate - Constants.Ecma119VolumeSetIdentifier;
        var spaces = new byte[spacesSize];
        Array.Fill(spaces, (byte)0x20);
        fs.Write(spaces, 0, spacesSize);

        var date = new byte[17];
        Array.Fill(date, (byte)'0');
        date[16] = 0;
        fs.Write(date, 0, 17);
        fs.Write(date, 0, 17);
        fs.Write(date, 0, 17);
        fs.Write(date, 0, 17);
        fs.WriteByte(0x01);

        fs.Seek(prependOffset + Constants.Ecma119DataAreaStart + Constants.SectorSize, SeekOrigin.Begin);
        fs.WriteByte(0xFF);
        fs.Write("CD001"u8);
        fs.WriteByte(0x01);
    }

    /// <summary>
    /// Asynchronously creates or rewrites an XISO image.
    /// When <paramref name="inRoot"/> is <c>null</c>,
    /// builds an AVL tree from the local file system and creates a new ISO.
    /// Otherwise, rewrites the ISO using the pre-built AVL tree and source stream.
    /// </summary>
    /// <param name="rootDirectory">Source directory for creation, or base name for rewrite mode.</param>
    /// <param name="outputDirectory">Directory where the output ISO file is written. When <c>null</c>, the current working directory is used.</param>
    /// <param name="inRoot">Pre-built AVL tree root. When <c>null</c>, the tree is generated from the file system.</param>
    /// <param name="sourceStream">Source ISO stream for reading file data in rewrite mode; <c>null</c> when creating from a file system.</param>
    /// <param name="inName">Explicit output filename. When <c>null</c>, the directory name plus <c>.iso</c> is used.</param>
    /// <param name="progressCallback">Optional callback invoked with (<c>currentBytes</c>, <c>totalBytes</c>) during write.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="prependSectors">
    /// Optional number of 2048-byte sectors to prepend to the output image before the
    /// XISO filesystem begins, leaving room for a video partition. Sector numbers stored
    /// in directory entries remain partition-relative; only physical file positions shift.
    /// </param>
    /// <param name="excludePatterns">
    /// Optional glob patterns of files and directories to omit from the image when creating
    /// from a file system (see <see cref="GlobMatcher"/> for the supported syntax).
    /// Ignored in rewrite mode.
    /// </param>
    /// <param name="progress">
    /// Optional structured progress channel; receives <see cref="ProgressInfo"/> events
    /// (counts, per-entry additions, completion) — see <see cref="CreateXiso"/>.
    /// </param>
    /// <returns>A task that completes with 0 on success, 1 on error. The first tuple element is the result code; the second is the output ISO path.</returns>
    public static async Task<(int Result, string? OutIsoPath)> CreateXisoAsync(
        string rootDirectory,
        string? outputDirectory,
        AvlNode? inRoot,
        Stream? sourceStream,
        string? inName,
        ProgressCallback? progressCallback,
        CancellationToken cancellationToken = default,
        int? prependSectors = null,
        IReadOnlyList<string>? excludePatterns = null,
        IProgress<ProgressInfo>? progress = null)
    {
        return await Task.Run(() =>
        {
            var result = CreateXiso(rootDirectory, outputDirectory, inRoot, sourceStream,
                out var outPath, inName, progressCallback, cancellationToken, prependSectors, excludePatterns,
                progress);
            return (result, outPath);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the output ISO path does not collide with the source directory.
    /// Covers #55: <c>extract-xiso -c &lt;dir&gt;</c> creating <c>&lt;dir&gt;.iso</c> inside <c>&lt;dir&gt;</c>
    /// with the same leaf name collides on case-insensitive filesystems, and direct equality
    /// (<c>output == source</c>) which is a user error.
    /// </summary>
    /// <param name="fullSource">Full normalized source directory path (trimmed, no trailing separator).</param>
    /// <param name="fullOutput">Full normalized output file path.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the output path equals the source directory or is inside it with the same leaf name.
    /// </exception>
    private static void ValidateOutputNotColliding(string fullSource, string fullOutput)
    {
        var trimmedOutput = fullOutput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Direct equality (e.g. -c src -o src or --pack src src)
        if (string.Equals(fullSource, trimmedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Output ISO path must not be the same as source directory; use -o <file> or --pack <dir> <out.iso>");
        }

        // Inside with same leaf name (e.g. src/src.iso where src leaf is "src")
        if (trimmedOutput.Length > fullSource.Length &&
            trimmedOutput.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase) &&
            (trimmedOutput[fullSource.Length] == Path.DirectorySeparatorChar ||
             trimmedOutput[fullSource.Length] == Path.AltDirectorySeparatorChar))
        {
            var sourceLeaf = Path.GetFileName(fullSource);
            if (!string.IsNullOrEmpty(sourceLeaf))
            {
                var outFileNameNoExt = Path.GetFileNameWithoutExtension(trimmedOutput);
                if (string.Equals(outFileNameNoExt, sourceLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "Output ISO path must not be the same as source directory; use -o <file> or --pack <dir> <out.iso>");
                }
            }
        }
    }
}

// OffsetCalcContext is defined in DataStructures/OffsetCalcContext.cs