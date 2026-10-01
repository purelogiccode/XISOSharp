using System.Text.Json.Serialization;

namespace XISOSharp.Models;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Represents a single file or directory entry collected from an XISO image for validation.
/// </summary>
/// <param name="Path">Internal path with forward slashes (e.g. "/subdir/file.xbe").</param>
/// <param name="Size">File size in bytes (0 for directories).</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
internal record FileTreeEntry(string Path, long Size, bool IsDirectory);

/// <summary>
/// JSON report side (source/output) for <see cref="XISOSharp.XisoValidator.WriteReport"/>.
/// Members serialize camelCase via the report serializer context options.
/// </summary>
/// <param name="Path">Image path.</param>
/// <param name="FileCount">Total files in the image.</param>
/// <param name="DirCount">Total directories in the image.</param>
/// <param name="TotalBytes">Total file data bytes in the image.</param>
internal sealed record ValidationReportSide(string Path, int FileCount, int DirCount, long TotalBytes);

/// <summary>
/// JSON report issue entry for <see cref="XISOSharp.XisoValidator.WriteReport"/>.
/// </summary>
/// <param name="Type">Issue type name (e.g. "MissingInOutput").</param>
/// <param name="Path">The file path (XISO internal path with forward slashes).</param>
/// <param name="SourceSize">Size in the source ISO (0 if missing in source).</param>
/// <param name="OutputSize">Size in the output ISO (0 if missing in output).</param>
/// <param name="SourceHash">Lowercase hex SHA-256 in the source (null if not computed).</param>
/// <param name="OutputHash">Lowercase hex SHA-256 in the output (null if not computed).</param>
internal sealed record ValidationReportIssue(
    string Type,
    string Path,
    long SourceSize,
    long OutputSize,
    string? SourceHash,
    string? OutputHash);

/// <summary>
/// JSON validation report for <see cref="XISOSharp.XisoValidator.WriteReport"/>.
/// Named DTOs (instead of anonymous types) so System.Text.Json source generation
/// keeps working in trimmed single-file publishes, where reflection-based
/// serialization is disabled.
/// </summary>
/// <param name="Source">Source image summary.</param>
/// <param name="Output">Output image summary.</param>
/// <param name="Passed">Whether validation passed with no issues.</param>
/// <param name="IssueCount">Number of issues found.</param>
/// <param name="Issues">Issue details.</param>
internal sealed record ValidationReport(
    ValidationReportSide Source,
    ValidationReportSide Output,
    bool Passed,
    int IssueCount,
    List<ValidationReportIssue> Issues);

/// <summary>
/// Trim-safe System.Text.Json source-generation context for <see cref="ValidationReport"/>.
/// </summary>
[JsonSerializable(typeof(ValidationReport))]
internal sealed partial class ValidationReportJsonContext : JsonSerializerContext;
