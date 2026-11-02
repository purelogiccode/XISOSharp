# Compression (CISO, CCI + ZAR/zstd)

Pure-managed **CISO** (Compressed ISO) writer/reader with BCL DEFLATE (version 1) + LZ4 (version 2) — port of xdvdfs `ciso` crate (`SectorLinearBlockDevice` + `CisoSectorInput` → `ciso::write::write_ciso_image` + `SplitOutput`), including split `.1.cso`/`.2.cso`… output and input (`ciso::split` parity).

**CCI** ("CCIM", Cerbios Compressed Image) read/write support comes from the standalone
[CCISharp](https://github.com/purelogiccode/CCISharp) package (`net8.0`/`net9.0`/`net10.0`,
zero dependencies): `XisoCci` compresses/decompresses, `CciBlockDevice` exposes random
access, and `.cci`/`.1.cci` inputs work through the same reader verbs as CISO. See
[CCI (CCIM)](#cci-ccim).

GOD/SVOD containers (a header file or its `.data` folder) are accepted as a
compression source: the decompressed sector view is materialized to a temporary
ISO before CISO/CCI encoding, so `compress`/`convert` work without a manual
`decompress` step.

Also covered here: **ZAR/ZArchive block compression** (`ZArchiveSharp`, pure-C# zstd) — see [ZArchive / zstd block compression](#zarchive--zstd-block-compression).

- [Format](#format)
- [CISO v2 (LZ4) writer](#ciso-v2-lz4-writer)
- [Split output & input](#split-output--input)
- [CCI (CCIM)](#cci-ccim)
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
- `--jobs 1..64` (API: `CompressToCso(..., jobs: n)` / `CompressStream(..., jobs: n)`) compresses
  sectors on a bounded worker pool. Sectors are read in batches, compressed in parallel, then
  written in sector order, so the output is **byte-identical** to the sequential writer (level `0`
  and `jobs = 1` keep the sequential loop). Progress events stay in sector order.

`Lz4.Compress`/`Lz4.Decompress` are public: compress with `acceleration = 1` reproduces `lz4_flex`
byte for byte; `Decompress` implements the LZ4 block specification and decodes any conforming block.

---

## Split output & input

Like `xdvdfs compress` (which always writes through `ciso::split::SplitOutput`), `compress` splits
output at `0xffbf6000` (~4 GiB, `CisoWriter.DefaultSplitPoint`) into `<base>.1.cso`,
`<base>.2.cso`, … parts. `--ciso-split <bytes>` overrides the split point; `--ciso-split 0` writes a
single `.cso`. This is a compression-format default, not a FATX-safe limit: plain-ISO `split`
defaults to the more conservative `Constants.FatxMaxFileSize` (`0xFF000000`, 4 GiB − 16 MiB) so
its parts fit FATX, while CISO/CCI keep `0xffbf6000` for xdvdfs byte parity.

Format detail (`ciso::split` parity): each write that starts before the split point is written whole
into the current part **at its absolute (global) position**, so part `k` is a sparse file whose data
occupies the global range `[prev part's length, own length)` — a part's file can overshoot
`k·splitPoint` by up to one write, and the next part starts exactly at that end. Part names mirror
Rust `Path::with_extension("{n}.cso")`: `game.cso` → `game.1.cso`, extensionless `game` → `game.1.cso`.

Input: `decompress` (and `CisoReader`/`CisoBlockDevice` APIs) accept `image.1.cso` and resolve
`image.2.cso`, … alongside (detection mirrors `xdvdfs-cli/src/img.rs::open_image`: the `.1` compound
extension marks split input).

---

## CCI (CCIM)

CCI is the compressed container used by the Cerbios BIOS and produced by
Repackinator/XboxToolkit. Each 2048-byte sector is stored raw or as one LZ4
block, with a `(sectors + 1) × u32` random-access index at the end. The
normative description is
[`Team-Resurgent/libCCI` `FORMAT.md`](https://github.com/Team-Resurgent/libCCI/blob/main/FORMAT.md).

XISOSharp consumes [CCISharp](https://github.com/purelogiccode/CCISharp) 1.0.1,
an independent MIT implementation (no GPL code). The API surface:

```csharp
// Compress any supported source (ISO, split ISO, CISO, CCI or a directory).
int rc = XisoCci.CompressToCci(
    @"C:\games\game.iso",
    @"C:\games\game.cci",
    level: 9,                       // 0 = store, 1..9 = fast-LZ4 presets
    splitBytes: XisoCci.DefaultSplitBytes, // ~4 GiB; 0 = single file
    overwrite: false);

// Extract (split sets are stitched transparently).
int rc2 = XisoCci.DecompressToIso(@"C:\games\game.1.cci", @"C:\games\game.iso");

// Random access.
using CciBlockDevice device = new(@"C:\games\game.cci");
byte[] sector = new byte[2048];
device.Read(0, sector);
```

- **Split sets**: `game.1.cci` … `game.9.cci`; open any numbered part and the
  reader discovers the set from `.1`. Outputs are named after the game, not the
  part: extraction creates `game/`, rewrite writes `game.iso`, and recompressing
  `game.2.cci` derives `game.cci`.
- **Encoder**: fast LZ4 block encoder. Output is spec-valid and interoperable
  with libCCI/Repackinator, but not byte-identical to their LZ4-HC output.
- **Hardened reader**: CCISharp 1.0.1 caps a part's index at 64 MiB and returns
  `InvalidIndex` for hostile headers instead of allocating an index sized only
  by the part file length.
- **In-place guards**: `--repair`, `--copy-in`, and `--set-filetime` refuse CCI
  (compressed bytes are not patch-stable); decompress first.
- **Reader verbs**: `-l`, `-t`, `-x`, `--unpack`, `-r`, `checksum`,
  `--sector-layout`, `--ranges`, `--filetime` (read) and `-i` all operate on
  `.cci`/`.1.cci` through the decompressed view.

---

## CLI

```
XISOSharp compress <sourceDir|image> [output] [--format cso|cci]
    [--ciso-level 0..9] [--ciso-version 1|2|auto] [--ciso-split bytes]
    [--cci-level 0..9] [--cci-split bytes] [--batch <dir>] [--jobs <n>]
XISOSharp cso <sourceDir|image> [output.cso]              # CISO alias
XISOSharp cci <sourceDir|image> [output.cci]              # CCI alias
XISOSharp decompress <cso|cci> [output.iso] [--batch <dir>]
XISOSharp uncso|decso <cso|.1.cso> [output.iso]           # CISO aliases
XISOSharp uncci <cci|.1.cci> [output.iso]                 # CCI alias
```

| Flag | Effect |
|---|---|
| `--ciso-level 0..9` | Compression level (default `9`). v1: maps to `CompressionLevel` (`NoCompression`/`Fastest`/`Optimal`/`SmallestSize`). v2: `0` = store; `1..9` = LZ4 acceleration `10 - level` (level 9 is byte-identical to xdvdfs). |
| `--ciso-version 1\|2\|auto` | Payload codec. `2` (default, `auto` = `2`): LZ4, fixed `align 2`, byte-compatible with modern `xdvdfs compress`. `1`: classic DEFLATE. |
| `--ciso-split <bytes>` | Split point override. Default: `0xffbf6000` (~4 GiB) - xdvdfs `SplitOutput` behavior. `0` = single-file output. |
| `--format cso\|cci` | Output container. Default `cso`; `cci` selects the CCISharp writer. |
| `--cci-level 0..9` | CCI level: `0` stores every sector raw; `1..9` map to the fast LZ4 block encoder's acceleration (`10 - level`, level 9 = smallest). |
| `--cci-split <bytes>` | CCI split point. Default `0xffbf6000` (~4 GiB) into `.1.cci`/`.2.cci` …; `0` = single `.cci`. |
| `--jobs <n>` | CISO sector compression workers, `1..64` (default `1`). Output is byte-identical to the sequential writer and progress stays in sector order. With `--batch`, `<n>` runs `<n>` conversions in parallel instead; CCI single-file compression is single-threaded. |

Examples:

```bash
# Directory → CISO (build ISO then compress in one pass); default v2 LZ4, split output
XISOSharp compress ./game_dir game.cso --ciso-level 9
#   → game.1.cso (+ game.2.cso, … for images > ~4 GiB)

# ISO → single-file CISO (classic layout, escape hatch)
XISOSharp cso game.iso game.cso --ciso-split 0

# Classic DEFLATE CISO with a custom split point
XISOSharp cso game.iso game.cso --ciso-version 1 --ciso-split 1073741824

# Decompress (single or split input)
XISOSharp decompress game.1.cso game.iso
XISOSharp uncso game.cso

# CCI: single-file and split output
XISOSharp cci game.iso game.cci --cci-split 0
XISOSharp cci game.iso game.cci --cci-level 9
#   → game.1.cci (+ game.2.cci … for images > ~4 GiB)

# Any compressed/split source can be recompressed
XISOSharp compress --format cci game.1.iso game.cci --cci-split 0
XISOSharp uncci game.1.cci game.iso

# Batch conversion (compress / decompress / convert / split)
XISOSharp compress --batch ./isos --format cci --cci-split 0 -y
XISOSharp decompress --batch ./compressed --skip-existing -y
XISOSharp convert --batch ./isos --format zar -y
XISOSharp split --batch ./isos --size half -y
XISOSharp compress --batch ./isos --output-name "{name}.xiso.{format}" --jobs 4 -y
```

Batch mode scans the verb's own inputs (`.iso`/`.xiso` for `compress`, unsplit
`.iso`/`.xiso` for `convert --format zar`, `.cso`/`.cci` for `decompress`, `.zar` for
`convert --format iso`, and unsplit `.iso`/`.xiso` for `split`). Split sets are
collapsed to their first part for the verbs that read them; ZAR conversion
requires an unsplit ISO (join the parts first). `--skip-existing` resumes an interrupted run,
`--continue-on-error` keeps going after a failed file (partial outputs are
removed) and returns exit code `1` after the summary, `--jobs 1..64` converts in
parallel, and `--output-name` supports `{name}` (source stem with a `.1` split
marker stripped), `{ext}` and `{format}`.

Multi-file handling: `compress` accepts `sourceDir` or `image.iso`; when given a directory it first builds an ISO (via `XisoWriter.CreateXiso` pipeline) then wraps via `CisoWriter`.

---

## API surface

```csharp
// XISOSharp/CisoWriter.cs
public static int CompressToCso(string sourcePath, string? outputCsoPath = null, int level = 6,
    long? splitBytes = null, byte version = VersionLz4,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static int CompressToCso(string sourcePath, string? outputCsoPath, int level,
    long? splitBytes, byte version, int jobs,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default); // jobs 1..64
public static void CompressStream(Stream source, Stream dest, int level = 6, byte version = VersionLz4,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public static void CompressStream(Stream source, Stream dest, int level, byte version, int jobs,
    IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
public const long DefaultSplitPoint = 0xffbf6000; // ciso::split FILE_SPLIT_POINT
public const int MaxParallelism = 64;

// XISOSharp/Lz4.cs
public static int Compress(ReadOnlySpan<byte> input, Span<byte> destination, int acceleration = 1);
public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination);

// XISOSharp/CisoReader.cs
public static int DecompressToIso(string csoPath, string? outputIsoPath = null, ...); // single or *.1.cso input
public static void ReadFromCso(string csoPath, long offset, Span<byte> buffer);
public static bool IsCso(string path);

// XISOSharp/XisoCci.cs (CCISharp-backed)
public static int CompressToCci(string sourcePath, string? outputCciPath = null, int level = 9,
    long splitBytes = DefaultSplitBytes, bool overwrite = false, ...);
public static int DecompressToIso(string cciPath, string? outputIsoPath = null, bool overwrite = false, ...);
public static string DeriveDefaultCciPath(string sourcePath, bool isDirectory);
public static string DeriveDefaultIsoPath(string cciPath);
public const long DefaultSplitBytes = CciFormat.DefaultSplitBytes; // 0xffbf6000

// XISOSharp/BlockDevice/CciBlockDevice.cs
public sealed class CciBlockDevice : IBlockDevice
{
    public CciBlockDevice(string cciPath, FileShare share = FileShare.Read);
    public CciFile Image { get; }
    public long Length { get; }
    public int Read(long offset, Span<byte> buffer);
}
```

Both writer versions share the `+12` saving threshold and the `ciso write.rs` layout (header → index reservation → aligned per-sector payloads → index fill). Split output flows through an internal `CisoSplitOutput` stream (writer) and `CisoSplitInputStream` (reader) mirroring `ciso::split::SplitOutput`/`SplitFileReader`. CLI wiring in `XISOSharp.Cli/Program.cs:RunCompressMode` / `RunDecompressMode` (progress `FileCount`/`FileAdded`/`FinishedPacking`).

Header detection: `IsCso` checks `CISO` magic + `headerSize==24` + `version 1/2` (for split input it checks the given `*.1.cso` part, which carries the header).

---

## Block device

`XISOSharp/BlockDevice/CisoBlockDevice.cs` implements `IBlockDevice` over `.cso` via `index` random-access; `CciBlockDevice` does the same over `.cci` via CCISharp's sector index:

- On-demand sector decompress with **single-sector cache** (`_cachedSectorIndex` + `_cachedSectorData` 2048 bytes) — avoids realloc per read.
- Accepts a path (single or split `*.1.cso` parts), a `FileStream`, or any seekable `Stream` (e.g. the composite split stream).
- **Auto-detect**: any `.cso`/`*.1.cso` path is routed through `CisoBlockDevice`, so `checksum`, `extract`, `unpack`, `list`/`tree`, `pack` (iso→rewrite) and `rewrite` all operate on the decompressed view directly — results identical to the source ISO. `XisoReader.DecodeXiso` opens inputs via `OpenImageStream` (plain `FileStream`, or `CisoBlockDevice` wrapped in `BlockDeviceStream`); detection is by extension (`img.rs::open_image` parity) with a `CISO` magic sniff fallback so renamed containers still resolve (the CLI rewrite flow appends `.old`: `game.cso` → `game.cso.old`). Rewrite/extract output names strip the container suffix (`game.cso` → `game.iso`, extract dir `game/`). An `IBlockDevice` overload (`ComputeImageChecksum(dev, name, ...)`) accepts any device (`File`/`Memory`/`Ciso`/`Offset`), mirroring xdvdfs `compute_checksum(blockdev)`.
- `checksum`/`extract`/`list` operate directly on `.cso` without decompressing to a temp file.

```bash
XISOSharp checksum game.iso      # plain ISO
XISOSharp checksum game.1.cso    # CISO (single or split) — same hash as game.iso
```

```bash
XISOSharp checksum game.iso      # plain ISO
XISOSharp checksum game.1.cso    # CISO (single or split) — same hash as game.iso
XISOSharp -d out -x game.cso     # extract from CISO — same files as game.iso
XISOSharp rewrite game.cso       # rewrite from CISO — same bytes as rewriting game.iso
```

Usage:

```csharp
using var dev = new CisoBlockDevice("game.1.cso"); // single file or split parts
var (rootSector, rootSize, lseek) = XisoReader.VerifyXiso(dev, "game.cso");
```

CCI:

```csharp
using var dev = new CciBlockDevice("game.1.cci"); // single file or split parts
var (rootSector, rootSize, lseek) = XisoReader.VerifyXiso(dev, "game.cci");
```

See [xdvdfs Compat — Block Device](xdvdfs-compat.md#block-device).

---

## Round-trip & interop

- **Round-trip**: `directory → CISO → ISO → directory` verified SHA3-256 stable (`XisoChecksum.ComputeImageChecksumHex` identical before/after), both versions, single and split.
- **Saving threshold**: sectors where `payloadLen + 12 >= 2048` left plain, matching `ciso` crate `threshold`.
- **v2 byte parity**: the LZ4 encoder is a byte-exact `lz4_flex 0.11.3` port (golden vectors hand-traced from the reference algorithm in `XisoTests`); v2 images decompress correctly with xdvdfs and vice versa.
- **Split parity**: `compress` output (`.1.cso`, `.2.cso`, …) matches `ciso::split` semantics (absolute-position sparse parts, overshoot writes included) and round-trips through `decompress`/`ReadFromCso`/`CisoBlockDevice`.
- **Split golden vectors vs Rust**: `XISOSharp.Tests/CisoSplitInteropTests.cs` runs both directions against the reference `xdvdfs-cli 0.8.3` binary (gitignored `References/xdvdfs-0.8.3`, tests skip if absent).
  Rust-compressed ISO/dir → our `DecompressToIso` → stock `md5` content-identical; our single `.cso` → stock `md5` content-identical.
  Multi-part Rust output verified locally: 4.5 GB random dir → genuine `big.1.cso` (4.29 GB, overshoot 1176 B past `0xffbf6000`) + `big.2.cso` → our decompress → per-file MD5 match.
  Caveat: stock 0.8.3 itself cannot read sparse multi-part files (its `SplitFileReader` maps parts at cumulative sizes while data sits at global offsets — panics on its own output too); `CisoSplitInputStream` tiles by previous part length and reads them correctly. Stock `unpack`/`copy-out` take raw ISOs only — `md5`/`ls`/`tree`/`info`/`checksum` are the cso-aware oracles.
- **Index random-access**: `BlockDeviceRead` over `.cso` via `index` random-access, not streaming — `CopyOut`/`ComputeFileHash` can seek arbitrarily.

Verified:

```bash
XISOSharp compress sourceDir out.cso
XISOSharp decompress out.1.cso rebuilt.iso
XISOSharp checksum source.iso rebuilt.iso --silent  # hex match
# Also: decompress Rust-produced v2 CISO → same checksum
```

---

## ZArchive / zstd block compression

`ZArchiveSharp` packs directories into `.zar` archives (ZArchive 0.1.2 format),
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
- **Interop**: archives written by `ZArchiveSharp` open in `zarchive.exe` and vice
  versa; raw frames additionally decode with native `zstd -d`.
- **Limits**: single-shot 64 KiB blocks only; no dictionaries, legacy frames,
  multithreading, or streaming API. Decoder caps default to 512 MiB window /
  frame content (configurable via `ZstdDecoderOptions`); output is valid
  interoperable zstd, not byte-identical to libzstd.

See also: [CLI](cli.md) · [xdvdfs Compat](xdvdfs-compat.md) · [Archival](archival.md) · [Xiso Format](xiso-format.md)
