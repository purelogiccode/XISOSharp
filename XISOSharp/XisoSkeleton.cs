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

        Dictionary<string, string> actual = new(StringComparer.OrdinalIgnoreCase);
        if (imagePath.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ZarEntry entry in XisoZarchive.ListZar(imagePath))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                byte[]? hash = XisoZarchive.ComputeFileHash(imagePath, entry.Path, HashAlgorithmName.SHA1);
                if (hash is not null)
                {
                    actual[NormalizeHashPath(entry.Path)] = Convert.ToHexString(hash).ToLowerInvariant();
                }
            }
        }
        else
        {
            using XisoImage image = XisoImage.Open(imagePath);
            foreach (ExplorerNode node in image.ListFlat())
            {
                if (node.IsDirectory)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                string? hash = image.ComputeHashHex(node.FullPath, HashAlgorithmName.SHA1);
                if (hash is not null)
                {
                    actual[NormalizeHashPath(node.FullPath)] = hash.ToLowerInvariant();
                }
            }
        }

        List<HashVerificationEntry> entries = [];
        int matched = 0;
        int mismatched = 0;
        int missing = 0;
        foreach ((string path, string digest) in expected.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!actual.TryGetValue(path, out string? actualDigest))
            {
                missing++;
                entries.Add(new HashVerificationEntry(path, digest, null, HashVerificationStatus.Missing));
            }
            else if (string.Equals(digest, actualDigest, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                entries.Add(new HashVerificationEntry(path, digest, actualDigest, HashVerificationStatus.Match));
            }
            else
            {
                mismatched++;
                entries.Add(new HashVerificationEntry(path, digest, actualDigest, HashVerificationStatus.Mismatch));
            }
        }

        int extra = 0;
        foreach ((string path, string digest) in actual.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (expected.ContainsKey(path))
            {
                continue;
            }

            extra++;
            entries.Add(new HashVerificationEntry(path, null, digest, HashVerificationStatus.Extra));
        }

        return new HashVerificationResult(expected.Count, matched, mismatched, missing, extra, entries);
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
