# XisoReader API

`XisoReader` (`XISOSharp` namespace) is a **static class** providing all read-side
operations: verification, extraction, listing, tree traversal, rewriting, volume info,
directory listing, auditing, hashing, and copy-out.

- [Method index](#method-index)
- [VerifyXiso](#verifyxiso)
- [Extract / List / Tree / Rewrite](#extract--list--tree--rewrite)
- [Resume interrupted unpacks](#resume-interrupted-unpacks)
- [DecodeXiso (main entry)](#decodexiso-main-entry)
- [DecodeXisoAsync](#decodexisoasync)
- [GetVolumeInfo](#getvolumeinfo)
- [ListDirectory / GetEntryInfo](#listdirectory--getentryinfo)
- [CopyOut](#copyout)
- [ComputeFileHash / ComputeDirectoryHashes](#computefilehash--computedirectoryhashes)
- [AuditXiso](#auditxiso)
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
| `GetVolumeInfo` | Volume descriptor metadata without throwing |
| `ListDirectory` | Metadata of entries in a directory |
| `ListDirectoryFlat` | Entry **names** of a directory (non-recursive convenience) |
| `GetEntryInfo` | Metadata of one entry by path |
| `CopyOut` | Copy one file or directory out of an image |
| `ComputeFileHash` | Hash one file (MD5/SHA-256/… via `HashAlgorithmName`) |
| `ComputeDirectoryHashes` | Hash every file under a path |
| `GetXexInfo` | Parse the Xbox 360 XEX2 header of a `.xex` file |
| `AuditXiso` | Deep integrity audit |

## VerifyXiso

```csharp
public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
    FileStream fs, string isoName, int? skipSectors = null)
```

Verifies the header magic at all known disc offsets (or at the `skipSectors` offset
when given) and returns the root directory table location and the detected disc offset.

| Parameter | Meaning |
|---|---|
| `fs` | Open, readable `FileStream` positioned anywhere |
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
| `progress` | Rewrite: structured progress channel (`IProgress<ProgressInfo>`) — see [XisoWriter API](api-xisowriter.md#structured-progress-iprogresprogressinfo). Extract: a `FileAdded` event per file actually written (skipped/excluded files are silent) |
| `options` | Extract/unpack only: resume options — see [Resume interrupted unpacks](#resume-interrupted-unpacks) |

`UnpackImage` is the convenience form of `Extract`: it probes the optimized-tag marker
to pick `llCompat` automatically and defaults the output directory to the ISO name
(minus `.iso`), so callers never need to know the image layout.

All return 0 on success.

## Resume interrupted unpacks

```csharp
public sealed class UnpackOptions
{
    public bool SkipExisting { get; set; }
    public bool ShouldSkip(string destPath, long fileSize);
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
```

Reads the volume descriptor **without throwing** on validation errors. Returns a
`VolumeInfo` record:

| Member | Type | Meaning |
|---|---|---|
| `IsValid` | `bool` | Header magic found |
| `RootDirSector` | `uint` | Root directory table sector |
| `RootDirSize` | `uint` | Root directory table size (bytes) |
| `DiscLseek` | `long` | Detected disc offset |
| `FileLength` | `long` | File size |
| `TotalSectors` | `long` | Total sectors |

## ListDirectory / ListDirectoryFlat / GetEntryInfo

```csharp
public static IReadOnlyList<EntryInfo> ListDirectory(string isoPath, string internalPath = "/")
public static IReadOnlyList<string> ListDirectoryFlat(string isoPath, string internalPath = "/")
public static EntryInfo? GetEntryInfo(string isoPath, string internalPath)
```

- `internalPath` uses forward slashes, e.g. `"/"`, `"/subdir"`, `"/subdir/file.bin"`.
- `ListDirectory` returns `EntryInfo` records: `Name`, `IsDirectory`, `StartSector`,
  `FileSize`, `Attributes`, `LeftChildOffset`, `RightChildOffset`.
- `ListDirectoryFlat` returns just the entry names — the library behind the CLI's
  `--ls` flag.
- Throws `InvalidDataException` when a path does not exist.
- `GetEntryInfo` returns `null` for a missing path.

## CopyOut

```csharp
public static void CopyOut(string isoPath, string internalPath, string destPath,
    UnpackOptions? options = null, CancellationToken cancellationToken = default)
```

Copies one file — or an entire directory, recursively — out of the image to
`destPath` without a full extraction. With `SkipExisting`, up-to-date destinations
are skipped per [Resume interrupted unpacks](#resume-interrupted-unpacks).

## ComputeFileHash / ComputeDirectoryHashes

```csharp
public static byte[]? ComputeFileHash(
    string isoPath, string internalPath, HashAlgorithmName algorithm)

public static IReadOnlyList<(string Path, byte[] Hash)> ComputeDirectoryHashes(
    string isoPath, string internalPath, HashAlgorithmName algorithm)
```

- Hash one file (`null` if not found) or every file under a directory (recursive).
- Any `HashAlgorithmName` works — the CLI exposes `--md5` and `--sha256`.

## GetXexInfo

```csharp
public static XexInfo? GetXexInfo(string isoPath, string internalPath)
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
