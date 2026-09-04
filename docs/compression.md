# Compression (CISO + ZAR/zstd)

Pure-managed **CISO** (Compressed ISO) writer/reader with BCL DEFLATE (version 1) + LZ4 (version 2) — port of xdvdfs `ciso` crate (`SectorLinearBlockDevice` + `CisoSectorInput` → `ciso::write::write_ciso_image` + `SplitOutput`), including split `.1.cso`/`.2.cso`… output and input (`ciso::split` parity).

Also covered here: **ZAR/ZArchive block compression** (`ZARSharp`, pure-C# zstd) — see [ZArchive / zstd block compression](#zarchive--zstd-block-compression).

- [Format](#format)
- [CISO v2 (LZ4) writer](#ciso-v2-lz4-writer)
- [Split output & input](#split-output--input)
- [CLI](#cli)
- [API surface](#api-surface)
- [Block device](#block-device)
- [Round-trip & interop](#round-trip--interop)

---

## Format

CISO header (24 bytes, LE): `CISO` magic | headerSize=24 | uncompressedSize (u64 LE) | blockSize=2048 (u32 LE) | version (u8) | align (u8) then `index[totalBlocks+1]` LE `u32` entries. Version semantics differ in the index high bit:

- **v1 (classic DEFLATE):** high bit `0x80000000` = **plain** (stored) sector; per-sector payload = raw DEFLATE (BCL `DeflateStream`).
- **v2 (xdvdfs / ciso 0.2, LZ4):** high bit `0x80000000` = **compressed** sector; per-sector payload = the sector's LZ4 frame with the 7-byte frame header and 4-byte end mark stripped, i.e. `[u32 LE block info][block data]` where the block-info high bit marks an in-frame uncompressed block (exactly what `ciso read.rs` re-wraps with its `LZ4_HEADER` constant before feeding the frame decoder).

Saving threshold (both versions, `ciso write.rs`): store plain when the payload does not save more than 12 bytes (`payloadLen + 12 >= 2048`). `align`: v2 fixes **2** (`ciso 0.2 CSOHeader::new`); v1 keeps the dynamic 0 (<2 GB) / 1 (<4 GB) / 2 (else) sizing. The reader accepts any `align` and both versions.

```
CISO header: magic "CISO" (4) | 0x18 (4 LE) | uncompressedSize (8 LE) | blockSize=2048 (4 LE) | ver (1) | align (1)
index: (totalBlocks+1) * u32 LE entries (v1: 0x80000000 = plain; v2: 0x80000000 = compressed)
sectors: DEFLATE (v1) or LZ4-frame-minus-header/footer (v2) per 2048-byte sector, or plain when not saving
```

> Known quirk (kept for parity): the final index entry stores `position >> align`, which rounds
> down, so the last compressed sector's index gap can be up to `(1 << align) - 1` bytes shorter
> than the true payload. The bytes are present in the file; the C# reader extends the read for the
> final sector (the Rust reader pads with zeros instead, which can corrupt payloads with non-zero
> tails).

---

## CISO v2 (LZ4) writer

`CisoWriter.CompressToCso` defaults to **version 2** — matching what modern `xdvdfs compress`
produces (fixed `align 2`, LZ4 sectors). The LZ4 block encoder in `XISOSharp/Lz4.cs` is a pure-managed,
byte-exact port of `lz4_flex 0.11.3` block compression (the encoder `ciso 0.2` uses), so v2 output is
**byte-identical** to `xdvdfs compress` (given the same input image). No external packages: the
encoder keeps `IsTrimmable`/`IsAotCompatible` true.

- `--ciso-level 0` → store all sectors plain.
- `--ciso-level 1..9` → maps inversely to the LZ4 acceleration parameter (`acceleration = 10 - level`);
  level 9 = acceleration 1 = the exact `lz4_flex`/xdvdfs output. Higher acceleration grows the search
  step sooner (faster, larger output, still spec-valid LZ4).
- `--ciso-version 1` → classic DEFLATE payload with the dynamic align and `0x80000000` = plain bit.

`Lz4.Compress`/`Lz4.Decompress` are public: compress with `acceleration = 1` reproduces `lz4_flex`
byte for byte; `Decompress` implements the LZ4 block specification and decodes any conforming block.

---

## Split output & input

Like `xdvdfs compress` (which always writes through `ciso::split::SplitOutput`), `compress` splits
output at `0xffbf6000` (~4 GiB, `CisoWriter.DefaultSplitPoint`) into `<base>.1.cso`,
`<base>.2.cso`, … parts. `--ciso-split <bytes>` overrides the split point; `--ciso-split 0` writes a
single `.cso`.

Format detail (`ciso::split` parity): each write that starts before the split point is written whole
into the current part **at its absolute (global) position**, so part `k` is a sparse file whose data
occupies the global range `[prev part's length, own length)` — a part's file can overshoot
`k·splitPoint` by up to one write, and the next part starts exactly at that end. Part names mirror
Rust `Path::with_extension("{n}.cso")`: `game.cso` → `game.1.cso`, extensionless `game` → `game.1.cso`.

Input: `decompress` (and `CisoReader`/`CisoBlockDevice` APIs) accept `image.1.cso` and resolve
`image.2.cso`, … alongside (detection mirrors `xdvdfs-cli/src/img.rs::open_image`: the `.1` compound
extension marks split input).

---

## CLI

```
XISOSharp.Cli compress <sourceDir|image.iso> [output.cso] [--ciso-level 0..9] [--ciso-version 1|2|auto] [--ciso-split bytes]
XISOSharp.Cli cso <sourceDir|image.iso> [output.cso]       # alias
XISOSharp.Cli decompress <cso|.1.cso> [output.iso]
XISOSharp.Cli uncso|decso <cso|.1.cso> [output.iso]        # aliases
```

| Flag | Effect |
|---|---|
| `--ciso-level 0..9` | Compression level (default `9`). v1: maps to `CompressionLevel` (`NoCompression`/`Fastest`/`Optimal`/`SmallestSize`). v2: `0` = store; `1..9` = LZ4 acceleration `10 - level` (level 9 is byte-identical to xdvdfs). |
| `--ciso-version 1\|2\|auto` | Payload codec. `2` (default, `auto` = `2`): LZ4, fixed `align 2`, byte-compatible with modern `xdvdfs compress`. `1`: classic DEFLATE. |
| `--ciso-split <bytes>` | Split point override. Default: `0xffbf6000` (~4 GiB) — xdvdfs `SplitOutput` behavior. `0` = single-file output. |

Examples:

```bash
# Directory → CISO (build ISO then compress in one pass); default v2 LZ4, split output
XISOSharp.Cli compress ./game_dir game.cso --ciso-level 9
#   → game.1.cso (+ game.2.cso, … for images > ~4 GiB)

# ISO → single-file CISO (classic layout, escape hatch)
XISOSharp.Cli cso game.iso game.cso --ciso-split 0

# Classic DEFLATE CISO with a custom split point
XISOSharp.Cli cso game.iso game.cso --ciso-version 1 --ciso-split 1073741824

# Decompress (single or split input)
XISOSharp.Cli decompress game.1.cso game.iso
XISOSharp.Cli uncso game.cso
```

Multi-file handling: `compress` accepts `sourceDir` or `image.iso`; when given a directory it first builds an ISO (via `XisoWriter.CreateXiso` pipeline) then wraps via `CisoWriter`.

---

## API surface

```csharp
// XISOSharp/CisoWriter.cs
public static int CompressToCso(string sourcePath, string? outputCsoPath = null, int level = 6,
    long? splitBytes = null, byte version = VersionLz4,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static void CompressStream(Stream source, Stream dest, int level = 6, byte version = VersionLz4,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public const long DefaultSplitPoint = 0xffbf6000; // ciso::split FILE_SPLIT_POINT

// XISOSharp/Lz4.cs
public static int Compress(ReadOnlySpan<byte> input, Span<byte> destination, int acceleration = 1);
public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination);

// XISOSharp/CisoReader.cs
public static int DecompressToIso(string csoPath, string? outputIsoPath = null, ...); // single or *.1.cso input
public static void ReadFromCso(string csoPath, long offset, Span<byte> buffer);
public static bool IsCso(string path);
```

Both writer versions share the `+12` saving threshold and the `ciso write.rs` layout (header → index reservation → aligned per-sector payloads → index fill). Split output flows through an internal `CisoSplitOutput` stream (writer) and `CisoSplitInputStream` (reader) mirroring `ciso::split::SplitOutput`/`SplitFileReader`. CLI wiring in `XISOSharp.Cli/Program.cs:RunCompressMode` / `RunDecompressMode` (progress `FileCount`/`FileAdded`/`FinishedPacking`).

Header detection: `IsCso` checks `CISO` magic + `headerSize==24` + `version 1/2` (for split input it checks the given `*.1.cso` part, which carries the header).

---

## Block device

`XISOSharp/BlockDevice/CisoBlockDevice.cs` implements `IBlockDevice` over `.cso` via `index` random-access:

- On-demand sector decompress with **single-sector cache** (`_cachedSectorIndex` + `_cachedSectorData` 2048 bytes) — avoids realloc per read.
- Accepts a path (single or split `*.1.cso` parts), a `FileStream`, or any seekable `Stream` (e.g. the composite split stream).
- **Auto-detect**: any `.cso`/`*.1.cso` path is routed through `CisoBlockDevice`, so `checksum`, `extract`, `unpack`, `list`/`tree`, `pack` (iso→rewrite) and `rewrite` all operate on the decompressed view directly — results identical to the source ISO. `XisoReader.DecodeXiso` opens inputs via `OpenImageStream` (plain `FileStream`, or `CisoBlockDevice` wrapped in `BlockDeviceStream`); detection is by extension (`img.rs::open_image` parity) with a `CISO` magic sniff fallback so renamed containers still resolve (the CLI rewrite flow appends `.old`: `game.cso` → `game.cso.old`). Rewrite/extract output names strip the container suffix (`game.cso` → `game.iso`, extract dir `game/`). An `IBlockDevice` overload (`ComputeImageChecksum(dev, name, ...)`) accepts any device (`File`/`Memory`/`Ciso`/`Offset`), mirroring xdvdfs `compute_checksum(blockdev)`.
- `checksum`/`extract`/`list` operate directly on `.cso` without decompressing to a temp file.

```bash
XISOSharp.Cli checksum game.iso      # plain ISO
XISOSharp.Cli checksum game.1.cso    # CISO (single or split) — same hash as game.iso
```

```bash
XISOSharp.Cli checksum game.iso      # plain ISO
XISOSharp.Cli checksum game.1.cso    # CISO (single or split) — same hash as game.iso
XISOSharp.Cli -d out -x game.cso     # extract from CISO — same files as game.iso
XISOSharp.Cli rewrite game.cso       # rewrite from CISO — same bytes as rewriting game.iso
```

Usage:

```csharp
using var dev = new CisoBlockDevice("game.1.cso"); // single file or split parts
var (rootSector, rootSize, lseek) = XisoReader.VerifyXiso(dev, "game.cso");
```

See [xdvdfs Compat — Block Device](xdvdfs-compat.md#block-device).

---

## Round-trip & interop

- **Round-trip**: `directory → CISO → ISO → directory` verified SHA3-256 stable (`XisoChecksum.ComputeImageChecksumHex` identical before/after), both versions, single and split.
- **Saving threshold**: sectors where `payloadLen + 12 >= 2048` left plain, matching `ciso` crate `threshold`.
- **v2 byte parity**: the LZ4 encoder is a byte-exact `lz4_flex 0.11.3` port (golden vectors hand-traced from the reference algorithm in `XisoTests`); v2 images decompress correctly with xdvdfs and vice versa.
- **Split parity**: `compress` output (`.1.cso`, `.2.cso`, …) matches `ciso::split` semantics (absolute-position sparse parts, overshoot writes included) and round-trips through `decompress`/`ReadFromCso`/`CisoBlockDevice`.
- **Index random-access**: `BlockDeviceRead` over `.cso` via `index` random-access, not streaming — `CopyOut`/`ComputeFileHash` can seek arbitrarily.

Verified:

```bash
XISOSharp.Cli compress sourceDir out.cso
XISOSharp.Cli decompress out.1.cso rebuilt.iso
XISOSharp.Cli checksum source.iso rebuilt.iso --silent  # hex match
# Also: decompress Rust-produced v2 CISO → same checksum
```

---

## ZArchive / zstd block compression

`ZARSharp` packs directories into `.zar` archives (ZArchive 0.1.2 format),
compressing every 64 KiB block with a dependency-free pure-C# zstd encoder
(RFC 8878), level 6 by default — the same rule as upstream
`src/zarchivewriter.cpp::StoreBlock` (`ZSTD_compress(..., 6)`, store raw when
the compressed form is not smaller).

```csharp
ZArchiveTool.Pack("./game_dir", "game.zar");                    // zstd L6 default
ZArchiveTool.Pack("./game_dir", "game.zar", compressor: new ZstdCompressor(ZstdCompressionOptions.FromLevel(1)));
ZArchiveTool.Pack("./game_dir", "game.zar", compressor: new ZarRawCompressor()); // store raw
ZArchiveTool.Extract("game.zar", "./game_out");
```

```csharp
// Standalone single-shot frames, levels 1-6 (fast → lazy strategies).
var c = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
byte[] frame = c.CompressBlock(data);
```

- **Ratio**: within ~1% of native libzstd at the same level on text-like input
  (measured: 64 KiB source blob L6 2573 B vs native 2572 B); incompressible
  blocks are stored raw with zero expansion beyond the frame header.
- **Interop**: archives written by `ZARSharp` open in `zarchive.exe` and vice
  versa; raw frames additionally decode with native `zstd -d`.
- **Limits**: single-shot 64 KiB blocks only; no dictionaries, legacy frames,
  multithreading, or streaming API. Decoder caps default to 512 MiB window /
  frame content (configurable via `ZstdDecoderOptions`); output is valid
  interoperable zstd, not byte-identical to libzstd.

See also: [CLI](cli.md) · [xdvdfs Compat](xdvdfs-compat.md) · [Archival](archival.md) · [Xiso Format](xiso-format.md)
