# XISO Format

XISO (also called **XDVDFS**, the Xbox DVD filesystem) is the filesystem used on Xbox
game discs. This page documents the on-disk format as implemented by XISOSharp — a
faithful port of the reference implementation in `extract-xiso.c` v2.7.1, cross-checked
against [xdvdfs](https://github.com/antangelo/xdvdfs) and
[XboxKit](https://github.com/Deterous/XboxKit).

- [Conventions](#conventions)
- [Volume header (sector 32)](#volume-header-sector-32)
- [Directory entries](#directory-entries)
- [Attributes](#attributes)
- [Directory tables and the AVL tree](#directory-tables-and-the-avl-tree)
- [Optimized tag](#optimized-tag)
- [ECMA-119 volume descriptors](#ecma-119-volume-descriptors)
- [Media-enable patching](#media-enable-patching)
- [Alignment and limits](#alignment-and-limits)

## Conventions

| Constant | Value | Meaning |
|---|---|---|
| Sector size | 2,048 bytes | Fundamental addressing unit |
| Header offset | `0x10000` (sector 32) | Volume header location within the game partition |
| Root directory sector | `0x108` (sector 264) | Default root directory table location |
| File alignment | 64 KB (`0x10000`) | Total image length is a multiple of 64 KB |
| Padding byte | `0xFF` | Fills file data to sector boundaries |
| Endianness | Little-endian | All multi-byte fields |

Sector numbers stored in the image are **partition-relative**: offset 0 is the start of
the game partition. Tools that read a full disc dump add the disc offset (see
[Redump & Disc Layouts](redump-workflows.md)).

## Volume header (sector 32)

The header occupies exactly one sector at offset `0x10000`:

| Field | Offset | Size | Description |
|---|---|---|---|
| Magic | `0x10000` | 20 | ASCII `MICROSOFT*XBOX*MEDIA` |
| Root dir sector | `+20` | 4 | Sector index of the root directory table |
| Root dir size | `+24` | 4 | Size of the root directory table in bytes |
| FILETIME | `+28` | 8 | Windows FILETIME (1601 epoch, 100 ns units) |
| Unused | `+36` | `0x7C8` | Reserved, zero-filled |
| Trailing magic | `+36+0x7C8` | 20 | Second `MICROSOFT*XBOX*MEDIA` |

`20 + 4 + 4 + 8 + 0x7C8 + 20 = 2048` — the header is exactly one sector.

On read, the trailing magic is verified (mismatch ⇒ "appears to be corrupt"). The
**root directory sector/size** point at the root directory table; a root sector and
size of both zero means an empty image.

## Directory entries

Each entry in a directory table is 14 bytes plus the filename, padded to a multiple of
4 bytes:

| Field | Offset | Size | Description |
|---|---|---|---|
| Left offset | 0 | 2 | Left child offset in **DWORDs** (×4 = byte offset within the table); 0 = none |
| Right offset | 2 | 2 | Right child offset in DWORDs; 0 = none |
| Start sector | 4 | 4 | Sector of file data (or of the subdirectory table for directories) |
| File size | 8 | 4 | Bytes (files); directory table size (directories) |
| Attributes | 12 | 1 | Bit flags — see below |
| Filename length | 13 | 1 | 0–255 |
| Filename | 14 | n | Latin-1 / Windows-1252 bytes |

- Entry length = `14 + filename_length`, rounded up to a multiple of 4.
- If an entry would cross a 2048-byte sector boundary, the table is padded to the next
  sector first.
- **End of table**: the first 2 bytes `0xFFFF` mark the end. When `0xFFFF` is
  encountered mid-table (legacy linked-list layout), the reader jumps to the start of
  the next sector.
  **Empty-directory** tables are also recognized via `0x0000` + 12-byte `0x00` header
  (`Constants.IsEmptyDirectoryHeader` at `Constants.cs:61`, `xdvdfs` `read.rs:38` — all-`0xFF` or all-`0x00` header). A valid entry with `left=0x0000` (no left child) but non-zero tail is **not** treated as empty (peek distinguishes). See [xdvdfs Compat](xdvdfs-compat.md).
- Filenames containing `.`, `..`, `/`, or `\` are rejected on read.

## Attributes

| Bit | Value | Meaning |
|---|---|---|
| Read-only | `0x01` | |
| Hidden | `0x02` | |
| System | `0x04` | |
| **Directory** | `0x10` | Entry is a directory |
| Archive | `0x20` | |
| Normal | `0x80` | |

Bits `0x08` and `0x40` are reserved; audit mode (`-V`) flags them (`Reserved attribute bits set: 0x…`) and all readers mask via `Constants.AttributeValidMask 0xB7` / `MaskAttributes(byte)` (`TraverseXiso` at `XisoReader.cs:408`, `ReadDirectoryEntries:1906`, `XisoChecksum:204` read `hdr[12]` masked; `AuditWalk:1310` flags raw before masking). `IsDirectory` derived from masked `0x10`.

## Directory tables and the AVL tree

Each directory's entries form a **case-insensitive AVL (self-balancing binary search)
tree** keyed by filename:

- Left/right offsets are **byte offsets ÷ 4** (DWORDs) from the start of the directory
  table to the child entry.
- Insertion compares filenames case-insensitively (ASCII uppercase fold). When a
  duplicate name is encountered, the reader drops the second entry (the reference C
  implementation reports it as corruption).
- Balancing uses single and double rotations (`AvlSkew`: `NoSkew`, `LeftSkew`,
  `RightSkew`), ported exactly from the C implementation for byte-identical output.
- **Empty directories** are represented by the `AvlNode.EmptySubdirectory` sentinel and
  written as a single sector of `0xFF` with a table size of one sector (2048 bytes).
  On read, an empty table is detected by its leading `0xFFFF`.

When creating an image the writer performs a **three-pass layout**:

1. Compute directory table sizes (including sector-boundary padding).
2. Assign sector positions: directory tables first (root at sector `0x108`), then file
   data, depth-first.
3. Write: file data first (depth-first pre-order), then each directory table, padded
   to sector boundaries.

## Optimized tag

After writing, the 24-byte string `in!xiso!2.7.1 (01.11.14)` is written at byte offset
**31337**:

- Detection accepts the 7-byte prefix `in!xiso`.
- The tag marks the image as having an optimized (AVL) directory layout.
- The CLI reads it before processing: optimized images use the modern right-offset
  calculation; images without it use the legacy linked-list-compatible calculation
  (`llCompat`).
- Rewrite mode skips images that already carry the tag ("already optimized").
- With `--prepend-sectors`, the tag shifts together with the game partition.

## ECMA-119 volume descriptors

At the end of the header area the image carries ISO-9660-compatible descriptors so that
burning software recognizes it:

| Field | Offset | Content |
|---|---|---|
| Primary volume descriptor | `0x8000` | `01 "CD001" 01` |
| Volume space size | `0x8000 + 80` | Total sectors, little- and big-endian |
| Volume set size | `0x8000 + 120` | Fixed 12-byte record |
| Volume set identifier | `0x8000 + 190` | Spaces |
| Creation date | `0x8000 + 813` | Four 16-character zero dates + `01` |
| Terminator | `0x8000 + 2048` (`0x8800`) | `FF "CD001" 01` |

## Media-enable patching

During create/rewrite, `.xbe` files are scanned for the 8-byte pattern
`E8 CA FD FF FF 85 C0 7D`; when found, **byte 7 is patched from `0x7D` to `0xEB`**.
This "media-enable" patch lets homebrew executables run from writable media.

- Implemented with a Boyer–Moore search over overlapping chunks.
- Enabled by default; disable with `-m` (CLI) or `Logger.MediaEnable = false` (library).

## Alignment and limits

- File data starts at `start_sector × 2048` and is padded to whole sectors with `0xFF`.
- The complete image length is padded to a multiple of 64 KB.
- **4 GB per-file limit**: the on-disk file-size field is a 32-bit unsigned integer
  (`XisoFileTooLargeException` for larger files).
- Total image size must fit a 32-bit sector count.

## References

- `XISOSharp.Core/Constants.cs` — all constants used above
- `XISOSharp.Core/XisoReader.cs` — header verification, tree traversal, extraction
- `XISOSharp.Core/XisoWriter.cs` — layout calculation and writing
- `XISOSharp.Core/AvlTree.cs` — AVL insert/rotate/traverse
- `References/xdvdfs-0.8.3` — independent Rust implementation used for cross-checking
- `References/XboxKit-0.7` — C# reference implementation used for cross-checking (`XgdTables`, `XisoRanges`, `XboxPrng`, `ZArchive` ported)
- `References/xdvdfs-0.8.3/tests/img.py::BuildImage` — `build-image` wax capture vectors verified
- CISO format: header `CISO` + `blockSize 2048` + `totalBlocks` + `align` 0/1/2 + `ver` 1 DEFLATE `0x80000000` plain bit vs 2 LZ4 + `index` + per-sector DEFLATE/LZ4 (threshold `+12`) — via `CisoWriter`/`CisoBlockDevice` (see [Compression](compression.md))

See also: [Redump & Disc Layouts](redump-workflows.md) · [Library Overview](library.md)
