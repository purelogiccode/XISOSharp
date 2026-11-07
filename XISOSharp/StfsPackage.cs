using System.Diagnostics.CodeAnalysis;
using XISOSharp.Models;
using SharpEntry = StfsSharp.Models.StfsEntry;
using SharpPackage = StfsSharp.StfsPackage;
using SharpPackageInfo = StfsSharp.Models.StfsPackageInfo;
using SharpPackageKind = StfsSharp.Models.StfsPackageKind;
using SharpProgress = StfsSharp.Models.StfsProgress;
using SharpProgressKind = StfsSharp.Models.StfsProgressKind;
using SharpValidationIssue = StfsSharp.Models.StfsValidationIssue;
using SharpValidationReport = StfsSharp.Models.StfsValidationReport;

namespace XISOSharp;

/// <summary>
/// Read-only access to a single-file Xbox 360 STFS package (XBLA or XBLIG):
/// the file table, per-file block chains and the file data behind the STFS
/// hash tables. The package file itself starts with the <c>XContent</c>
/// header; there is no <c>.data</c> directory (that layout is GOD/SVOD).
/// <see cref="Validate"/> performs a deep SHA-1 hash-tree check.
/// </summary>
/// <remarks>
/// This is a compatibility facade over the standalone
/// <see href="https://www.nuget.org/packages/StfsSharp">StfsSharp</see>
/// package. The public XISOSharp model types are preserved and mapped from the
/// StfsSharp models; they will be removed in XISOSharp 2.0.0.
/// </remarks>
public sealed class StfsPackage : IDisposable
{
    private readonly SharpPackage _inner;
    private readonly IReadOnlyList<StfsEntry> _entries;
    private readonly Dictionary<string, StfsEntry> _byPath;

    private StfsPackage(SharpPackage inner)
    {
        _inner = inner;
        Info = Map(inner.Info);

        List<StfsEntry> entries = new(inner.Entries.Count);
        Dictionary<string, StfsEntry> byPath = new(inner.Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SharpEntry entry in inner.Entries)
        {
            StfsEntry mapped = Map(entry);
            entries.Add(mapped);
            byPath[mapped.Path] = mapped;
        }

        _entries = entries.AsReadOnly();
        _byPath = byPath;
    }

    /// <summary>Package metadata and STFS volume layout.</summary>
    public StfsPackageInfo Info { get; }

    /// <summary>Every file and directory in the package, in file-table order.</summary>
    public IReadOnlyList<StfsEntry> Entries => _entries;

    /// <summary>
    /// Returns true when <paramref name="path"/> is a readable single-file STFS
    /// package with an XBLA or XBLIG content type. This is a cheap header probe;
    /// use <see cref="Open"/> for full validation.
    /// </summary>
    /// <param name="path">Path to the package file.</param>
    public static bool IsStfsPackage(string? path) => SharpPackage.IsStfsPackage(path);

    /// <summary>Opens an XBLA/XBLIG STFS package for reading.</summary>
    /// <param name="path">Path to the package file.</param>
    /// <param name="share">File sharing mode for the package file.</param>
    /// <returns>An open, disposable package reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The package does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid XBLA/XBLIG STFS package.</exception>
    /// <exception cref="UnauthorizedAccessException">The package cannot be opened.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public static StfsPackage Open(string path, FileShare share = FileShare.Read) =>
        new(SharpPackage.Open(path, share));

    /// <summary>
    /// Attempts to open an XBLA/XBLIG STFS package. Returns <c>false</c> for a
    /// missing, unreadable or invalid package instead of throwing.
    /// </summary>
    /// <param name="path">Path to the package file.</param>
    /// <param name="package">The open package when the method returns <c>true</c>.</param>
    public static bool TryOpen(string path, [NotNullWhen(true)] out StfsPackage? package)
    {
        if (SharpPackage.TryOpen(path, out SharpPackage? inner))
        {
            package = new StfsPackage(inner);
            return true;
        }

        package = null;
        return false;
    }

    /// <summary>
    /// Finds an entry by relative path. Accepts <c>/</c> or <c>\</c> separators,
    /// ignores a leading separator, and compares case-insensitively (the Xbox
    /// 360 file system is case-insensitive).
    /// </summary>
    /// <param name="path">Relative path inside the package.</param>
    /// <returns>The matching entry, or <c>null</c> when it does not exist.</returns>
    public StfsEntry? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        return _byPath.TryGetValue(normalized, out StfsEntry? entry) ? entry : null;
    }

    /// <summary>
    /// Reads a file into a new byte array. Files larger than
    /// <see cref="int.MaxValue"/> are rejected; use <see cref="CopyTo(StfsEntry,Stream)"/>
    /// for large files.
    /// </summary>
    /// <param name="entry">A file entry returned by <see cref="Entries"/> or <see cref="Find"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The entry is a directory or does not belong to this package.</exception>
    /// <exception cref="IOException">The file is too large or its data is truncated.</exception>
    /// <exception cref="ObjectDisposedException">The package has been disposed.</exception>
    public byte[] ReadAllBytes(StfsEntry entry) => _inner.ReadAllBytes(ToSharp(entry));

    /// <summary>
    /// Copies one file's data to <paramref name="destination"/> without loading
    /// the whole file into memory.
    /// </summary>
    /// <param name="entry">A file entry returned by <see cref="Entries"/> or <see cref="Find"/>.</param>
    /// <param name="destination">Writable destination stream.</param>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The entry is a directory or does not belong to this package.</exception>
    /// <exception cref="InvalidDataException">The file's block chain or data is truncated.</exception>
    /// <exception cref="ObjectDisposedException">The package has been disposed.</exception>
    public void CopyTo(StfsEntry entry, Stream destination) =>
        _inner.CopyTo(ToSharp(entry), destination);

    /// <summary>
    /// Streams one file's bytes to an internal sink. Used by the checksum
    /// walker so large files are never buffered in full.
    /// </summary>
    internal void ReadFileChunks<TState>(StfsEntry entry, TState state,
        Action<TState, byte[], int, int> sink) =>
        _inner.ReadFileChunks(ToSharp(entry), state, sink);

    /// <summary>
    /// Extracts every file and directory into <paramref name="outputDirectory"/>.
    /// Unsafe entry names are rejected before anything is written outside the
    /// destination.
    /// </summary>
    /// <param name="outputDirectory">Destination directory (created when missing).</param>
    /// <param name="skipExisting">Skip files that already exist instead of overwriting them.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files written.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe or its data is truncated.</exception>
    /// <exception cref="IOException">A destination file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public int ExtractTo(string outputDirectory, bool skipExisting = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        ExtractTo(outputDirectory, ExtractNamePolicy.Reject, skipExisting, progress, ct);

    /// <summary>
    /// Extracts every file and directory into <paramref name="outputDirectory"/>
    /// using <paramref name="namePolicy"/> for entry names that are unsafe on
    /// the host (path separators, drive-relative colons, trailing dots/spaces,
    /// DOS device names).
    /// </summary>
    /// <param name="outputDirectory">Destination directory (created when missing).</param>
    /// <param name="namePolicy">
    /// <see cref="ExtractNamePolicy.Reject"/> (fail fast),
    /// <see cref="ExtractNamePolicy.Sanitize"/> (replace unsafe characters and
    /// disambiguate collisions) or <see cref="ExtractNamePolicy.Skip"/> (omit
    /// the entry and a skipped directory's subtree).
    /// </param>
    /// <param name="skipExisting">Skip files that already exist instead of overwriting them.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of files written.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe or its data is truncated.</exception>
    /// <exception cref="IOException">A destination file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public int ExtractTo(string outputDirectory, ExtractNamePolicy namePolicy,
        bool skipExisting = false, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default) =>
        _inner.ExtractTo(
            outputDirectory,
            Map(namePolicy),
            skipExisting,
            progress is null ? null : new ProgressBridge(progress),
            message => Logger.LogErr(message),
            ct);

    /// <summary>
    /// Performs a deep integrity check of the package: every logical data block
    /// is hashed and compared with its level-0 hash-table entry, every file
    /// block chain is walked for truncation/cycles, every level-0 table is
    /// checked against its parent table, and the top hash and header hash in
    /// the volume descriptor are verified. Signatures are not verified.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validation report; <see cref="StfsValidationReport.IsValid"/> is <c>true</c> when every check passed.</returns>
    /// <exception cref="ObjectDisposedException">The package has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public StfsValidationReport Validate(CancellationToken ct = default) => Map(_inner.Validate(ct));

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();

    private SharpEntry ToSharp(StfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SharpEntry? known = _inner.Find(entry.Path);
        return known ?? throw new ArgumentException(
            $"Entry '{entry.Path}' is not part of STFS package '{Info.PackagePath}'.", nameof(entry));
    }

    private static StfsPackageKind Map(SharpPackageKind kind) =>
        kind == SharpPackageKind.Xbla ? StfsPackageKind.Xbla : StfsPackageKind.Xblig;

    private static StfsPackageInfo Map(SharpPackageInfo info) => new(
        info.PackagePath,
        Map(info.Kind),
        info.TitleName,
        info.TitleId,
        info.TitleIdHex,
        info.DiscNumber,
        info.DiscCount,
        info.ContentType,
        info.PackageLength,
        info.HeaderSize,
        info.FirstBlockOffset,
        info.TotalBlockCount,
        info.FreeBlockCount,
        info.FileTableBlockCount,
        info.FileTableBlockNumber,
        info.IsReadOnlyFormat,
        info.FileCount,
        info.DirectoryCount,
        info.TotalFileSize);

    private static StfsEntry Map(SharpEntry entry) => new(
        entry.Path,
        entry.Name,
        entry.IsDirectory,
        entry.Size,
        entry.StartBlock,
        entry.BlockCount,
        entry.IsContiguous,
        entry.Created,
        entry.Modified);

    private static StfsValidationReport Map(SharpValidationReport report) => new(
        report.IsValid,
        report.FilesChecked,
        report.BlocksChecked,
        report.HashTablesChecked,
        report.BytesChecked,
        report.Issues.Select(static issue => Map(issue)).ToArray());

    private static StfsValidationIssue Map(SharpValidationIssue issue) =>
        new(issue.Area, issue.Reason);

    private static StfsSharp.Models.ExtractNamePolicy Map(ExtractNamePolicy policy) => policy switch
    {
        ExtractNamePolicy.Reject => StfsSharp.Models.ExtractNamePolicy.Reject,
        ExtractNamePolicy.Sanitize => StfsSharp.Models.ExtractNamePolicy.Sanitize,
        ExtractNamePolicy.Skip => StfsSharp.Models.ExtractNamePolicy.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown extract name policy."),
    };

    private sealed class ProgressBridge(IProgress<ProgressInfo> progress) : IProgress<SharpProgress>
    {
        public void Report(SharpProgress value) => progress.Report(new ProgressInfo(
            value.Type == SharpProgressKind.DirectoryAdded
                ? ProgressInfoType.DirAdded
                : ProgressInfoType.FileAdded,
            Path: value.Path,
            Sector: value.Sector,
            Size: value.Size));
    }
}
