using System.Security.Cryptography;

namespace XISOSharp;

/// <summary>
/// Skeleton / petrify support, ported from <c>XDVDFS.ProcessXISO(..., skeleton:true)</c>.
/// </summary>
public static class XisoSkeleton
{
    private const long SectorSize = Constants.SectorSize;

    private static bool WriteBytes(FileStream inFs, FileStream outFs, long offset, long length)
    {
        var buf = new byte[64 * SectorSize];
        long copied = 0;
        if (offset >= 0) inFs.Seek(offset, SeekOrigin.Begin);
        while (copied < length)
        {
            var toRead = (int)Math.Min(buf.Length, length - copied);
            var n = inFs.Read(buf, 0, toRead);
            if (n == 0) break;
            outFs.Write(buf, 0, n);
            copied += n;
        }

        return copied == length;
    }

    private static void WriteZeroes(FileStream outFs, long offset, long length)
    {
        var buf = new byte[64 * SectorSize];
        long written = 0;
        if (offset >= 0) outFs.Seek(offset, SeekOrigin.Begin);
        while (written < length)
        {
            var toWrite = (int)Math.Min(buf.Length, length - written);
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
    /// <param name="quiet">Suppress info.</param>
    /// <param name="ct">Cancellation token.</param>
    public static bool Petrify(string inputPath, string? skeletonPath = null, string? hashPath = null,
        long isoOffset = 0, bool quiet = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var skel = skeletonPath ?? DeriveSkeletonPath(inputPath);
        var hash = hashPath ?? DeriveHashPath(inputPath);

        using var isoFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var isoLen = isoFs.Length;
        var xisoLength = isoLen - isoOffset;
        if (xisoLength <= 0) return false;

        (var bones, var fileRanges) =
            XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
        var ranges = XisoRanges.MergeRanges(bones, fileRanges);
        var fileEntries = XisoRanges.GetFileEntries(isoFs, isoOffset);

        // Open outputs
        using var skelFs = new FileStream(skel, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var hashWriter = new StreamWriter(hash, false, System.Text.Encoding.UTF8);

        if (!quiet) Logger.Log($"[INFO] Writing skeleton to {skel}\n");
        if (!quiet) Logger.Log($"[INFO] Hashing {fileEntries.Count} files to {hash}\n");

        // Hash and skeleton in one pass similar to XDVDFS.ProcessXISO
        // For correctness, we first hash all files in offset-sorted order, then create skeleton by sector walking
        // Hashing phase: stream each file once.
        foreach ((var path, var off, var size) in fileEntries)
        {
            ct.ThrowIfCancellationRequested();
            using var sha1 = SHA1.Create();
            var hashBuf = new byte[64 * SectorSize];
            long remaining = size;
            isoFs.Seek(off, SeekOrigin.Begin);
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(hashBuf.Length, remaining);
                var n = isoFs.Read(hashBuf, 0, toRead);
                if (n == 0) break;
                sha1.TransformBlock(hashBuf, 0, n, null, 0);
                remaining -= n;
            }

            sha1.TransformFinalBlock([], 0, 0);
            hashWriter.WriteLine($"{Convert.ToHexString(sha1.Hash!).ToLowerInvariant()} {path}");
        }

        hashWriter.Flush();

        // Skeleton creation: copy isoOffset prefix verbatim (for Redump), then walk XISO partition
        if (isoOffset > 0)
        {
            isoFs.Seek(0, SeekOrigin.Begin);
            if (!WriteBytes(isoFs, skelFs, -1, isoOffset)) return false;
        }

        isoFs.Seek(isoOffset, SeekOrigin.Begin);
        long numBytes = 0;
        while (numBytes < xisoLength)
        {
            ct.ThrowIfCancellationRequested();
            var currentByte = isoOffset + numBytes;
            var currentSector = (currentByte + SectorSize - 1) / SectorSize;
            long bytesUntilEndOfExtent = 0;
            long bytesToWipe = 0;
            var isBone = false;

            if (ranges.Count > 0 && currentSector > ranges[^1].End)
            {
                bytesToWipe = xisoLength - numBytes;
            }
            else
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    if (currentSector >= ranges[i].Start && currentSector <= ranges[i].End)
                    {
                        bytesUntilEndOfExtent = ((ranges[i].End + 1) * SectorSize) - currentByte;
                        // Check bone
                        for (var b = 0; b < bones.Count; b++)
                        {
                            if (currentSector >= bones[b].Start && currentSector <= bones[b].End)
                            {
                                isBone = true;
                                break;
                            }
                        }

                        // bones include this sector => bytesUntil uses bone extent if present
                        if (isBone)
                        {
                            // find bone extent that contains currentSector
                            for (var b = 0; b < bones.Count; b++)
                            {
                                if (currentSector >= bones[b].Start && currentSector <= bones[b].End)
                                {
                                    bytesUntilEndOfExtent = ((bones[b].End + 1) * SectorSize) - currentByte;
                                    break;
                                }
                            }
                        }

                        break;
                    }
                    else if (currentSector < ranges[i].Start && (i == 0 || currentSector > ranges[i - 1].End))
                    {
                        bytesToWipe = (ranges[i].Start * SectorSize) - currentByte;
                        break;
                    }
                }
            }

            // If filler region (bytesToWipe>0) then we need to decide: in skeleton, filler gaps are zeroed too?
            // XboxKit skeleton zeros file data but also zeros filler? ProcessXISO skeleton zeroes non-bone extents.
            // So both filler and file data are zeroed unless it's a bone.
            if (bytesToWipe > 0)
            {
                // Filler gap — already zero in skeleton
                WriteZeroes(skelFs, -1, bytesToWipe);
                numBytes += bytesToWipe;
                isoFs.Seek(bytesToWipe, SeekOrigin.Current);
            }
            else
            {
                var bytesToRead = bytesUntilEndOfExtent > 0 ? bytesUntilEndOfExtent : xisoLength - numBytes;
                if (isBone)
                {
                    if (!WriteBytes(isoFs, skelFs, -1, bytesToRead)) return false;
                }
                else
                {
                    WriteZeroes(skelFs, -1, bytesToRead);
                    isoFs.Seek(bytesToRead, SeekOrigin.Current);
                }

                numBytes += bytesToRead;
            }
        }

        return numBytes == xisoLength;
    }

    private static string DeriveSkeletonPath(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? "";
        var full = Path.GetFileName(input) ?? "skeleton";
        // Strip compound extensions
        if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".redump.iso".Length];
        else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".video.iso".Length];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".iso".Length];
        else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) full = full[..^".xiso".Length];
        return Path.Combine(dir, $"{full}.skeleton.xiso");
    }

    private static string DeriveHashPath(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? "";
        var full = Path.GetFileName(input) ?? "hash";
        if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".redump.iso".Length];
        else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".video.iso".Length];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) full = full[..^".iso".Length];
        else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) full = full[..^".xiso".Length];
        return Path.Combine(dir, $"{full}.hash");
    }
}