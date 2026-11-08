using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Skeleton / petrify support, ported from <c>XDVDFS.ProcessXISO(..., skeleton:true)</c>.
/// </summary>
public static class XisoSkeleton
{
    private const long SectorSize = Constants.SectorSize;

    private static bool WriteBytes(FileStream inFs, FileStream outFs, long offset, long length)
    {
        byte[] buf = new byte[64 * SectorSize];
        long copied = 0;
        if (offset >= 0) inFs.Seek(offset, SeekOrigin.Begin);
        while (copied < length)
        {
            int toRead = (int)Math.Min(buf.Length, length - copied);
            int n = inFs.Read(buf, 0, toRead);
            if (n == 0) break;
            outFs.Write(buf, 0, n);
            copied += n;
        }

        return copied == length;
    }

    private static void WriteZeroes(FileStream outFs, long offset, long length)
    {
        byte[] buf = new byte[64 * SectorSize];
        long written = 0;
        if (offset >= 0) outFs.Seek(offset, SeekOrigin.Begin);
        while (written < length)
        {
            int toWrite = (int)Math.Min(buf.Length, length - written);
            outFs.Write(buf, 0, toWrite);
            written += toWrite;
        }
    }

    /// <summary>
    /// Creates a skeleton XISO (all file data zeroed, filesystem intact) and optionally a hash file.
    /// </summary>
    /// <param name="inputPath">Source XISO (or Redump game partition via <paramref name="isoOffset"/>).</param>
    /// <param name="skeletonPath">Destination skeleton path. If null, derives <c>.skeleton.xiso</c>.</param>
    /// <param name="hashPath">Optional hash file path (<c>sha1 hex + space + path</c> per line). If null, derives <c>.hash</c>.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition within the file (for Redump).</param>
    /// <param name="xisoLength">
    /// Byte length of the XISO partition. When set (Redump inputs), the skeleton is the
    /// partition only — no Redump prefix is emitted — matching XboxKit <c>-p</c>
    /// (<c>ProcessXISO(isoFS, XISO_OFFSET, XISO_LENGTH, …)</c>). When null, the file
    /// remainder after <paramref name="isoOffset"/> is walked (standalone XISOs, and the
    /// legacy prefix-verbatim behavior when <paramref name="isoOffset"/> is set without it).
    /// </param>
    /// <param name="quiet">Suppress info.</param>
    /// <param name="ct">Cancellation token.</param>
    public static bool Petrify(string inputPath, string? skeletonPath = null, string? hashPath = null,
        long isoOffset = 0, long? xisoLength = null, bool quiet = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string skel = skeletonPath ?? DeriveSkeletonPath(inputPath);
        string hash = hashPath ?? DeriveHashPath(inputPath);

        using FileStream isoFs = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        long isoLen = isoFs.Length;
        long partLength = xisoLength ?? (isoLen - isoOffset);
        if (partLength <= 0) return false;

        (List<(uint Start, uint End)> bones, _) =
            XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
        List<(string Path, long Offset, uint Size)> fileEntries = XisoRanges.GetFileEntries(isoFs, isoOffset);

        // Open outputs
        using FileStream skelFs = new(skel, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using StreamWriter hashWriter = new(hash, false, System.Text.Encoding.UTF8);

        if (!quiet) Logger.Log($"[INFO] Writing skeleton to {skel}\n");
        if (!quiet) Logger.Log($"[INFO] Hashing {fileEntries.Count} files to {hash}\n");

        // Hash and skeleton in one pass similar to XDVDFS.ProcessXISO
        // For correctness, we first hash all files in offset-sorted order, then create skeleton by sector walking
        // Hashing phase: stream each file once.
        foreach ((string path, long off, uint size) in fileEntries)
        {
            ct.ThrowIfCancellationRequested();
            using SHA1 sha1 = SHA1.Create();
            byte[] hashBuf = new byte[64 * SectorSize];
            long remaining = size;
            isoFs.Seek(off, SeekOrigin.Begin);
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(hashBuf.Length, remaining);
                int n = isoFs.Read(hashBuf, 0, toRead);
                if (n == 0) break;
                sha1.TransformBlock(hashBuf, 0, n, null, 0);
                remaining -= n;
            }

            sha1.TransformFinalBlock([], 0, 0);
            hashWriter.WriteLine($"{Convert.ToHexString(sha1.Hash!).ToLowerInvariant()} {path}");
        }

        hashWriter.Flush();

        // Skeleton creation: with an explicit partition length (Redump) only the
        // partition is emitted (XboxKit parity — no Redump prefix). Otherwise the
        // isoOffset prefix is copied verbatim, then the remainder is walked.
        if (isoOffset > 0 && xisoLength is null)
        {
            isoFs.Seek(0, SeekOrigin.Begin);
            if (!WriteBytes(isoFs, skelFs, -1, isoOffset)) return false;
        }

        isoFs.Seek(isoOffset, SeekOrigin.Begin);

        // Boundary-aware segment walk: every filesystem (bone) sector is copied
        // verbatim, everything else is zeroed. Bone ranges are walked as keep
        // segments — never zero to the end of a merged file+bone extent, which
        // would pave over bone islands sharing the extent with file data and
        // leave an unlistable skeleton. Copies use absolute seeks so the input
        // position can never desync from the output position.
        long baseSector = isoOffset / SectorSize; // XGD offsets and plain XISOs are sector-aligned
        List<(long Start, long End)> keep = []; // partition-relative [start, end) byte ranges
        foreach ((uint s, uint e) in bones)
        {
            long cs = Math.Max(s, baseSector);
            long ce = Math.Min(e, baseSector + ((partLength + SectorSize - 1) / SectorSize) - 1);
            if (ce < cs)
            {
                continue;
            }

            long bs = (cs - baseSector) * SectorSize;
            long be = Math.Min((ce - baseSector + 1) * SectorSize, partLength);
            if (bs >= be)
            {
                continue;
            }

            if (keep.Count > 0 && bs <= keep[^1].End)
            {
                keep[^1] = (keep[^1].Start, Math.Max(keep[^1].End, be));
            }
            else
            {
                keep.Add((bs, be));
            }
        }

        // Preserve the system preamble (ECMA-119 PVD, extract-xiso optimized tag
        // and any other pre-filesystem metadata). Those sectors are invisible to
        // the XDVDFS walk, so a bones-only skeleton would drop them and Apply
        // could never reproduce the original image byte-for-byte.
        long headerBase = XisoReader.TryFindHeaderBase(isoFs, isoOffset, out long detectedHeader)
            ? detectedHeader
            : isoOffset + Constants.HeaderOffset;
        long preambleEnd = Math.Min(headerBase - isoOffset, partLength);
        if (preambleEnd > 0)
        {
            keep.Add((0, preambleEnd));
        }

        // Preserve the pad-byte tail of a file extent. The packer fills the rest
        // of the file's last sector with 0xFF; Apply writes only the file's own
        // bytes, so keeping an all-0xFF tail makes Petrify -> Apply
        // byte-identical without retaining any file data. A tail containing
        // anything else is real file-adjacent data and stays zeroed.
        byte[] tailBuffer = new byte[SectorSize];
        foreach ((_, long offset, uint size) in fileEntries)
        {
            if (size == 0)
            {
                continue;
            }

            long fileEnd = offset - isoOffset + size;
            long sectorEnd = ((fileEnd + SectorSize - 1) / SectorSize) * SectorSize;
            long tailLength = Math.Min(sectorEnd, partLength) - fileEnd;
            if (tailLength <= 0 || tailLength > tailBuffer.Length)
            {
                continue;
            }

            try
            {
                isoFs.Seek(isoOffset + fileEnd, SeekOrigin.Begin);
                int total = 0;
                while (total < tailLength)
                {
                    int read = isoFs.Read(tailBuffer, total, (int)tailLength - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                bool allPad = total == tailLength;
                for (int i = 0; allPad && i < tailLength; i++)
                {
                    allPad = tailBuffer[i] == Constants.PadByte;
                }

                if (allPad)
                {
                    keep.Add((fileEnd, fileEnd + tailLength));
                }
            }
            catch (IOException)
            {
                // A short read leaves the tail zeroed like the rest of the data.
            }
        }

        keep.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        List<(long Start, long End)> merged = new(keep.Count);
        foreach ((long start, long end) in keep)
        {
            if (start >= end)
            {
                continue;
            }

            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        keep = merged;

        long numBytes = 0;
        foreach ((long ks, long ke) in keep)
        {
            ct.ThrowIfCancellationRequested();
            if (ks > numBytes)
            {
                WriteZeroes(skelFs, -1, ks - numBytes);
                numBytes = ks;
            }

            if (ke > numBytes)
            {
                isoFs.Seek(isoOffset + numBytes, SeekOrigin.Begin);
                if (!WriteBytes(isoFs, skelFs, -1, ke - numBytes)) return false;
                numBytes = ke;
            }
        }

        if (numBytes < partLength)
        {
            ct.ThrowIfCancellationRequested();
            WriteZeroes(skelFs, -1, partLength - numBytes);
            numBytes = partLength;
        }

        return numBytes == partLength;
    }

    /// <summary>
    /// Applies file data from a source directory to a skeleton XISO produced by
    /// <see cref="Petrify"/>, reproducing the original image byte-for-byte. The
    /// skeleton's filesystem and system sectors are preserved; only each
    /// recorded file extent is overwritten with the corresponding source file.
    /// </summary>
    /// <param name="skeletonPath">Skeleton image (typically <c>*.skeleton.xiso</c>).</param>
    /// <param name="sourceDirectory">Directory holding the original file tree.</param>
    /// <param name="outputPath">Destination XISO path.</param>
    /// <param name="hashPath">
    /// Optional <c>.hash</c> sidecar produced by <see cref="Petrify"/>. When
    /// supplied, every restored file's SHA-1 is verified while it is written.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="strictNames">
    /// When <c>true</c>, fail on a source component that exceeds the Xbox
    /// 42-character limit or is outside Latin-1; when <c>false</c> (default),
    /// log a warning and continue.
    /// </param>
    /// <returns>The number of files restored.</returns>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The source directory does not exist.</exception>
    /// <exception cref="FileNotFoundException">The skeleton or hash file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The skeleton is not a readable XISO, the source tree does not match the
    /// skeleton (missing/extra file or size mismatch), or a hash differs.
    /// </exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int Apply(string skeletonPath, string sourceDirectory, string outputPath,
        string? hashPath = null, CancellationToken cancellationToken = default, bool strictNames = false) =>
        Apply(skeletonPath, sourceDirectory, outputPath, hashPath, cancellationToken, strictNames,
            SymlinkMode.Follow);

    /// <summary>
    /// <see cref="Apply(string,string,string,string?,CancellationToken,bool)"/> with an
    /// explicit host-symlink policy for <paramref name="sourceDirectory"/>.
    /// </summary>
    public static int Apply(string skeletonPath, string sourceDirectory, string outputPath,
        string? hashPath, CancellationToken cancellationToken, bool strictNames, SymlinkMode symlinkMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(skeletonPath);
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(skeletonPath))
        {
            throw new FileNotFoundException($"Skeleton not found: {skeletonPath}", skeletonPath);
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        string fullSkeleton = Path.GetFullPath(skeletonPath);
        string fullOutput = Path.GetFullPath(outputPath);
        if (XisoPaths.AreSamePath(fullSkeleton, fullOutput))
        {
            throw new IOException($"Output '{outputPath}' must not overwrite the skeleton '{skeletonPath}'.");
        }

        if (hashPath != null && !File.Exists(hashPath))
        {
            throw new FileNotFoundException($"Hash file not found: {hashPath}", hashPath);
        }

        Dictionary<string, string> expectedHashes = hashPath is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadHashFile(hashPath);

        using Stream skeletonStream = XisoImageSource.OpenRead(skeletonPath);
        VolumeInfo volume = XisoReader.GetVolumeInfo(skeletonStream, skeletonPath);
        if (!volume.IsValid)
        {
            throw new InvalidDataException($"Skeleton is not a valid XISO image: {skeletonPath}");
        }

        List<(string Path, long Offset, uint Size)> entries =
            XisoRanges.GetFileEntries(skeletonStream, volume.DiscLseek);
        Dictionary<string, SourceFile> sourceFiles = EnumerateSourceFiles(sourceDirectory, symlinkMode);
        ValidateSourceNames(sourceFiles.Keys, strictNames);

        ValidateSourceTree(entries, sourceFiles);
        if (expectedHashes.Count > 0)
        {
            ValidateHashEntries(entries, expectedHashes);
        }

        string outputDirectory = Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        string tempPath = Path.Combine(outputDirectory,
            $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");

        bool created = false;
        try
        {
            using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                       65536))
            {
                created = true;
                SparseFile.Prepare(output, skeletonStream.Length);

                // Preserve every filesystem/system sector first.
                skeletonStream.Seek(0, SeekOrigin.Begin);
                skeletonStream.CopyTo(output, 1 << 20);

                byte[] buffer = new byte[1 << 20];
                foreach ((string internalPath, long offset, uint size) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SourceFile source = sourceFiles[NormalizeHashPath(internalPath)];
                    using FileStream input = new(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536);
                    using SHA1 sha1 = SHA1.Create();
                    output.Seek(offset, SeekOrigin.Begin);

                    long remaining = size;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = input.Read(buffer, 0, toRead);
                        if (read <= 0)
                        {
                            throw new InvalidDataException(
                                $"Source file '{source.FullPath}' ended before the skeleton extent " +
                                $"({size} bytes expected for '{internalPath}').");
                        }

                        sha1.TransformBlock(buffer, 0, read, null, 0);
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }

                    sha1.TransformFinalBlock([], 0, 0);
                    if (expectedHashes.TryGetValue(NormalizeHashPath(internalPath), out string? expected))
                    {
                        string actual = Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Hash mismatch for '{internalPath}': expected {expected}, actual {actual}.");
                        }
                    }
                }

                output.SetLength(skeletonStream.Length);
            }

            File.Move(tempPath, fullOutput, overwrite: true);
            created = false;
            if (!string.IsNullOrEmpty(hashPath))
            {
                Logger.Log($"[INFO] Applied {entries.Count} files to {outputPath} (hash verified)\n");
            }
            else
            {
                Logger.Log($"[INFO] Applied {entries.Count} files to {outputPath}\n");
            }

            return entries.Count;
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private readonly record struct SourceFile(string FullPath, long Length);

    private static Dictionary<string, SourceFile> EnumerateSourceFiles(string sourceDirectory,
        SymlinkMode symlinkMode)
    {
        Dictionary<string, SourceFile> files = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(sourceDirectory);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] entries = Directory.GetFileSystemEntries(directory);
            Array.Sort(entries, StringComparer.Ordinal);

            foreach (string entry in entries)
            {
                string relative = Path.GetRelativePath(sourceDirectory, entry).Replace('\\', '/');
                FileAttributes attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != FileAttributes.None;
                bool isLink = (attributes & FileAttributes.ReparsePoint) != FileAttributes.None;

                if (isDirectory)
                {
                    if (isLink)
                    {
                        HandleSourceLink(relative, isDirectory: true, symlinkMode);
                        continue;
                    }

                    pending.Push(entry);
                    continue;
                }

                if (isLink && HandleSourceLink(relative, isDirectory: false, symlinkMode))
                {
                    continue;
                }

                string key = NormalizeHashPath(relative);
                FileInfo info = new(entry);
                long length = isLink ? XisoWriter.PackedFileLength(info) : info.Length;
                if (!files.TryAdd(key, new SourceFile(info.FullName, length)))
                {
                    throw new InvalidDataException(
                        $"Source directory contains a case-insensitive duplicate: '{relative}'.");
                }
            }
        }

        return files;
    }

    /// <summary>Applies the create-time link policy while walking an Apply source tree.</summary>
    private static bool HandleSourceLink(string relative, bool isDirectory, SymlinkMode symlinkMode)
    {
        if (isDirectory)
        {
            if (symlinkMode == SymlinkMode.Reject)
            {
                throw new InvalidDataException(
                    $"Cannot use directory link '{relative}': --symlink-mode reject.");
            }

            Logger.LogErr(symlinkMode == SymlinkMode.Skip
                ? $"symlink: skipping directory link {relative}.\n"
                : $"warning: skipping reparse point (symlink/junction): {relative}, not descending.\n");
            return true;
        }

        if (symlinkMode == SymlinkMode.Follow)
        {
            Logger.LogErr(
                $"warning: packing symlink as target content (links are not preserved): {relative}.\n");
            return false;
        }

        if (symlinkMode == SymlinkMode.Reject)
        {
            throw new InvalidDataException(
                $"Cannot use file link '{relative}': --symlink-mode reject.");
        }

        Logger.LogErr($"symlink: skipping file link {relative}.\n");
        return true;
    }

    private static void ValidateSourceNames(IEnumerable<string> paths, bool strictNames)
    {
        foreach (string path in paths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string? error = XisoNameRules.GetPathError(path);
            if (error is null)
            {
                continue;
            }

            if (strictNames)
            {
                throw new InvalidDataException($"{error} Source path: '{path}'.");
            }

            Logger.LogErr($"warning: {error} Source path: '{path}'.\n");
            Logger.Warned = true;
        }
    }

    private static void ValidateSourceTree(IReadOnlyList<(string Path, long Offset, uint Size)> entries,
        IReadOnlyDictionary<string, SourceFile> sourceFiles)
    {
        HashSet<string> skeletonPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, _, uint size) in entries)
        {
            string key = NormalizeHashPath(path);
            skeletonPaths.Add(key);
            if (!sourceFiles.TryGetValue(key, out SourceFile source))
            {
                throw new InvalidDataException(
                    $"Missing source file '{path}': the skeleton expects {size} bytes.");
            }

            if (source.Length != size)
            {
                throw new InvalidDataException(
                    $"Size mismatch for '{path}': skeleton expects {size} bytes, source has {source.Length}.");
            }
        }

        foreach (string path in sourceFiles.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!skeletonPaths.Contains(path))
            {
                throw new InvalidDataException(
                    $"Extra source file '{path}': it has no entry in the skeleton.");
            }
        }
    }

    private static void ValidateHashEntries(IReadOnlyList<(string Path, long Offset, uint Size)> entries,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        HashSet<string> skeletonPaths = new(entries.Select(static entry => NormalizeHashPath(entry.Path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string path in expectedHashes.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!skeletonPaths.Contains(path))
            {
                throw new InvalidDataException($"Extra hash entry '{path}': it has no entry in the skeleton.");
            }
        }
    }

    private static Dictionary<string, string> ReadHashFile(string hashPath)
    {
        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        int lineNumber = 0;
        foreach (string raw in File.ReadLines(hashPath))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                throw new InvalidDataException(
                    $"Invalid hash file line {lineNumber}: expected '<sha1> <path>'.");
            }

            string digest = line[..separator].Trim();
            string path = line[separator..].Trim();
            if (digest.Length != 40 || !digest.All(static c => Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException(
                    $"Invalid hash file line {lineNumber}: '{digest}' is not a SHA-1 hex digest.");
            }

            expected[NormalizeHashPath(path)] = digest.ToLowerInvariant();
        }

        return expected;
    }

    /// <summary>
    /// Verifies an image against the <c>.hash</c> sidecar produced by
    /// <see cref="Petrify"/> (or XboxKit <c>--petrify</c>). Each line is
    /// <c>sha1hex path</c>; paths may use a leading <c>/</c> or not. Works for
    /// plain ISO, split ISO, CISO, CCI and ZAR inputs.
    /// </summary>
    /// <param name="imagePath">Image or archive to verify.</param>
    /// <param name="hashFilePath">The <c>.hash</c> sidecar.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-file comparison results plus match/mismatch/missing/extra totals.</returns>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    /// <exception cref="FileNotFoundException">The image or hash file does not exist.</exception>
    /// <exception cref="InvalidDataException">A hash-file line is malformed.</exception>
    public static HashVerificationResult VerifyHashes(string imagePath, string hashFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrEmpty(hashFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);
        }

        if (!File.Exists(hashFilePath))
        {
            throw new FileNotFoundException($"Hash file not found: {hashFilePath}", hashFilePath);
        }

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        int lineNumber = 0;
        foreach (string raw in File.ReadLines(hashFilePath))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                throw new InvalidDataException(
                    $"Invalid hash file line {lineNumber}: expected '<digest> <path>'.");
            }

            string digest = line[..separator].Trim();
            string path = line[separator..].Trim();
            if (digest.Length is not (32 or 40 or 64) || !digest.All(static c => Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException(
                    $"Invalid hash file line {lineNumber}: '{digest}' is not a 32 (MD5), 40 (SHA-1) " +
                    "or 64 (SHA-256) character hex digest.");
            }

            expected[NormalizeHashPath(path)] = digest.ToLowerInvariant();
        }

        // Compute only the algorithms the sidecar actually uses, through the
        // shared container-aware sidecar builder (ISO/split/CISO/CCI/GOD/STFS/ZAR).
        HashSet<HashAlgorithmName> algorithms = [];
        foreach (string digest in expected.Values)
        {
            algorithms.Add(DigestAlgorithm(digest.Length));
        }

        Dictionary<string, Dictionary<HashAlgorithmName, string>> actual =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (HashAlgorithmName algorithm in algorithms)
        {
            foreach (FileHashEntry entry in
                     XisoReader.ComputeFileHashSidecar(imagePath, algorithm, cancellationToken))
            {
                string key = NormalizeHashPath(entry.Path);
                if (!actual.TryGetValue(key, out Dictionary<HashAlgorithmName, string>? digests))
                {
                    digests = [];
                    actual[key] = digests;
                }

                digests[algorithm] = entry.HashHex;
            }
        }

        List<HashVerificationEntry> entries = [];
        int matched = 0;
        int mismatched = 0;
        int missing = 0;
        foreach ((string path, string digest) in expected.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            HashAlgorithmName algorithm = DigestAlgorithm(digest.Length);
            string algorithmName = DigestAlgorithmName(algorithm);
            string? actualDigest = actual.TryGetValue(path, out Dictionary<HashAlgorithmName, string>? found) &&
                                   found.TryGetValue(algorithm, out string? value)
                ? value
                : null;

            if (actualDigest is null)
            {
                missing++;
                entries.Add(new HashVerificationEntry(path, digest, null, HashVerificationStatus.Missing)
                {
                    Algorithm = algorithmName,
                });
            }
            else if (string.Equals(digest, actualDigest, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                entries.Add(new HashVerificationEntry(path, digest, actualDigest, HashVerificationStatus.Match)
                {
                    Algorithm = algorithmName,
                });
            }
            else
            {
                mismatched++;
                entries.Add(new HashVerificationEntry(path, digest, actualDigest, HashVerificationStatus.Mismatch)
                {
                    Algorithm = algorithmName,
                });
            }
        }

        int extra = 0;
        foreach ((string path, Dictionary<HashAlgorithmName, string> digests) in
                 actual.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (expected.ContainsKey(path))
            {
                continue;
            }

            extra++;
            entries.Add(new HashVerificationEntry(path, null, digests.Values.FirstOrDefault(),
                HashVerificationStatus.Extra)
            {
                Algorithm = digests.Keys.FirstOrDefault() == default
                    ? "sha1"
                    : DigestAlgorithmName(digests.Keys.First()),
            });
        }

        return new HashVerificationResult(expected.Count, matched, mismatched, missing, extra, entries);
    }

    private static HashAlgorithmName DigestAlgorithm(int digestLength) => digestLength switch
    {
        32 => HashAlgorithmName.MD5,
        40 => HashAlgorithmName.SHA1,
        64 => HashAlgorithmName.SHA256,
        _ => throw new InvalidDataException(
            $"Unsupported hash digest length {digestLength}: expected MD5 (32), SHA-1 (40) or SHA-256 (64) hex characters."),
    };

    private static string DigestAlgorithmName(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5) return "md5";
        if (algorithm == HashAlgorithmName.SHA256) return "sha256";
        return "sha1";
    }

    private static string NormalizeHashPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static string DeriveSkeletonPath(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? "";
        string full = Path.GetFileName(input);
        // Strip compound extensions
        if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".redump.iso".Length];
        else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".video.iso".Length];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".iso".Length];
        else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) full = full[..^".xiso".Length];
        return Path.Combine(dir, $"{full}.skeleton.xiso");
    }

    private static string DeriveHashPath(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? "";
        string full = Path.GetFileName(input);
        if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".redump.iso".Length];
        else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".video.iso".Length];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".iso".Length];
        else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) full = full[..^".xiso".Length];
        return Path.Combine(dir, $"{full}.hash");
    }
}
