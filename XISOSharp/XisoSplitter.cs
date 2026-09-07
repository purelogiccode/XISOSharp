using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Splits plain XISO images into FATX-friendly parts and reassembles them
/// (TODO #17, xdvdfs #97). The CSO compressor already splits its output
/// (<see cref="CisoWriter"/>, <c>ciso::split</c> parity); this is the
/// plain-<c>.iso</c> counterpart, a managed equivalent of <c>split</c>/<c>copy /b</c>
/// with XISO awareness: inputs are pre-validated, cut points are sector-aligned,
/// and parts follow the <c>&lt;base&gt;.1.iso</c>, <c>&lt;base&gt;.2.iso</c>, …
/// naming (mirroring <c>CisoSplitFile.PartPath</c>, which uses <c>.cso</c>).
/// </summary>
/// <remarks>
/// Splitting is a pure byte partition — every part holds a contiguous global
/// byte range, so concatenation restores the image bit-for-bit. <see cref="Join"/>
/// additionally probes the reassembled output with
/// <see cref="XisoReader.GetVolumeInfo(string)"/> and rejects it when the volume no
/// longer parses (truncated/corrupt part sets); byte-level integrity across
/// machines should still be confirmed with <c>checksum</c>/<c>validate</c>.
/// Instances are not needed (all static); every call opens and closes its
/// files, so concurrent splits/joins of different images are safe.
/// </remarks>
public static class XisoSplitter
{
    /// <summary>
    /// Default maximum part size: the FATX 4 GiB file cap
    /// (<c>0x100000000</c>, already a multiple of the 2048-byte sector).
    /// </summary>
    public const long DefaultPartSizeBytes = 4294967296L;

    /// <summary>Length of the <c>.1.iso</c> part suffix, for base-name recovery.</summary>
    private const int PartSuffixLength = 6;

    /// <summary>
    /// Builds the path of split part <paramref name="partIndex"/> (0-based) for
    /// an output base path: <c>game</c> → <c>game.1.iso</c>,
    /// <c>game.iso</c> → <c>game.1.iso</c>, <c>a.b.iso</c> → <c>a.b.1.iso</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The base path is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The part index is negative.</exception>
    public static string PartPath(string outputBase, int partIndex)
    {
        if (string.IsNullOrEmpty(outputBase))
            throw new ArgumentException("Output base path must not be empty.", nameof(outputBase));
        if (partIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(partIndex), "Part index must not be negative.");
        return Path.ChangeExtension(outputBase, $"{partIndex + 1}.iso");
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> refers to the first part of a
    /// split image (<c>*.1.iso</c>, case-insensitive).
    /// </summary>
    public static bool IsSplitPath(string? path)
    {
        return path != null && path.EndsWith(".1.iso", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits an XISO image into parts of at most
    /// <paramref name="partSizeBytes"/> bytes. The effective part size rounds
    /// down to a 2048-byte sector multiple so parts concatenate losslessly.
    /// An image that fits produces a single <c>.1.iso</c> part. Existing part
    /// paths are refused (no silent overwrites); parts created by a failed or
    /// cancelled call are removed again.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c>).</param>
    /// <param name="outputBase">Base path for the parts (see <see cref="PartPath"/>).</param>
    /// <param name="partSizeBytes">Maximum part size in bytes (≥ one sector).</param>
    /// <param name="cancellationToken">Cancels the copy (partial parts removed).</param>
    /// <param name="progress">
    /// Optional <see cref="ProgressInfoType.FileProgress"/> reports
    /// (<c>Count</c> = image bytes, <c>Size</c> = bytes written so far,
    /// <c>Path</c> = current part path).
    /// </param>
    /// <returns>Paths of the parts written, in join order.</returns>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The part size is below one sector.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="IOException">A part path already exists, or on read/write errors.</exception>
    public static IReadOnlyList<string> Split(string isoPath, string outputBase, long partSizeBytes,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null)
    {
        if (string.IsNullOrEmpty(outputBase))
            throw new ArgumentException("Output base path must not be empty.", nameof(outputBase));
        var length = ValidateImage(isoPath);
        if (partSizeBytes < Constants.SectorSize)
            throw new ArgumentOutOfRangeException(nameof(partSizeBytes),
                $"Part size must be at least one sector ({Constants.SectorSize} bytes).");
        var aligned = (partSizeBytes / Constants.SectorSize) * Constants.SectorSize;
        return SplitCore(isoPath, outputBase, aligned, length, cancellationToken, progress);
    }

    /// <summary>
    /// Splits an XISO image into two sector-aligned halves (first half rounds
    /// up). Degenerate inputs that admit no aligned cut produce a single
    /// <c>.1.iso</c> part. Same validation, overwrite, and cleanup rules as
    /// <see cref="Split"/>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c>).</param>
    /// <param name="outputBase">Base path for the parts (see <see cref="PartPath"/>).</param>
    /// <param name="cancellationToken">Cancels the copy (partial parts removed).</param>
    /// <param name="progress">
    /// Optional <see cref="ProgressInfoType.FileProgress"/> reports
    /// (<c>Count</c> = image bytes, <c>Size</c> = bytes written so far,
    /// <c>Path</c> = current part path).
    /// </param>
    /// <returns>Paths of the parts written, in join order.</returns>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="IOException">A part path already exists, or on read/write errors.</exception>
    public static IReadOnlyList<string> SplitHalves(string isoPath, string outputBase,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null)
    {
        if (string.IsNullOrEmpty(outputBase))
            throw new ArgumentException("Output base path must not be empty.", nameof(outputBase));
        var length = ValidateImage(isoPath);
        var cut = ((length + 1) / 2 + Constants.SectorSize - 1) / Constants.SectorSize * Constants.SectorSize;
        if (cut <= 0 || cut >= length)
            cut = length;
        return SplitCore(isoPath, outputBase, cut, length, cancellationToken, progress);
    }

    /// <summary>
    /// Reassembles a split image: concatenates <paramref name="firstPartPath"/>
    /// (<c>*.1.iso</c>) with every sequentially numbered sibling
    /// (<c>.2.iso</c>, <c>.3.iso</c>, …; the first gap ends the set) into
    /// <paramref name="outputPath"/>. The output must not exist and must not
    /// be one of the parts; after concatenation it must parse as an XISO.
    /// </summary>
    /// <param name="firstPartPath">Path of the first split part (<c>*.1.iso</c>).</param>
    /// <param name="outputPath">Destination for the reassembled image.</param>
    /// <param name="cancellationToken">Cancels the copy (partial output removed).</param>
    /// <param name="progress">
    /// Optional <see cref="ProgressInfoType.FileProgress"/> reports
    /// (<c>Count</c> = total part bytes, <c>Size</c> = bytes written so far,
    /// <c>Path</c> = <paramref name="outputPath"/>).
    /// </param>
    /// <returns><paramref name="outputPath"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A path is null or empty, the first part is not a <c>*.1.iso</c> path,
    /// or the output is one of the parts.
    /// </exception>
    /// <exception cref="FileNotFoundException">The first part does not exist.</exception>
    /// <exception cref="IOException">The output already exists, or on read/write errors.</exception>
    /// <exception cref="XisoFormatException">The reassembled output is not a valid XISO.</exception>
    public static string Join(string firstPartPath, string outputPath,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null)
    {
        if (string.IsNullOrEmpty(firstPartPath))
            throw new ArgumentException("First part path must not be empty.", nameof(firstPartPath));
        if (string.IsNullOrEmpty(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));
        if (!IsSplitPath(firstPartPath))
            throw new ArgumentException(
                $"Join expects the first split part (*.1.iso): {firstPartPath}", nameof(firstPartPath));
        if (!File.Exists(firstPartPath))
            throw new FileNotFoundException($"Split part not found: {firstPartPath}", firstPartPath);

        var baseName = firstPartPath[..^PartSuffixLength];
        var parts = new List<string> { firstPartPath };
        for (var i = 1;; i++)
        {
            var next = PartPath(baseName, i);
            if (!File.Exists(next))
                break;
            parts.Add(next);
        }

        var outputFull = Path.GetFullPath(outputPath);
        foreach (var part in parts)
        {
            if (string.Equals(Path.GetFullPath(part), outputFull, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Output must not be one of the parts: {outputPath}",
                    nameof(outputPath));
        }

        if (File.Exists(outputPath))
            throw new IOException($"Output already exists: {outputPath}");

        cancellationToken.ThrowIfCancellationRequested();
        var total = parts.Sum(p => new FileInfo(p).Length);
        var created = false;
        try
        {
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 65536))
            {
                created = true;
                long copied = 0;
                foreach (var part in parts)
                {
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 65536);
                    var bytesToCopy = input.Length;
                    var baseCopied = copied;
                    var justCopied = XisoFileCopier.CopyExact(input, bytesToCopy,
                        // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
                        (buf, count) => output.Write(buf, 0, count),
                        buffer: null,
                        done => progress?.Report(new ProgressInfo(ProgressInfoType.FileProgress,
                            Count: total, Path: outputPath, Size: baseCopied + done)),
                        cancellationToken);
                    copied = baseCopied + justCopied;
                }
            }

            var volume = XisoReader.GetVolumeInfo(outputPath);
            if (!volume.IsValid)
                throw new XisoFormatException(
                    $"Joined output is not a valid XISO (missing or corrupt parts?): {outputPath}");
            return outputPath;
        }
        catch
        {
            if (created)
            {
                try
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                }
                catch
                {
                    // Best-effort cleanup; the original error takes precedence.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Validates the image path shared by the split entries: path
    /// presence plus a <see cref="XisoReader.GetVolumeInfo(string)"/> probe. Returns
    /// the image length in bytes. Callers validate <c>outputBase</c> themselves.
    /// </summary>
    /// <exception cref="ArgumentException">The image path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    private static long ValidateImage(string isoPath)
    {
        if (string.IsNullOrEmpty(isoPath))
            throw new ArgumentException("Image path must not be empty.", nameof(isoPath));
        if (!File.Exists(isoPath))
            throw new FileNotFoundException($"Image not found: {isoPath}", isoPath);
        var volume = XisoReader.GetVolumeInfo(isoPath);
        if (!volume.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");
        return new FileInfo(isoPath).Length;
    }

    /// <summary>
    /// Copies <paramref name="length"/> image bytes into sector-aligned parts
    /// of at most <paramref name="alignedPartSize"/> bytes (already a sector
    /// multiple). Part paths are claimed up front so an existing part aborts
    /// before anything is written.
    /// </summary>
    private static IReadOnlyList<string> SplitCore(string isoPath, string outputBase, long alignedPartSize,
        long length, CancellationToken cancellationToken, IProgress<ProgressInfo>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Overflow-safe ceil(length / alignedPartSize); length is positive here
        // (ValidateImage rejects non-images, including empty files).
        var partCount = (int)((length - 1) / alignedPartSize + 1);
        var parts = new List<string>(partCount);
        for (var i = 0; i < partCount; i++)
        {
            var part = PartPath(outputBase, i);
            if (File.Exists(part))
                throw new IOException($"Split part already exists: {part}");
            parts.Add(part);
        }

        var created = new List<string>(partCount);
        try
        {
            using var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            long copied = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var partPath = parts[i];
                using var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536);
                created.Add(partPath);
                var remaining = Math.Min(alignedPartSize, length - input.Position);
                var baseCopied = copied;
                var justCopied = XisoFileCopier.CopyExact(input, remaining,
                    // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
                    (buf, count) => output.Write(buf, 0, count),
                    buffer: null,
                    done => progress?.Report(new ProgressInfo(ProgressInfoType.FileProgress,
                        Count: length, Path: partPath, Size: baseCopied + done)),
                    cancellationToken);
                copied = baseCopied + justCopied;
            }

            return parts;
        }
        catch
        {
            foreach (var part in created)
            {
                try
                {
                    if (File.Exists(part)) File.Delete(part);
                }
                catch
                {
                    // Best-effort cleanup; the original error takes precedence.
                }
            }

            throw;
        }
    }
}
