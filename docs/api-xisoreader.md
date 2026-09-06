# XisoReader API

`XisoReader` (`XISOSharp` namespace) is a **static class** providing all read-side
operations: verification, extraction, listing, tree traversal, rewriting, volume info,
directory listing, auditing, hashing, copy-out, and copy-in.

- [Method index](#method-index)
- [VerifyXiso](#verifyxiso)
- [Extract / List / Tree / Rewrite](#extract--list--tree--rewrite)
- [Resume interrupted unpacks](#resume-interrupted-unpacks)
- [DecodeXiso (main entry)](#decodexiso-main-entry)
- [DecodeXisoAsync](#decodexisoasync)
- [GetVolumeInfo](#getvolumeinfo)
- [ListDirectory / GetEntryInfo](#listdirectory--getentryinfo)
- [GetSectorLayout](#getsectorlayout)
- [CopyOut](#copyout)
- [CopyIn](#copyin)
- [ComputeFileHash / ComputeDirectoryHashes](#computefilehash--computedirectoryhashes)
- [AuditXiso](#auditxiso)
- [Repair](#repair)
- [Salvage](#salvage)
- [Disc offset probing](#disc-offset-probing)

## Method index

| Method | Description |
|---|---|
| `VerifyXiso` | Low-level header verification; returns root directory metadata + disc offset |
| `Extract` | Extract an image to a directory |
| `UnpackImage` | Unpack the whole image; auto-detects the optimized layout and ISO-named default output |
| `List` | List top-level entries |
| `Tree` | Recursive listing with sizes and totals |
| `Rewrite` | Rewrite an image into the optimized layout |
| `DecodeXiso` | Main entry point (all modes) |
| `DecodeXisoAsync` | Async wrapper of `DecodeXiso` |
| `OpenImageStream` | Open an image for reading (`.cso`-aware); caller owns the stream |
| `IsOptimizedImage` | Probe the optimized tag (path or stream overload) |
| `GetVolumeInfo` | Volume descriptor metadata without throwing |
| `ListDirectory` | Metadata of entries in a directory |
| `ListDirectoryFlat` | Entry **names** of a directory (non-recursive convenience) |
| `GetEntryInfo` | Metadata of one entry by path |
| `GetSectorLayout` | Explicit sector layout: files/tables → sector ranges + used/free ranges |
| `CopyOut` | Copy one file or directory out of an image |
| `CopyIn` | Copy one host file into an image (replace or add, in place) |
| `ComputeFileHash` | Hash one file (MD5/SHA-256/… via `HashAlgorithmName`) |
| `ComputeDirectoryHashes` | Hash every file under a path |
| `GetXexInfo` | Parse the Xbox 360 XEX2 header of a `.xex` file |
| `AuditXiso` | Deep integrity audit |
| `Repair` | Fix safely-patchable issues in place (`.old` backup, dry-run) |
| `Salvage` | Rebuild a readable image from a corrupt one (repack + re-audit) |

## VerifyXiso

```csharp
public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
    Stream fs, string isoName, int? skipSectors = null)
```

Verifies the header magic at all known disc offsets (or at the `skipSectors` offset
when given) and returns the root directory table location and the detected disc offset.

| Parameter | Meaning |
|---|---|
| `fs` | Open, readable stream positioned anywhere (file, memory, block-device backed) |
| `isoName` | Display name used in error messages |
| `skipSectors` | Treat the XISO as starting `N` sectors into the file (Redump). Negative → `ArgumentOutOfRangeException` |

Throws: `XisoFormatException` (invalid/corrupt), `IOException` (file too short),
`XisoEmptyException` (no files).

## Extract / Unpack / List / Tree / Rewrite

```csharp
public static int Extract(
    string xisoPath, string? outputPath, bool llCompat,
    CancellationToken cancellationToken = default, int? skipSectors = null,
    UnpackOptions? options = null, IProgress<ProgressInfo>? progress = null)

public static int UnpackImage(
    string isoPath, string? outputPath = null,
    CancellationToken cancellationToken = default, int? skipSectors = null,
    UnpackOptions? options = null, IProgress<ProgressInfo>? progress = null)

public static int List(
    string xisoPath, bool llCompat,
    CancellationToken cancellationToken = default, int? skipSectors = null)

public static int Tree(
    string xisoPath, bool llCompat,
    CancellationToken cancellationToken = default, int? skipSectors = null)

public static int Rewrite(
    string xisoPath, string? outputPath, out string? outIsoPath,
    CancellationToken cancellationToken = default,
    string? outputName = null, int? skipSectors = null, int? prependSectors = null,
    IProgress<ProgressInfo>? progress = null)
```

| Parameter | Meaning |
|---|---|
| `xisoPath` | Path of the ISO (for `Rewrite`, the source; the `.old` rename is internal) |
| `outputPath` | `null` → extract into an ISO-named subdirectory of the current directory; otherwise the target directory |
| `llCompat` | `true` = legacy linked-list right-offset calculation; `false` = optimized layout |
| `outputName` | Rewrite only: custom output filename (default: original name with `.iso`) |
| `skipSectors` | Read offset (Redump video partition), in 2048-byte sectors |
| `prependSectors` | Rewrite only: reserve zero-filled sectors before the filesystem |
| `progress` | Rewrite: structured progress channel (`IProgress<ProgressInfo>`) — see [XisoWriter API](api-xisowriter.md#structured-progress-iprogresprogressinfo). Extract: a `FileAdded` event per file actually written (skipped/excluded files are silent), plus a per-chunk `FileProgress` event (`Size` = bytes copied so far, `Count` = total file bytes) while each file copies |
| `options` | Extract/unpack only: resume options — see [Resume interrupted unpacks](#resume-interrupted-unpacks) |

`UnpackImage` is the convenience form of `Extract`: it probes the optimized-tag marker
to pick `llCompat` automatically and defaults the output directory to the ISO name
(minus `.iso`), so callers never need to know the image layout.

All return 0 on success.

### Stream-based overloads

`Extract`, `UnpackImage`, `List`, `Tree`, `DecodeXiso`, and `DecodeXisoAsync`
each have a `Stream` overload taking `(Stream imageStream, string imageName, ...)`
in place of the input path — for memory, network, or embedded-resource images.
The random-access primitives have them too (TODO #19): `GetVolumeInfo`,
`ListDirectory`, `GetEntryInfo`, `CopyOut`, `ComputeFileHash`, and `GetXexInfo` —
these are what `XisoExplorer` uses to support `.cso` inputs.
The stream must be readable and seekable (`ArgumentException` otherwise) and is
left open; `imageName` (typically the file name) drives output naming and
messages. `IsOptimizedImage` has a matching `Stream` overload (position
restored), and `OpenImageStream` — the `.cso`-aware opener the path APIs use
internally — is public too. Rewrite stays path-only (it is file-identity based,
the `.old` dance), so the stream `DecodeXiso` refuses `ExtractMode.Rewrite`
with `ArgumentException`.

```csharp
using var image = new MemoryStream(File.ReadAllBytes("game.iso"));
int rc = XisoReader.UnpackImage(image, "game.iso", "./out"); // stream stays open
```

### Filesystem-based overloads (TODO #7, xdvdfs #166)

`UnpackImage` also takes any `IFilesystem` destination in place of `outputPath`:

```csharp
public static int UnpackImage(
    string isoPath, IFilesystem filesystem,
    CancellationToken cancellationToken = default, int? skipSectors = null,
    UnpackOptions? options = null, IProgress<ProgressInfo>? progress = null)

public static int UnpackImage(
    Stream imageStream, string imageName, IFilesystem filesystem,
    CancellationToken cancellationToken = default, int? skipSectors = null,
    UnpackOptions? options = null, IProgress<ProgressInfo>? progress = null)
```

Files land at the **filesystem root** (no ISO-named subdirectory, no process
working-directory changes), with the same per-file hardening as the disk path:
skip-existing resume probes the destination filesystem, truncated data and
failed writes throw the same `ExtractFileException` errors, and zero-byte files
are created. See [`IFilesystem`](api-utilities.md#ifilesystem-destinations) for
the `LocalFilesystem` / `MemoryFilesystem` implementations and the path rules.

```csharp
var memory = new MemoryFilesystem();
XisoReader.UnpackImage("game.iso", memory);
byte[] defaultXbe = memory.ReadAllBytes("default.xbe"); // never touched the disk
```

## Resume interrupted unpacks

```csharp
public sealed class UnpackOptions
{
    public bool SkipExisting { get; set; }
    public bool ContinueOnError { get; set; }
    public bool ShouldSkip(string destPath, long fileSize);
    public bool ShouldSkip(string destPath, long fileSize, IFilesystem filesystem);
}
```

When `SkipExisting` is set, `Extract` / `UnpackImage` / `CopyOut` leave any
destination file already holding the same byte count untouched (logged as
`skip: <path>`) instead of overwriting it — re-running an interrupted unpack
completes the missing files instead of redoing the finished ones. XISO stores no
per-file timestamps, so **size is the identity signal**: a same-size file is assumed
to be a complete earlier write, while a missing or short file (a torn write) is
rewritten. Cancellation is honored per entry (`OperationCanceledException`), and the
process working directory is restored even when the run aborts.

When `ContinueOnError` is set, a per-file failure (uncreatable destination,
truncated data, failed write) is recorded and skipped instead of aborting;
an uncreatable directory skips its subtree. The run still throws at the end —
an `ExtractErrorException` with code `ErrExtractFailed` listing every failure
(the xdvdfs `Failed to unpack image` wrapper) — so the failure is visible to
callers. Structural table corruption still aborts immediately (TODO #16).

## Extraction errors

```csharp
public sealed class ExtractFileException : ExtractErrorException
{
    public string InternalPath { get; }  // entry inside the image
    public string DestPath { get; }      // destination being written
    public uint StartSector { get; }     // partition-relative data sector
    public long FileSize { get; }        // reported size in bytes
    public long BytesRead { get; }       // bytes read before a short read (-1 otherwise)
}
```

Every per-file extraction failure carries this context (TODO #9, xdvdfs #187):
`Message` reads `Failed to extract "<entry>" (sector N, M bytes) ->
"<dest>": <reason>`, with the OS error as `InnerException`. New
`ExtractError` codes: `ErrFileTruncated` (-5004, image data ends early),
`ErrFileWrite` (-5005, destination create/write failed), `ErrExtractFailed`
(-5006, end-of-run summary under `ContinueOnError`). Extracted files are
integrity-checked: out-of-image data ranges are refused before the destination
is created, and the on-disk length is re-statted after the copy.

## DecodeXiso (main entry)

```csharp
public static int DecodeXiso(
    string xisoPath,
    string? outputPath,
    ExtractMode mode,
    out string? outIsoPath,
    bool llCompat,
    CancellationToken cancellationToken = default,
    string? outputName = null,
    int? skipSectors = null,
    int? prependSectors = null,
    IProgress<ProgressInfo>? progress = null,
    UnpackOptions? unpackOptions = null)
```

The generic entry point used by the wrappers above. `mode` is one of `ExtractMode`:
`Extract`, `List`, `Tree`, `Rewrite`, `GenerateAvl` (internal use), or `Verify`.
The `Stream` overload is covered under [Stream-based overloads](#stream-based-overloads).

## DecodeXisoAsync

```csharp
public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
    string xisoPath, string? outputPath, ExtractMode mode,
    bool llCompat = false, CancellationToken cancellationToken = default,
    string? outputName = null, int? skipSectors = null, int? prependSectors = null,
    IProgress<ProgressInfo>? progress = null, UnpackOptions? unpackOptions = null)
```

Runs `DecodeXiso` on the thread pool. Returns the result code and, in rewrite mode,
the output path.

## GetVolumeInfo

```csharp
public static VolumeInfo GetVolumeInfo(string isoPath)
public static VolumeInfo GetVolumeInfo(Stream imageStream, string imageName = "memory") // TODO #19
```

Reads the volume descriptor **without throwing** on validation errors. Returns a
`VolumeInfo` record:

| Member | Type | Meaning |
|---|---|---|
| `IsValid` | `bool` | Header magic found |
| `RootDirSector` | `uint` | Root directory table sector |
| `RootDirSize` | `uint` | Root directory table size (bytes) |
| `DiscLseek` | `long` | Detected disc offset |
| `DiscFormat` | `string` | Friendly disc-layout identity from `DiscLseek`: `RAW`, `GLOBAL (XGD2)`, `XGD3`, `XGD2 Hybrid`, `XGD1`, or `Unknown` (invalid volumes always report `Unknown`) |
| `FileLength` | `long` | File size |
| `TotalSectors` | `long` | Total sectors |

## ListDirectory / ListDirectoryFlat / GetEntryInfo

```csharp
public static IReadOnlyList<EntryInfo> ListDirectory(string isoPath, string internalPath = "/")
public static IReadOnlyList<EntryInfo> ListDirectory(Stream imageStream, string imageName, string internalPath = "/") // TODO #19
public static IReadOnlyList<string> ListDirectoryFlat(string isoPath, string internalPath = "/")
public static EntryInfo? GetEntryInfo(string isoPath, string internalPath)
public static EntryInfo? GetEntryInfo(Stream imageStream, string imageName, string internalPath) // TODO #19
```

- `internalPath` uses forward slashes, e.g. `"/"`, `"/subdir"`, `"/subdir/file.bin"`.
- `ListDirectory` returns `EntryInfo` records: `Name`, `IsDirectory`, `StartSector`,
  `FileSize`, `Attributes`, `LeftChildOffset`, `RightChildOffset`.
- `ListDirectoryFlat` returns just the entry names — the library behind the CLI's
  `--ls` flag.
- Throws `InvalidDataException` when a path does not exist.
- `GetEntryInfo` returns `null` for a missing path.

## GetSectorLayout

```csharp
public static SectorLayout GetSectorLayout(string isoPath)
```

Explicit sector layout (xdvdfs #49, TODO #4) — the library behind low-level disk
analysis and the allocator input for in-place patching (TODO #5):

```csharp
public record FileSectorExtent(string Path, bool IsDirectory, uint StartSector, uint SectorCount, uint FileSize);
public record SectorRange(uint StartSector, uint SectorCount);
public record SectorLayout(VolumeInfo Volume, IReadOnlyList<FileSectorExtent> Entries,
    IReadOnlyList<SectorRange> UsedRanges, IReadOnlyList<SectorRange> FreeRanges, long TotalSectors);
```

- All sectors are partition-relative (same numbering as `EntryInfo.StartSector`).
- `Entries`: one extent per file plus one per directory table (`IsDirectory`,
  `FileSize` = table byte size), sorted by start sector. Empty files appear with
  `SectorCount` 0.
- `UsedRanges`: merged allocated ranges (volume header + tables + file data).
- `FreeRanges`: unallocated gaps; used + free tile `[0, TotalSectors)` exactly.
- Throws `XisoFormatException` on invalid images and on corrupt tables
  (out-of-image extents, cycles) with `invalid TOC entry` messages.

## CopyOut

```csharp
public static void CopyOut(string isoPath, string internalPath, string destPath,
    UnpackOptions? options = null, CancellationToken cancellationToken = default,
    IProgress<ProgressInfo>? progress = null)
public static void CopyOut(Stream imageStream, string imageName, string internalPath, string destPath, // TODO #19
    UnpackOptions? options = null, CancellationToken cancellationToken = default,
    IProgress<ProgressInfo>? progress = null)
```

Copies one file — or an entire directory, recursively — out of the image to
`destPath` without a full extraction. With `SkipExisting`, up-to-date destinations
are skipped per [Resume interrupted unpacks](#resume-interrupted-unpacks).
Each copied file reports per-chunk `FileProgress` events on `progress`.

## CopyIn

```csharp
public static void CopyIn(string isoPath, string hostFile, string internalPath,
    bool createBackup = true)
// same behavior via XisoPatcher.CopyIntoImage(isoPath, hostFile, internalPath, createBackup)
```

Copies one host file **into** the image, modifying it in place — the reverse of
`CopyOut` (xdvdfs #165, TODO #5). Replaces `internalPath` when it exists, or
adds it as a new file when only its parent directory exists (paths work like the
reader APIs: `/`-separated, case-insensitive). Copying directories in is not
supported: a host *directory* fails fast with `InvalidDataException` (not the
`FileNotFoundException` a missing host file raises), before anything is
written.

Two cases, chosen automatically (image size never changes):

- **Fits the existing allocation** — data is overwritten in place (`0xFF` tail
  fill) and only the parent table's 8-byte entry record (start sector + file
  size) is updated. Every other byte is untouched.
- **Larger, or a new file** — a free run is allocated via `SectorAllocator`
  seeded from `GetSectorLayout`, and the parent table is re-serialized with
  `DirectoryEntryTableWriter`. If the grown table no longer fits, it moves and
  only the grandparent link (or the volume-header root fields, when the root
  table moves) is updated — the cascade is exactly one table deep.

A `<iso>.old` backup of the pre-patch image is written first (replacing any
previous backup) unless `createBackup` is false. Copied-in `.xbe` files get the
same media-enable patch as packed ones; the volume timestamp is preserved.
`InvalidDataException` covers malformed paths, missing parents, directory
targets, and out-of-space (`need N sectors but only M free`); corrupt images
fail with `XisoFormatException` before anything is written. CLI: `--copy-in
<iso> <host> <path>` (`--no-backup` skips the backup).

## ComputeFileHash / ComputeDirectoryHashes

```csharp
public static byte[]? ComputeFileHash(
    string isoPath, string internalPath, HashAlgorithmName algorithm)
public static byte[]? ComputeFileHash( // TODO #19
    Stream imageStream, string imageName, string internalPath, HashAlgorithmName algorithm)

public static IReadOnlyList<(string Path, byte[] Hash)> ComputeDirectoryHashes(
    string isoPath, string internalPath, HashAlgorithmName algorithm)
```

- Hash one file (`null` if not found) or every file under a directory (recursive).
- Any `HashAlgorithmName` works — the CLI exposes `--md5` and `--sha256`.

## GetXexInfo

```csharp
public static XexInfo? GetXexInfo(string isoPath, string internalPath)
public static XexInfo? GetXexInfo(Stream imageStream, string imageName, string internalPath) // TODO #19
```

Parses the Xbox 360 executable (XEX2) header of a `.xex` file inside the image. All
fields are read big-endian per the XEX2 specification (see `xex2_info.h` in
[xenia](https://github.com/xenia-project/xenia)):

| `XexInfo` member | Meaning |
|---|---|
| `ModuleFlags` | Title / DLL / user-mode etc. bit flags |
| `HeaderSize` | XEX header region size (typically `0x4000`) |
| `EntryPoint` | Entry point RVA (optional header) |
| `ImageBaseAddress` | Image base address (optional header) |
| `ImageSize` / `LoadAddress` | Security info |
| `Region` | NTSC-U / NTSC-J / PAL bit flags |
| `AllowedMediaTypes` | Media type bitmask (hard disk, DVD-9, …) |
| `MediaId` / `TitleId` / `Version` | Execution info |
| `Platform` / `DiscNumber` / `DiscCount` | Execution info |
| `EncryptionType` / `CompressionType` | File format info |

Returns `null` when the path does not exist, points to a directory, or the file is not
an XEX2 executable. Validated against retail Xbox 360 Redump images (`Perfect Dark
Zero`, `Payday 2`). The CLI exposes this as `--xex-info`.

## GetXbeInfo

```csharp
public static XbeInfo? GetXbeInfo(string isoPath, string internalPath)
public static XbeInfo? GetXbeInfo(Stream imageStream, string imageName, string internalPath)
```

Parses the original-Xbox executable (XBEH) header + certificate of a `.xbe` file
inside the image — the OG-Xbox counterpart to `GetXexInfo`, which no reference
tool offers. All fields are read little-endian per the XBE specification (see
`xbe.h` in Cxbx-Reloaded):

| `XbeInfo` member | Meaning |
|---|---|
| `BaseAddress` | Image base address (retail: `0x00010000`) |
| `EntryPoint` | Entry point address |
| `SectionCount` / `InitFlags` | Section count, init flags |
| `CertSize` | Certificate size (retail: 464, `0x1D0` — anything else is rejected) |
| `CertTimeDate` | Certificate timestamp (raw DWORD) |
| `TitleId` | Title ID |
| `TitleName` | Title name (UTF-16, up to 40 chars) |
| `AlternateTitleIds` | 16 alternate title IDs (usually zero) |
| `AllowedMedia` | Hard disk / DVD-CD / DVD-5-RO / DVD-9-RO / DVD-5-RW / DVD-9-RW / dongle / media board |
| `GameRegion` | North America / Japan / rest-of-world bitmask (`0x07` = worldwide) |
| `GameRatings` | Ratings bitmask (raw DWORD) |
| `DiskNumber` / `Version` | Multi-disc number, game version (raw DWORD) |

The certificate address is a load pointer (file offset = address − base);
out-of-range, below-base, and truncated certificates return `null` instead of
reading out of bounds. Returns `null` when the path does not exist, points to
a directory, or the file is not an XBEH executable. The CLI exposes this as
`--xbe-info`; `XisoExplorer` has a matching `GetXbeInfo` method.

## Checksum (SHA3-256)

```csharp
public static byte[] ComputeImageChecksum(string isoPath, CancellationToken ct = default);
public static string ComputeImageChecksumHex(string isoPath, CancellationToken ct = default);
```

In `XISOSharp` namespace via `XisoChecksum` (xdvdfs `checksum` compat): deterministic **SHA3-256** over `SortedDictionary Ordinal` `/DIR/FILE` UTF-8 path bytes + streamed file data (`IncrementalHash SHA3_256`, BCL on .NET 8+). NOT SHA256 of full image. CLI `checksum` prints `hex tab path`. See [xdvdfs Compat](xdvdfs-compat.md#checksum).

## Archival (Redump) — via XisoRedump / XisoOperations / XisoRanges / XboxPrng

```csharp
// Video / update
bool XisoRedump.TryExtractVideo(string redumpPath, string? outputVideoPath, out string? outPath);
bool XisoRedump.TryExtractUpdate(string redumpPath, string? outputUpdatePath, string? outputVideoPath = null);

// Filler / seed / wipe / trim / petrify
byte[] XisoOperations.ExtractFiller(string isoPath, long isoOffset = 0);
uint? XisoOperations.ExtractSeed(string isoPath); // XGD1 only, brute-force
int XisoOperations.WipeFiller(string inputPath, string outputPath);
int XisoOperations.TrimXiso(string inputPath, string? outputPath);
int XisoSkeleton.Petrify(string inputPath, string? skeletonPath, string? hashPath);

// Rebuild lossless
int XisoRedump.RebuildRedump(string xisoPath, string videoPath, string? fillerOrSeedPath, string? updatePath, string outputRedumpPath, int[]? securitySectors = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

// Ranges
(IReadOnlyList<(long Start,long End)> SysRanges, IReadOnlyList<(long Start,long End)> FileRanges) XisoRanges.GetXisoRanges(string isoPath, long isoOffset = 0);
IReadOnlyList<(long Start,long End)> XisoRanges.MergeRanges(IEnumerable<(long,long)> a, IEnumerable<(long,long)> b);
IReadOnlyList<(string Path, long Offset, uint Size)> XisoRanges.CollectFileEntries(FileStream fs, long isoOffset);
```

All ported from `LibXGD/XGD.cs:11` tables (`XgdTables.cs`) + `XDVDFS.cs` (`GetValidSectors`/`ProcessXISO`). See [Archival](archival.md) and [xdvdfs Compat](xdvdfs-compat.md).

## Block-Device overloads

```csharp
public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(IBlockDevice dev, string isoName, int? skipSectors = null);
public static AuditResult AuditXiso(IBlockDevice dev);
public static IReadOnlyList<EntryInfo> ListDirectory(IBlockDevice dev, string internalPath = "/");
```

`IBlockDevice` stack under `XISOSharp.Core/BlockDevice/` (`FileBlockDevice`/`MemoryBlockDevice`/`OffsetBlockDevice`/`CisoBlockDevice`). Mirror `xdvdfs-core/src/blockdev.rs` — enables in-memory golden fixtures + CISO random-access via `CisoBlockDevice` single-sector cache without `no_std` target. All `FileStream` overloads delegate to these.

## AuditXiso

```csharp
public static AuditResult AuditXiso(string isoPath)
```

Deep integrity audit — the library behind the CLI's `-V` flag:

- header magic at all known offsets
- optimized-tag presence at offset 31337
- full directory tree walk with **cycle detection**
- sector bounds for every entry (file and directory)
- reserved attribute bits (`0x08`, `0x40`)
- filename validity

Returns `AuditResult` (`IsValid`, `FilesChecked`, `DirsChecked`, `Issues`).

## Repair

```csharp
public static RepairResult Repair(string isoPath, bool createBackup = true, bool dryRun = false)
// same behavior via XisoRepairer.RepairInPlace(isoPath, createBackup, dryRun)
```

Fixes the audit's safely-patchable (class-C) issues in place — the only repair
verb any of the reference tools offers (TODO #26, Phase 1):

- **Reserved attribute bits** — the entry's attribute byte is rewritten with
  `Constants.MaskAttributes` (`& 0xB7`); readers already mask, so this is pure
  normalization.
- **Missing optimized tag** — the exact tag bytes `CreateXiso` writes are
  stored at offset 31337 (only when the file is long enough to hold them).
- **Separators in filenames** — length-preserving `_` substitution (the name
  length byte is untouched, so the table layout cannot shift). When two names
  would collide, the entry is left alone and reported.

Every patch is length-preserving: the image size never changes and every other
byte is untouched. Pointers from records with reserved bits are not trusted
(the collect walk skips descending into them until their fix is decided, then
converges over bounded passes), so a corrupt directory entry can never send
repairs wandering into file data. Each pass ends with a re-audit — repairing a
clean image is a no-op success.

Truncation, structural (depth/chain/cycle), and refused (bad magic/root)
classes are reported, never patched (Phase 2 salvage rebuild). CISO
containers and split parts are refused with `InvalidDataException`
(decompress/reassemble first); invalid images fail with `XisoFormatException`
before anything is written. A `<iso>.old` backup is written first (replacing
any previous backup) unless `createBackup` is false. Returns `RepairResult`
(`Fixed`, `Remaining`, `BackupPath`, `DryRun`; `Success` when the image now
passes). CLI: `--repair <file>` (`--dry-run` previews; `--no-backup` skips the
backup).

## Salvage

```csharp
public static SalvageResult Salvage(string sourcePath, string? outputPath = null)
// same behavior via XisoSalvager.Salvage(sourcePath, outputPath)
```

Rebuilds a readable image from a corrupt one — the answer to the audit's
truncation and structural classes, which no in-place patch can fix (TODO #26,
Phase 2):

- A bounded walk reusing the auditor's hardening limits (depth cap, per-table
  entry cap, cycle tracking) copies every entry reachable without tripping a
  truncation or structural gate into a staging directory, then repacks it
  through the `CreateXiso` pipeline into a fresh plain `.iso`.
- Class-C quirks do not block salvage: reserved bits are masked and path
  separators in names are sanitized to `_`. Anything uncarriable is reported
  in `Skipped`: unreadable entries, orphaned right-link tails,
  separator-collision (or host-unusable) names, and subtrees past the depth
  gate (dropped whole — carrying them would rebuild an image that still fails
  the audit).
- The source is only ever read, never written: no backup is needed and CISO
  input is allowed (reads go through the decompressed view). Split parts read
  as the truncated images they are. A missing tree root (class R) throws
  `XisoFormatException` — there is nothing to salvage with.
- The rebuilt image is re-audited; `OutputIssues` carries the verdict
  (`Success` when it passes). A salvage that still fails is reported, not
  hidden.

`outputPath` defaults to the source's directory plus the source stem with a
`.salvaged.iso` suffix (`XisoSalvager.DefaultOutputPath`); an existing file is
overwritten and a missing parent directory is created. Returns
`SalvageResult` (`Copied`, `Skipped`, `OutputPath`, `OutputIssues`). CLI:
`--salvage <file>` (`--repair-out <path>` overrides the output; existing
files follow the `-y`/`-n` convention).

## Disc offset probing

`VerifyXiso` probes the header at these offsets, in order (via `Constants.cs:127` + `XgdTables.cs` + `IBlockDevice` offset probes):

| # | Offset | Layout |
|---|---|---|
| 1 | `0x00000000` | RAW |
| 2 | `0x0FD90000` | GLOBAL / XGD2 |
| 3 | `0x02080000` | XGD3 |
| 4 | `0x89D80000` | **Hybrid (XGD2-Hybrid)** — native since 2026-08-26 (`Xgd2HybridLseekOffset`) |
| 5 | `0x18300000` | XGD1 |

When `skipSectors` is provided, probing is skipped and the header must be at
`skipSectors × 2048 + 0x10000`.

See also: [Library Overview](library.md) · [XisoWriter API](api-xisowriter.md) ·
[Utilities & Types](api-utilities.md) · [XISO Format](xiso-format.md)
