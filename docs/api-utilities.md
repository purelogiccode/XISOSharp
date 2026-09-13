# Utilities & Types

Supporting public API surface of `XISOSharp.Core`: logging, glob matching, AVL tree
helpers, format constants, records, enums, delegates, and exceptions.

- [Logger](#logger)
- [GlobMatcher](#globmatcher)
- [AvlTree](#avltree)
- [BoyerMoore](#boyermoore)
- [FileTimeHelper](#filetimehelper)
- [XisoExplorer & XisoAttributes](#xisoexplorer--xisoattributes)
- [Constants](#constants)
- [XisoValidator](#xisovalidator)
- [Unpack & path safety](#unpack--path-safety)
- [Records](#records)
- [Enums](#enums)
- [Delegates](#delegates)
- [Exceptions](#exceptions)

## Logger

`public static class Logger` — centralized, redirectable output.

| Member | Type | Description |
|---|---|---|
| `Out` | `TextWriter` | Normal output (default `Console.Out`) |
| `Error` | `TextWriter` | Error output (default `Console.Error`) |
| `Quiet` | `bool` | Suppress non-error output |
| `RealQuiet` | `bool` | Suppress all output including errors |
| `Warned` | `bool` | Set when a warning is issued |
| `TotalBytes` / `TotalFiles` | `long` / `int` | Per-operation totals |
| `TotalBytesAllIsos` / `TotalFilesAllIsos` | `long` / `int` | Cross-operation totals |
| `RemoveSystemUpdate` | `bool` | Skip `$SystemUpdate` entries (read + create) |
| `MediaEnable` | `bool` | `.xbe` media-enable patching (default `true`) |
| `XboxDiscLseek` | `long` | Detected disc offset (used by rewrite) |
| `Log(message, args)` | `void` | Write to `Out` unless `Quiet` |
| `LogLine(message)` | `void` | Write a line to `Out` unless `Quiet` |
| `Flush()` | `void` | Flush `Out` unless `Quiet` |
| `LogErr(message, args)` | `void` | Write to `Error` unless `RealQuiet` |

## GlobMatcher / WaxGlob

`public sealed class GlobMatcher` — matches relative paths against glob patterns.
Used by `XisoWriter.CreateXiso` for `excludePatterns`; also the façade over `WaxGlob` for ordered remapping.

```csharp
var matcher = new GlobMatcher(["**/*.tmp", "**/node_modules/**"]);
bool excluded = matcher.IsMatch("sub/notes.tmp"); // true

// With captures (build-image)
GlobMatchResult r = matcher.MatchWithGroups("assets/foo.bin");
if (r.IsMatch) Console.WriteLine(string.Join(", ", r.Groups)); // Groups[0]=whole, [1..n]=per-*/**
```

| Member | Description |
|---|---|
| `GlobMatcher(IEnumerable<string> patterns)` | Compile patterns (empty ones ignored; `null` argument throws) |
| `bool IsMatch(string? relativePath)` | `true` if any pattern matches; backslashes normalized to `/` |
| `GlobMatchResult MatchWithGroups(string? path)` / `bool TryMatch(..., out GlobMatchResult)` | Remap-aware: returns `IsMatch` + `Groups` (`Groups[0]` whole + per-`*/**` captures) via `WaxGlob` delegation |

Syntax (case-insensitive, `/` separator, same as xdvdfs `wax`):

| Pattern | Meaning |
|---|---|
| `*` | Any characters within one segment |
| `?` | Exactly one character within one segment |
| `**` | As a complete segment: zero or more segments. A trailing `/**` also matches the directory itself |
| `[abc]`, `[a-z]`, `[!abc]` | Character classes with `!`/`^` negation |
| `\x` | Escapes the next character |
| `{a,b}` | Brace alternation (xdvdfs `wax` compat) |
| `{0}` / `{1..n}` | Capture substitution in `imagePath` (whole match / per-`*/**` segment) — evaluated in `RemapFilesystem` ordered first-wins with `!negation` + suffix re-add |

Patterns without a leading `**/` are anchored to the source root. A trailing `/` is
treated as `/**`. Malformed patterns degrade to literals and never throw.

`WaxGlob` is the underlying `wax`-style engine: `new WaxGlob(string pattern)` (public sealed, `RegexPattern` exposed), `bool IsMatch(string)` + `string? GetCapture(string)` + `{0}` whole. `GlobMatcher` keeps single matcher entry point — `-X` (non-capturing) and remap (capturing) both use it.

### RemapFilesystem

`public static class RemapFilesystem` — ordered `host→image` remapping (`xdvdfs` `RemapOverlayFilesystem` parity).

| Member | Description |
|---|---|
| `RemapRule { string HostGlob; string ImagePath; bool IsExclusion; }` | `!` prefix → `IsExclusion` |
| `DryRunRemap(sourceDir, rules)` | Returns `IReadOnlyList<(HostPath,ImagePath)>` without writing |
| `BuildImage(sourceDir, outputIso, rules, progress, ct)` | `CreateFromRemapTree` via `IsRemap` |
| `GenerateSpecText(rules, outputPath)` / `ParseSpecFile(path)` / `WriteSpec(path,rules,output)` | `xdvdfs.toml` preserve-order `[metadata] output` + `[map_rules]` |

Manual `xdvdfs.toml` subset parser — see [xdvdfs Compat — Build-Image](xdvdfs-compat.md#build-image).

## BlockDevice

`XISOSharp.Core/BlockDevice/` — `xdvdfs-core/src/blockdev.rs` parity (`read(offset,buf)`/`write`/`len()` + `OffsetWrapper`):

| Type | Description |
|---|---|
| `IBlockDevice : IDisposable` | `long Length; int Read(long offset, Span<byte> buf); void Write(long offset, ReadOnlySpan<byte> buf);` |
| `FileBlockDevice` | Wraps `FileStream` (`BufferSize 65536`, `Stream.CopyTo` chunked) |
| `MemoryBlockDevice` | In-memory `byte[]` (golden `.iso` blobs, no temp files) |
| `OffsetBlockDevice` | `OffsetWrapper` parity — probes `[0, Global, Xgd3, Hybrid, Xgd1]` skip-sectors & CISO offset |
| `CisoBlockDevice` | CISO random-access, `index[totalBlocks+1]` LE u32 + single-sector cache, DEFLATE v1 + LZ4 v2 on-demand |

Overloads: `VerifyXiso(IBlockDevice)`, `AuditXiso(IBlockDevice)`, `ListDirectory(IBlockDevice, path)`. FileStream overloads delegate thinly.

## Redump / Ranges / Archival

| Type | Purpose |
|---|---|
| `XgdTables` | Port of `LibXGD/XGD.cs:11` — `XISO_OFFSET[4]` (incl. `0x89D80000` hybrid), `REDUMP_ISO_LENGTH[9]`, `VIDEO_L0_LENGTH[19]`, `VIDEO_L1_LENGTH[19]`, `WAVE_PVD[24]`, `GetVideoType`, `GetRedumpIsoTypeBySize`, `GetWave` (PVD at `0x832D`) |
| `XisoRanges` | `GetXisoRanges`/`GetValidSectors`/`MergeRanges`/`CollectFileEntries` (sorted by `Offset`, `quiet` → `Logger.Quiet`) — recursive `cur=isoOffset+rootOffset+childOffset`, `left==0xFFFF` sentinel |
| `XboxPrng` | RC4-like PRNG (`XboxPRNG.cs`) — `BruteForceSeed(ReadOnlySpan<byte>)`, `SimulateSectors(long)`, `WriteSectors(Stream,long)` (XGD1 seed extraction) |
| `XisoOperations` | `ExtractFiller`, `TryExtractSeed`/`ExtractSeed`, `WipeFiller`/`ProcessWipe`, `TrimXiso`/`WipeAndTrim` |
| `XisoSkeleton` | `Petrify` (zeroed XISO + SHA-1 `hex + " " + path` lines, `CollectFileEntries` sorted) |
| `XisoRedump` | `TryExtractVideo` (L0 head + L1 tail), `TryExtractUpdate` (`FindUpdateOffset` `ABCDABCD`, `l1Trimmed` split), `RebuildRedump` (full `l0Padding`/`l1Padding` + PRNG/`securitySectors`) |
| `SecuritySectors` | `Parse(path)` → `int[]` sorted, `4096`-sector `start-end` validation (`4095` length) |
| `XisoZarchive` | `CreateZar` (XISO → `.zar` via `ZArchiveSharp.ZArchiveWriter`: zstd L6 blocks, raw fallback, optional `IZarBlockCompressor`, `removeUpdate`, partition offsets) |
| `XisoChecksum` | `ComputeImageChecksum` / `ComputeImageChecksumHex` — SHA3-256 `SortedDictionary Ordinal` (`/path` UTF-8 + streamed data, `xdvdfs` compat; BCL `SHA3_256` when supported, else pure-managed fallback) |
| `CisoWriter` / `CisoReader` | `CompressToCso` (BCL DEFLATE v1 `0x80000000`, `align` 0/1/2, threshold `+12`, `--ciso-split` shim) / `DecompressToIso` (v1 DEFLATE + v2 LZ4) |

See [Archival](archival.md) and [Compression](compression.md).

## AvlTree

`public static class AvlTree` — AVL tree operations on `AvlNode` (in
`XISOSharp.DataStructures`).

| Method | Description |
|---|---|
| `int AvlCompareKey(string lhs, string rhs)` | Case-insensitive key comparison |
| `AvlNode? AvlFetch(AvlNode? root, string filename)` | Look up a node by filename |
| `AvlResult AvlInsert(ref AvlNode? root, AvlNode node)` | Insert + rebalance; `AvlError` on duplicate |
| `int AvlTraverseDepthFirst(AvlNode? root, TraversalCallback callback, object? context, AvlTraversalMethod method, int depth)` | Pre/In/Post-order traversal |
| `void FreeTree(AvlNode? root)` | Release a tree (postfix) |

`AvlNode` members: `Filename`, `FileSize`, `StartSector`, `OldStartSector`, `Left`,
`Right`, `Parent`, `Subdirectory`, `Offset`, `DirStart`, and the singleton
`AvlNode.EmptySubdirectory` sentinel used for empty directories.

## BoyerMoore

`public class BoyerMoore` — Boyer–Moore pattern search (used for `.xbe` media
patching; also standalone).

```csharp
var bm = new BoyerMoore([0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D]);
bm.Init();
int index = bm.Search(buffer, 0, buffer.Length);   // -1 when not found
bm.Done();
```

| Member | Description |
|---|---|
| `BoyerMoore(byte[] pattern, int alphabetSize = 256)` | Constructor |
| `void Init()` / `void Done()` | Build / release lookup tables |
| `int Search(byte[] text, int startIndex, int length)` | First match index or −1 |
| `int Search(byte[] text)` | Convenience overload |

## FileTimeHelper

`public static class FileTimeHelper` — `void WriteFileTimeNow(Span<byte> destination)`
writes the current Windows FILETIME (8 bytes, little-endian) into the header area.

## XisoExplorer & XisoAttributes

`public sealed class XisoExplorer` — UI-agnostic explorer over one image (`.iso`,
single `.cso`, or split `.1.cso`). Stateless by default; `XisoExplorerOptions`
switches it to keep-open for VFS/mount consumers.

| Member | Description |
|---|---|
| `XisoExplorer(string isoPath)` | Opens and probes eagerly; throws `XisoFormatException` on a bad image |
| `XisoExplorer(string, XisoExplorerOptions)` | Same, with `KeepOpen`/`Share` control |
| `Volume` | Probed `VolumeInfo` (see records below) |
| `ListChildren(string)` | Direct children, on-disc order |
| `GetNode(string)` | Exact-path node or `null`; `"/"` is the synthetic root |
| `OpenReadStream(string)` / `OpenReadStream(ExplorerNode)` | Bounded seekable `Stream` over a file's extent; directories throw `InvalidDataException` |
| `CopyOut`, `ComputeHashHex`, `GetXexInfo`, `GetXbeInfo` | Per-node extraction/hashing/XEX/XBE parsing |
| `Normalize` / `Combine` | Static image-internal path helpers |
| `Dispose()` | Releases the held stream (keep-open); a no-op in stateless mode |

`XisoExplorerOptions`: `KeepOpen` (hold one stream for the explorer's lifetime) and
`Share` (plain-ISO `FileShare`, default `FileShare.Read`; use `FileShare.ReadWrite`
so AV scanners/sync clients can touch the image while mounted).

**Read-bounds contract.** `OpenReadStream`/`ReadFileBytes` expose only a file's own
`[0, Size)` extent: the offset is `DiscLseek + StartSector * 2048` and the length is
the entry's `FileSize`, never the image's remaining bytes. A corrupt TOC therefore
cannot leak the next file's sectors. Seeking at/after the end and reading returns 0
bytes (standard `Stream` semantics), and short image data simply returns fewer bytes —
no copy is made, so 4 GB files stream in place. CISO/split-CISO inputs work
transparently because the stream rides the decompressed block device. A stateless
explorer's read stream owns the image handle (dispose it); a keep-open explorer's read
streams stay valid until the explorer is disposed. Operations on a keep-open explorer
are serialized by an internal lock (mirrors a VFS mounter's single-handle design).

```csharp
using var explorer = new XisoExplorer("game.cso", new XisoExplorerOptions { KeepOpen = true });
VolumeInfo vol = explorer.Volume;
using Stream s = explorer.OpenReadStream("/default.xex");
s.Seek(0x100, SeekOrigin.Begin);
int n = s.Read(buffer);                       // 0 at/past the file end
int oneShot = XisoReader.ReadFileBytes("game.iso", "/default.xbe", buffer, fileOffset: 0);
```

`XisoReader.ReadFileBytes` has string and stream overloads; the stream overload leaves
the caller's stream open. Both throw `InvalidDataException` for a missing path or a
directory target, and `ArgumentOutOfRangeException` for a negative offset.

`public static class XisoAttributes` — pure bit math (no Win32 calls) mapping a raw
XDVDFS attribute byte to `System.IO.FileAttributes`:

| Raw bit | Windows flag |
|---|---|
| always set | `ReadOnly` (images are read-only) |
| `0x10` / `0x02` / `0x04` / `0x20` | `Directory` / `Hidden` / `System` / `Archive` |
| none of the above | `Normal` added |

Reserved bits (`0x48`) are masked first, so raw on-disk bytes map identically to the
already-masked `ExplorerNode.Attributes` / `EntryInfo.Attributes`.

## Constants

`public static class Constants` — all format constants (see
[XISO Format](xiso-format.md) for context). Highlights:

| Constant | Value |
|---|---|
| `HeaderData` / `HeaderDataLength` | `"MICROSOFT*XBOX*MEDIA"` / 20 |
| `HeaderOffset` | `0x10000` |
| `SectorSize` / `FileModulus` | 2048 / `0x10000` |
| `RootDirectorySector` | `0x108` |
| `OptimizedTagOffset` / `OptimizedTag` | 31337 / `"in!xiso!2.7.1 (01.11.14)"` (`in!xiso` prefix) |
| `GlobalLseekOffset` / `Xgd2LseekOffset` | `0x0FD90000` |
| `Xgd2HybridLseekOffset` | `0x89D80000` — hybrid (probe #4, `HybridLseekOffset` alias) |
| `Xgd3LseekOffset` / `Xgd1LseekOffset` | `0x02080000` / `0x18300000` |
| `AttributeRo/Hid/Sys/Dir/Arc/Nor` | `0x01/0x02/0x04/0x10/0x20/0x80` + `AttributeReservedMask 0x48` / `AttributeValidMask 0xB7` / `MaskAttributes(byte)` |
| `EmptyDirectorySentinel` | `0xFFFF` and `0x0000` + 12-byte `0x00` header (`IsEmptyDirectoryHeader`, `xdvdfs` compat) |
| `MediaEnable` / `MediaEnableByte` | `E8 CA FD FF FF 85 C0 7D` / `0xEB` (`Length 8`, overlap `7`) |
| `CisoMagic` / `Ciso*` | `CISO` / `BlockSize 2048` / `HeaderSize 24` / `VersionDeflate 1` `0x80000000` vs `VersionLz4 2` / `align` 0/1/2 / `CompressionSavingThreshold 12` |
| `ExisoVersion` | `"2.7.1 (01.11.14)"` — extract-xiso baseline this port tracks (provenance only; not printed) |
| `NumSectors(uint size)` | Ceiling sector count |
| `Banner` | `XISOSharp v<version> for <os> - https://github.com/purelogiccode/XISOSharp` (`OperatingSystem.Is*()`; version from the assembly informational version) |

## XisoValidator

`public static class XisoValidator` — conversion validation (see
[Validation](validation.md)).

| Method | Description |
|---|---|
| `ValidationResult ValidateConversion(string sourcePath, string outputPath, bool verifyChecksums = false)` | Compare two images' file trees (counts, paths, sizes, optional SHA-256) |
| `void LogResult(ValidationResult result, string sourcePath, string outputPath)` | Print the `[VALIDATE]` summary |
| `void WriteReport(ValidationResult result, string sourcePath, string outputPath, string reportPath)` | Write a JSON report |

## Unpack & path safety

`public sealed class UnpackOptions` — resume + robustness options for extract/unpack/copy-out
(see [XisoReader API](api-xisoreader.md#resume-interrupted-unpacks)):

| Member | Description |
|---|---|
| `bool SkipExisting { get; set; }` | Skip destinations already holding a same-size file (`skip: <path>`) |
| `bool ContinueOnError { get; set; }` | Record per-file failures and continue; the run ends with the `ErrExtractFailed` summary |
| `bool ShouldSkip(string destPath, long fileSize)` | The size-match predicate (never skips unresolvable paths) |
| `bool ShouldSkip(string destPath, long fileSize, IFilesystem filesystem)` | Same predicate against any destination filesystem (disk, memory, custom) |

`public static class XisoPaths` — full-path comparison behind the input==output
guards (an output must never silently overwrite one of its inputs). Case sensitivity
follows the OS: insensitive on Windows/macOS, sensitive on Unix.

| Member | Description |
|---|---|
| `bool AreSamePath(string? a, string? b)` | `true` when both paths resolve to the same file system entry |
| `bool IsWithinDirectory(string? path, string? directory)` | `true` when `path` lies inside `directory` (sibling-prefix safe, e.g. `C:\src2\x` is not inside `C:\src`) |

The library throws `IOException` before writing when an output collides with an
input (`CompressToCso` / `DecompressToIso`, split `.N.cso` parts onto the source,
`WipeFiller` / `WipeAndTrim`, `RebuildRedump` onto any component); the CLI refuses
even earlier — before any prompt, move, or write — and additionally covers rewrite
`-o` onto the input or its `.old` backup. The only same-path write allowed is the
explicit in-place one: `TrimXiso(input, input)` (safe `SetLength` truncation).

## IFilesystem destinations

`public interface IFilesystem` — write-only destination abstraction for extraction
(TODO #7, xdvdfs #166). `UnpackImage` (see
[filesystem-based overloads](api-xisoreader.md#filesystem-based-overloads-todo-7-xdvdfs-166))
lands the unpacked tree in any implementation; `UnpackOptions.ShouldSkip` probes it
for resume.

| Member | Description |
|---|---|
| `Stream CreateFile(string path)` | Create/truncate the file for writing; the stream's final length must match the image's reported size |
| `void CreateDirectory(string path)` | Create the directory and all parents; root is a no-op |
| `bool FileExists(string path)` | `true` when a file (not a directory) exists — resume probe |
| `long FileLength(string path)` | Byte length, or `-1` when unresolvable (never skipped, reports as truncated) |

Paths are destination-root-relative, forward-slash separated, case-insensitive
(`"sub/file.bin"`, `"/sub/file.bin"`, `"sub\\file.bin"` all denote the same file);
`""`/`"/"` denotes the root. Implementations need not be thread-safe.

| Implementation | Behavior |
|---|---|
| `LocalFilesystem(string? root = null)` | Local disk under `root` (cwd-relative when `null`, matching the legacy unpack). `static LocalFilesystem Instance` is the shared cwd-relative instance. File creation matches the reader's direct path (`FileMode.Create`, 64 KiB buffered, no sharing); parent directories are **not** auto-created |
| `MemoryFilesystem` | In-process memory: files are byte snapshots committed when the unpack closes each stream (visible to `FileExists`/`FileLength`/`ReadAllBytes`/`FileNames` immediately after). Re-creating a file truncates it; parent directories are materialized automatically. `ReadAllBytes(path)`, `FileNames`, `DirectoryNames` for inspection |
| custom | Zip archives, network uploads, GUI preview panes — implement the four members |

## Records

| Record | Members |
|---|---|
| `VolumeInfo` | `IsValid`, `RootDirSector`, `RootDirSize`, `DiscLseek`, `DiscFormat` (friendly layout name), `FileLength`, `TotalSectors`, `CreationTime` (`DateTimeOffset?` from the descriptor FILETIME), `FileTimeRaw`, `DescriptorSector` (32 normally, 0 for rebuilt images, −1 when invalid) |
| `EntryInfo` | `Name`, `IsDirectory`, `StartSector`, `FileSize`, `Attributes` (masked `0xB7`), `LeftChildOffset`, `RightChildOffset` |
| `AuditResult` | `IsValid`, `FilesChecked`, `DirsChecked`, `Issues` (incl. `Reserved attribute bits set: 0x…`) |
| `RepairResult` | `Fixed`, `Remaining`, `BackupPath`, `DryRun`, `Success` — outcome of `XisoReader.Repair` (see [Repair](api-xisoreader.md#repair)) |
| `SalvageResult` | `Copied`, `Skipped`, `OutputPath`, `OutputIssues`, `Success` — outcome of `XisoReader.Salvage` (see [Salvage](api-xisoreader.md#salvage)) |
| `ValidationIssue` | `Type`, `Path`, `SourceSize`, `OutputSize`, `SourceHash`, `OutputHash` |
| `ValidationResult` | `Passed`, `SourceFileCount`, `OutputFileCount`, `SourceDirCount`, `OutputDirCount`, `SourceTotalBytes`, `OutputTotalBytes`, `Issues` |
| `ProgressInfo` | `Type` (`ProgressInfoType`), `Count`, `Path`, `Sector`, `Size` — structured progress event for writes and extraction (see [XisoWriter API](api-xisowriter.md#structured-progress-iprogresprogressinfo)) |
| `XexInfo` | `ModuleFlags`, `HeaderSize`, `EntryPoint`, `ImageBaseAddress`, `ImageSize`, `LoadAddress`, `Region`, `AllowedMediaTypes`, `MediaId`, `TitleId`, `Version`, `Platform`, `DiscNumber`, `DiscCount`, `EncryptionType`, `CompressionType` — Xbox 360 XEX2 header (see [XisoReader API](api-xisoreader.md#getxexinfo)) |
| `RemapRule` | `HostGlob`, `ImagePath`, `IsExclusion` — ordered remap rule (`!` prefix) |
| `GlobMatchResult` | `IsMatch`, `Groups` (`Groups[0]` whole + per-`*/**` captures) |
| `CisoHeader` | `BlockSize`, `TotalBlocks`, `Align`, `Version` (1 DEFLATE `0x80000000` vs 2 LZ4) |

## Enums

| Enum | Values |
|---|---|
| `ExtractMode` | `GenerateAvl`, `Extract`, `List`, `Rewrite`, `Tree`, `Verify` |
| `ExtractError` | `ErrEndOfSector` (−5001), `ErrIsoRewritten` (−5002), `ErrIsoNoFiles` (−5003) |
| `AvlResult` | `NoErr`, `AvlError`, `AvlBalanced` |
| `AvlTraversalMethod` | `Prefix`, `Infix`, `Postfix` |
| `ValidationIssueType` | `MissingInOutput`, `ExtraInOutput`, `SizeMismatch`, `ChecksumMismatch` |
| `ProgressInfoType` | `FileCount`, `DirCount`, `DirAdded`, `FileAdded`, `FileProgress`, `FinishedPacking` — progress event kinds (`FileAdded` also fires per written file in extract mode; `FileProgress` fires per chunk while extracting) |

## Delegates

| Delegate | Signature | Used by |
|---|---|---|
| `ProgressCallback` | `void(long currentValue, long finalValue)` | `XisoWriter.CreateXiso` |
| `TraversalCallback` | `int(AvlNode node, object? context, int depth)` | `AvlTree.AvlTraverseDepthFirst` |

## Exceptions

| Exception | Base | Notes |
|---|---|---|
| `XisoFormatException` | `IOException` | Invalid or corrupt image |
| `XisoEmptyException` | `ExtractErrorException` | Image has no files |
| `XisoFileTooLargeException` | `IOException` | File > ~4 GB; exposes `FileName`, `FileSize` |
| `ExtractErrorException` | `Exception` | Non-fatal errors; exposes `ErrorCode` (`ExtractError`) |

See also: [Library Overview](library.md) · [XisoReader API](api-xisoreader.md) ·
[XisoWriter API](api-xisowriter.md)
