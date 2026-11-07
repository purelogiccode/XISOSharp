using CCISharp;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// CCI ("CCIM", Cerbios Compressed Image) compression and extraction helpers.
/// Wraps <see cref="CciWriter"/> and <see cref="CciFile"/> so XISOSharp callers
/// do not need to reference CCISharp types directly.
/// </summary>
/// <remarks>
/// CCISharp levels <c>0..9</c> use the fast LZ4 block encoder; levels
/// <c>10..12</c> use the managed LZ4-HC encoder (12 is the highest effort and
/// corresponds to the reference encoder's HC preset). Output is fully
/// interoperable with libCCI/Repackinator, but HC byte parity is best-effort,
/// not a guarantee. Parallel compression with <c>jobs</c> &gt; 1 is
/// byte-identical to sequential output.
/// </remarks>
public static class XisoCci
{
    /// <summary>
    /// Default split point used by CCI output (<c>0xffbf6000</c>, ~4 GiB). This
    /// is the xdvdfs/CCISharp compression default, not a FATX-safe limit: it is
    /// below the 32-bit file-size field but above
    /// <see cref="Constants.FatxMaxFileSize"/>.
    /// </summary>
    public const long DefaultSplitBytes = CciFormat.DefaultSplitBytes;

    /// <summary>
    /// Compresses an image into one or more CCI parts.
    /// Plain ISO input is compressed directly; split ISO, CISO, CCI and GOD
    /// input is first materialized to a temporary ISO through the decompressed
    /// view.
    /// </summary>
    /// <param name="sourcePath">Source image, directory or GOD container (header or <c>.data</c> folder).</param>
    /// <param name="outputCciPath">
    /// Output path (<c>game.cci</c> or <c>game</c>). When <c>null</c>, a sibling
    /// path is derived with <see cref="DeriveDefaultCciPath"/>.
    /// </param>
    /// <param name="level">Compression level <c>0..12</c> (default <c>9</c>; <c>10..12</c> select LZ4-HC).</param>
    /// <param name="splitBytes">
    /// Split target in bytes; <c>0</c> writes a single part. Defaults to
    /// <see cref="DefaultSplitBytes"/>.
    /// </param>
    /// <param name="overwrite">Overwrite existing output files (default <c>false</c>).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>0</c> on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside <c>0..12</c>.</exception>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="NotSupportedException">The source is a ZArchive (convert it to ISO first).</exception>
    /// <exception cref="IOException">The output exists and <paramref name="overwrite"/> is <c>false</c>, or an I/O error occurred.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int CompressToCci(string sourcePath, string? outputCciPath = null, int level = 9,
        long splitBytes = DefaultSplitBytes, bool overwrite = false, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default) =>
        CompressToCciCore(sourcePath, outputCciPath, level, splitBytes, overwrite, jobs: 1, progress, ct);

    /// <summary>
    /// Compresses an image into one or more CCI parts with a bounded number of
    /// sector-compression workers. Output is byte-identical to the
    /// single-threaded overload for the same level and split settings.
    /// </summary>
    /// <param name="sourcePath">Source image, directory or GOD container (header or <c>.data</c> folder).</param>
    /// <param name="outputCciPath">
    /// Output path (<c>game.cci</c> or <c>game</c>). When <c>null</c>, a sibling
    /// path is derived with <see cref="DeriveDefaultCciPath"/>.
    /// </param>
    /// <param name="level">Compression level <c>0..12</c> (<c>10..12</c> select LZ4-HC).</param>
    /// <param name="splitBytes">Split target in bytes; <c>0</c> writes a single part.</param>
    /// <param name="overwrite">Overwrite existing output files.</param>
    /// <param name="jobs">Sector-compression workers, <c>1..64</c>. <c>1</c> is sequential.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>0</c> on success.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside <c>0..12</c> or <paramref name="jobs"/> is outside <c>1..64</c>.</exception>
    public static int CompressToCci(string sourcePath, string? outputCciPath, int level, long splitBytes,
        bool overwrite, int jobs, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        CompressToCciCore(sourcePath, outputCciPath, level, splitBytes, overwrite, jobs, progress, ct);

    private static int CompressToCciCore(string sourcePath, string? outputCciPath, int level, long splitBytes,
        bool overwrite, int jobs, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 12);
        ArgumentOutOfRangeException.ThrowIfNegative(splitBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(jobs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jobs, CciWriteOptions.MaxJobs);
        ct.ThrowIfCancellationRequested();

        // A GOD `.data` folder is a container, not a directory to pack.
        XisoImageSource.TryDetect(sourcePath, out XisoImageSourceInfo? sourceInfo);
        bool isGod = sourceInfo is not null && sourceInfo.Format == XisoImageFormat.God;
        if (!isGod && GodHeader.IsGodContainer(sourcePath))
        {
            throw new InvalidDataException(
                $"GOD container '{sourcePath}' is incomplete or has no readable data fragments.");
        }

        bool isDirectory = Directory.Exists(sourcePath) && !isGod;
        if (!isDirectory && !File.Exists(sourcePath) && !isGod)
        {
            throw new FileNotFoundException($"Source image not found: {sourcePath}", sourcePath);
        }

        string output = outputCciPath ?? DeriveDefaultCciPath(sourcePath, isDirectory);
        long effectiveSplit = splitBytes;

        string probe = ResolveOutputProbe(output, effectiveSplit > 0);
        if (sourceInfo is not null)
        {
            foreach (string part in sourceInfo.Parts)
            {
                if (XisoPaths.AreSamePath(part, probe))
                    throw new IOException("Source and destination paths are the same.");
            }
        }
        else if (!isDirectory && XisoPaths.AreSamePath(sourcePath, probe))
        {
            throw new IOException("Source and destination paths are the same.");
        }

        string? tempIso = null;
        try
        {
            string sourceFile = sourcePath;
            if (isDirectory)
            {
                tempIso = Path.Combine(Path.GetTempPath(), $"xisosh-temp-{Guid.NewGuid():N}.iso");
                int packed = XisoWriter.PackFromDirectory(sourcePath, tempIso, cancellationToken: ct);
                if (packed != 0)
                {
                    throw new IOException($"Failed to pack directory {sourcePath} to a temporary ISO.");
                }

                sourceFile = tempIso;
            }
            else if (sourceInfo is not null &&
                     (sourceInfo.Format != XisoImageFormat.Iso || sourceInfo.IsSplit))
            {
                if (sourceInfo.Format == XisoImageFormat.Zar)
                {
                    throw new NotSupportedException(
                        "ZArchive (.zar) input is not a sector image; convert it to ISO first.");
                }

                // Split ISO/CISO/CCI input: materialize the decompressed image so
                // CCISharp's path overload can split the output correctly.
                tempIso = Path.Combine(Path.GetTempPath(), $"xisosh-temp-{Guid.NewGuid():N}.iso");
                using (Stream source = XisoImageSource.OpenRead(sourcePath))
                using (FileStream destination = new(tempIso, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 1 << 20, FileOptions.SequentialScan))
                {
                    source.CopyTo(destination);
                }

                sourceFile = tempIso;
            }

            CciWriteOptions options = new()
            {
                Level = level,
                Jobs = jobs,
                SplitBytes = effectiveSplit,
                Overwrite = overwrite,
                CancellationToken = ct,
                Progress = progress is null ? null : new ProgressAdapter(progress),
            };

            CciError error = CciWriter.Compress(sourceFile, output, options);
            ThrowForError(error, output, ct);
            return 0;
        }
        finally
        {
            if (tempIso != null)
            {
                TryDelete(tempIso);
            }
        }
    }

    /// <summary>
    /// Extracts a CCI image to a raw ISO file. Split sets are stitched
    /// transparently.
    /// </summary>
    /// <param name="cciPath">Path to a <c>.cci</c> file or the first split part.</param>
    /// <param name="outputIsoPath">
    /// Destination ISO path; when <c>null</c>, derived with
    /// <see cref="DeriveDefaultIsoPath"/>.
    /// </param>
    /// <param name="overwrite">Overwrite an existing output file (default <c>false</c>).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>0</c> on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cciPath"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cciPath"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The CCI image was not found.</exception>
    /// <exception cref="IOException">The output exists and <paramref name="overwrite"/> is <c>false</c>, or an I/O error occurred.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int DecompressToIso(string cciPath, string? outputIsoPath = null, bool overwrite = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cciPath);
        ct.ThrowIfCancellationRequested();

        string output = outputIsoPath ?? DeriveDefaultIsoPath(cciPath);
        if (XisoPaths.AreSamePath(cciPath, output))
        {
            throw new IOException("Source and destination paths are the same.");
        }

        CciError error = CciFile.Open(
            cciPath,
            new CciReaderOptions { Share = FileShare.Read },
            out CciFile? file);

        if (error != CciError.None || file is null)
        {
            ThrowForError(error, cciPath, ct);
        }

        using (file)
        {
            CciExtractOptions options = new()
            {
                Overwrite = overwrite,
                Progress = progress is null ? null : new ProgressAdapter(progress),
                CancellationToken = ct,
            };

            error = file!.ExtractToIso(output, options);
            ThrowForError(error, output, ct);
        }

        return 0;
    }

    /// <summary>
    /// Derives the default CCI output path for a source image or directory:
    /// <c>game.iso</c>/<c>game.1.iso</c>/<c>game.cso</c> → <c>game.cci</c>.
    /// </summary>
    /// <param name="sourcePath">Source image or directory path.</param>
    /// <param name="isDirectory">Whether <paramref name="sourcePath"/> is a directory.</param>
    public static string DeriveDefaultCciPath(string sourcePath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        if (isDirectory)
        {
            string trimmed = XisoPaths.TrimTrailingSeparators(Path.GetFullPath(sourcePath));
            string parent = Path.GetDirectoryName(trimmed) ?? Directory.GetCurrentDirectory();
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "image";
            return Path.Combine(parent, name + CciFormat.Extension);
        }

        string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        string file = Path.GetFileName(sourcePath);
        string stem = XisoImageSource.HasImageSuffix(file)
            ? XisoImageSource.StripImageSuffix(file)
            : Path.GetFileNameWithoutExtension(file);

        return Path.Combine(directory, stem + CciFormat.Extension);
    }

    /// <summary>
    /// Derives the default ISO output path for a CCI image:
    /// <c>game.cci</c>/<c>game.1.cci</c> → <c>game.iso</c>.
    /// </summary>
    /// <param name="cciPath">CCI path.</param>
    public static string DeriveDefaultIsoPath(string cciPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cciPath);

        string directory = Path.GetDirectoryName(cciPath) ?? string.Empty;
        string file = Path.GetFileName(cciPath);
        string stem;
        if (file.EndsWith(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(CciNaming.StripPartSuffix(file));
        }
        else
        {
            stem = Path.GetFileNameWithoutExtension(file);
        }

        return Path.Combine(directory, stem + ".iso");
    }

    private static string ResolveOutputProbe(string output, bool split)
    {
        string file = output;
        if (!file.EndsWith(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            if (split)
            {
                // CCISharp treats an extension-less output as the part base.
                return $"{file}.1{CciFormat.Extension}";
            }

            return file + CciFormat.Extension;
        }

        if (!split) return file;

        string stem = file[..^CciFormat.Extension.Length];
        return $"{stem}.1{CciFormat.Extension}";
    }

    private static void ThrowForError(CciError error, string path, CancellationToken ct)
    {
        switch (error)
        {
            case CciError.None:
                return;
            case CciError.Cancelled:
                throw new OperationCanceledException("CCI operation cancelled.", ct);
            case CciError.AlreadyExists:
                throw new IOException($"Output already exists: {path}");
            case CciError.NotFound:
                throw new FileNotFoundException($"CCI image not found: {path}", path);
            case CciError.AccessDenied:
                throw new UnauthorizedAccessException($"Access denied: {path}");
            case CciError.InvalidArgument:
                throw new ArgumentException($"Invalid CCI argument for '{path}': {error.ToMessage()}");
            case CciError.InvalidHeader:
            case CciError.UnsupportedHeaderSize:
            case CciError.UnsupportedBlockSize:
            case CciError.UnsupportedVersion:
            case CciError.UnsupportedIndexAlignment:
            case CciError.InvalidSize:
            case CciError.InvalidIndex:
            case CciError.CorruptSector:
            case CciError.Truncated:
            case CciError.Lz4:
            case CciError.Range:
                throw new InvalidDataException($"Invalid CCI image '{path}': {error.ToMessage()}");
            case CciError.TooManyParts:
                throw new IOException($"CCI split would produce more than nine parts: {path}");
            default:
                throw new IOException($"CCI I/O error for '{path}': {error.ToMessage()}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>Maps CCISharp progress onto XISOSharp's <see cref="ProgressInfo"/> shape.</summary>
    private sealed class ProgressAdapter : IProgress<CciProgress>
    {
        private readonly IProgress<ProgressInfo> _inner;

        internal ProgressAdapter(IProgress<ProgressInfo> inner) => _inner = inner;

        public void Report(CciProgress value) =>
            _inner.Report(new ProgressInfo(
                ProgressInfoType.FileProgress,
                Count: value.TotalSectors * CciFormat.SectorSize,
                Path: null,
                Sector: value.SectorsDone,
                Size: value.BytesIn));
    }
}
