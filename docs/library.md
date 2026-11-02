# Library Overview

`XISOSharp.Core` is the class library behind the CLI. It is published to NuGet as
**`XISOSharp`** and contains the complete XISO read/write engine plus CCI
(`CCISharp`) and ZAR (`ZArchiveSharp`) container support.

- [Packaging & compatibility](#packaging--compatibility)
- [Namespaces and types](#namespaces-and-types)
- [Container input](#container-input)
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
| Dependencies | `ZArchiveSharp` (ZAR), `CCISharp` (CCI/CCIM) |
| Signing | strong-named assembly |
| Trimming / AOT | `IsTrimmable`, `IsAotCompatible` |
| Versioning | MinVer from git tags (format `v1.0.2` / `1.0.2`) |
| Symbols | `snupkg` via SourceLink |
| Docs | XML documentation generated; package README; MIT license |
| API validation | Package validation (strict) across target frameworks |

## Namespaces and types

Core operations live in `XISOSharp`; data models, records, and enums
(`UnpackOptions`, `ProcessRunResult`, `ExplorerNode`, `XisoExplorerOptions`,
`VolumeInfo`, `EntryInfo`, `AuditResult`, `ProgressInfo`, `ExtractMode`, …) live in
`XISOSharp.Models`; `IBlockDevice` / `IFilesystem` live in `XISOSharp.Interfaces`.
Internal data structures stay in `XISOSharp.DataStructures`.

| Type | Purpose |
|---|---|
| [`XisoImage`](api-utilities.md#xisoimage) / `XisoImageHash` | Open-once disposable facade (list/read/extract/validate/title/hash, async wrappers) + full-image CRC32/MD5/SHA-1/SHA-256/SHA3-256 |
| [`XisoReader`](api-xisoreader.md) | Verify, extract, list, tree, rewrite, info, `ls`, audit, repair, salvage, hash, copy-out, copy-in, checksum, filetime, split/join, XEX/XBE info, XEX title metadata, BlockDevice overloads |
| `XbeAssets` | XBE logo / `$$XTIMAGE` / `$$XSIMAGE` extraction with BCL-only PNG and DDS output |
| `XisoImageSource` / `XisoImageFormat` / `XisoImageSourceInfo` | Unified detection and open (`TryDetect`, `OpenRead`, `OpenBlockDevice`, `StripImageSuffix`) for ISO, split ISO, CISO, CCI, GOD/SVOD, split variants and ZAR |
| `XisoCci` / `CciBlockDevice` | CCI/CCIM compression, extraction, default names and random access (CCISharp-backed) |
| `XisoZarchive` (read APIs) | `ListZar`, `ExtractZar`, `ConvertZarToIso` alongside the existing `CreateZar` |
| `StfsPackage` / `StfsEntry` / `StfsPackageInfo` | Read-only XBLA/XBLIG single-file STFS package: file table, hash-chain block lists, `Find`, `ReadAllBytes`, `CopyTo`, `ExtractTo` and volume/title metadata |
| [`XisoExplorer`](api-utilities.md#xisoexplorer--xisoattributes) / [`XisoAttributes`](api-utilities.md#xisoexplorer--xisoattributes) | In-place exploration (list/get/copy/hash/XEX/XBE, bounded `OpenReadStream`, keep-open VFS mode) + Windows attribute mapping |
| [`XisoWriter`](api-xisowriter.md) | Create and rewrite images + `build-image` remap (`CreateFromRemapTree`) |
| [`XisoPatcher`](api-xisoreader.md#copyin) / [`DirectoryEntryTableWriter`](api-xisowriter.md#directoryentrytablewriter) | In-place file patching (`CopyIntoImage`) + single-table build/serialize primitive |
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
| [`BlockDevice/*`](api-utilities.md#blockdevice) | `IBlockDevice` + `File`/`Memory`/`Offset`/`Stream`/`Ciso`/`Cci`/`God` devices |
| [`RemapFilesystem`](api-utilities.md) | Ordered `host→image` remapping, `xdvdfs.toml`, `GenerateSpecText`, `DryRunRemap` |
| Records/enums | `VolumeInfo`, `EntryInfo`, `AuditResult`, `ValidationResult`, `XexInfo`, `XexTitleInfo`, `XbeInfo`, `XbeImage`, `TitleInfo`/`TitleKind`, `RemapRule`, `XisoLayoutSpec`, `XisoLayoutEntry`, `ProgressInfo`, `ExtractMode`, … |
| Exceptions | `XisoFormatException`, `XisoEmptyException`, `XisoFileTooLargeException`, `ExtractErrorException` |

## Container input

Use `XisoImageSource` when you need the same format coverage as the CLI:

```csharp
if (XisoImageSource.TryDetect(path, out XisoImageSourceInfo? info) && info is not null)
{
    Console.WriteLine($"{info.Format}: {info.Length} bytes, {info.Parts.Count} part(s)");
}

using Stream image = XisoImageSource.OpenRead(@"C:\games\game.1.cci");
using IBlockDevice dev = XisoImageSource.OpenBlockDevice(@"C:\games\game.1.iso");
using IBlockDevice god = XisoImageSource.OpenBlockDevice(@"C:\games\4D5307E6"); // or its .data folder
```

## Open-once facade

`XisoImage` keeps one decompressed view open, probes the volume eagerly and
serializes operations internally:

```csharp
using XisoImage image = XisoImage.Open(@"C:\games\game.1.cci");
Console.WriteLine($"{image.Source.Format} {image.Volume.DiscFormat} {image.Length}");

foreach (ExplorerNode node in image.ListFlat("/"))
    Console.WriteLine(node.FullPath);

byte[] readme = image.ReadFile("/readme.txt");
XisoImageHashResult hash = image.ComputeImageHash();   // CRC32/MD5/SHA1/SHA256/SHA3-256
TitleInfo? title = image.GetTitleInfo();               // default.xbe / default.xex / default_mp.xbe
AuditResult audit = image.Validate();
```

| Format | Detection | Open behavior |
|---|---|---|
| `Iso` | `.iso`/`.xiso`/`.img` or any unknown plain file | `FileStream` / `FileBlockDevice` |
| `SplitIso` | `<base>.1.iso` | `SplitIsoStream` concatenates consecutive parts |
| `Ciso` / `SplitCiso` | `.cso`/`.1.cso`, magic sniff fallback | `CisoBlockDevice` |
| `Cci` / `SplitCci` | `.cci`/`.1.cci`, magic sniff fallback | `CciBlockDevice` |
| `Zar` | `.zar` | Not a sector image; use `XisoZarchive.ExtractZar`/`ConvertZarToIso` |

`OpenRead`/`OpenBlockDevice` throw `FileNotFoundException`, `InvalidDataException`
or `NotSupportedException` (ZAR) rather than returning a partially valid view.

## XBLA/XBLIG STFS packages

A single-file XBLA or XBLIG package is an STFS file system, not an XISO sector
image, so it has its own reader instead of `XisoImageSource`:

```csharp
using StfsPackage package = StfsPackage.Open(@"C:\games\4D5307E6");
Console.WriteLine($"{package.Info.TitleName} ({package.Info.Kind})");

foreach (StfsEntry entry in package.Entries)
    Console.WriteLine($"{entry.Path} {entry.Size}");

byte[] defaultXex = package.ReadAllBytes(package.Find("default.xex")!);
package.ExtractTo(@"C:\games\4D5307E6-extracted", skipExisting: true);
```

`StfsPackage` follows the STFS volume descriptor and the level-0/level-1/level-2
hash-table interleave, including writable-format packages with primary and
secondary tables. `XisoChecksum.ComputeStfsChecksum` uses the same sorted-path
SHA3-256 tree hash as ISO and ZAR, so equivalent trees compare equal. The reader
is read-only; STFS writing/rebuilding is out of scope.

## Design notes

- **Faithful port**: the engine mirrors `extract-xiso.c` (reference build `202609111233`) operation-for-operation
  so output is byte-identical to the reference tool (verified by the oracle-gated
  interop tests and the battle harness).
- **Directory layout** is an AVL tree; the writer performs a three-pass layout
  calculation — see [XISO Format](xiso-format.md).
- **Synchronous core**: the engine is synchronous and thread-static buffers keep it
  allocation-light; async wrappers offload to the thread pool.
- **Current-directory based creation**: like the C tool, `XisoWriter.CreateXiso` walks
  the file system using `Directory.SetCurrentDirectory` internally and restores the
  original directory afterwards (even on failure). Concurrent creates are safe but
  serialized process-wide via an internal lock — they run one at a time (TODO #22).

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
Logger.RouteInfoToError = true;   // send info to Error, keep Out for JSON
```

Progress during extraction/creation is reported through `Logger` and — for write
operations — through the optional `ProgressCallback` (`long currentBytes, long
totalBytes`) and the structured `IProgress<ProgressInfo>` channel (`FileCount`,
`DirCount`, `DirAdded`, `FileAdded`, `FinishedPacking` events, plus per-chunk
`FileProgress` while extracting — see [`ProgressInfo`](api-utilities.md#records)).

## Quick samples

```csharp
using XISOSharp;
using XISOSharp.Models;             // VolumeInfo, XexInfo, XbeInfo, ProgressInfo, …
using System.IO;                    // FileShare, FileAttributes
using System.Security.Cryptography; // HashAlgorithmName (explorer hashing)

// Extract
int result = XisoReader.Extract("game.iso", "./out", llCompat: false);

// Resume an interrupted unpack (skip same-size files already on disk)
int resumed = XisoReader.UnpackImage("game.iso", "./out",
    options: new UnpackOptions { SkipExisting = true });

// Copy independent files with four workers (output is byte-identical)
int fast = XisoReader.UnpackImage("game.iso", "./out",
    options: new UnpackOptions { MaxDegreeOfParallelism = 4 });

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

// In-memory unpack (no disk writes; also LocalFilesystem / custom IFilesystem)
var memory = new MemoryFilesystem();
XisoReader.UnpackImage("game.iso", memory);
byte[] xbe = memory.ReadAllBytes("default.xbe");

// Extraction pre-sizes known-length destinations (LocalFilesystem: SetLength +
// SequentialScan; MemoryFilesystem: capacity) before copying (Phase 7).

// Explore an image in-process (no extraction; .iso or .cso)
var explorer = new XisoExplorer("game.iso");   // probes the volume, fails fast
foreach (var node in explorer.ListChildren("/"))
    Console.WriteLine($"{(node.IsDirectory ? "dir " : "file")} {node.FullPath} ({node.Size} B)");
explorer.CopyOut("/docs/readme.txt", "./readme.txt");
string? sha256 = explorer.ComputeHashHex("/default.xbe", HashAlgorithmName.SHA256);
XexInfo? xex = explorer.GetXexInfo("/default.xex");
XbeInfo? xbe = explorer.GetXbeInfo("/default.xbe"); // OG-Xbox cert, no reference tool parses this

// Read file bytes in place — no extraction (VFS/Dokan ReadFile hot path)
using (Stream data = explorer.OpenReadStream("/default.xex"))
{
    data.Seek(0x100, SeekOrigin.Begin);
    int n = data.Read(buffer);                 // bounded to the file's Size (.cso-aware)
}
int read = XisoReader.ReadFileBytes("game.iso", "/default.xbe", buffer, fileOffset: 0x200);

// Keep-open explorer for an app that hammers one image (mount/VFS mode):
// one held stream, serialized calls, read streams die with Dispose.
using var mounted = new XisoExplorer("game.iso",
    new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });
VolumeInfo vol = mounted.Volume;               // IsValid, DiscFormat, FileLength, CreationTime, DescriptorSector
ExplorerNode? node = mounted.GetNode("/sub/file.bin");
using Stream nodeData = mounted.OpenReadStream(node);
FileAttributes attrs = XisoAttributes.ToWindowsFileAttributes(node.Attributes);

// Split for FATX (4 GiB default cap) and reassemble (CLI: split / join)
IReadOnlyList<string> parts = XisoReader.SplitXiso("game.iso", "game", 4L * 1024 * 1024 * 1024);
XisoReader.JoinSplitXiso(parts[0], "rejoined.iso");

// Create with exclusions (WaxGlob {0}/{n} also works for build-image)
XisoWriter.CreateXiso(
    "source_dir", "./out", null, null, out var isoPath, "game.iso", null,
    excludePatterns: ["**/*.tmp", "**/node_modules/**"]);

// Build-image ordered remapping (wax captures, negation)
var rules = new[] { new RemapRule("bin", "/"), new RemapRule("assets/**", "/assets/{1}") };
RemapFilesystem.BuildImage("./src", "out.iso", rules); // via RemapFilesystem → CreateFromRemapTree

// Deterministic image (xdvdfs stamp 0 → byte-identical output for identical input)
XisoWriter.PackFromDirectory("source_dir", "game.iso", fileTime: 0);

// Explicit sector layout + allocator (reallocation primitive for in-place patching)
SectorLayout layout = XisoReader.GetSectorLayout("game.iso");
var allocator = SectorAllocator.FromLayout(layout);
uint sector = allocator.AllocateForBytes(1_000_000); // first-fit free run, no overlaps

// Pack to a fixed layout (PlanToEnhance Phase 8; CLI: --layout)
XisoLayoutSpec spec = XisoLayoutSpec.FromSectorLayout(layout);
XisoWriter.PackFromDirectory("game_dir", "repacked.iso", layout: spec);

// Info
VolumeInfo info = XisoReader.GetVolumeInfo("game.iso");
Console.WriteLine($"Valid: {info.IsValid}, root sector: {info.RootDirSector}");

// Audit (flags reserved 0x48 + empty 0x0000)
AuditResult audit = XisoReader.AuditXiso("game.iso");
Console.WriteLine(audit.IsValid ? "PASS" : $"FAIL: {string.Join("; ", audit.Issues)}");

// Repair patchable issues in place (keeps .old backup; dryRun previews)
RepairResult repaired = XisoReader.Repair("game.iso");

// Rebuild reachable entries from a damaged image into a fresh audited .iso
SalvageResult salvaged = XisoReader.Salvage("game.iso"); // -> game.salvaged.iso

// Executable + disc identity (no reference tool covers XBE)
XbeInfo? cert = XisoReader.GetXbeInfo("game.iso", "/default.xbe");
Console.WriteLine($"{cert?.TitleName} [{cert?.TitleId:X8}]");
Console.WriteLine($"Layout: {XisoReader.GetVolumeInfo("game.iso").DiscFormat}");

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

All reader paths also accept rebuilt "sector-0" XISOs (descriptor at absolute
offset 0): probes report `DiscLseek = 0` / `DescriptorSector = 0`, and explorer
mounts, reads, sector ranges, ZAR packing, and in-place `CopyIn` work on them.
`RebuildRedump` rejects sector-0 inputs (repack to the standard layout first).

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
mode reports `ProgressInfoType.FileAdded` per written file plus per-chunk
`ProgressInfoType.FileProgress` while each file copies (driven by the shared
`XisoFileCopier` core, which reuses one buffer instead of allocating per file).
Outputs that collide
with their inputs throw `IOException` before writing (input==output guard); path
comparison lives in `XisoPaths`, resume options in `UnpackOptions`.

See also: [XisoReader API](api-xisoreader.md) · [XisoWriter API](api-xisowriter.md) ·
[Utilities & Types](api-utilities.md) · [Building](building.md)
