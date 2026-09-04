using XISOSharp.Models;

namespace XISOSharp;

#pragma warning disable RCS1194 // Implement exception constructors — standard overloads are sufficient for modern .NET

/// <summary>
/// Per-file extraction failure carrying the full error context (TODO #9,
/// xdvdfs #187): which entry failed, where it lives in the image, where it
/// was going on disk, and the underlying cause as <see cref="Exception.InnerException"/>
/// — the BCL shape of xdvdfs's <c>Failed to create file X / Caused by</c> chain.
/// Thrown fail-fast by <c>ExtractFile</c>/<c>CopyOutFile</c>; collected in
/// <see cref="UnpackOptions.Failures"/> under <see cref="UnpackOptions.ContinueOnError"/>
/// and summarized as <see cref="ExtractError.ErrExtractFailed"/> at the end of the run.
/// </summary>
public sealed class ExtractFileException : ExtractErrorException
{
    /// <summary>Path of the entry inside the image (e.g. <c>game\sub\file.bin</c>).</summary>
    public string InternalPath { get; }

    /// <summary>Destination the extractor tried to write (CWD-relative in traverse mode).</summary>
    public string DestPath { get; }

    /// <summary>Partition-relative start sector of the entry's data.</summary>
    public uint StartSector { get; }

    /// <summary>Reported size of the entry in bytes.</summary>
    public long FileSize { get; }

    /// <summary>Bytes actually read before the failure (truncation); -1 when not a short read.</summary>
    public long BytesRead { get; }

    /// <summary>
    /// Creates a per-file extraction failure with full image/destination context.
    /// Prefer the <c>For*</c> factories, which phrase the detail line.
    /// </summary>
    public ExtractFileException(
        ExtractError code,
        string internalPath,
        string destPath,
        uint startSector,
        long fileSize,
        string detail,
        long bytesRead = -1,
        Exception? innerException = null)
        : base(code, FormatMessage(internalPath, destPath, startSector, fileSize, detail, bytesRead),
            innerException!)
    {
        InternalPath = internalPath;
        DestPath = destPath;
        StartSector = startSector;
        FileSize = fileSize;
        BytesRead = bytesRead;
    }

    private static string FormatMessage(string internalPath, string destPath, uint startSector, long fileSize,
        string detail, long bytesRead)
    {
        var where = $"\"{internalPath}\" (sector {startSector}, {fileSize} bytes) -> \"{destPath}\"";
        return bytesRead >= 0
            ? $"Failed to extract {where}: {detail} (read {bytesRead} of {fileSize} bytes)"
            : $"Failed to extract {where}: {detail}";
    }

    /// <summary>Destination create/open failed (the xdvdfs #187 shape: bad name, denied, missing drive).</summary>
    internal static ExtractFileException ForCreate(string internalPath, string destPath, uint startSector,
        long fileSize, Exception inner)
        => new(ExtractError.ErrFileWrite, internalPath, destPath, startSector, fileSize,
            $"could not create output file: {inner.Message}", innerException: inner);

    /// <summary>Destination directory could not be created; the subtree is skipped under continue-on-error.</summary>
    internal static ExtractFileException ForDirectory(string internalPath, string destPath, Exception inner)
        => new(ExtractError.ErrFileWrite, internalPath, destPath, 0, 0,
            $"could not create output directory: {inner.Message}", innerException: inner);

    /// <summary>Write failed mid-copy (disk full, device removed).</summary>
    internal static ExtractFileException ForWrite(string internalPath, string destPath, uint startSector,
        long fileSize, long bytesRead, Exception inner)
        => new(ExtractError.ErrFileWrite, internalPath, destPath, startSector, fileSize,
            $"write failed: {inner.Message}", bytesRead, inner);

    /// <summary>
    /// Image data ends before the reported size: truncated download, torn
    /// image, or an entry pointing past end of image.
    /// </summary>
    internal static ExtractFileException ForTruncated(string internalPath, string destPath, uint startSector,
        long fileSize, long bytesRead)
        => new(ExtractError.ErrFileTruncated, internalPath, destPath, startSector, fileSize,
            "image data ends before the reported file size", bytesRead);
}
#pragma warning restore RCS1194
