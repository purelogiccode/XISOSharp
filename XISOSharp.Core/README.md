# XISOSharp

[![NuGet](https://img.shields.io/nuget/v/XISOSharp.svg)](https://www.nuget.org/packages/XISOSharp/)
[![NuGet](https://img.shields.io/nuget/dt/XISOSharp.svg)](https://www.nuget.org/packages/XISOSharp/)

A pure C# class library for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images. A direct conversion of the [extract-xiso](https://github.com/XboxDev/extract-xiso) tool (v2.7.1) from C to C#. All logic — including the AVL tree, Boyer-Moore search, XISO header verification, directory traversal, format rewriting, and media-enable patching — is ported directly from the reference C implementation to produce byte-identical output.

---

## Table of Contents

- [Installation](#installation)
- [Supported Disc Formats](#supported-disc-formats)
- [Quick Start](#quick-start)
- [API Reference](#api-reference)
  - [XisoReader](#xisoreader)
  - [XisoWriter](#xisowriter)
  - [Logger](#logger)
  - [AvlTree](#avltree)
  - [BoyerMoore](#boyermoore)
  - [FileTimeHelper](#filetimehelper)
  - [Constants](#constants)
- [Data Types](#data-types)
  - [Enums](#enums)
  - [Classes](#classes)
  - [Delegates](#delegates)
  - [Exceptions](#exceptions)
- [Usage Examples](#usage-examples)
  - [Extracting Files from an XISO](#extracting-files-from-an-xiso)
  - [Listing Files in an XISO](#listing-files-in-an-xiso)
  - [Rewriting an XISO](#rewriting-an-xiso)
  - [Creating an XISO from a Directory](#creating-an-xiso-from-a-directory)
  - [Progress Reporting](#progress-reporting)
  - [Cancellation Support](#cancellation-support)
  - [Suppressing Output](#suppressing-output)
  - [Disabling Media-Enable Patching](#disabling-media-enable-patching)
  - [Skipping System Update Folders](#skipping-system-update-folders)
  - [Redirecting Log Output](#redirecting-log-output)
  - [Graphical Progress (WPF / Blazor)](#graphical-progress-wpf--blazor)
- [Error Handling](#error-handling)
- [Thread Safety](#thread-safety)
- [Compatibility](#compatibility)
- [Performance](#performance)
- [License](#license)

---

## Installation

```
dotnet add package XISOSharp
```

The package is strong-name signed and includes XML documentation, Source Link for debugging, and `.snupkg` symbol packages for all target frameworks.

---

## Supported Disc Formats

| Format | Description | Lseek Offset |
|--------|-------------|--------------|
| **RAW** | Raw XISO (no offset) | `0` |
| **GLOBAL** | Retail/Xbox Live discs | `0x0FD90000` |
| **XGD2** | Xbox 360 XGD2 discs (same as GLOBAL) | `0x0FD90000` |
| **XGD3** | Xbox 360 XGD3 discs | `0x02080000` |
| **XGD1** | Xbox 360 XGD1 discs | `0x18300000` |

The library automatically detects the disc format during verification by probing each known offset.

---

## Quick Start

```csharp
using XISOSharp;

// Extract all files from an XISO image
XisoReader.DecodeXiso("game.iso", "output_folder", ExtractMode.Extract, out _);

// List all files in an XISO image (prints to console)
XisoReader.DecodeXiso("game.iso", null, ExtractMode.List, out _);

// Create an XISO image from a directory
XisoWriter.CreateXiso("source_folder", "output_folder", null, null, out _, "game.iso", null);
```

---

## API Reference

### XisoReader

Static class for reading and processing XISO disc images.

#### `DecodeXiso`

Main entry point for processing an XISO image. Verifies the image header, then performs extraction, listing, or rewriting based on the specified mode.

```csharp
public static int DecodeXiso(
    string xisoPath,
    string? outputPath,
    ExtractMode mode,
    out string? outIsoPath,
    bool llCompat = false,
    CancellationToken cancellationToken = default,
    string? outputName = null)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `xisoPath` | `string` | Path to the XISO file to process. |
| `outputPath` | `string?` | Output directory for extraction or rewrite output. When `null` in extract mode, a directory named after the ISO is created. |
| `mode` | `ExtractMode` | Operating mode: `Extract`, `List`, or `Rewrite`. |
| `outIsoPath` | `out string?` | Receives the path to the output ISO file when in rewrite mode. |
| `llCompat` | `bool` | If `true`, uses backwards-compatible (non-optimized) right-offset calculation. Defaults to `false`. |
| `cancellationToken` | `CancellationToken` | Token to monitor for cancellation requests. |
| `outputName` | `string?` | Custom output filename for rewrite mode. When `null`, the original filename with `.iso` extension is used. |

**Returns**: `0` on success, non-zero on error.

**Exceptions**:
- `FileNotFoundException` — input file does not exist
- `InvalidDataException` — file is not a valid XISO image
- `IOException` — read errors
- `ExtractErrorException` — non-fatal extraction error (see error code)

#### `DecodeXisoAsync`

Asynchronous wrapper around `DecodeXiso` that runs the synchronous engine on a thread pool thread.

```csharp
public static async Task<(int Result, string? OutIsoPath)> DecodeXisoAsync(
    string xisoPath,
    string? outputPath,
    ExtractMode mode,
    bool llCompat = false,
    CancellationToken cancellationToken = default)
```

**Returns**: A tuple containing `Result` (0 on success) and `OutIsoPath` (the output ISO path in rewrite mode).

#### `VerifyXiso`

Low-level method that validates the XISO header and returns root directory metadata. Most users should use `DecodeXiso` instead.

```csharp
public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(
    FileStream fs, string isoName)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `fs` | `FileStream` | Open file stream positioned anywhere. |
| `isoName` | `string` | Display name for error messages. |

**Returns**: Tuple of `(rootDirSector, rootDirSize, discLseek)`.

**Exceptions**:
- `InvalidDataException` — no valid XISO header found or trailing magic byte mismatch
- `IOException` — file too short to contain expected header data
- `ExtractErrorException` — root directory sector and size are both zero (empty ISO)

#### `Tree`

Recursively lists all files in an XISO image in a tree format, showing full paths and sizes.

```csharp
public static int Tree(
    string xisoPath,
    bool llCompat = false,
    CancellationToken cancellationToken = default)
```

#### `GetVolumeInfo`

Reads the XISO volume descriptor and returns metadata about the image without throwing on validation errors.

```csharp
public static VolumeInfo GetVolumeInfo(string isoPath)
```

**Returns**: A `VolumeInfo` record containing `IsValid`, `RootDirSector`, `RootDirSize`, `DiscLseek`, `FileLength`, and `TotalSectors`.

#### `ListDirectory`

Returns metadata about all entries in the specified directory within an XISO image.

```csharp
public static IReadOnlyList<EntryInfo> ListDirectory(string isoPath, string internalPath = "/")
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `isoPath` | `string` | Path to the XISO file. |
| `internalPath` | `string` | Path within the ISO (e.g. `"/"` for root, `"/subdir"` for a subdirectory). |

**Returns**: List of `EntryInfo` records, each containing `Name`, `IsDirectory`, `StartSector`, `FileSize`, `Attributes`, `LeftChildOffset`, `RightChildOffset`.

#### `GetEntryInfo`

Returns metadata about a specific file or directory entry within an XISO image.

```csharp
public static EntryInfo? GetEntryInfo(string isoPath, string internalPath)
```

**Returns**: An `EntryInfo` record, or `null` if the path does not exist.

#### `CopyOut`

Copies a single file or directory from an XISO image to the local filesystem. If the path points to a file, it is extracted to `destPath`. If the path points to a directory, all its contents are recursively extracted.

```csharp
public static void CopyOut(string isoPath, string internalPath, string destPath)
```

**Exceptions**:
- `InvalidDataException` — path does not exist in the XISO
- `IOException` — read or write errors

#### `ComputeFileHash`

Computes the hash of a single file within an XISO image.

```csharp
public static byte[]? ComputeFileHash(string isoPath, string internalPath, HashAlgorithmName algorithm)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `isoPath` | `string` | Path to the XISO file. |
| `internalPath` | `string` | Path within the ISO (e.g. `"/subdir/file.xbe"`). |
| `algorithm` | `HashAlgorithmName` | Hash algorithm to use (`HashAlgorithmName.MD5` or `HashAlgorithmName.SHA256`). |

**Returns**: Hash bytes, or `null` if the file does not exist.

#### `ComputeDirectoryHashes`

Computes hashes for all files in a directory (or the entire image) within an XISO.

```csharp
public static List<(string Path, byte[] Hash)> ComputeDirectoryHashes(
    string isoPath, string internalPath, HashAlgorithmName algorithm)
```

**Returns**: List of `(path, hash)` tuples for all files.

#### `AuditXiso`

Performs a deep integrity audit of an XISO image. Validates the header, walks the entire directory tree, checks sector bounds, detects cycles, validates filenames and attributes, and verifies the optimized tag.

```csharp
public static AuditResult AuditXiso(string isoPath)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `isoPath` | `string` | Path to the XISO file to audit. |

**Returns**: An `AuditResult` record with `IsValid`, `FilesChecked`, `DirsChecked`, and `Issues`.

**Exceptions**:
- `FileNotFoundException` — input file does not exist
- `IOException` — read errors

---

Static class for creating and rewriting XISO disc images.

#### `CreateXiso`

Creates a new XISO from a local directory, or rewrites an existing ISO using a pre-built AVL tree.

```csharp
public static int CreateXiso(
    string rootDirectory,
    string? outputDirectory,
    AvlNode? inRoot,
    Stream? sourceStream,
    out string? outIsoPath,
    string? inName,
    ProgressCallback? progressCallback = null,
    CancellationToken cancellationToken = default)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `rootDirectory` | `string` | Source directory for creation, or base name for rewrite mode. |
| `outputDirectory` | `string?` | Directory where the output ISO is written. When `null`, the current working directory is used. |
| `inRoot` | `AvlNode?` | Pre-built AVL tree root. When `null`, the tree is generated from the file system. |
| `sourceStream` | `Stream?` | Source ISO stream for reading file data in rewrite mode; `null` when creating from a file system. |
| `outIsoPath` | `out string?` | Receives the full path of the created output ISO file. |
| `inName` | `string?` | Explicit output filename. When `null`, the directory name plus `.iso` is used. |
| `progressCallback` | `ProgressCallback?` | Optional callback invoked with `(currentBytes, totalBytes)` during write. |
| `cancellationToken` | `CancellationToken` | Token to monitor for cancellation requests. |

**Returns**: `0` on success, `1` on error.

#### `CreateXisoAsync`

Asynchronous wrapper around `CreateXiso`.

```csharp
public static async Task<(int Result, string? OutIsoPath)> CreateXisoAsync(
    string rootDirectory,
    string? outputDirectory,
    AvlNode? inRoot,
    Stream? sourceStream,
    string? inName,
    ProgressCallback? progressCallback = null,
    CancellationToken cancellationToken = default)
```

**Returns**: A tuple containing `Result` (0 on success) and `OutIsoPath`.

---

### Logger

Static class providing configurable text output. All output is thread-safe and can be redirected.

#### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Out` | `TextWriter` | `Console.Out` | Writer for normal output. Set to `TextWriter.Null` to discard. |
| `Error` | `TextWriter` | `Console.Error` | Writer for error output. Set to `TextWriter.Null` to discard. |

#### Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Quiet` | `bool` | `false` | When `true`, suppresses all non-error output. |
| `RealQuiet` | `bool` | `false` | When `true`, suppresses all output including errors. |
| `Warned` | `bool` | `false` | Set to `true` when a warning is issued during processing. |
| `TotalBytes` | `long` | `0` | Cumulative bytes written across the current operation. |
| `TotalFiles` | `int` | `0` | Cumulative files processed in the current operation. |
| `TotalBytesAllIsos` | `long` | `0` | Cumulative bytes across all processed ISO images. |
| `TotalFilesAllIsos` | `int` | `0` | Cumulative file count across all processed ISO images. |
| `RemoveSystemUpdate` | `bool` | `false` | When `true`, files in `$SystemUpdate` folders are skipped. |
| `MediaEnable` | `bool` | `true` | When `true`, `.xbe` files are automatically patched for media-enable during creation/rewrite. |
| `XboxDiscLseek` | `long` | `0` | Disc lseek offset detected during verification, used in rewrite mode. |

#### Methods

```csharp
// Writes a formatted message to Out (unless Quiet is true).
public static void Log(string message, params object?[] args)

// Writes a line to Out (unless Quiet is true). No format arguments.
public static void LogLine(string message)

// Flushes Out (unless Quiet is true).
public static void Flush()

// Writes a formatted error message to Error (unless RealQuiet is true).
public static void LogErr(string message, params object?[] args)
```

---

### AvlTree

Static class providing AVL (Adelson-Velsky/Landis) balanced binary search tree operations. Used internally for XISO directory indexing but exposed for advanced scenarios.

#### `AvlCompareKey`

Compares two strings case-insensitively using ASCII rules.

```csharp
public static int AvlCompareKey(string lhs, string rhs)
```

#### `AvlFetch`

Looks up a node in the AVL tree by filename.

```csharp
public static AvlNode? AvlFetch(AvlNode? root, string filename)
```

**Returns**: The matching `AvlNode` or `null` if not found.

#### `AvlInsert`

Inserts a node into the AVL tree, rebalancing as needed. Duplicate filenames are rejected.

```csharp
public static AvlResult AvlInsert(ref AvlNode? root, AvlNode node)
```

**Returns**:
- `AvlResult.AvlBalanced` — tree grew taller
- `AvlResult.NoErr` — insertion completed without height change
- `AvlResult.AvlError` — duplicate key

#### `AvlTraverseDepthFirst`

Traverses the AVL tree depth-first in the specified order.

```csharp
public static int AvlTraverseDepthFirst(
    AvlNode? root,
    TraversalCallback callback,
    object? context,
    AvlTraversalMethod method,
    int depth)
```

**Returns**: `0` if the full traversal completed, or the non-zero value returned by the callback (early termination).

#### `FreeTree`

Frees an entire AVL tree by clearing all node references so the garbage collector can reclaim memory.

```csharp
public static void FreeTree(AvlNode? root)
```

---

### BoyerMoore

Implements the Boyer-Moore string search algorithm for efficient pattern matching in byte arrays. Used internally for media-enable patching of `.xbe` files.

```csharp
public class BoyerMoore
{
    public BoyerMoore(byte[] pattern, int alphabetSize = 256)
    public void Init()
    public int Search(byte[] text, int startIndex, int length)
    public int Search(byte[] text)
    public void Done()
}
```

| Member | Description |
|--------|-------------|
| Constructor | Initializes a new pattern matcher with the given pattern and alphabet size. |
| `Init()` | Builds the bad-character and good-suffix shift tables. Must be called before `Search`. |
| `Search(byte[], int, int)` | Searches for the pattern within a subrange of the text buffer. Returns the index of the first match, or `-1`. |
| `Search(byte[])` | Searches the entire text buffer starting at offset 0. |
| `Done()` | Releases the shift tables. Re-initialize before searching again. |

---

### FileTimeHelper

Static helper for converting between .NET timestamps and Windows FILETIME values.

```csharp
public static class FileTimeHelper
{
    public static void WriteFileTimeNow(Span<byte> destination)
}
```

Writes the current UTC time as a Windows FILETIME (two little-endian 32-bit words, 8 bytes total) into the destination span. Used internally for writing timestamps into XISO headers.

---

### Constants

Static class containing all magic values, offsets, and constants used by the XISO format. See the IntelliSense tooltips or XML documentation for detailed descriptions.

Key constants include:

| Constant | Value | Description |
|----------|-------|-------------|
| `HeaderData` | `"MICROSOFT*XBOX*MEDIA"` | XISO header magic string |
| `HeaderOffset` | `0x10000` | Offset of XISO header from start of image |
| `SectorSize` | `2048` | One sector = 2 KB |
| `RootDirectorySector` | `0x108` | Sector index of root directory table |
| `GlobalLseekOffset` | `0x0FD90000` | Sector offset for GLOBAL layout |
| `Xgd2LseekOffset` | `0x0FD90000` | Sector offset for XGD2 layout (same as Global) |
| `Xgd3LseekOffset` | `0x02080000` | Sector offset for XGD3 layout |
| `Xgd1LseekOffset` | `0x18300000` | Sector offset for XGD1 layout |
| `ReadWriteBufferSize` | `0x00200000` | 2 MB buffer for file copy operations |
| `NumSectors(uint size)` | — | Computes the number of sectors required to hold `size` bytes |

---

## Data Types

### Enums

#### `ExtractMode`

Operating mode for XISO image processing.

| Value | Description |
|-------|-------------|
| `GenerateAvl` | Build the AVL tree directory structure without writing an output file. |
| `Extract` | Extract files from the XISO image to disk. |
| `List` | List the contents of the XISO image to the logger. |
| `Rewrite` | Rewrite the XISO image with an optimized AVL directory structure. |
| `Tree` | Recursively list all files with sizes in a tree format. |
| `Verify` | Deep-audit the XISO image: validate header, walk tree, check sector bounds, detect cycles. |

#### `ExtractError`

Error codes for non-fatal extraction failures.

| Value | Code | Description |
|-------|------|-------------|
| `ErrEndOfSector` | `-5001` | Unexpected end of sector while reading a directory entry chain. |
| `ErrIsoRewritten` | `-5002` | XISO image has already been rewritten (optimized format detected). |
| `ErrIsoNoFiles` | `-5003` | XISO image references no files in its directory table. |

#### `AvlResult`

Result codes returned by AVL tree insertion.

| Value | Description |
|-------|-------------|
| `NoErr` | Operation completed successfully without requiring rebalancing. |
| `AvlError` | An error occurred during the operation (e.g., duplicate key). |
| `AvlBalanced` | Operation completed and the tree was rebalanced. |

#### `AvlTraversalMethod`

Traversal order when walking an AVL tree.

| Value | Description |
|-------|-------------|
| `Prefix` | Pre-order: visit node before children. |
| `Infix` | In-order: visit left child, then node, then right child. |
| `Postfix` | Post-order: visit children before node. |

#### `AvlSkew`

Skew direction of an AVL tree node.

| Value | Description |
|-------|-------------|
| `NoSkew` | Node is balanced (subtrees have equal height). |
| `LeftSkew` | Left subtree is taller. |
| `RightSkew` | Right subtree is taller. |

---

### Records

#### `VolumeInfo`

Metadata about an XISO volume descriptor.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | Whether the volume magic is valid. |
| `RootDirSector` | `uint` | Sector index of the root directory table. |
| `RootDirSize` | `uint` | Size of the root directory table in bytes. |
| `DiscLseek` | `long` | Disc lseek offset detected during probing. |
| `FileLength` | `long` | Total size of the ISO file in bytes. |
| `TotalSectors` | `long` | Total number of sectors in the ISO. |

#### `EntryInfo`

Metadata about a single directory entry within an XISO image.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Filename of the entry. |
| `IsDirectory` | `bool` | Whether this entry is a directory. |
| `StartSector` | `uint` | Sector index where the entry's data begins. |
| `FileSize` | `uint` | Size of the file data in bytes (0 for directories). |
| `Attributes` | `byte` | Raw attribute byte (see `Constants` for flag definitions). |
| `LeftChildOffset` | `ushort` | Left child offset in the directory tree (0 if none). |
| `RightChildOffset` | `ushort` | Right child offset in the directory tree (0 if none). |

#### `AuditResult`

Result of a deep integrity audit of an XISO image.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | Whether the image passed all checks. |
| `FilesChecked` | `int` | Number of file entries audited. |
| `DirsChecked` | `int` | Number of directory entries audited. |
| `Issues` | `IReadOnlyList<string>` | List of human-readable issues found during the audit. |

---

### Classes

#### `AvlNode`

Node in an AVL balanced binary search tree. Used to index XISO directory entries by filename.

| Field | Type | Description |
|-------|------|-------------|
| `Offset` | `uint` | Byte offset of this node's directory entry within its parent sector. |
| `DirStart` | `long` | Start byte position of the directory table this node belongs to. |
| `Filename` | `string` | Filename (case-insensitive key for the AVL tree). |
| `FileSize` | `uint` | Size of the file in bytes, or size of the directory entry table for directories. |
| `StartSector` | `uint` | Sector index where the file data or subdirectory table begins. |
| `Subdirectory` | `AvlNode?` | Root of an AVL tree containing the children of this directory node, or `EmptySubdirectory`. |
| `OldStartSector` | `uint` | Original sector position before rewrite. |
| `Skew` | `AvlSkew` | Current balance state of this node. |
| `Left` | `AvlNode?` | Left child in the AVL tree. |
| `Right` | `AvlNode?` | Right child in the AVL tree. |
| `EmptySubdirectory` | `static AvlNode` | Singleton sentinel representing an empty subdirectory. |

#### `DirEntry`

Represents an on-disk directory entry in the XISO filesystem.

| Field | Type | Description |
|-------|------|-------------|
| `Left` | `DirEntry?` | Left child directory entry in the on-disk tree. |
| `Parent` | `DirEntry?` | Parent directory entry. |
| `AvlNode` | `AvlNode?` | Associated AVL node that indexes this entry by filename. |
| `Filename` | `string` | Filename of the file or directory. |
| `FilenameLength` | `byte` | Length of the filename in bytes (ASCII). |
| `ROffset` | `ushort` | Right-child offset (in DWORDs) within the directory sector. |
| `Attributes` | `byte` | File attribute flags (e.g., `Constants.AttributeDir`, `Constants.AttributeArc`). |
| `FileSize` | `uint` | Size of the file in bytes, or size of the directory entry table for directories. |
| `StartSector` | `uint` | Sector index where the file data or subdirectory begins. |

#### `CreateList`

Describes a source directory and optional output name for creating an XISO image. Entries can be chained for batch creation.

| Property | Type | Description |
|----------|------|-------------|
| `Path` | `string` | Source directory path whose contents will be packed into the XISO. |
| `Name` | `string?` | Optional output filename. When `null`, the directory name is used. |
| `Next` | `CreateList?` | Next entry in a linked list of creation tasks, or `null`. |

---

### Delegates

#### `ProgressCallback`

```csharp
public delegate void ProgressCallback(long currentValue, long finalValue);
```

Invoked during extraction/creation to report progress.
- `currentValue` — number of bytes processed so far
- `finalValue` — total number of bytes to process (may be `0` if unknown)

#### `TraversalCallback`

```csharp
public delegate int TraversalCallback(AvlNode node, object? context, int depth);
```

Invoked for each node during an AVL tree traversal.
- `node` — the current tree node being visited
- `context` — arbitrary context object passed to the traversal
- `depth` — current depth within the tree (0 = root)
- **Returns**: `0` to continue traversal; any non-zero value stops the traversal

---

### Exceptions

#### `ExtractErrorException`

Thrown when a non-fatal extraction error occurs.

```csharp
public class ExtractErrorException : Exception
{
    public ExtractError ErrorCode { get; }
    public ExtractErrorException(ExtractError code)
}
```

| Member | Type | Description |
|--------|------|-------------|
| `ErrorCode` | `ExtractError` | The specific error code that caused this exception. |

---

## Usage Examples

### Extracting Files from an XISO

```csharp
using XISOSharp;

var result = XisoReader.DecodeXiso(
    xisoPath: "game.iso",
    outputPath: "extracted_files",
    mode: ExtractMode.Extract,
    outIsoPath: out _,
    llCompat: false);

if (result == 0)
    Console.WriteLine("Extraction completed successfully.");
```

With a custom progress callback:

```csharp
XisoReader.DecodeXiso(
    "game.iso", "output", ExtractMode.Extract, out _,
    llCompat: false,
    cancellationToken: CancellationToken.None);

// Access stats after completion:
Console.WriteLine($"Processed {Logger.TotalFiles} files, {Logger.TotalBytes} bytes");
```

### Listing Files in an XISO

```csharp
using XISOSharp;

// Lists all files in the XISO to the configured Logger output
XisoReader.DecodeXiso("game.iso", null, ExtractMode.List, out _);
```

Output appears on `Logger.Out` (defaults to `Console.Out`) and includes filename, size, and starting sector for each entry.

### Rewriting an XISO

Rewriting rebuilds the directory structure into an optimized AVL layout, reducing fragmentation and potentially improving read performance on original Xbox hardware.

```csharp
XisoReader.DecodeXiso(
    xisoPath: "game.iso",
    outputPath: "rewritten",
    mode: ExtractMode.Rewrite,
    outIsoPath: out var rewrittenPath,
    llCompat: false);

Console.WriteLine($"Rewritten ISO written to: {rewrittenPath}");

// Optionally delete the original after successful rewrite
if (File.Exists(rewrittenPath))
    File.Delete("game.iso");
```

### Creating an XISO from a Directory

```csharp
using XISOSharp;

var result = XisoWriter.CreateXiso(
    rootDirectory: "my_game_files",
    outputDirectory: "output",
    inRoot: null,           // Null to generate AVL tree from filesystem
    sourceStream: null,     // Null when creating from filesystem
    outIsoPath: out var isoPath,
    inName: "my_game.iso",
    progressCallback: null,
    cancellationToken: CancellationToken.None);

if (result == 0)
    Console.WriteLine($"ISO created at: {isoPath}");
```

### Progress Reporting

The `ProgressCallback` delegate provides real-time progress during creation and extraction:

```csharp
void OnProgress(long current, long total)
{
    if (total > 0)
    {
        var pct = (double)current / total * 100;
        Console.Write($"\rProgress: {pct:F1}% ({current:N0} / {total:N0} bytes)");
    }
    else
    {
        Console.Write($"\rBytes written: {current:N0}");
    }
}

XisoWriter.CreateXiso(
    "source", "output", null, null, out _,
    inName: "game.iso",
    progressCallback: OnProgress);
```

### Cancellation Support

Both `DecodeXiso` and `CreateXiso` accept a `CancellationToken`:

```csharp
var cts = new CancellationTokenSource();

// Cancel after 30 seconds
cts.CancelAfter(TimeSpan.FromSeconds(30));

try
{
    XisoReader.DecodeXiso("large.iso", "output", ExtractMode.Extract, out _,
        cancellationToken: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was cancelled.");
}
```

Async overloads are also available:

```csharp
var cts = new CancellationTokenSource();

var (result, outPath) = await XisoReader.DecodeXisoAsync(
    "game.iso", "output", ExtractMode.Extract,
    cancellationToken: cts.Token);
```

### Suppressing Output

Configure `Logger.Quiet` or `Logger.RealQuiet` before calling any API:

```csharp
// Suppress informational output, keep errors
Logger.Quiet = true;

// Suppress ALL output including errors
Logger.RealQuiet = true;

XisoReader.DecodeXiso("game.iso", "output", ExtractMode.Extract, out _);
```

Reset them after processing if you need normal output for subsequent operations.

### Disabling Media-Enable Patching

By default, `.xbe` files are automatically patched to bypass media-check on modified Xbox consoles. Disable this behavior:

```csharp
Logger.MediaEnable = false;

XisoWriter.CreateXiso("source", "output", null, null, out _, "game.iso", null);
```

### Skipping System Update Folders

```csharp
Logger.RemoveSystemUpdate = true;

XisoReader.DecodeXiso("game.iso", "output", ExtractMode.Extract, out _);
```

When enabled, any files within a `$SystemUpdate` folder are excluded from extraction and creation.

### Redirecting Log Output

Send log output to a file or custom `TextWriter`:

```csharp
using var logWriter = new StreamWriter("xiso.log");

Logger.Out = logWriter;
Logger.Error = logWriter;

XisoReader.DecodeXiso("game.iso", "output", ExtractMode.Extract, out _);

logWriter.Flush();
```

### Graphical Progress (WPF / Blazor)

The `ProgressCallback` delegate integrates naturally into GUI frameworks:

```csharp
// WPF example
var progress = new Progress<(long current, long total)>(p =>
{
    Dispatcher.Invoke(() =>
    {
        if (p.total > 0)
            progressBar.Value = (double)p.current / p.total * 100;
        statusLabel.Text = $"{p.current:N0} / {p.total:N0} bytes";
    });
});

await Task.Run(() =>
{
    XisoReader.DecodeXiso("game.iso", "output", ExtractMode.Extract, out _,
        cancellationToken: tokenSource.Token);
});
```

---

## Error Handling

The library uses a combination of return codes and exceptions:

| Mechanism | Usage |
|-----------|-------|
| **Return code** (`int`) | `0` = success, non-zero = failure. Returned by `DecodeXiso` and `CreateXiso`. |
| `ExtractErrorException` | Thrown for non-fatal extraction errors. Check `ErrorCode` for the specific error. |
| `InvalidDataException` | Thrown when a file is not a valid XISO image. |
| `IOException` | Thrown for file read/write errors. |
| `FileNotFoundException` | Thrown when the input file does not exist. |
| `OperationCanceledException` | Thrown when a `CancellationToken` is triggered. |

**Example:**

```csharp
try
{
    var result = XisoReader.DecodeXiso("game.iso", "output", ExtractMode.Extract, out _);
    if (result != 0)
        Console.Error.WriteLine("Operation failed.");
}
catch (ExtractErrorException ex)
{
    Console.Error.WriteLine($"Extract error: {ex.ErrorCode} - {ex.Message}");
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"Invalid XISO: {ex.Message}");
}
```

---

## Thread Safety

The core processing engine is **not thread-safe** for concurrent operations on the same file. However:

- Each call to `DecodeXiso` or `CreateXiso` is self-contained and safe to call from different threads when processing different files.
- `Logger` is safe for concurrent access across multiple operations.
- Async overloads (`DecodeXisoAsync`, `CreateXisoAsync`) run the synchronous engine on a thread pool thread via `Task.Run`, which is safe for UI thread responsiveness.

---

## Compatibility

| Target | Version |
|--------|---------|
| .NET 8 | `net8.0` |
| .NET 9 | `net9.0` |
| .NET 10 | `net10.0` |

The library has zero external dependencies beyond the .NET runtime.

---

## Performance

- **2 MB read/write buffer** for file copy operations, configurable via `Constants.ReadWriteBufferSize`.
- **Boyer-Moore search** for efficient `.xbe` media-enable pattern matching.
- **AVL balanced tree** for O(log n) directory lookups during create/rewrite.
- **Synchronous I/O** for maximum throughput; async overloads are provided for UI responsiveness, not I/O concurrency.
- The library produces **byte-identical output** to the original C `extract-xiso` tool v2.7.1.

---

## License

MIT License — see the [LICENSE](LICENSE) file included in the package for full terms.
