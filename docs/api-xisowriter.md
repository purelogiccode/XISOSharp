# XisoWriter API

`XisoWriter` (`XISOSharp` namespace) is a **static class** providing image creation and
rewriting. It builds an AVL directory tree from a local file system (create mode) or
from a pre-built tree over an existing image (rewrite mode), then writes the image in
three passes.

- [CreateXiso](#createxiso)
- [CreateXisoAsync](#createxisoasync)
- [Rewrite mode](#rewrite-mode)
- [Exclusion patterns](#exclusion-patterns)
- [Progress reporting](#progress-reporting)

## CreateXiso

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
    IReadOnlyList<string>? excludePatterns = null)
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
    IReadOnlyList<string>? excludePatterns = null)
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

```csharp
XisoWriter.CreateXiso(src, outDir, null, null, out _, "game.iso",
    (current, total) => Console.WriteLine($"{current}/{total} bytes"));
```

See also: [Library Overview](library.md) · [XisoReader API](api-xisoreader.md) ·
[Utilities & Types](api-utilities.md)
