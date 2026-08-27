# Compression (CISO)

Pure-managed **CISO** (Compressed ISO) with BCL DEFLATE (version 1) + LZ4 read interop (version 2) — port of xdvdfs `ciso` crate (`SectorLinearBlockDevice` + `CisoSectorInput` → `ciso::write::write_ciso_image` + `SplitOutput`).

- [Format](#format)
- [CLI](#cli)
- [API surface](#api-surface)
- [Block device](#block-device)
- [Round-trip & interop](#round-trip--interop)

---

## Format

CISO header (24 bytes, LE): `CISO 00 00 00 01 | blockSize=2048 (u32 LE) | totalBlocks (u64 LE) | align (u8) | ver (u8)` then `index[totalBlocks+1]` LE `u32` offsets (0x80000000 = not compressed) + compressed 2048-byte sectors (DEFLATE per block via `DeflateStream`, LZ4 for read). `align` is dynamic: `0` if `<2 GB`, `1` if `<4 GB`, `2` else — matches `ciso` crate. Saving threshold: skip compression when `compressed+12 >= 2048` (write plain).

```
CISO header: magic "CISO" (4) | 0x00*4 | blockSize=2048 (4 LE) | totalBlocks (8 LE) | align (1) | ver=1 DEFLATE or 2 LZ4
index: (totalBlocks+1) * u32 LE offsets (plain bit 0x80000000)
sectors: zlib deflate or lz4 of each 2048-byte sector, or plain when not saving
```

Writer sets `ver=1` (DEFLATE) with `align` per size; reader accepts `ver=1` DEFLATE and `ver=2` LZ4 (via `K4os.Compression.LZ4` or manual pure decode) with `align` respected for random-access offset decode.

---

## CLI

```
extract-xiso compress <sourceDir|image.iso> [output.cso] [--ciso-level 0..9] [--ciso-split N]
extract-xiso cso <sourceDir|image.iso> [output.cso]       # alias
extract-xiso decompress <cso> [output.iso]
extract-xiso uncso|decso <cso> [output.iso]                # aliases
```

| Flag | Effect |
|---|---|
| `--ciso-level 0..9` | Compression level (default `9`): `0` NoCompression, `1..3` Fastest, `4..6` Optimal, `7..9` SmallestSize → maps to `CompressionLevel` (`NoCompression`/`Fastest`/`Optimal`/`SmallestSize`). Forwarded to `DeflateStream` per block. |
| `--ciso-split <bytes>` | xdvdfs `SplitOutput` threshold compatibility shim — warns `SplitOutput not supported in C# (ignored)` and writes single `.cso` (no `part` files). |

Examples:

```bash
# Directory → CISO (build ISO then compress in one pass)
extract-xiso compress ./game_dir game.cso --ciso-level 9

# ISO → CISO
extract-xiso cso game.iso game.cso

# Plain vs compressed threshold (+12 saving)
# Files: directory→CISO→ISO round-trip, plain sectors left uncompressed

# Decompress
extract-xiso decompress game.cso game.iso
extract-xiso uncso game.cso
```

Multi-file handling: `compress` accepts `sourceDir` or `image.iso`; when given a directory it first builds an ISO (via `XisoWriter.CreateXiso` pipeline) then wraps via `CisoWriter`.

---

## API surface

```csharp
// XISOSharp.Core/CisoWriter.cs
public static int CompressToCso(string sourcePath, string outputCsoPath, int level = 9, long? splitBytes = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static bool IsCso(string path);
public static (uint blockSize, ulong totalBlocks, byte align, byte version) ReadCsoHeader(string csoPath);

// XISOSharp.Core/CisoReader.cs
public static int DecompressToIso(string csoPath, string outputIsoPath, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static bool TryDecompressHeader(string csoPath, out Header header);
```

Both handle DEFLATE v1 `0x80000000` plain-bit and LZ4 v2 with `align` random-access. CLI wiring in `XISOSharp.Cli/Program.cs:RunCompressMode` / `RunDecompressMode` (progress `FileCount`/`DirCount`/`FileAdded`/`FinishedPacking` via `BlockDevice`).

Header detection: `IsCso` checks `CISO` magic + `blockSize==2048` + `version 1/2` (`CisoReader.IsCso` at `CisoReader.cs:18`).

---

## Block device

`XISOSharp.Core/BlockDevice/CisoBlockDevice.cs` implements `IBlockDevice` over `.cso` via `index` random-access:

- On-demand sector decompress with **single-sector cache** (`_cachedSectorIndex` + `_cachedSectorData` 2048 bytes) — avoids realloc per read.
- Supports `FileBlockDevice`/`MemoryBlockDevice`/`OffsetBlockDevice` composition — `CisoBlockDevice` can wrap any `IBlockDevice` source.
- Passed to `XisoReader.VerifyXiso(IBlockDevice)` so `checksum`, `audit`, `list` can operate directly on `.cso` without decompressing to temp file (future auto-detect for `extract`).

Usage:

```csharp
using var dev = new CisoBlockDevice(new FileBlockDevice(File.OpenRead("game.cso")));
var (rootSector, rootSize, lseek) = XisoReader.VerifyXiso(dev, "game.cso");
```

See [xdvdfs Compat — Block Device](xdvdfs-compat.md#block-device).

---

## Round-trip & interop

- **Round-trip**: `directory → CISO → ISO → directory` verified SHA3-256 stable (`XisoChecksum.ComputeImageChecksumHex` identical before/after).
- **Saving threshold**: sectors where `compressed.Length + 12 >= 2048` left plain (flagged `| 0x80000000`), matching `ciso` crate `threshold`.
- **LZ4 interop**: CISO v2 images produced by xdvdfs (LZ4) decompress correctly (reader handles both `ver`); C# writer only emits v1 DEFLATE (widest compatibility).
- **Index random-access**: `BlockDeviceRead` over `.cso` via `index` random-access, not streaming — `CopyOut`/`ComputeFileHash` can seek arbitrarily.

Verified:

```bash
extract-xiso compress sourceDir out.cso
extract-xiso decompress out.cso rebuilt.iso
extract-xiso checksum source.iso rebuilt.iso --silent  # hex match
# Also: decompress Rust-produced v2 CISO → same checksum
```

See also: [CLI](cli.md) · [xdvdfs Compat](xdvdfs-compat.md) · [Archival](archival.md) · [Xiso Format](xiso-format.md)
