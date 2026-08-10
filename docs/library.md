# Library Overview

`XISOSharp.Core` is the class library behind the CLI. It is published to NuGet as
**`XISOSharp`** and contains the complete XISO read/write engine with no runtime
dependencies.

- [Packaging & compatibility](#packaging--compatibility)
- [Namespaces and types](#namespaces-and-types)
- [Design notes](#design-notes)
- [Error handling](#error-handling)
- [Logging](#logging)
- [Quick samples](#quick-samples)
- [Async and cancellation](#async-and-cancellation)

## Packaging & compatibility

| Aspect | Value |
|---|---|
| Package ID | `XISOSharp` |
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Dependencies | none (runtime) |
| Signing | strong-named assembly |
| Trimming / AOT | `IsTrimmable`, `IsAotCompatible` |
| Versioning | MinVer from git tags (format `v2.7.1` / `2.7.1`) |
| Symbols | `snupkg` via SourceLink |
| Docs | XML documentation generated; package README; MIT license |
| API validation | Package validation (strict) across target frameworks |

## Namespaces and types

Everything lives in the `XISOSharp` namespace, except the internal data structures in
`XISOSharp.DataStructures`.

| Type | Purpose |
|---|---|
| [`XisoReader`](api-xisoreader.md) | Verify, extract, list, tree, rewrite, info, audit, hash, copy-out |
| [`XisoWriter`](api-xisowriter.md) | Create and rewrite images |
| [`Logger`](api-utilities.md#logger) | Configurable text output with quiet/silent modes |
| [`GlobMatcher`](api-utilities.md#globmatcher) | Glob pattern matching for exclusion |
| [`AvlTree`](api-utilities.md#avltree) | AVL tree operations on directory entries |
| [`BoyerMoore`](api-utilities.md#boyermoore) | Pattern search used for `.xbe` media patching |
| [`FileTimeHelper`](api-utilities.md#filetimehelper) | FILETIME writer |
| [`Constants`](api-utilities.md#constants) | All format constants |
| [`XisoValidator`](api-utilities.md#xisovalidator) | Conversion validation (compare two images) |
| Records/enums | `VolumeInfo`, `EntryInfo`, `AuditResult`, `ValidationResult`, `ExtractMode`, `ExtractError`, … |
| Exceptions | `XisoFormatException`, `XisoEmptyException`, `XisoFileTooLargeException`, `ExtractErrorException` |

## Design notes

- **Faithful port**: the engine mirrors `extract-xiso.c` v2.7.1 operation-for-operation
  so output is byte-identical to the reference tool (verified by the test suite and
  `Verify-Output.ps1`).
- **Directory layout** is an AVL tree; the writer performs a three-pass layout
  calculation — see [XISO Format](xiso-format.md).
- **Synchronous core**: the engine is synchronous and thread-static buffers keep it
  allocation-light; async wrappers offload to the thread pool.
- **Current-directory based creation**: like the C tool, `XisoWriter.CreateXiso` walks
  the file system using `Directory.SetCurrentDirectory` internally and restores the
  original directory afterwards. Callers should not run concurrent create operations in
  the same process.

## Error handling

| Exception | Base | When |
|---|---|---|
| `XisoFormatException` | `IOException` | Not a valid XISO, corrupt header/tree, bad offsets |
| `XisoEmptyException` | `ExtractErrorException` | Image contains no files |
| `XisoFileTooLargeException` | `IOException` | A file exceeds the ~4 GB XISO limit (`FileName`, `FileSize`) |
| `ExtractErrorException` | `Exception` | Non-fatal extraction errors with an `ErrorCode` |

`ExtractError` codes: `ErrEndOfSector` (−5001), `ErrIsoRewritten` (−5002),
`ErrIsoNoFiles` (−5003). The CLI treats `ErrIsoNoFiles` as success (0).

## Logging

`Logger` is a static class with redirectable writers:

```csharp
Logger.Out = TextWriter.Null;     // discard normal output
Logger.Error = Console.Error;     // keep errors
Logger.Quiet = true;              // suppress non-error output
Logger.RealQuiet = true;          // suppress everything
```

Progress during extraction/creation is reported through `Logger` and — for write
operations — through the optional `ProgressCallback` (`long currentBytes, long
totalBytes`) and the structured `IProgress<ProgressInfo>` channel (`FileCount`,
`DirCount`, `DirAdded`, `FileAdded`, `FinishedPacking` events — see
[`ProgressInfo`](api-utilities.md#records)).

## Quick samples

```csharp
using XISOSharp;

// Extract
int result = XisoReader.Extract("game.iso", "./out", llCompat: false);

// List
XisoReader.List("game.iso", llCompat: false);

// Create with exclusions
XisoWriter.CreateXiso(
    "source_dir", "./out", null, null, out var isoPath, "game.iso", null,
    excludePatterns: ["**/*.tmp", "**/node_modules/**"]);

// Info
VolumeInfo info = XisoReader.GetVolumeInfo("game.iso");
Console.WriteLine($"Valid: {info.IsValid}, root sector: {info.RootDirSector}");

// Audit
AuditResult audit = XisoReader.AuditXiso("game.iso");
Console.WriteLine(audit.IsValid ? "PASS" : $"FAIL: {string.Join("; ", audit.Issues)}");
```

## Async and cancellation

All long-running operations accept a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource();
(int result, string? outPath) = await XisoWriter.CreateXisoAsync(
    "source_dir", "./out", null, null, "game.iso", null, cts.Token);
```

Async variants: `XisoReader.DecodeXisoAsync`, `XisoWriter.CreateXisoAsync`.

See also: [XisoReader API](api-xisoreader.md) · [XisoWriter API](api-xisowriter.md) ·
[Utilities & Types](api-utilities.md) · [Building](building.md)
