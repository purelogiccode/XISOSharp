namespace XISOSharp;

/// <summary>
/// XISO gap handling operations: filler extract, seed, wipe, trim.
/// Ported from <c>References/XboxKit-0.7/LibXGD/XDVDFS.cs:ProcessXISO</c> and <c>XboxPRNG</c>.
/// </summary>
public static class XisoOperations
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

    // -----------------------------------------------------------------------
    // Filler extraction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts random filler (gaps) from an XISO (or Redump game partition) to a separate file.
    /// </summary>
    public static bool ExtractFiller(string inputPath, string outputFillerPath, long isoOffset = 0,
        long? xisoLengthOverride = null, bool quiet = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var isoFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var xisoLength = xisoLengthOverride ?? isoFs.Length - isoOffset;
        // If isoOffset lies inside a Redump, clamp to that partition's declared length via tables if possible
        // Caller may pass exact length; otherwise use file remainder.
        return ExtractFiller(isoFs, isoOffset, xisoLength, outputFillerPath, quiet, cancellationToken);
    }

    /// <summary>
    /// Extracts filler from an open XISO stream to a file.
    /// </summary>
    /// <param name="isoFs">Open ISO stream containing the XISO partition.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <param name="xisoLength">Byte length of the XISO partition.</param>
    /// <param name="outputFillerPath">Destination path for the filler file.</param>
    /// <param name="quiet">When <c>true</c>, suppresses informational logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool ExtractFiller(FileStream isoFs, long isoOffset, long xisoLength, string outputFillerPath,
        bool quiet = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (var bones, var fileRanges) =
            XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
        var ranges = XisoRanges.MergeRanges(bones, fileRanges);
        if (!quiet)
        {
            foreach ((var s, var e) in ranges)
                Logger.Log($"[INFO] XISO File Extent: {s}-{e}\n");
        }

        using var fillerFs = new FileStream(outputFillerPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        isoFs.Seek(isoOffset, SeekOrigin.Begin);
        return ProcessExtractFiller(isoFs, isoOffset, xisoLength, ranges, fillerFs, cancellationToken);
    }

    private static bool ProcessExtractFiller(FileStream isoFs, long isoOffset, long xisoLength,
        List<(uint Start, uint End)> ranges, FileStream fillerFs, CancellationToken ct)
    {
        long numBytes = 0;
        isoFs.Seek(isoOffset, SeekOrigin.Begin);
        while (numBytes < xisoLength)
        {
            ct.ThrowIfCancellationRequested();
            var currentByte = isoOffset + numBytes;
            var currentSector = (currentByte + SectorSize - 1) / SectorSize;
            long bytesToWipe = 0;
            long bytesUntilEndOfExtent = 0;

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
                        break;
                    }
                    else if (currentSector < ranges[i].Start && (i == 0 || currentSector > ranges[i - 1].End))
                    {
                        bytesToWipe = (ranges[i].Start * SectorSize) - currentByte;
                        break;
                    }
                }
            }

            if (bytesToWipe > 0)
            {
                if (!WriteBytes(isoFs, fillerFs, -1, bytesToWipe)) return false;
                numBytes += bytesToWipe;
            }
            else
            {
                var skip = bytesUntilEndOfExtent > 0 ? bytesUntilEndOfExtent : xisoLength - numBytes;
                isoFs.Seek(skip, SeekOrigin.Current);
                numBytes += skip;
            }
        }

        return numBytes == xisoLength;
    }

    // -----------------------------------------------------------------------
    // Seed extraction (XGD1 only)
    // -----------------------------------------------------------------------

    /// <summary>Extracts the XGD1 PRNG seed from an XISO image.</summary>
    /// <param name="inputPath">Path to the XISO or Redump ISO.</param>
    /// <param name="xisoOffset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses informational logging.</param>
    /// <returns>The seed value, or <c>null</c> if not an XGD1 image or extraction fails.</returns>
    public static uint? ExtractSeed(string inputPath, long xisoOffset = 0, bool quiet = false)
    {
        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return XboxPrng.ExtractSeed(fs, xisoOffset, quiet);
    }

    /// <summary>Tries to extract the XGD1 seed and write it as a 4-byte little-endian file.</summary>
    /// <param name="inputPath">Source XISO path.</param>
    /// <param name="outputSeedPath">Destination path for the 4-byte seed file.</param>
    /// <param name="xisoOffset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <returns><c>true</c> if the seed was extracted and written; otherwise <c>false</c>.</returns>
    public static bool TryExtractSeed(string inputPath, string outputSeedPath, long xisoOffset = 0, bool quiet = false)
    {
        var seed = ExtractSeed(inputPath, xisoOffset, quiet);
        if (!seed.HasValue) return false;
        using var seedFs = new FileStream(outputSeedPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> seedBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed.Value);
        seedFs.Write(seedBytes);
        if (!quiet) Logger.Log($"[INFO] Filler data seed: {seed.Value:X8} -> {outputSeedPath}\n");
        return true;
    }

    // -----------------------------------------------------------------------
    // Wipe filler (zero gaps)
    // -----------------------------------------------------------------------

    /// <summary>Wipes filler gaps by zeroing them in a new output file.</summary>
    /// <param name="inputPath">Source XISO path.</param>
    /// <param name="outputPath">Destination path for the wiped image.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool WipeFiller(string inputPath, string outputPath, long isoOffset = 0, bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (XisoPaths.AreSamePath(inputPath, outputPath))
            throw new IOException($"Output '{outputPath}' must not overwrite its input '{inputPath}'");
        using var isoFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var isoSize = isoFs.Length;
        var xisoLength = isoSize - isoOffset;
        // If input is standalone XISO, isoOffset is 0; for Redump game partition, caller passes offset.
        (var bones, var fileRanges) =
            XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
        var ranges = XisoRanges.MergeRanges(bones, fileRanges);

        var totalLength = xisoLength;
        // Detect Redump XISO length truncation: if unknown size, use file size; else use declared length for completeness.
        // For XISO inputs, keep file size as length.

        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        return ProcessWipe(isoFs, isoOffset, totalLength, ranges, outFs, cancellationToken);
    }

    private static bool ProcessWipe(FileStream isoFs, long isoOffset, long xisoLength,
        List<(uint Start, uint End)> ranges, FileStream outFs, CancellationToken ct)
    {
        long numBytes = 0;
        isoFs.Seek(isoOffset, SeekOrigin.Begin);
        while (numBytes < xisoLength)
        {
            ct.ThrowIfCancellationRequested();
            var currentByte = isoOffset + numBytes;
            var currentSector = (currentByte + SectorSize - 1) / SectorSize;
            long bytesUntilEndOfExtent = 0;
            long bytesToWipe = 0;

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
                        break;
                    }
                    else if (currentSector < ranges[i].Start && (i == 0 || currentSector > ranges[i - 1].End))
                    {
                        bytesToWipe = (ranges[i].Start * SectorSize) - currentByte;
                        break;
                    }
                }
            }

            if (bytesToWipe > 0)
            {
                if (bytesToWipe % SectorSize != 0) return false;
                WriteZeroes(outFs, -1, bytesToWipe);
                numBytes += bytesToWipe;
                isoFs.Seek(bytesToWipe, SeekOrigin.Current);
            }
            else
            {
                long bytesToRead;
                if (bytesToWipe > 0) bytesToRead = bytesToWipe;
                else if (bytesUntilEndOfExtent > 0) bytesToRead = bytesUntilEndOfExtent;
                else bytesToRead = xisoLength - numBytes;

                // Check if skeleton vs bone distinction needed: for wipe we always copy bones too.
                if (!WriteBytes(isoFs, outFs, -1, bytesToRead)) return false;
                numBytes += bytesToRead;
            }
        }

        return numBytes == xisoLength;
    }

    // -----------------------------------------------------------------------
    // Trim (truncate after last extent)
    // -----------------------------------------------------------------------

    /// <summary>Trims an XISO to the last used sector, optionally writing to a new file.</summary>
    /// <param name="inputPath">Source XISO path.</param>
    /// <param name="outputPath">Destination path, or <c>null</c> to trim in place.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition inside the file.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool TrimXiso(string inputPath, string? outputPath, long isoOffset = 0, bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outPath = outputPath ?? inputPath; // if same, we'll truncate in place via temp
        var inPlace = string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outPath),
            StringComparison.OrdinalIgnoreCase);

        if (inPlace)
        {
            // Trim in place: compute trimmed length and SetLength
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536);
            (var bones, var fileRanges) =
                XisoRanges.GetXisoRanges(fs, isoOffset, quiet);
            var ranges = XisoRanges.MergeRanges(bones, fileRanges);
            if (ranges.Count == 0) return false;
            var trimmedLen = ((long)ranges[^1].End + 1) * SectorSize;
            // For standalone XISO, trimmedLen is within xiso partition; account for isoOffset
            var totalTrimmed = isoOffset + trimmedLen;
            if (!quiet) Logger.Log($"[INFO] Trimming XISO to {trimmedLen} bytes (partition) / {totalTrimmed} total\n");
            if (totalTrimmed < fs.Length)
                fs.SetLength(totalTrimmed);
            return true;
        }
        else
        {
            using var isoFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            (var bones, var fileRanges) =
                XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
            var ranges = XisoRanges.MergeRanges(bones, fileRanges);
            if (ranges.Count == 0) return false;
            var trimmedLen = ((long)ranges[^1].End + 1) * SectorSize;
            // Need total bytes to copy: isoOffset + trimmedLen, but if isoOffset>0, copy prefix too.
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            isoFs.Seek(0, SeekOrigin.Begin);
            var toCopy = isoOffset + trimmedLen;
            if (!WriteBytes(isoFs, outFs, -1, toCopy)) return false;
            if (!quiet) Logger.Log($"[INFO] Trimmed XISO written to {outPath} ({toCopy} bytes)\n");
            return true;
        }
    }

    /// <summary>
    /// Combined trim+wipe operation for --best alias: wipe filler then trim in one copy,
    /// avoiding double I/O. If outputPath is null, overwrites via temp.
    /// </summary>
    public static bool WipeAndTrim(string inputPath, string outputPath, long isoOffset = 0, bool quiet = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (XisoPaths.AreSamePath(inputPath, outputPath))
            throw new IOException($"Output '{outputPath}' must not overwrite its input '{inputPath}'");
        using var isoFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        (var bones, var fileRanges) =
            XisoRanges.GetXisoRanges(isoFs, isoOffset, quiet);
        var ranges = XisoRanges.MergeRanges(bones, fileRanges);
        if (ranges.Count == 0) return false;
        var trimmedLen = ((long)ranges[^1].End + 1) * SectorSize;
        var xisoLength = trimmedLen; // we cap at trimmed length

        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        // Write isoOffset prefix (if Redump) as-is? For standalone XISO, isoOffset 0.
        if (isoOffset > 0)
        {
            isoFs.Seek(0, SeekOrigin.Begin);
            if (!WriteBytes(isoFs, outFs, -1, isoOffset)) return false;
        }

        isoFs.Seek(isoOffset, SeekOrigin.Begin);
        long numBytes = 0;
        while (numBytes < xisoLength)
        {
            ct.ThrowIfCancellationRequested();
            var currentByte = isoOffset + numBytes;
            var currentSector = (currentByte + SectorSize - 1) / SectorSize;
            long bytesToWipe = 0, bytesUntilEnd = 0;
            if (currentSector > ranges[^1].End)
            {
                bytesToWipe = xisoLength - numBytes;
            }
            else
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    if (currentSector >= ranges[i].Start && currentSector <= ranges[i].End)
                    {
                        bytesUntilEnd = ((ranges[i].End + 1) * SectorSize) - currentByte;
                        break;
                    }
                    else if (currentSector < ranges[i].Start && (i == 0 || currentSector > ranges[i - 1].End))
                    {
                        bytesToWipe = (ranges[i].Start * SectorSize) - currentByte;
                        break;
                    }
                }
            }

            if (bytesToWipe > 0)
            {
                WriteZeroes(outFs, -1, bytesToWipe);
                numBytes += bytesToWipe;
                isoFs.Seek(bytesToWipe, SeekOrigin.Current);
            }
            else
            {
                var toRead = bytesUntilEnd > 0 ? bytesUntilEnd : xisoLength - numBytes;
                if (!WriteBytes(isoFs, outFs, -1, toRead)) return false;
                numBytes += toRead;
            }
        }

        return true;
    }
}