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
    /// <returns>0 on success, 1 on error.</returns>
    public static int CreateXiso(
        string rootDirectory,
        string? outputDirectory,
        AvlNode? inRoot,
        Stream? sourceStream,
        out string? outIsoPath,
        string? inName,
        ProgressCallback? progressCallback,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outIsoPath = null;
        var err = 0;

        Logger.TotalBytes = Logger.TotalFiles = 0;

        var cwd = Directory.GetCurrentDirectory();
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
        else if (isoName.Length > 1 && isoName[1] == ':')
        {
            isoName = isoName[1..];
        }

        var xisoPath = Path.Combine(outputDirectory, isoName + (inName != null ? "" : ".iso"));

        Logger.Log($"{(inRoot != null ? "rewriting" : "\ncreating")} {isoName}{(inName != null ? "" : ".iso")}:\n\n");

        var root = new AvlNode
        {
            Filename = isoDir,
            StartSector = Constants.RootDirectorySector
        };

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
            Logger.Log("generating avl tree from filesystem: ");
            Logger.Flush();

            err = GenerateAvlTreeLocal(ref root.Subdirectory, ref n);

            for (var i = 0; i < n; i++) Logger.Log("\b");
            for (var i = 0; i < n; i++) Logger.Log(" ");
            for (var i = 0; i < n; i++) Logger.Log("\b");

            Logger.Log($"{(err != 0 ? "failed!" : "[OK]")}\n\n");
        }

        if (err != 0) goto cleanup;

        cancellationToken.ThrowIfCancellationRequested();
        progressCallback?.Invoke(0, Logger.TotalBytes);

        Logger.TotalBytes = Logger.TotalFiles = 0;

        var startSector = root.StartSector;

        AvlTree.AvlTraverseDepthFirst(root, CalculateDirectoryRequirements, null,
            AvlTraversalMethod.Prefix, 0);

        var offsetCtx = new OffsetCalcContext { CurrentSector = startSector };
        AvlTree.AvlTraverseDepthFirst(root, (n, c, d) =>
        {
            CalculateDirectoryOffsets(n, (OffsetCalcContext)c!, d);
            return 0;
        }, offsetCtx, AvlTraversalMethod.Prefix, 0);

        var bufSize = Math.Max(Constants.ReadWriteBufferSize, Constants.HeaderOffset);
        var buf = new byte[bufSize];

        try
        {
            using var xisoFs = new FileStream(xisoPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 65536
            });

            outIsoPath = xisoPath;

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

            xisoFs.Seek((long)root.StartSector * Constants.SectorSize, SeekOrigin.Begin);

            var wtContext = new WriteTreeContext
            {
                XisoStream = xisoFs,
                Path = null,
                SourceStream = sourceStream,
                Progress = progressCallback,
                FinalBytes = Logger.TotalBytes,
                CancellationToken = cancellationToken
            };

            AvlTree.AvlTraverseDepthFirst(root, WriteTreeCallback, wtContext,
                AvlTraversalMethod.Prefix, 0);

            var pos = xisoFs.Seek(0, SeekOrigin.End);
            var pad = ((Constants.FileModulus - pos % Constants.FileModulus) % Constants.FileModulus);
            if (pad > 0)
            {
                Array.Clear(buf, 0, (int)pad);
                xisoFs.Write(buf, 0, (int)pad);
            }

            WriteVolumeDescriptors(xisoFs, (uint)((pos + pad) / Constants.SectorSize));

            xisoFs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            var tagBytes = Encoding.ASCII.GetBytes(Constants.OptimizedTag);
            xisoFs.Write(tagBytes, 0, Constants.OptimizedTagLength);

            if (inRoot == null)
            {
                Logger.Log($"\nsucessfully created {isoName}{(inName != null ? "" : ".iso")} ({Logger.TotalFiles} files totalling {Logger.TotalBytes} bytes added)\n");
            }
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
                Progress = ctx.Progress,
                FinalBytes = ctx.FinalBytes,
                Path = ctx.Path != null
                    ? ctx.Path + avl.Filename + Constants.PathCharStr
                    : Constants.PathCharStr
            };

            Logger.Log($"adding {subCtx.Path} (0 bytes) [OK]\n");

            if (!ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
            {
                if (ctx.SourceStream == null)
                {
                    Directory.SetCurrentDirectory(avl.Filename);
                }

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, WriteFileCallback, subCtx,
                    AvlTraversalMethod.Prefix, 0);

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, WriteTreeCallback, subCtx,
                    AvlTraversalMethod.Prefix, 0);

                var xisoFs = (FileStream)ctx.XisoStream;
                xisoFs.Seek((long)avl.StartSector * Constants.SectorSize, SeekOrigin.Begin);
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

                if (ctx.SourceStream == null)
                {
                    Directory.SetCurrentDirectory("..");
                }
            }
            else
            {
                var xisoFs = (FileStream)ctx.XisoStream;
                xisoFs.Seek((long)avl.StartSector * Constants.SectorSize, SeekOrigin.Begin);
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

        var nameBytes = Encoding.ASCII.GetBytes(avl.Filename);
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
        xisoFs.Seek((long)avl.StartSector * Constants.SectorSize, SeekOrigin.Begin);

        var bufSize = Math.Max(Constants.SectorSize, Constants.ReadWriteBufferSize) + 1;
        var buf = new byte[bufSize];

        Stream srcStream;
        if (ctx.SourceStream == null)
        {
            srcStream = new FileStream(avl.Filename, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });
        }
        else
        {
            srcStream = ctx.SourceStream;
            srcStream.Seek((long)avl.OldStartSector * Constants.SectorSize + Logger.XboxDiscLseek,
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
                Logger.LogErr($"WARNING: File {avl.Filename} is truncated. Reported size: {originalSize} bytes, wrote size: {avl.FileSize} bytes!\n");
            }

            Logger.TotalFiles++;
            Logger.TotalBytes += avl.FileSize;
            ctx.Progress?.Invoke(Logger.TotalBytes, ctx.FinalBytes);
        }
    }

    /// <summary>
    /// Recursively descends into the current working directory, building an AVL tree
    /// of all files and subdirectories found. Updates the progress display as it goes.
    /// </summary>
    /// <param name="outRoot">Receives the root of the generated AVL tree.</param>
    /// <param name="ioN">Running count of filename characters for progress display.</param>
    /// <returns>0 on success.</returns>
    internal static int GenerateAvlTreeLocal(ref AvlNode? outRoot, ref int ioN)
    {
        var entries = Directory.GetFileSystemEntries(".");
        var emptyDir = true;

        foreach (var entryPath in entries)
        {
            var entryName = Path.GetFileName(entryPath);

            if (entryName is "." or "..")
                continue;

            for (var i = ioN; i > 0; i--) Logger.Log("\b");
            Logger.Log(entryName);
            var nameLen = entryName.Length;
            for (var j = nameLen; j < ioN; j++) Logger.Log(" ");
            for (var j = nameLen; j < ioN; j++) Logger.Log("\b");
            ioN = nameLen;
            Logger.Flush();

            var attr = File.GetAttributes(entryPath);
            var avl = new AvlNode { Filename = entryName };

            if ((attr & FileAttributes.Directory) != 0)
            {
                if (Logger.RemoveSystemUpdate && entryName.Contains("$SystemUpdate"))
                    continue;

                emptyDir = false;
                var prevDir = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(entryName);

                GenerateAvlTreeLocal(ref avl.Subdirectory, ref ioN);

                Directory.SetCurrentDirectory(prevDir);
            }
            else
            {
                emptyDir = false;
                var fi = new FileInfo(entryPath);
                if (fi.Length > uint.MaxValue)
                {
                    Logger.LogErr($"file {avl.Filename} is too large for xiso, skipping...\n");
                    continue;
                }
                avl.FileSize = (uint)fi.Length;
                Logger.TotalBytes += avl.FileSize;
                Logger.TotalFiles++;
            }

            AvlTree.AvlInsert(ref outRoot, avl);
        }

        if (emptyDir)
        {
            outRoot = AvlNode.EmptySubdirectory;
        }

        return 0;
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
                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, (n, c, d) =>
                {
                    CalculateDirectorySize(n, ref avl.FileSize, d);
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
    /// <param name="depth">Not used.</param>
    internal static void CalculateDirectorySize(AvlNode avl, ref uint outSize, int depth)
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
        return size / Constants.SectorSize + (size % Constants.SectorSize != 0 ? 1u : 0u);
    }

    /// <summary>
    /// Traversal callback that assigns sector positions to directory entries
    /// and delegates file position assignment to <see cref="WriteDirStartAndFilePositions"/>.
    /// </summary>
    /// <param name="avl">Current node being visited.</param>
    /// <param name="ctx">Context tracking the current sector counter.</param>
    /// <param name="depth">Not used.</param>
    internal static void CalculateDirectoryOffsets(AvlNode avl, OffsetCalcContext ctx, int depth)
    {
        if (avl.Subdirectory != null)
        {
            if (ReferenceEquals(avl.Subdirectory, AvlNode.EmptySubdirectory))
            {
                avl.StartSector = ctx.CurrentSector;
                ctx.CurrentSector += 1;
            }
            else
            {
                avl.StartSector = ctx.CurrentSector;
                var dirStart = (long)avl.StartSector * Constants.SectorSize;
                ctx.CurrentSector += NumSectors(avl.FileSize);

                var wdsafp = new WdsafpContext
                {
                    CurrentSector = ctx.CurrentSector,
                    DirStart = dirStart
                };

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, (n, c, d) =>
                {
                    WriteDirStartAndFilePositions(n, (WdsafpContext)c!, d);
                    return 0;
                }, wdsafp, AvlTraversalMethod.Prefix, 0);

                ctx.CurrentSector = wdsafp.CurrentSector;

                AvlTree.AvlTraverseDepthFirst(avl.Subdirectory, (n, c, d) =>
                {
                    CalculateDirectoryOffsets(n, (OffsetCalcContext)c!, d);
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
    /// <param name="depth">Not used.</param>
    /// <returns>Always 0.</returns>
    internal static int WriteDirStartAndFilePositions(AvlNode avl, WdsafpContext ctx, int depth)
    {
        avl.DirStart = ctx.DirStart;

        if (avl.Subdirectory == null)
        {
            avl.StartSector = ctx.CurrentSector;
            ctx.CurrentSector += NumSectors(avl.FileSize);
        }

        return 0;
    }

    /// <summary>
    /// Writes the ECMA-119 primary volume descriptor set at the end of the XISO image.
    /// This includes the data area identifier, volume space size, volume set size,
    /// volume set identifier, and creation date fields.
    /// </summary>
    /// <param name="fs">File stream positioned at the data area start offset.</param>
    /// <param name="totalSectors">Total number of sectors in the image.</param>
    internal static void WriteVolumeDescriptors(FileStream fs, uint totalSectors)
    {
        var big = (int)totalSectors;
        var little = (int)totalSectors;

        big = (big << 24) | ((big << 8) & 0xFF0000) | ((big >> 8) & 0xFF00) | (big >> 24);

        fs.Seek(Constants.Ecma119DataAreaStart, SeekOrigin.Begin);
        fs.WriteByte(0x01);
        fs.Write("CD001"u8);
        fs.WriteByte(0x01);

        fs.Seek(Constants.Ecma119VolumeSpaceSize, SeekOrigin.Begin);
        Span<byte> leBuf = stackalloc byte[4];
        Span<byte> beBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(leBuf, (uint)little);
        fs.Write(leBuf);
        BinaryPrimitives.WriteUInt32BigEndian(beBuf, totalSectors);
        fs.Write(beBuf);

        fs.Seek(Constants.Ecma119VolumeSetSize, SeekOrigin.Begin);
        byte[] volumeSetSize = [0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x08, 0x08, 0x00];
        fs.Write(volumeSetSize, 0, 12);

        fs.Seek(Constants.Ecma119VolumeSetIdentifier, SeekOrigin.Begin);
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

        fs.Seek(Constants.Ecma119DataAreaStart + Constants.SectorSize, SeekOrigin.Begin);
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
    /// <returns>A task that completes with 0 on success, 1 on error. The first tuple element is the result code; the second is the output ISO path.</returns>
    public static async Task<(int Result, string? OutIsoPath)> CreateXisoAsync(
        string rootDirectory,
        string? outputDirectory,
        AvlNode? inRoot,
        Stream? sourceStream,
        string? inName,
        ProgressCallback? progressCallback,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var result = CreateXiso(rootDirectory, outputDirectory, inRoot, sourceStream,
                out var outPath, inName, progressCallback, cancellationToken);
            return (result, outPath);
        }, cancellationToken);
    }
}

/// <summary>
/// Context object passed through the directory-offset calculation traversal.
/// Tracks the current sector counter being assigned to directory entries.
/// </summary>
internal class OffsetCalcContext
{
    /// <summary>Current sector number being assigned by the offset calculator.</summary>
    public uint CurrentSector;
}
