namespace XISOSharp.Models;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Metadata about an XISO volume descriptor (sector 32 on the disc).
/// </summary>
/// <param name="IsValid">Whether the volume magic is valid.</param>
/// <param name="RootDirSector">Sector index of the root directory table.</param>
/// <param name="RootDirSize">Size of the root directory table in bytes.</param>
/// <param name="DiscLseek">Disc lseek offset detected during probing.</param>
/// <param name="FileLength">Total size of the ISO file in bytes.</param>
/// <param name="TotalSectors">Total number of sectors in the ISO.</param>
public record VolumeInfo(
    bool IsValid,
    uint RootDirSector,
    uint RootDirSize,
    long DiscLseek,
    long FileLength,
    long TotalSectors)
{
    /// <summary>
    /// Volume descriptor FILETIME as a timestamp (UTC). <c>null</c> when the
    /// volume is invalid; raw 0 maps to 1601-01-01. Agrees with
    /// <see cref="XisoReader.GetFileTime(string, int?)"/> for the same image.
    /// </summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>
    /// Raw FILETIME field as stored in the descriptor (0 = 1601-01-01).
    /// Populated by <see cref="XisoReader.GetVolumeInfo(string)"/> from the same
    /// descriptor read that probes validity.
    /// </summary>
    public ulong FileTimeRaw { get; init; }

    /// <summary>
    /// Sector index the volume descriptor was found at, partition-relative
    /// (32 normally, 0 for sector-0/rebuilt images). <c>-1</c> when invalid.
    /// </summary>
    public int DescriptorSector { get; init; } = -1;

    /// <summary>
    /// Friendly name of the detected disc layout derived from
    /// <see cref="DiscLseek"/>: <c>RAW</c> (plain, offset 0), <c>GLOBAL (XGD2)</c>,
    /// <c>XGD3</c>, <c>XGD2 Hybrid</c>, or <c>XGD1</c>. Returns <c>Unknown</c> when
    /// the volume is invalid or the offset matches no known layout.
    /// </summary>
    public string DiscFormat => !IsValid
        ? "Unknown"
        : DiscLseek switch
        {
            0 => "RAW",
            Constants.GlobalLseekOffset => "GLOBAL (XGD2)",
            Constants.Xgd3LseekOffset => "XGD3",
            Constants.Xgd2HybridLseekOffset => "XGD2 Hybrid",
            Constants.Xgd1LseekOffset => "XGD1",
            _ => "Unknown",
        };
}

/// <summary>
/// Metadata about a single directory entry within an XISO image.
/// </summary>
/// <param name="Name">Filename of the entry.</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
/// <param name="StartSector">Sector index where the entry's data begins.</param>
/// <param name="FileSize">Size of the file data in bytes (0 for directories).</param>
/// <param name="Attributes">Raw attribute byte (see <see cref="Constants"/> for flag definitions).</param>
/// <param name="LeftChildOffset">Left child offset in the directory tree (0 if none).</param>
/// <param name="RightChildOffset">Right child offset in the directory tree (0 if none).</param>
public record EntryInfo(
    string Name,
    bool IsDirectory,
    uint StartSector,
    uint FileSize,
    byte Attributes,
    ushort LeftChildOffset,
    ushort RightChildOffset);

/// <summary>
/// A single file's (or directory table's) on-disk extent: the sector range its
/// bytes occupy. All sectors are partition-relative — the same numbering as
/// <see cref="EntryInfo.StartSector"/> and <see cref="VolumeInfo.RootDirSector"/>
/// (add <see cref="VolumeInfo.DiscLseek"/>/<c>2048</c> for the file-absolute sector).
/// </summary>
/// <param name="Path">Image-internal path with forward slashes (<c>"/file"</c>, <c>"/sub/nested"</c>).</param>
/// <param name="IsDirectory">Whether this extent is a directory's table region rather than file data.</param>
/// <param name="StartSector">Partition-relative first sector of the extent.</param>
/// <param name="SectorCount">Sectors occupied (<c>ceil(FileSize / 2048)</c>); 0 for empty files.</param>
/// <param name="FileSize">Byte size (table byte size for directories).</param>
public record FileSectorExtent(
    string Path,
    bool IsDirectory,
    uint StartSector,
    uint SectorCount,
    uint FileSize);

/// <summary>
/// A contiguous run of partition-relative sectors.
/// </summary>
/// <param name="StartSector">First sector of the run.</param>
/// <param name="SectorCount">Number of sectors in the run.</param>
public record SectorRange(
    uint StartSector,
    uint SectorCount);

/// <summary>
/// Explicit sector layout of an XISO image (xdvdfs #49): every file and directory
/// table mapped to its sector range, plus the merged allocated ranges and the free
/// gaps between them. Low-level disk analysis/debugging view; the free ranges are
/// the allocator input for in-place patching (TODO #5).
/// </summary>
/// <param name="Volume">Volume descriptor metadata.</param>
/// <param name="Entries">Per-file and per-directory extents, sorted by start sector.</param>
/// <param name="UsedRanges">Merged allocated ranges (volume header + tables + file data).</param>
/// <param name="FreeRanges">Unallocated gaps tiling <c>[0, TotalSectors)</c> with <paramref name="UsedRanges"/>.</param>
/// <param name="TotalSectors">Partition sector count (<c>(FileLength - DiscLseek) / 2048</c>).</param>
public record SectorLayout(
    VolumeInfo Volume,
    IReadOnlyList<FileSectorExtent> Entries,
    IReadOnlyList<SectorRange> UsedRanges,
    IReadOnlyList<SectorRange> FreeRanges,
    long TotalSectors);

/// <summary>
/// Result of a deep integrity audit of an XISO image.
/// </summary>
/// <param name="IsValid">Whether the image passed all checks.</param>
/// <param name="FilesChecked">Number of file entries audited.</param>
/// <param name="DirsChecked">Number of directory entries audited.</param>
/// <param name="Issues">List of human-readable issues found during the audit.</param>
public record AuditResult(
    bool IsValid,
    int FilesChecked,
    int DirsChecked,
    IReadOnlyList<string> Issues);

/// <summary>
/// Type of validation issue found during conversion validation.
/// </summary>
public enum ValidationIssueType
{
    /// <summary>A file exists in the source but not in the output.</summary>
    MissingInOutput,

    /// <summary>A file exists in the output but not in the source.</summary>
    ExtraInOutput,

    /// <summary>File sizes differ between source and output.</summary>
    SizeMismatch,

    /// <summary>File checksums differ between source and output.</summary>
    ChecksumMismatch
}

/// <summary>
/// A single validation issue found during conversion comparison.
/// </summary>
/// <param name="Type">The type of issue.</param>
/// <param name="Path">The file path (XISO internal path with forward slashes).</param>
/// <param name="SourceSize">Size in the source ISO (0 if missing in source).</param>
/// <param name="OutputSize">Size in the output ISO (0 if missing in output).</param>
/// <param name="SourceHash">SHA-256 hash in the source (null if checksums not computed).</param>
/// <param name="OutputHash">SHA-256 hash in the output (null if checksums not computed).</param>
public record ValidationIssue(
    ValidationIssueType Type,
    string Path,
    long SourceSize,
    long OutputSize,
    byte[]? SourceHash,
    byte[]? OutputHash);

/// <summary>
/// Result of a post-conversion validation comparing source and output XISO images.
/// </summary>
/// <param name="Passed">Whether validation passed with no issues.</param>
/// <param name="SourceFileCount">Total files in the source image.</param>
/// <param name="OutputFileCount">Total files in the output image.</param>
/// <param name="SourceDirCount">Total directories in the source image.</param>
/// <param name="OutputDirCount">Total directories in the output image.</param>
/// <param name="SourceTotalBytes">Total file data bytes in the source.</param>
/// <param name="OutputTotalBytes">Total file data bytes in the output.</param>
/// <param name="Issues">List of validation issues found.</param>
public record ValidationResult(
    bool Passed,
    int SourceFileCount,
    int OutputFileCount,
    int SourceDirCount,
    int OutputDirCount,
    long SourceTotalBytes,
    long OutputTotalBytes,
    IReadOnlyList<ValidationIssue> Issues);
