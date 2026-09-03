using System.Text;

namespace XISOSharp;

/// <summary>
/// Redump rebuild and video-partition helpers ported from <c>References/XboxKit-0.7/LibXGD/XGD.cs</c>
/// and <c>References/XboxKit-0.7/XboxKit/RebuildISO.cs</c>.
/// </summary>
public static class XisoRedump
{
    private const long SectorSize = Constants.SectorSize;
    private static readonly byte[] FillerPattern = "ABCDABCDABCDABCD"u8.ToArray();

    // -----------------------------------------------------------------------
    // Helpers — stream copy
    // -----------------------------------------------------------------------

    private static bool WriteBytes(FileStream inFs, FileStream outFs, long offset, long length)
    {
        var buf = new byte[64 * Constants.SectorSize];
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
        var buf = new byte[64 * Constants.SectorSize];
        Array.Clear(buf, 0, buf.Length);
        long written = 0;
        if (offset >= 0) outFs.Seek(offset, SeekOrigin.Begin);
        while (written < length)
        {
            var toWrite = (int)Math.Min(buf.Length, length - written);
            outFs.Write(buf, 0, toWrite);
            written += toWrite;
        }
    }

    private static bool TryReadAt(FileStream fs, long offset, Span<byte> buf)
    {
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
        }
        catch
        {
            return false;
        }

        var total = 0;
        while (total < buf.Length)
        {
            var n = fs.Read(buf[total..]);
            if (n == 0) break;
            total += n;
        }

        return total == buf.Length;
    }

    // -----------------------------------------------------------------------
    // Video partition
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the video partition (L0 head + L1 tail) from a Redump ISO.
    /// Mirrors <c>XGD.ExtractVideo</c>. Returns false when <paramref name="redumpPath"/>
    /// is not a known Redump size or its wave cannot be determined.
    /// </summary>
    public static bool TryExtractVideo(string redumpPath, string? outputVideoPath, out string? outPath,
        bool quiet = false, CancellationToken cancellationToken = default)
    {
        outPath = null;
        cancellationToken.ThrowIfCancellationRequested();

        var isoSize = new FileInfo(redumpPath).Length;
        var redumpIsoType = XgdTables.GetRedumpIsoTypeBySize(isoSize);
        if (redumpIsoType < 0)
        {
            if (!quiet) Logger.LogErr($"[ERROR] Unexpected Redump ISO size {isoSize}, cannot determine video type\n");
            return false;
        }

        using var isoFs = new FileStream(redumpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var videoType = XgdTables.GetVideoType(isoFs, redumpIsoType);
        if (videoType < 0)
        {
            if (!quiet) Logger.LogErr("[ERROR] Cannot determine video type (wave PVD unknown)\n");
            return false;
        }

        var l0 = XgdTables.VideoL0Length[videoType];
        var l1 = XgdTables.VideoL1Length[videoType];

        var videoPath = outputVideoPath ?? DeriveVideoPath(redumpPath);
        outPath = videoPath;

        if (!quiet)
            Logger.Log($"[INFO] Writing video partition to {videoPath} (type {videoType}, L0 {l0} + L1 {l1})\n");

        using var videoFs = new FileStream(videoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        if (!WriteBytes(isoFs, videoFs, 0, l0)) return false;
        if (!WriteBytes(isoFs, videoFs, isoSize - l1, l1)) return false;
        return true;
    }

    private static string DeriveVideoPath(string redumpPath)
    {
        var dir = Path.GetDirectoryName(redumpPath) ?? "";
        var filename = Path.GetFileNameWithoutExtension(redumpPath) ?? "video";
        // Strip compound extensions like .redump
        string[] compounds = [".video.iso", ".redump.iso", ".skeleton.xiso", ".xiso"];
        var full = Path.GetFileName(redumpPath) ?? "";
        foreach (var ext in compounds)
        {
            if (full.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                filename = full[..^ext.Length];
                break;
            }
        }

        return Path.Combine(dir, $"{filename}.video.iso");
    }

    // -----------------------------------------------------------------------
    // System update (XGD3) — heuristic filler-scan, ported from ExtractVideo.SUOffset
    // -----------------------------------------------------------------------

    private static long FindUpdateOffset(FileStream videoFs)
    {
        var updateOffset = videoFs.Length;
        var videoBuf = new byte[16];
        ReadOnlySpan<byte> filler = FillerPattern;
        while (updateOffset >= SectorSize)
        {
            videoFs.Seek(updateOffset - SectorSize, SeekOrigin.Begin);
            var total = 0;
            while (total < videoBuf.Length)
            {
                var n = videoFs.Read(videoBuf, total, videoBuf.Length - total);
                if (n == 0) break;
                total += n;
            }

            if (total < 16) break;
            if (filler.SequenceEqual(videoBuf))
                break;
            updateOffset -= SectorSize;
        }

        return updateOffset;
    }

    /// <summary>
    /// Extracts the XGD3 system-update file <c>su20076000_00000000</c> from a video partition.
    /// When <paramref name="wipe"/> is true, the update range inside <paramref name="videoPath"/> is zeroed.
    /// Mirrors <c>ExtractVideo.ExtractSU</c>. Returns false if the video size is not XGD3.
    /// </summary>
    public static bool TryExtractUpdate(string videoPath, string? outputUpdatePath, bool wipe = true,
        bool quiet = false)
    {
        var videoLen = new FileInfo(videoPath).Length;
        var videoType = XgdTables.GetVideoTypeBySize(videoLen);
        if (videoType != 16 && videoType != 17 && videoType != 18)
        {
            if (!quiet) Logger.Log($"[INFO] Cannot extract update — not an XGD3 video partition (size {videoLen})\n");
            return false;
        }

        var updatePath = outputUpdatePath ??
                         Path.Combine(Path.GetDirectoryName(videoPath) ?? "", "su20076000_00000000");
        using var videoFs = new FileStream(videoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536);
        var updateOffset = FindUpdateOffset(videoFs);
        var updateLength = videoFs.Length - updateOffset - SectorSize;
        if (updateLength <= 0)
        {
            if (!quiet) Logger.LogErr("[ERROR] No system update found in video partition\n");
            return false;
        }

        if (!quiet)
            Logger.Log($"[INFO] Writing system update to {updatePath} ({updateLength} bytes at {updateOffset})\n");
        using (var updateFs = new FileStream(updatePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            if (!WriteBytes(videoFs, updateFs, updateOffset, updateLength))
                return false;
        }

        if (wipe)
        {
            if (!quiet) Logger.Log($"[INFO] Zeroing system update in {videoPath}\n");
            WriteZeroes(videoFs, updateOffset, updateLength);
        }

        return true;
    }

    // Non-allocating helper for Rebuild: split L1 tail when update file exists.
    private static bool WriteSplitL1(FileStream videoFs, FileStream redumpFs, long l0Length, long l1Length,
        FileStream? updateFs)
    {
        if (updateFs != null)
        {
            var suSize = updateFs.Length;
            var l1Trimmed = l1Length - suSize - SectorSize;
            if (!WriteBytes(videoFs, redumpFs, l0Length, l1Trimmed)) return false;
            if (!WriteBytes(updateFs, redumpFs, 0, suSize)) return false;
            videoFs.Seek(-SectorSize, SeekOrigin.End);
            if (!WriteBytes(videoFs, redumpFs, -1, SectorSize)) return false;
        }
        else
        {
            if (!WriteBytes(videoFs, redumpFs, l0Length, l1Length)) return false;
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // Rebuild Redump — ported verbatim from XGD.RebuildRedump
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rebuilds a Redump ISO from its components. Mirrors <c>XGD.RebuildRedump</c>.
    /// </summary>
    /// <param name="xisoPath">Game partition XISO.</param>
    /// <param name="videoPath">Video partition (from <see cref="TryExtractVideo"/>).</param>
    /// <param name="fillerOrSeedPath">Optional filler file or 4-byte seed file (XGD1 PRNG). Pass null if unavailable.</param>
    /// <param name="updatePath">Optional XGD3 system-update file (<c>su20076000_00000000</c>).</param>
    /// <param name="outputRedumpPath">Destination Redump ISO.</param>
    /// <param name="securitySectorsPath">Optional path to <c>sectors.txt</c>; required when rebuilding from seed.</param>
    /// <param name="quiet">Suppress info output.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static bool RebuildRedump(
        string xisoPath,
        string videoPath,
        string? fillerOrSeedPath,
        string? updatePath,
        string outputRedumpPath,
        string? securitySectorsPath = null,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var videoLen = new FileInfo(videoPath).Length;
        var videoType = XgdTables.GetVideoTypeBySize(videoLen);
        // If videoType unknown (-1), try PVD path via redump-type heuristic? Fall back to size-based video type using file length directly if it matches VIDEO_LENGTH
        if (videoType < 0)
        {
            if (!quiet) Logger.LogErr($"[ERROR] Unexpected video partition size {videoLen}\n");
            return false;
        }

        var xisoType = XgdTables.GetXisoTypeFromVideo(videoType);
        var xisoLength = XgdTables.XisoLength[xisoType];
        var redumpLength = XgdTables.GetRedumpLength(videoType);
        var
            xgdType = xisoType; // XisoType maps 1:1 to XGD type for security-sector validation (Hybrid maps to 2, which follows XGD2 rule of 1 sector range)

        // Open XISO and validate
        using var isoFs = new FileStream(xisoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        // Validate magic via Verify-like check (optional but preserves XboxKit IsValidXISO)
        // We'll just check header at 0x10000

        {
            Span<byte> magic = stackalloc byte[Constants.HeaderDataLength];
            if (!TryReadAt(isoFs, Constants.HeaderOffset, magic) ||
                !magic.SequenceEqual(Encoding.ASCII.GetBytes(Constants.HeaderData)))
            {
                if (!quiet) Logger.LogErr($"[ERROR] Invalid XISO file: {xisoPath}\n");
                return false;
            }
        }
        isoFs.Seek(0, SeekOrigin.Begin);
        var isoSize = isoFs.Length;

        using var videoFs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        // Determine filler vs PRNG
        FileStream? fillerFs = null;
        XboxPrng? prng = null;
        int[] securitySectors = [];

        if (!string.IsNullOrEmpty(fillerOrSeedPath) && File.Exists(fillerOrSeedPath))
        {
            var fillerLen = new FileInfo(fillerOrSeedPath).Length;
            if (fillerLen == 4 && xisoType == 0)
            {
                // Treat as seed file
                using var seedFs = new FileStream(fillerOrSeedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> seedBuf = stackalloc byte[4];
                seedFs.ReadExactly(seedBuf);
                var seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(seedBuf);
                if (!quiet) Logger.Log($"[INFO] Using seed {seed:X8} from {fillerOrSeedPath}\n");
                prng = new XboxPrng(seed);
                // Need security sectors
                if (!string.IsNullOrEmpty(securitySectorsPath))
                {
                    securitySectors = SecuritySectors.ParseFile(securitySectorsPath, redumpLength, xgdType, quiet) ??
                                      [];
                }
                else if (File.Exists("sectors.txt"))
                {
                    securitySectors = SecuritySectors.ParseFile("sectors.txt", redumpLength, xgdType, quiet) ?? [];
                }
                else
                {
                    // Try default file in same dir as xiso
                    var candidate = Path.Combine(Path.GetDirectoryName(xisoPath) ?? "", "sectors.txt");
                    if (File.Exists(candidate))
                        securitySectors = SecuritySectors.ParseFile(candidate, redumpLength, xgdType, quiet) ?? [];
                }

                if (securitySectors.Length == 0 && xisoType == 0)
                {
                    Logger.LogErr(
                        "[ERROR] To rebuild from an initial seed, a list of security sector ranges is needed in sectors.txt\n");
                    return false;
                }
            }
            else if (fillerLen > 4)
            {
                fillerFs = new FileStream(fillerOrSeedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                if (!quiet) Logger.Log($"[INFO] Using filler {fillerOrSeedPath} ({fillerLen} bytes)\n");
                // If filler is trimmed (excludes security sectors), need sectors.txt too
                var expectedFiller = GetExpectedFillerSize(isoFs, xisoLength, quiet);
                // Rewind isoFS after helper moved it
                isoFs.Seek(0, SeekOrigin.Begin);
                if (fillerLen < expectedFiller)
                {
                    var secPath = securitySectorsPath ??
                                  Path.Combine(Path.GetDirectoryName(xisoPath) ?? "", "sectors.txt");
                    if (!File.Exists(secPath)) secPath = "sectors.txt";
                    if (File.Exists(secPath))
                        securitySectors = SecuritySectors.ParseFile(secPath, redumpLength, xgdType, quiet) ?? [];
                    if (securitySectors.Length == 0)
                    {
                        Logger.LogErr("[ERROR] Filler file excludes security sectors but sectors.txt missing\n");
                        fillerFs.Dispose();
                        return false;
                    }
                }
                else
                {
                    // Optionally load sectors if file exists anyway for zero-skip logic (PRNG path already)
                    var secPath = securitySectorsPath ?? "sectors.txt";
                    if (File.Exists(secPath))
                        securitySectors = SecuritySectors.ParseFile(secPath, redumpLength, xgdType, quiet) ?? [];
                }
            }
        }
        else
        {
            // No filler/seed — check if we can rebuild trimmed XISO? Only allowed if xisoType matches size exactly.
            if (isoSize != xisoLength && !quiet)
            {
                Logger.Log(
                    "[INFO] No filler data provided, using XISO only (may not match Redump if gaps were filler)\n");
            }
        }

        FileStream? updateFs = null;
        if (!string.IsNullOrEmpty(updatePath) && File.Exists(updatePath))
        {
            updateFs = new FileStream(updatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (!quiet) Logger.Log($"[INFO] Using system update {updatePath} ({updateFs.Length} bytes)\n");
        }

        using var redumpFs = new FileStream(outputRedumpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        bool result;
        try
        {
            result = RebuildRedumpInternal(isoFs, redumpFs, videoFs, fillerFs, updateFs, prng, securitySectors,
                videoType, quiet, cancellationToken);
        }
        finally
        {
            fillerFs?.Dispose();
            updateFs?.Dispose();
        }

        return result;
    }

    private static long GetExpectedFillerSize(FileStream isoFs, long xisoLength, bool quiet)
    {
        (var sys, var file) =
            XisoRanges.GetXisoRanges(isoFs, 0, quiet);
        var all = XisoRanges.MergeRanges(sys, file);
        long validBytes = 0;
        foreach ((var s, var e) in all) validBytes += (e - s + 1) * SectorSize;
        return xisoLength - validBytes;
    }

    private static bool RebuildRedumpInternal(
        FileStream isoFs, FileStream redumpFs, FileStream videoFs,
        FileStream? fillerFs, FileStream? updateFs, XboxPrng? prng,
        int[] securitySectors, int videoType, bool quiet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var xisoType = XgdTables.GetXisoTypeFromVideo(videoType);
        var xisoLength = XgdTables.XisoLength[xisoType];
        var xisoOffset = XgdTables.XisoOffset[xisoType];
        var redumpLength = XgdTables.GetRedumpLength(videoType);
        var l0Length = XgdTables.VideoL0Length[videoType];
        var l1Length = XgdTables.VideoL1Length[videoType];

        // Write L0
        if (!WriteBytes(videoFs, redumpFs, 0, l0Length)) return false;

        var l0Padding = xisoOffset - l0Length;
        if (l0Padding < 0) return false;
        WriteZeroes(redumpFs, -1, l0Padding);

        // Game partition
        var isoSize = isoFs.Length;
        isoFs.Seek(0, SeekOrigin.Begin);
        var writeFiller = fillerFs != null || prng != null;

        if (!writeFiller)
        {
            if (!quiet) Logger.Log("[INFO] No filler data provided, using XISO only\n");
            if (!WriteBytes(isoFs, redumpFs, -1, isoSize)) return false;
        }
        else
        {
            (var sysRanges, var fileRanges) =
                XisoRanges.GetXisoRanges(isoFs, 0, quiet);
            var ranges = XisoRanges.MergeRanges(sysRanges, fileRanges);
            if (!quiet)
            {
                foreach ((var start, var end) in ranges)
                    Logger.Log($"[INFO] XISO File Extent: {start}-{end}\n");
            }

            var xisoOffsetSector = xisoOffset / SectorSize;
            long currentByte = 0;
            isoFs.Seek(0, SeekOrigin.Begin);
            while (currentByte < xisoLength)
            {
                ct.ThrowIfCancellationRequested();
                var currentSector = (currentByte + SectorSize - 1) / SectorSize;
                long xisoBytes = 0;
                long fillerBytes = 0;

                // Security sector wipe pass
                if (prng != null || fillerFs != null)
                {
                    var wiped = false;
                    for (var i = 0; i < securitySectors.Length; i++)
                    {
                        if (currentSector + xisoOffsetSector == securitySectors[i])
                        {
                            if (!quiet)
                            {
                                Logger.Log(
                                    $"[INFO] Wiping security sectors {securitySectors[i]}-{securitySectors[i] + 4095}\n");
                            }

                            const long secBytes = 4096 * SectorSize;
                            WriteZeroes(redumpFs, -1, secBytes);
                            prng?.SimulateSectors(secBytes / SectorSize);
                            currentByte += secBytes;
                            isoFs.Seek(secBytes, SeekOrigin.Current);
                            wiped = true;
                            break;
                        }
                    }

                    if (wiped) continue;
                }

                if (ranges.Count > 0 && currentSector > ranges[^1].End)
                {
                    fillerBytes = xisoLength - currentByte;
                }
                else
                {
                    for (var i = 0; i < ranges.Count; i++)
                    {
                        if (currentSector >= ranges[i].Start && currentSector <= ranges[i].End)
                        {
                            xisoBytes = ((ranges[i].End + 1) * SectorSize) - currentByte;
                            break;
                        }
                        else if (currentSector < ranges[i].Start && (i == 0 || currentSector > ranges[i - 1].End))
                        {
                            fillerBytes = (ranges[i].Start * SectorSize) - currentByte;
                            break;
                        }
                    }
                }

                if (prng != null || fillerFs != null)
                {
                    for (var i = 0; i < securitySectors.Length; i++)
                    {
                        if (currentSector + xisoOffsetSector < securitySectors[i] + 4095)
                        {
                            if (currentSector + xisoOffsetSector + (fillerBytes / SectorSize) >= securitySectors[i])
                            {
                                fillerBytes = (securitySectors[i] - currentSector - xisoOffsetSector) * SectorSize;
                                break;
                            }
                            else if (currentSector + xisoOffsetSector + (xisoBytes / SectorSize) >= securitySectors[i])
                            {
                                xisoBytes = (securitySectors[i] - currentSector - xisoOffsetSector) * SectorSize;
                                break;
                            }
                        }
                    }
                }

                if (fillerBytes > 0)
                {
                    if (fillerBytes % SectorSize != 0) return false;
                    if (prng != null)
                        prng.WriteSectors(redumpFs, fillerBytes / SectorSize);
                    else if (fillerFs != null && !WriteBytes(fillerFs, redumpFs, -1, fillerBytes))
                        return false;
                    currentByte += fillerBytes;
                    isoFs.Seek(fillerBytes, SeekOrigin.Current);
                }
                else
                {
                    var bytesToWrite = xisoBytes > 0 ? xisoBytes : xisoLength - currentByte;
                    if (!WriteBytes(isoFs, redumpFs, -1, bytesToWrite)) return false;
                    currentByte += bytesToWrite;
                }
            }

            if (currentByte != xisoLength) return false;
        }

        // L1 padding
        var l1Padding = redumpLength - l1Length - (xisoOffset + xisoLength);
        WriteZeroes(redumpFs, -1, l1Padding);

        // L1
        return WriteSplitL1(videoFs, redumpFs, l0Length, l1Length, updateFs);
    }

    /// <summary>
    /// Tries to rebuild a Redump ISO by inferring video, filler, and update paths from additional files.
    /// </summary>
    /// <param name="additionalFiles">Candidate component files (video, filler, update).</param>
    /// <param name="xisoPath">Game partition XISO path.</param>
    /// <param name="outputRedumpPath">Destination Redump ISO path.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool TryRebuildFromArgs(string[] additionalFiles, string xisoPath, string outputRedumpPath,
        bool quiet = false)
    {
        // Attempt to infer video/filler/seed/update among additionalFiles by size/extension
        string? video = null, filler = null, update = null;
        foreach (var f in additionalFiles)
        {
            if (!File.Exists(f)) continue;
            var sz = new FileInfo(f).Length;
            var name = Path.GetFileName(f);
            if (video == null && (f.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase) ||
                                  XgdTables.GetVideoTypeBySize(sz) >= 0))
            {
                video = f;
            }
            else if (update == null && name.StartsWith("su20076000_00000000", StringComparison.OrdinalIgnoreCase))
            {
                update = f;
            }
            else if (filler == null && (f.EndsWith(".filler", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".seed", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".rc4", StringComparison.OrdinalIgnoreCase) || sz == 4))
            {
                filler = f;
            }
        }

        if (video == null)
        {
            if (!quiet) Logger.LogErr("[ERROR] No video partition file provided for rebuild\n");
            return false;
        }

        return RebuildRedump(xisoPath, video, filler, update, outputRedumpPath, null, quiet);
    }
}