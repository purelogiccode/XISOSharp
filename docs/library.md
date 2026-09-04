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
| [`XisoReader`](api-xisoreader.md) | Verify, extract, list, tree, rewrite, info, audit, hash, copy-out, checksum, BlockDevice overloads |
| [`XisoWriter`](api-xisowriter.md) | Create and rewrite images + `build-image` remap (`CreateFromRemapTree`) |
| [`Logger`](api-utilities.md#logger) | Configurable text output with quiet/silent modes |
| [`GlobMatcher`/`WaxGlob`](api-utilities.md#globmatcher) | Glob matching for `-X` + ordered `wax` captures `{0}`/`{n}` for `build-image` |
| [`AvlTree`](api-utilities.md#avltree) | AVL tree operations on directory entries |
| [`BoyerMoore`](api-utilities.md#boyermoore) | Pattern search used for `.xbe` media patching |
| [`FileTimeHelper`](api-utilities.md#filetimehelper) | FILETIME writer |
| [`Constants`](api-utilities.md#constants) | All format constants (incl. `0x89D80000` hybrid, `0x48` reserved mask) |
| [`XisoValidator`](api-utilities.md#xisovalidator) | Conversion validation (compare two images) |
| [`XisoRanges`/`XisoSkeleton`/`XgdTables`/`XboxPrng`/`SecuritySectors`](api-utilities.md) | Redump archival: ranges, skeleton/SHA-1, wave tables, PRNG seed, `sectors.txt` |
| [`XisoRedump`/`XisoOperations`/`XisoZarchive`](api-utilities.md) | Video/filler/wipe/trim/petrify/update/ZAR/rebuild |
| [`XisoChecksum` / `CisoWriter`/`CisoReader`](api-utilities.md) | SHA3-256 image checksum + CISO compress/decompress |
| [`BlockDevice/*`](api-utilities.md#blockdevice) | `IBlockDevice` + `File`/`Memory`/`Offset`/`Ciso` (`CisoBlockDevice`) |
| [`RemapFilesystem`](api-utilities.md) | Ordered `host→image` remapping, `xdvdfs.toml`, `GenerateSpecText`, `DryRunRemap` |
| Records/enums | `VolumeInfo`, `EntryInfo`, `AuditResult`, `ValidationResult`, `XexInfo`, `RemapRule`, `ProgressInfo`, `ExtractMode`, … |
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

// Resume an interrupted unpack (skip same-size files already on disk)
int resumed = XisoReader.UnpackImage("game.iso", "./out",
    options: new UnpackOptions { SkipExisting = true });

// Keep going past bad files; the run still throws a summary at the end
try
{
    XisoReader.UnpackImage("game.iso", "./out",
        options: new UnpackOptions { ContinueOnError = true });
}
catch (ExtractFileException ex)  // one file: entry, sector, expected/actual
{
    Console.Error.WriteLine(ex.Message);
}
catch (ExtractErrorException ex)  // summary: every failure listed
{
    Console.Error.WriteLine(ex.Message);
}

// List
XisoReader.List("game.iso", llCompat: false);

// Stream input (memory, network, embedded resource — readable + seekable, stays open)
using var image = new MemoryStream(File.ReadAllBytes("game.iso"));
XisoReader.UnpackImage(image, "game.iso", "./out");

// Create with exclusions (WaxGlob {0}/{n} also works for build-image)
XisoWriter.CreateXiso(
    "source_dir", "./out", null, null, out var isoPath, "game.iso", null,
    excludePatterns: ["**/*.tmp", "**/node_modules/**"]);

// Build-image ordered remapping (wax captures, negation)
var rules = new[] { new RemapRule("bin", "/"), new RemapRule("assets/**", "/assets/{1}") };
XisoReader.BuildImage("./src", "out.iso", rules); // via RemapFilesystem → CreateFromRemapTree

// Info
VolumeInfo info = XisoReader.GetVolumeInfo("game.iso");
Console.WriteLine($"Valid: {info.IsValid}, root sector: {info.RootDirSector}");

// Audit (flags reserved 0x48 + empty 0x0000)
AuditResult audit = XisoReader.AuditXiso("game.iso");
Console.WriteLine(audit.IsValid ? "PASS" : $"FAIL: {string.Join("; ", audit.Issues)}");

// Block device (in-memory golden fixture)
using var dev = new MemoryBlockDevice(File.ReadAllBytes("game.iso"));
var (sector, size, lseek) = XisoReader.VerifyXiso(dev, "game.iso");

// Archival (XboxKit parity)
XisoRedump.TryExtractVideo("game.redump.iso", "game.video.iso", out _);
var filler = XisoOperations.ExtractFiller("game.iso");
XisoOperations.WipeFiller("game.iso", "wiped.iso");
XisoSkeleton.Petrify("game.iso", "skeleton.iso", "hash.txt");

// CISO
CisoWriter.CompressToCso("game.iso", "game.cso", level: 9);
CisoReader.DecompressToIso("game.cso", "rebuilt.iso");

// Checksum (SHA3-256, xdvdfs compat, sorted BTreeMap)
string hex = XisoChecksum.ComputeImageChecksumHex("game.iso");

// Rebuild lossless Redump
XisoRedump.RebuildRedump("game.xiso", "game.video.iso", "filler.bin", "su20076000_00000000", "rebuilt.redump.iso");
```

See: [CLI](cli.md) · [Archival](archival.md) · [xdvdfs Compat](xdvdfs-compat.md) · [Compression](compression.md)

## Async and cancellation

All long-running operations accept a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource();
(int result, string? outPath) = await XisoWriter.CreateXisoAsync(
    "source_dir", "./out", null, null, "game.iso", null, cts.Token);
```

Async variants: `XisoReader.DecodeXisoAsync`, `XisoWriter.CreateXisoAsync`.

Cancellation is honored per entry during extraction (an interrupted unpack throws
`OperationCanceledException` and still restores the working directory), and extract
mode reports `ProgressInfoType.FileAdded` per written file. Outputs that collide
with their inputs throw `IOException` before writing (input==output guard); path
comparison lives in `XisoPaths`, resume options in `UnpackOptions`.

See also: [XisoReader API](api-xisoreader.md) · [XisoWriter API](api-xisowriter.md) ·
[Utilities & Types](api-utilities.md) · [Building](building.md)
