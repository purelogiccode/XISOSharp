# XisoReader API

`XisoReader` (`XISOSharp` namespace) is a **static class** providing all read-side
operations: verification, extraction, listing, tree traversal, rewriting, volume info,
directory listing, auditing, hashing, and copy-out.

- [Method index](#method-index)
- [VerifyXiso](#verifyxiso)
- [Extract / List / Tree / Rewrite](#extract--list--tree--rewrite)
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

## Extract / List / Tree / Rewrite

```csharp
public static int Extract(
    string xisoPath, string? outputPath, bool llCompat,
    CancellationToken cancellationToken = default, int? skipSectors = null)

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
| `progress` | Rewrite only: structured progress channel (`IProgress<ProgressInfo>`) — see [XisoWriter API](api-xisowriter.md#structured-progress-iprogresprogressinfo) |

All return 0 on success.

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
    IProgress<ProgressInfo>? progress = null)
```

The generic entry point used by the wrappers above. `mode` is one of `ExtractMode`:
`Extract`, `List`, `Tree`, `Rewrite`, `GenerateAvl` (internal use), or `Verify`.

## DecodeXisoAsync

```csharp
public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
    string xisoPath, string? outputPath, ExtractMode mode,
    bool llCompat = false, CancellationToken cancellationToken = default,
    string? outputName = null, int? skipSectors = null, int? prependSectors = null,
    IProgress<ProgressInfo>? progress = null)
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
public static void CopyOut(string isoPath, string internalPath, string destPath)
```

Copies one file — or an entire directory, recursively — out of the image to
`destPath` without a full extraction.

## ComputeFileHash / ComputeDirectoryHashes

```csharp
public static byte[]? ComputeFileHash(
    string isoPath, string internalPath, HashAlgorithmName algorithm)

public static IReadOnlyList<(string Path, byte[] Hash)> ComputeDirectoryHashes(
    string isoPath, string internalPath, HashAlgorithmName algorithm)
```

- Hash one file (`null` if not found) or every file under a directory (recursive).
- Any `HashAlgorithmName` works — the CLI exposes `--md5` and `--sha256`.

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

`VerifyXiso` probes the header at these offsets, in order:

| # | Offset | Layout |
|---|---|---|
| 1 | `0x00000000` | RAW |
| 2 | `0x0FD90000` | GLOBAL / XGD2 |
| 3 | `0x02080000` | XGD3 |
| 4 | `0x18300000` | XGD1 |

When `skipSectors` is provided, probing is skipped and the header must be at
`skipSectors × 2048 + 0x10000`.

See also: [Library Overview](library.md) · [XisoWriter API](api-xisowriter.md) ·
[Utilities & Types](api-utilities.md) · [XISO Format](xiso-format.md)
