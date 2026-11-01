using CCISharp;
using XISOSharp.BlockDevice;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// CCI ("CCIM", Cerbios Compressed Image) compression and extraction helpers.
/// Wraps <see cref="CciWriter"/> and <see cref="CciFile"/> so XISOSharp callers
/// do not need to reference CCISharp types directly.
/// </summary>
/// <remarks>
/// The CCI encoder in CCISharp is a fast LZ4 block encoder: output is fully
/// interoperable with libCCI/Repackinator, but not byte-identical to their
/// LZ4-HC output. Levels <c>0..9</c> follow CCISharp's scale (<c>0</c> stores
/// every sector raw; <c>9</c> is the smallest fast-encoder output).
/// </remarks>
public static class XisoCci
{
    /// <summary>Default split point used by CCI output (~4 GiB, below the FATX per-file cap).</summary>
    public const long DefaultSplitBytes = CciFormat.DefaultSplitBytes;

    /// <summary>
    /// Compresses an image into one or more CCI parts.
    /// Plain ISO input is compressed directly; split ISO, CISO and CCI input is
    /// first materialized to a temporary ISO through the decompressed view.
    /// </summary>
    /// <param name="sourcePath">Source image or directory.</param>
    /// <param name="outputCciPath">
    /// Output path (<c>game.cci</c> or <c>game</c>). When <c>null</c>, a sibling
    /// path is derived with <see cref="DeriveDefaultCciPath"/>.
    /// </param>
    /// <param name="level">Compression level <c>0..9</c> (default <c>9</c>).</param>
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside <c>0..9</c>.</exception>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="NotSupportedException">The source is a ZArchive (convert it to ISO first).</exception>
    /// <exception cref="IOException">The output exists and <paramref name="overwrite"/> is <c>false</c>, or an I/O error occurred.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static int CompressToCci(string sourcePath, string? outputCciPath = null, int level = 9,
        long splitBytes = DefaultSplitBytes, bool overwrite = false, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(splitBytes);
        ct.ThrowIfCancellationRequested();

        bool isDirectory = Directory.Exists(sourcePath);
        if (!isDirectory && !File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source image not found: {sourcePath}", sourcePath);
        }

        string output = outputCciPath ?? DeriveDefaultCciPath(sourcePath, isDirectory);
        long effectiveSplit = splitBytes;

        string probe = ResolveOutputProbe(output, effectiveSplit > 0);
        if (!isDirectory && XisoPaths.AreSamePath(sourcePath, probe))
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
            else if (XisoImageSource.TryDetect(sourcePath, out XisoImageSourceInfo? info) &&
                     info is not null &&
                     (info.Format != XisoImageFormat.Iso || info.IsSplit))
            {
                if (info.Format == XisoImageFormat.Zar)
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
            stem = CciNaming.IsSplitPath(file)
                ? file[..^(CciFormat.Extension.Length + 2)]
                : file[..^CciFormat.Extension.Length];
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
                Count: (long)(value.TotalSectors * CciFormat.SectorSize),
                Path: null,
                Sector: (long)value.SectorsDone,
                Size: value.BytesIn));
    }
}
