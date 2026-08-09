# Utilities & Types

Supporting public API surface of `XISOSharp.Core`: logging, glob matching, AVL tree
helpers, format constants, records, enums, delegates, and exceptions.

- [Logger](#logger)
- [GlobMatcher](#globmatcher)
- [AvlTree](#avltree)
- [BoyerMoore](#boyermoore)
- [FileTimeHelper](#filetimehelper)
- [Constants](#constants)
- [XisoValidator](#xisovalidator)
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

## GlobMatcher

`public sealed class GlobMatcher` — matches relative paths against glob patterns.
Used by `XisoWriter.CreateXiso` for `excludePatterns`; also usable standalone.

```csharp
var matcher = new GlobMatcher(["**/*.tmp", "**/node_modules/**"]);
bool excluded = matcher.IsMatch("sub/notes.tmp"); // true
```

| Member | Description |
|---|---|
| `GlobMatcher(IEnumerable<string> patterns)` | Compile patterns (empty ones ignored; `null` argument throws) |
| `bool IsMatch(string? relativePath)` | `true` if any pattern matches; backslashes normalized to `/` |

Syntax (case-insensitive, `/` separator):

| Pattern | Meaning |
|---|---|
| `*` | Any characters within one segment |
| `?` | Exactly one character within one segment |
| `**` | As a complete segment: zero or more segments. A trailing `/**` also matches the directory itself |
| `[abc]`, `[a-z]`, `[!abc]` | Character classes with `!`/`^` negation |
| `\x` | Escapes the next character |

Patterns without a leading `**/` are anchored to the source root. A trailing `/` is
treated as `/**` (`a/**/` behaves like `a/**`). Malformed patterns degrade to literals
and never throw.

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

## Constants

`public static class Constants` — all format constants (see
[XISO Format](xiso-format.md) for context). Highlights:

| Constant | Value |
|---|---|
| `HeaderData` / `HeaderDataLength` | `"MICROSOFT*XBOX*MEDIA"` / 20 |
| `HeaderOffset` | `0x10000` |
| `SectorSize` / `FileModulus` | 2048 / `0x10000` |
| `RootDirectorySector` | `0x108` |
| `OptimizedTagOffset` / `OptimizedTag` | 31337 / `"in!xiso!2.7.1 (01.11.14)"` |
| `GlobalLseekOffset` / `Xgd2LseekOffset` | `0x0FD90000` |
| `Xgd3LseekOffset` / `Xgd1LseekOffset` | `0x02080000` / `0x18300000` |
| `AttributeRo/Hid/Sys/Dir/Arc/Nor` | `0x01/0x02/0x04/0x10/0x20/0x80` |
| `MediaEnable` / `MediaEnableByte` | `E8 CA FD FF FF 85 C0 7D` / `0xEB` |
| `ExisoVersion` | `"2.7.1 (01.11.14)"` |
| `NumSectors(uint size)` | Ceiling sector count |

## XisoValidator

`public static class XisoValidator` — conversion validation (see
[Validation](validation.md)).

| Method | Description |
|---|---|
| `ValidationResult ValidateConversion(string sourcePath, string outputPath, bool verifyChecksums = false)` | Compare two images' file trees (counts, paths, sizes, optional SHA-256) |
| `void LogResult(ValidationResult result, string sourcePath, string outputPath)` | Print the `[VALIDATE]` summary |
| `void WriteReport(ValidationResult result, string sourcePath, string outputPath, string reportPath)` | Write a JSON report |

## Records

| Record | Members |
|---|---|
| `VolumeInfo` | `IsValid`, `RootDirSector`, `RootDirSize`, `DiscLseek`, `FileLength`, `TotalSectors` |
| `EntryInfo` | `Name`, `IsDirectory`, `StartSector`, `FileSize`, `Attributes`, `LeftChildOffset`, `RightChildOffset` |
| `AuditResult` | `IsValid`, `FilesChecked`, `DirsChecked`, `Issues` |
| `ValidationIssue` | `Type`, `Path`, `SourceSize`, `OutputSize`, `SourceHash`, `OutputHash` |
| `ValidationResult` | `Passed`, `SourceFileCount`, `OutputFileCount`, `SourceDirCount`, `OutputDirCount`, `SourceTotalBytes`, `OutputTotalBytes`, `Issues` |

## Enums

| Enum | Values |
|---|---|
| `ExtractMode` | `GenerateAvl`, `Extract`, `List`, `Rewrite`, `Tree`, `Verify` |
| `ExtractError` | `ErrEndOfSector` (−5001), `ErrIsoRewritten` (−5002), `ErrIsoNoFiles` (−5003) |
| `AvlResult` | `NoErr`, `AvlError`, `AvlBalanced` |
| `AvlTraversalMethod` | `Prefix`, `Infix`, `Postfix` |
| `ValidationIssueType` | `MissingInOutput`, `ExtraInOutput`, `SizeMismatch`, `ChecksumMismatch` |

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
