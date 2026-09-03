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
    long TotalSectors);

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