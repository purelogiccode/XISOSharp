using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XISOSharp.Models;

namespace XISOSharp;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Represents a single file or directory entry collected from an XISO image for validation.
/// </summary>
/// <param name="Path">Internal path with forward slashes (e.g. "/subdir/file.xbe").</param>
/// <param name="Size">File size in bytes (0 for directories).</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
internal record FileTreeEntry(string Path, long Size, bool IsDirectory);

/// <summary>
/// JSON report side (source/output) for <see cref="XisoValidator.WriteReport"/>.
/// Members serialize camelCase via the report serializer context options.
/// </summary>
/// <param name="Path">Image path.</param>
/// <param name="FileCount">Total files in the image.</param>
/// <param name="DirCount">Total directories in the image.</param>
/// <param name="TotalBytes">Total file data bytes in the image.</param>
internal sealed record ValidationReportSide(string Path, int FileCount, int DirCount, long TotalBytes);

/// <summary>
/// JSON report issue entry for <see cref="XisoValidator.WriteReport"/>.
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
/// JSON validation report for <see cref="XisoValidator.WriteReport"/>.
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

/// <summary>
/// Provides post-conversion validation comparing source and output XISO images.
/// Supports file count, path, size, and optional SHA-256 checksum verification.
/// </summary>
public static class XisoValidator
{
    /// <summary>
    /// Validates a conversion by comparing the file trees of two XISO images.
    /// Works for both Redump→XISO and rewrite (−r) conversions.
    /// </summary>
    /// <param name="sourcePath">Path to the source ISO (Redump or original XISO).</param>
    /// <param name="outputPath">Path to the output XISO.</param>
    /// <param name="verifyChecksums">If <c>true</c>, also verify SHA-256 checksums for each file.</param>
    /// <returns>A <see cref="ValidationResult"/> describing the outcome.</returns>
    public static ValidationResult ValidateConversion(
        string sourcePath,
        string outputPath,
        bool verifyChecksums = false)
    {
        var sourceTree = CollectFileTree(sourcePath);
        var outputTree = CollectFileTree(outputPath);

        var sourceFiles = sourceTree.Where(static e => !e.IsDirectory).ToList();
        var outputFiles = outputTree.Where(static e => !e.IsDirectory).ToList();
        var sourceDirs = sourceTree.Where(static e => e.IsDirectory).ToList();
        var outputDirs = outputTree.Where(static e => e.IsDirectory).ToList();

        var sourceTotalBytes = sourceFiles.Sum(static e => e.Size);
        var outputTotalBytes = outputFiles.Sum(static e => e.Size);

        var issues = new List<ValidationIssue>();

        // Build case-insensitive dictionaries for comparison
        var sourceDict = new Dictionary<string, FileTreeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sourceFiles)
        {
            sourceDict[entry.Path] = entry;
        }

        var outputDict = new Dictionary<string, FileTreeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in outputFiles)
        {
            outputDict[entry.Path] = entry;
        }

        // Check for missing files (in source but not in output)
        foreach ((var path, FileTreeEntry entry) in sourceDict)
        {
            if (!outputDict.ContainsKey(path))
            {
                issues.Add(new ValidationIssue(
                    ValidationIssueType.MissingInOutput,
                    path,
                    entry.Size,
                    0,
                    null,
                    null));
            }
        }

        // Check for extra files (in output but not in source)
        foreach ((var path, FileTreeEntry entry) in outputDict)
        {
            if (!sourceDict.ContainsKey(path))
            {
                issues.Add(new ValidationIssue(
                    ValidationIssueType.ExtraInOutput,
                    path,
                    0,
                    entry.Size,
                    null,
                    null));
            }
        }

        // Check file sizes and optionally checksums for files present in both
        foreach ((var path, FileTreeEntry srcEntry) in sourceDict)
        {
            if (!outputDict.TryGetValue(path, out var outEntry))
                continue;

            if (srcEntry.Size != outEntry.Size)
            {
                issues.Add(new ValidationIssue(
                    ValidationIssueType.SizeMismatch,
                    path,
                    srcEntry.Size,
                    outEntry.Size,
                    null,
                    null));
            }
            else if (verifyChecksums && srcEntry.Size > 0)
            {
                var srcHash = XisoReader.ComputeFileHash(sourcePath, path, HashAlgorithmName.SHA256);
                var outHash = XisoReader.ComputeFileHash(outputPath, path, HashAlgorithmName.SHA256);

                if (srcHash != null && outHash != null && !srcHash.AsSpan().SequenceEqual(outHash))
                {
                    issues.Add(new ValidationIssue(
                        ValidationIssueType.ChecksumMismatch,
                        path,
                        srcEntry.Size,
                        outEntry.Size,
                        srcHash,
                        outHash));
                }
            }
        }

        var passed = issues.Count == 0;
        return new ValidationResult(
            passed,
            sourceFiles.Count,
            outputFiles.Count,
            sourceDirs.Count,
            outputDirs.Count,
            sourceTotalBytes,
            outputTotalBytes,
            issues);
    }

    /// <summary>
    /// Collects a flat list of all file and directory entries from an XISO image.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>List of <see cref="FileTreeEntry"/> for all entries in the image.</returns>
    private static List<FileTreeEntry> CollectFileTree(string isoPath)
    {
        var entries = new List<FileTreeEntry>();
        CollectEntries(isoPath, "/", entries);
        return entries;
    }

    /// <summary>
    /// Recursively collects entries from a directory within an XISO image.
    /// </summary>
    private static void CollectEntries(string isoPath, string currentPath, List<FileTreeEntry> entries)
    {
        var dirEntries = XisoReader.ListDirectory(isoPath, currentPath);

        foreach (var entry in dirEntries)
        {
            var fullPath = currentPath.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                entries.Add(new FileTreeEntry(fullPath, 0, true));
                CollectEntries(isoPath, fullPath, entries);
            }
            else
            {
                entries.Add(new FileTreeEntry(fullPath, entry.FileSize, false));
            }
        }
    }

    /// <summary>
    /// Logs the validation result to the console using the [VALIDATE] prefix.
    /// </summary>
    /// <param name="result">The validation result to display.</param>
    /// <param name="sourcePath">Path to the source ISO (for display).</param>
    /// <param name="outputPath">Path to the output ISO (for display).</param>
    public static void LogResult(ValidationResult result, string sourcePath, string outputPath)
    {
        var sourceName = Path.GetFileName(sourcePath);
        var outputName = Path.GetFileName(outputPath);

        Logger.Log(
            $"[VALIDATE] Source: {sourceName} ({result.SourceFileCount} files, {result.SourceTotalBytes:N0} bytes)\n");
        Logger.Log(
            $"[VALIDATE] Output: {outputName} ({result.OutputFileCount} files, {result.OutputTotalBytes:N0} bytes)\n");

        // File count
        if (result.SourceFileCount == result.OutputFileCount)
        {
            Logger.Log("[VALIDATE] File count: MATCH\n");
        }
        else
        {
            Logger.Log(
                $"[VALIDATE] File count: MISMATCH — source: {result.SourceFileCount}, output: {result.OutputFileCount}\n");
        }

        // File paths
        var pathIssues = result.Issues.Where(static i =>
            i.Type is ValidationIssueType.MissingInOutput or ValidationIssueType.ExtraInOutput).ToList();
        if (pathIssues.Count == 0)
            Logger.Log("[VALIDATE] File paths: MATCH\n");
        else
            Logger.Log($"[VALIDATE] File paths: MISMATCH — {pathIssues.Count} path difference(s)\n");

        // File sizes
        var sizeIssues = result.Issues.Where(static i => i.Type == ValidationIssueType.SizeMismatch).ToList();
        if (sizeIssues.Count == 0)
            Logger.Log("[VALIDATE] File sizes: MATCH\n");
        else
            Logger.Log($"[VALIDATE] File sizes: MISMATCH — {sizeIssues.Count} size difference(s)\n");

        // Checksums
        var checksumIssues = result.Issues.Where(static i => i.Type == ValidationIssueType.ChecksumMismatch).ToList();
        if (checksumIssues.Count > 0)
        {
            Logger.Log($"[VALIDATE] Checksums: FAIL — {checksumIssues.Count} checksum difference(s) (SHA-256)\n");
        }
        else if (result.Issues.Any(static i => i.Type == ValidationIssueType.ChecksumMismatch) ||
                 result.SourceFileCount == 0)
        {
            Logger.Log("[VALIDATE] Checksums: SKIPPED\n");
        }

        // Detailed issues
        foreach (var issue in result.Issues)
        {
            switch (issue.Type)
            {
                case ValidationIssueType.MissingInOutput:
                    Logger.LogErr($"[VALIDATE] MISSING: {issue.Path} (expected {issue.SourceSize:N0} bytes)\n");
                    break;
                case ValidationIssueType.ExtraInOutput:
                    Logger.LogErr($"[VALIDATE] EXTRA: {issue.Path} ({issue.OutputSize:N0} bytes)\n");
                    break;
                case ValidationIssueType.SizeMismatch:
                    Logger.LogErr(
                        $"[VALIDATE] SIZE MISMATCH: {issue.Path} — source: {issue.SourceSize:N0}, output: {issue.OutputSize:N0}\n");
                    break;
                case ValidationIssueType.ChecksumMismatch:
                    var srcHex = issue.SourceHash != null
                        ? Convert.ToHexString(issue.SourceHash).ToLowerInvariant()
                        : "?";
                    var outHex = issue.OutputHash != null
                        ? Convert.ToHexString(issue.OutputHash).ToLowerInvariant()
                        : "?";
                    Logger.LogErr(
                        $"[VALIDATE] CHECKSUM FAIL: {issue.Path} — source: {srcHex}..., output: {outHex}...\n");
                    break;
            }
        }

        // Final result
        if (result.Passed)
            Logger.Log("[VALIDATE] RESULT: PASS — All files validated successfully\n");
        else
            Logger.LogErr($"[VALIDATE] RESULT: FAIL — {result.Issues.Count} issue(s) found\n");
    }

    /// <summary>
    /// Writes a validation report to a file in JSON format.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="sourcePath">Path to the source ISO.</param>
    /// <param name="outputPath">Path to the output ISO.</param>
    /// <param name="reportPath">Path to write the JSON report.</param>
    public static void WriteReport(
        ValidationResult result,
        string sourcePath,
        string outputPath,
        string reportPath)
    {
        static string? Hex(byte[]? hash) =>
            hash != null ? Convert.ToHexString(hash).ToLowerInvariant() : null;

        var report = new ValidationReport(
            new ValidationReportSide(sourcePath, result.SourceFileCount, result.SourceDirCount,
                result.SourceTotalBytes),
            new ValidationReportSide(outputPath, result.OutputFileCount, result.OutputDirCount,
                result.OutputTotalBytes),
            result.Passed,
            result.Issues.Count,
            result.Issues.Select(static i => new ValidationReportIssue(
                i.Type.ToString(),
                i.Path,
                i.SourceSize,
                i.OutputSize,
                Hex(i.SourceHash),
                Hex(i.OutputHash))).ToList());

        var context = new ValidationReportJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var json = JsonSerializer.Serialize(report, context.ValidationReport);
        File.WriteAllText(reportPath, json, Encoding.UTF8);
    }
}