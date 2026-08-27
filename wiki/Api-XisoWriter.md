# XisoWriter API

`XisoWriter` (`XISOSharp` namespace) is a **static class** providing image creation and
rewriting — plus ordered **build-image remapping** (`RemapFilesystem` → `CreateFromRemapTree`) and the underlying writer for **CISO** (`CisoWriter`). It builds an AVL directory tree from a local file system (create mode), from an existing image (rewrite mode), or from remapped host→image rules (xdvdfs parity), then writes the image in
three passes.

- [CreateXiso](#createxiso)
- [CreateXisoAsync](#createxisoasync)
- [Rewrite mode](#rewrite-mode)
- [Exclusion patterns](#exclusion-patterns)
- [Progress reporting](#progress-reporting)

## CreateXiso / PackFromDirectory

```csharp
public static int PackFromDirectory(
    string sourceDirectory,
    string outputIsoPath,
    IReadOnlyList<string>? excludePatterns = null,
    ProgressCallback? progressCallback = null,
    CancellationToken cancellationToken = default,
    IProgress<ProgressInfo>? progress = null)

public static async Task<int> PackFromDirectoryAsync(
    string sourceDirectory,
    string outputIsoPath,
    IReadOnlyList<string>? excludePatterns = null,
    ProgressCallback? progressCallback = null,
    CancellationToken cancellationToken = default,
    IProgress<ProgressInfo>? progress = null)
```

`PackFromDirectory` is the convenience form of `CreateXiso` for packing a directory
into an ISO with a 1:1 mapping: it takes a single output ISO path (creating the
output directory as needed) instead of separate directory/name arguments. Repacking an
existing ISO is `XisoReader.Rewrite` (the CLI's `--pack <iso>` maps to it).

```csharp
public static int CreateXiso(
    string rootDirectory,
    string? outputDirectory,
    AvlNode? inRoot,
    Stream? sourceStream,
    out string? outIsoPath,
    string? inName,
    ProgressCallback? progressCallback,
    CancellationToken cancellationToken = default,
    int? prependSectors = null,
    IReadOnlyList<string>? excludePatterns = null,
    IProgress<ProgressInfo>? progress = null)
```

| Parameter | Meaning |
|---|---|
| `rootDirectory` | Create mode: source directory. Rewrite mode: base name of the output ISO |
| `outputDirectory` | Directory for the output ISO; `null` = current directory |
| `inRoot` | Pre-built AVL tree root; `null` = build the tree from the file system |
| `sourceStream` | Source ISO stream for rewrite mode; `null` for create mode |
| `outIsoPath` | Receives the full path of the created ISO |
| `inName` | Output filename; `null` = directory name + `.iso` |
| `progressCallback` | Optional `(currentBytes, totalBytes)` callback during file writes |
| `cancellationToken` | Cancellation support |
| `prependSectors` | Reserve `N` zero-filled sectors before the filesystem (Redump layouts) |
| `excludePatterns` | Glob patterns of files/directories to omit (create mode only) |
| `progress` | Structured progress channel — `IProgress<ProgressInfo>` events (counts, per-entry additions, completion). See [Progress reporting](#progress-reporting) |

Returns 0 on success, 1 on error (permission, I/O, or any exception while writing —
errors are logged and converted to the return code).

Throws: `ArgumentOutOfRangeException` for a negative `prependSectors`;
`XisoFileTooLargeException` when a source file exceeds ~4 GB.

## CreateXisoAsync

```csharp
public static async Task<(int Result, string? OutIsoPath)> CreateXisoAsync(
    string rootDirectory,
    string? outputDirectory,
    AvlNode? inRoot,
    Stream? sourceStream,
    string? inName,
    ProgressCallback? progressCallback,
    CancellationToken cancellationToken = default,
    int? prependSectors = null,
    IReadOnlyList<string>? excludePatterns = null,
    IProgress<ProgressInfo>? progress = null)
```

Async wrapper returning the result code and the output path.

## Rewrite mode

Rewrite mode is normally driven through `XisoReader.Rewrite`, which calls
`CreateXiso` with a pre-built tree:

1. Read the source image and build the AVL tree (`ExtractMode.GenerateAvl`).
2. Call `CreateXiso(rootDirectory: isoName, inRoot: tree, sourceStream: source, …)`.
3. File data is copied from `sourceStream` at the entries' original sectors (plus the
   detected disc offset); the `.xbe` media-enable patch applies as usual.

Because `inRoot` is non-null, the file system is **not** walked and `excludePatterns`
is ignored.

## Exclusion patterns

`excludePatterns` accepts shell-style globs matched against paths relative to
`rootDirectory` — the same syntax as the CLI's `-X` flag (see
[CLI Reference](cli.md#exclude-patterns)). Matching is handled by
[`GlobMatcher`](api-utilities.md#globmatcher).

- Excluded directories are never recursed into.
- Exclusion is silent (no warnings).
- When `Logger.RemoveSystemUpdate` is `true`, the pattern `**/$SystemUpdate/**` is
  implicitly prepended — this is how the CLI's `-s` flag maps onto create mode.

```csharp
XisoWriter.CreateXiso(src, outDir, null, null, out _, "game.iso", null,
    excludePatterns: ["**/*.tmp", "**/node_modules/**"]);
```

## Progress reporting

The optional `progressCallback` receives cumulative written bytes. `Logger.TotalFiles`
and `Logger.TotalBytes` additionally track per-operation totals, and
`Logger.TotalFilesAllIsos` / `Logger.TotalBytesAllIsos` accumulate across operations.

### Structured progress (`IProgress<ProgressInfo>`)

For UI progress bars and tree views, pass an `IProgress<ProgressInfo>` channel. Events
are delivered synchronously in order:

| Event (`ProgressInfoType`) | Payload | When |
|---|---|---|
| `FileCount` / `DirCount` | `Count` (totals) | Before writing starts. `DirCount` counts directory entries only (the image root `/` itself is not counted) |
| `DirAdded` | `Path` (e.g. `"/subdir"`), `Sector` | When each directory's write begins (parent before children; includes the root `/`) |
| `FileAdded` | `Path`, `Sector`, `Size` (written bytes) | After each file's data is written |
| `FinishedPacking` | — | Last, on success only |

Paths use forward slashes (`"/"` = root). The channel is honored in create **and**
rewrite modes (`XisoReader.Rewrite` / `DecodeXiso` also accept it).

```csharp
var progress = new Progress<ProgressInfo>(info =>
{
    switch (info.Type)
    {
        case ProgressInfoType.FileCount:
            Console.WriteLine($"{info.Count} files to write");
            break;
        case ProgressInfoType.FileAdded:
            Console.WriteLine($"added {info.Path} ({info.Size} bytes)");
            break;
        case ProgressInfoType.FinishedPacking:
            Console.WriteLine("done");
            break;
    }
});

XisoWriter.CreateXiso(src, outDir, null, null, out _, "game.iso", null, progress: progress);
```

> [!TIP]
> The built-in `Progress<T>` posts callbacks asynchronously when no synchronization
> context is present. For strict in-order delivery, implement `IProgress<ProgressInfo>`
> directly (as the test suite does).

```csharp
XisoWriter.CreateXiso(src, outDir, null, null, out _, "game.iso",
    (current, total) => Console.WriteLine($"{current}/{total} bytes"));
```

## BuildImage (xdvdfs parity)

```csharp
public static int CreateFromRemapTree(
    string sourceDirectory,
    string outputIsoPath,
    IReadOnlyList<RemapRule> rules,
    IProgress<ProgressInfo>? progress = null,
    CancellationToken ct = default);

public static IReadOnlyList<(string HostPath, string ImagePath)> DryRunRemap(
    string sourceDirectory, IReadOnlyList<RemapRule> rules);
public static string GenerateSpecText(IEnumerable<RemapRule> rules, string? outputPath);
```

`RemapFilesystem` (wax `WaxGlob` engine: `*`/`**`/`?`/`[]`/`{a,b}` + `{0}` whole + `{1..n}` captures) evaluates rules ordered first-wins with `!negation` + suffix re-add, builds the AVL via `IsRemap` flag (skips CWD), then same `CalculateDirectoryRequirements`/`CalculateDirectoryOffsets`/`WriteTreeCallback` pipeline. `xdvdfs.toml` manual TOML parser (`[map_rules]` preserve-order). See [xdvdfs Compat — Build-Image](xdvdfs-compat.md#build-image) and [Archival — Build-Image](archival.md).

## CISO

```csharp
// XISOSharp.Core/CisoWriter.cs + CisoReader.cs
public static int CompressToCso(string sourcePath, string outputCsoPath, int level = 9, long? splitBytes = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static int DecompressToIso(string csoPath, string outputIsoPath, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
```

Pure-managed BCL `DeflateStream` v1 (`0x80000000` plain bit, `align` 0/1/2 per size, threshold `+12`) + LZ4 v2 read via `CisoBlockDevice` single-sector cache + `IBlockDevice` stack (`File`/`Memory`/`Offset`/`Ciso`). CLI `compress|cso` / `decompress|uncso|decso` (`Program.cs:RunCompressMode`/`RunDecompressMode`). See [Compression](compression.md).

See also: [Library Overview](library.md) · [XisoReader API](api-xisoreader.md) ·
[Utilities & Types](api-utilities.md) · [xdvdfs Compat](xdvdfs-compat.md) · [Compression](compression.md)
