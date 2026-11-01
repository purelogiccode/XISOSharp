# Plan to Enhance XISOSharp

**Status:** CCISharp 1.0.0 published on NuGet (2026-11-01). XISOSharp 1.6.0 implements the Phase 0 image-source factory, Phase 1 CCI integration, Phase 2 split-ISO input, Phase 3 ZAR input, Phase 4 pathological-TOC traversal hardening (extract/list/rewrite plus the audit, sector-range, repair, salvage and ZAR follow-up walkers), Phase 5 batch conversion/naming, Phase 6 `--json` output, Phase 7 preallocation/sequential I/O, and Phase 8 explicit sector-layout packing (library + CLI + tests).
**Baseline:** XISOSharp `1.4.4` (tag), extract-xiso build `202609111233`, xdvdfs `0.8.3`
**Reference:** CCI spec/implementation copied to `References/libCCI-0.1.0-1b0087e` (commit `1b0087e`, GPL-2.0-or-later; reference only) and verified to build/round-trip.
**CCISharp repo:** <https://github.com/purelogiccode/CCISharp> — package <https://www.nuget.org/packages/CCISharp/1.0.0>.
**Date:** 2026-10-31
**Scope:** turn XISOSharp into the most complete original-Xbox disc-image library by closing the four format/robustness gaps, adding a dedicated `CCISharp` package, and adding three quality-of-life improvements. This document records the plan and its implementation status.

---

## 0. Executive summary

A full review of the upstream trackers — 56 extract-xiso issues, 54 extract-xiso PRs, 73 xdvdfs issues, 119 xdvdfs PRs — shows that **almost every actionable upstream request is already implemented in XISOSharp 1.4.4**. The remaining gaps are narrow but high-value:

| # | Gap | Upstream reference | Why it matters |
|---|-----|--------------------|----------------|
| A | **CCI (Cerbios Compressed Image) read/write/split** | xdvdfs #185 (open PR), #62, #119; xemu #1809 | The compressed format original-Xbox hardware actually uses; no library in the current XISOSharp/CHDStudio/ZArchiveSharp suite supports it (Team-Resurgent XboxToolkit is a GPL-3.0 reader) |
| B | **Split ISO sets as first-class input** | xdvdfs #97; Cerbios/Stellar storage | XISOSharp writes `game.1.iso` but cannot list/extract/validate/checksum the parts directly |
| C | **ZAR as an input format** | xdvdfs #119; XboxKit ZAR workflows | ZAR is write-only today; `list`/`extract`/`checksum`/convert are missing |
| D | **Pathological directory tables** | extract-xiso PR #80, #95, #76/#77 | Left-skewed tables deeper than 64 entries are rejected instead of read |
| E | Batch conversion + naming | xdvdfs #93/#114; extract-xiso #60 | `compress`/`decompress` accept only one source |
| F | Machine-readable output | — (differentiator) | Only `--validate-report` is JSON — **done in 1.5.0** |
| G | Preallocation / sequential I/O | extract-xiso PR #26 | Extraction writes without sizing the destination first — **done in 1.5.0** |

**Headline recommendation:** create a standalone **`CCISharp`** package (read + write + split), then integrate it into XISOSharp through a new unified image-source layer, alongside split-ISO input and ZAR input. That would make XISOSharp the only library we know of — managed or native — that reads and writes the full OG-Xbox matrix: ISO, Redump, XGD1/2/3, split ISO, CSO v1/v2, split CSO, CCI, split CCI, and ZAR.

> **CCI is not CSO.** It is a different container with a different magic, header, index layout, compression framing and split model. `CSOSharp` cannot read CCI and should not be extended to do so. See §4.

---

## 1. Method and sources verified

### 1.1 Upstream trackers read in full

- `XboxDev/extract-xiso`: issues #1–#111, PRs #1–#110 (open + closed).
- `antangelo/xdvdfs`: issues #1–#192, PRs #1–#189 (open + closed), including the open CCI PR #185.
- `Team-Resurgent/libCCI` (`1b0087e`): the **canonical CCI reference implementation and `FORMAT.md` specification**. A copy lives at `References/libCCI-0.1.0-1b0087e` (GPL-2.0-or-later; reference only).
- `xemu-project/xemu` #1809 (CCI request) and the xemu disc-image documentation.
- Team-Resurgent `XboxToolkit` / `Repackinator` public sources were read **only to confirm the on-disk format**; their code is GPL-3.0 and must not be copied (see §6.3).

### 1.2 Local libraries inspected

| Repository | What it contains | Relevance |
|---|---|---|
| `CSharp_XISOSharp` | XISO library + CLI + GUI + Tester | target |
| `References/libCCI-0.1.0-1b0087e` | canonical CCI reader/writer + `FORMAT.md` spec (GPL-2.0-or-later) | format authority and test oracle |
| `CSharp_ZArchiveSharp` | ZAR reader/writer, `ZArchiveTool.Extract`, node-handle reader API, BCL-only, MinVer | ZAR input |
| `CSharp_CHDStudio/CSOSharp` | read-only CSO/CISO/ZSO reader (`CsoFile`, `CsoStream`), K4os LZ4 | reference only; **not reusable for CCI** |
| `CSharp_CHDSharp` | CHD decoder | optional ecosystem integration, not core |
| `CSharp_RVZSharp` | GameCube/Wii RVZ | not applicable |
| `CSharp_VideoGameFileSystemParser` | ISO/UDF/XDVDFS filesystem parsers | potential CCISharp consumer |
| `CSharp_SimpleXisoDrive` | Dokan/FUSE mount app using XISOSharp + ZArchiveSharp + CHDSharp | potential CCISharp consumer |
| `CSharp_XISOStudio` | GUI using XISOSharp + ZArchiveSharp | potential CCISharp consumer |
| `CSharp_LibFuseSharp` | FUSE bindings | future mount adapter, not this plan |
| `CCDSharp`, `ISZSharp`, `MDSSharp`, `PBPSharp` | CD/PSP containers | not applicable |

### 1.3 Verification performed against the current tree

- `XISOSharp/FileTimeHelper.cs`, `XisoWriter.cs` — rewrite preserves the source volume FILETIME (no gap).
- `XISOSharp/XisoReader.cs` — `OpenImageStream` routes every format through `XisoImageSource`; the intra-table walk is an explicit in-order stack.
- `XISOSharp/XisoSplitter.cs` + `XisoRepairer.cs` — split ISO output/join exist; split parts are accepted as input through `SplitIsoFile`/`SplitIsoStream`.
- `XISOSharp/XisoZarchive.cs` — `CreateZar`, `ListZar`, `ExtractZar` and `ConvertZarToIso`.
- `XISOSharp/XisoPatcher.cs`, `SectorAllocator.cs`, `XisoValidator.cs`, `XisoChecksum.cs` — sector layout, copy-in, audit and SHA3-256 checksum already exist.
- `XISOSharp.Cli/Program.cs` — `--batch` covers extract/list/tree/rewrite/audit and the container verbs (`compress`/`decompress`/`convert`/`split`); JSON only via `--validate-report` (Phase 6).
- `XISOSharp.Tests` - current suite: 1860 passed / 1 opt-in skip on net8.0/net9.0; 1922 passed / 1 opt-in skip on net10.0.

---

## 2. What is already covered (do not re-implement)

| Upstream ask | Existing XISOSharp implementation |
|---|---|
| extract-xiso #32/#33 Redump offsets | `--skip-sectors`, offset probing, `Constants.GlobalLseekOffset`, XGD tables |
| extract-xiso #25 OOM on invalid TOC | `MaxTocEntriesPerTable`, visited-offset set, bounds checks, `XisoFormatException` |
| extract-xiso #66 audit image | `XisoValidator`, `--validate`, `--validate-report`, `AuditXiso` |
| extract-xiso #67 custom rewrite name | `DecodeXiso(outputName)`, `-o` |
| extract-xiso #92 empty dirs as files | `AvlNode.EmptySubdirectory` (`XisoReader.cs:900`) |
| extract-xiso #60/#61 bulk extraction | `--batch`, `--batch-recursive`, `--skip-existing`, `--continue-on-error` |
| extract-xiso #74/#81 filename edge cases | managed strings, `Latin1Encoding`, `XisoPaths` |
| extract-xiso #95 AVL build from a folder | `XisoFileTooLargeException` for oversized entries; pack validates names/sizes |
| xdvdfs #36 output == input | `XisoPaths.AreSamePath`, `CliOutputGuard` |
| xdvdfs #37/#155 filetime | full read/write API, `--file-time`, rewrite preserves source FILETIME |
| xdvdfs #49 dump sector layout | `GetSectorLayout`, `SectorLayout`, `--sector-layout`, `--ranges` |
| xdvdfs #165 copy files into image | `XisoPatcher.CopyIntoImage`, `--copy-in` / `--copy-out` |
| xdvdfs #166 generalize unpacking | `IFilesystem`, `UnpackOptions`, stream/device overloads |
| xdvdfs #97 split xISO | `XisoSplitter.Split` / `Join`, CISO split output/input |
| xdvdfs #101 deterministic pack | sorted entries + `--file-time 0`, determinism tests |
| xdvdfs #120 explore tab | `XisoExplorer` |
| xdvdfs #187 per-file error context | `ExtractFileException` with entry/sector/size |
| xdvdfs #190 resume/skip saved files | `--skip-existing`, `UnpackOptions` |
| xdvdfs #121/#125 image type + XGD offset | `VolumeInfo.DiscFormat`, `DiscLseek` |
| xdvdfs PR #94 Xbox 360 XDVDFS | XGD1/2/3/hybrid offsets, `--update`, `GetXexInfo` |
| xdvdfs PR #110 extract-xiso compat | CLI is already extract-xiso-compatible |
| xdvdfs PR #87 path remapping | `RemapFilesystem`, `build-image`, `image-spec` |
| extract-xiso PR #83 symlinks | file symlinks followed; directory reparse points skipped with warning |
| xdvdfs PR #42 integrity checksum | `XisoChecksum` (SHA3-256 tree hash) |

**Conclusion:** the upstream backlog does not justify a rewrite or a large port. The work is format completion and hardening.

---

## 3. Gap analysis and desired end state

### Gap A — CCI (Cerbios Compressed Image)

**Current state:** no CCI support anywhere in XISOSharp (`grep -i cci` finds nothing). `CisoReader`/`CisoWriter` handle CSO/CISO v1/v2 and split `.cso`; `XisoZarchive` handles ZAR write.

**Desired end state:**
- `CCISharp` package: read, write, split, validate.
- XISOSharp recognises `.cci` / `.1.cci` everywhere it recognises `.cso` / `.1.cso`.
- CLI: `compress --format cci`, `decompress`, aliases `cci` / `uncci`.
- GUI: format selector + filters.

### Gap B — split ISO input

**Current state:** `XisoSplitter` writes `game.1.iso`, `game.2.iso`; `Join` reassembles. `XisoReader.OpenImageStream` handles only plain ISO and CISO; `XisoRepairer` explicitly refuses split parts.

**Desired end state:** list, tree, extract, checksum, validate, sector-layout and compress/decompress read a split set directly, without writing a joined file first.

### Gap C — ZAR input

**Current state:** `CreateZar` only; a `.zar` is extracted to a scratch directory only inside `RebuildRedump`.

**Desired end state:** `list`, `extract`, `checksum`, `validate` (tree comparison) and `convert` from `.zar`; container-independent SHA3-256 so `game.zar` and `game.iso` produce the same checksum for the same tree.

### Gap D — pathological directory tables

**Current state:** `TraverseXiso` iterates right siblings but recurses on left children with `depth + 1`; `MaxTocDepth = 64` rejects a left-skewed chain longer than 64 entries.

**Desired end state:** an explicit-stack traversal that reads any acyclic table within the existing entry/offset bounds, with `MaxTocDepth` applying only to real subdirectory nesting.

### Gap E — batch conversion and naming

**Current state:** `compress`/`decompress`/`convert`/`split` accept explicit sources only; `--batch` covers the main extract/list/tree/rewrite/audit modes.

**Desired end state:** `--batch` / `--batch-recursive` for `compress`, `decompress`, `convert` (ZAR) and `split`; stable naming templates; `--jobs`, `--skip-existing`, `--continue-on-error`. **Done in 1.4.5.**

### Gap F — machine-readable output

**Current state:** text only except `--validate-report`.

**Desired end state:** `--json` on read-only verbs, stable schema, source-generated serialization (trim/AOT-safe), logs to stderr.

### Gap G — preallocation / sequential I/O

**Current state:** extraction streams writes without sizing; only CISO decompression calls `SetLength`.

**Desired end state:** pre-size known-length destination files, use sequential-scan hints, no API break.

### Gap H — explicit sector-layout packing (deferred)

**Current state:** layout can be dumped (`--sector-layout`) and used to seed copy-in, but `CreateXiso`/`BuildImage` cannot pack *to* a supplied layout.

**Desired end state (later):** `--layout <file>` places named files at fixed sectors and allocates the rest. Design only in this plan.

---

## 4. CCI vs CSO — definitive comparison

**They are different formats.** Both are sector-oriented compressed containers, but their headers, index semantics, compression framing and split model differ. A CISO reader cannot open a CCI and vice versa.

| Property | CSO / CISO (v1/v2) | CCI (Cerbios) |
|---|---|---|
| Magic | `CISO` (`0x4F534943` LE) | `CCIM` (`0x4D494343` LE) |
| Header size | 24 bytes | 32 bytes |
| Header fields | uncompressed size, block size, version, align | uncompressed size, index offset, block size, version, align, reserved |
| Index position | **front** of the file, immediately after the header | **end** of the file, at `indexOffset` |
| Index entry | file offset (`>> align`), high bit = plain (v1) / compressed (v2) | file offset (`>> 2`), high bit = LZ4 |
| Per-sector payload | v1: raw DEFLATE; v2: LZ4 frame minus 7-byte header / 4-byte end mark | length-prefixed LZ4 **block** with a 1-byte pad count and zero padding |
| Compression framing | stream/frame oriented | block oriented |
| Split model | each part holds a **sparse global byte range**; parts are not independent containers | each part is an **independent container** with its own header, sector range and index |
| Split naming | `game.1.cso`, `game.2.cso`, … | `game.1.cci`, `game.2.cci`, … |
| Splitting tool | `ciso::split`, xdvdfs `compress` | libCCI / Repackinator / XboxToolkit |
| Reference spec / implementation | `ciso` crate, xdvdfs `ciso` | **`Team-Resurgent/libCCI` + `FORMAT.md`** |
| Reference encoder | LZ4 (lz4_flex-compatible) | **LZ4-HC level 12** (`LZ4HC_CLEVEL_MAX`) |
| CSOSharp support | read v1/v2, **no write** | **none** |
| XISOSharp support | read + write v1/v2 + split | **none** |

**Practical consequences:**

1. Do **not** extend `CSOSharp` with CCI; the package name would lie and its read-only design would have to change.
2. `CSOSharp` is not used by XISOSharp today because XISOSharp already has a more capable CISO implementation (write + split, byte-identical to xdvdfs). Keep it that way.
3. CCI belongs in its own package (`CCISharp`) so XISOSharp, SimpleXisoDrive, XISOStudio and VideoGameFileSystemParser can share it.

---

## 5. Library reuse matrix

| Library | Reuse in this plan | Notes |
|---|---|---|
| **XISOSharp** | core target | image-source factory, CLI, GUI, tests |
| **ZArchiveSharp** | **yes** — `ZArchiveReader` / `ZArchiveTool.Extract` | ZAR input, tree checksum, ZAR→ISO |
| **CSOSharp** | **no** | read-only; different container; XISOSharp's CISO is stronger |
| **CHDSharp** | optional ecosystem only | CHD is a general compression container; keep it in SimpleXisoDrive/XISOStudio so XISOSharp stays Xbox-focused |
| **VideoGameFileSystemParser** | consumer only | parses XDVDFS/ISO/UDF; can add CCI container support by referencing CCISharp |
| **LibFuseSharp** | future | mount adapter belongs in SimpleXisoDrive, not in the core library |
| **CCDSharp / ISZSharp / MDSSharp / PBPSharp / RVZSharp** | **no** | different consoles/formats |

---

## 6. Decision: where CCI lives

### 6.1 Options considered

| Option | Pros | Cons |
|---|---|---|
| **1. New standalone `CCISharp` repo/package** | matches the one-package-per-repo pattern used by XISOSharp/ZArchiveSharp/CHDSharp/RVZSharp; reusable by mount/parser/GUI apps; independent CI/release; clean license boundary | new repo/CI overhead; cross-repo dev needs a project reference or a NuGet publish |
| **2. Add `CCISharp` as a second project inside the XISOSharp repo** | single PR/release; trivial project reference; can share `Lz4.cs` | couples a general format package to XISOSharp's tag/version/release routine; breaks the package-per-repo pattern |
| **3. Add CCI to `CSOSharp` (CHDStudio)** | next to another compression reader; K4os already present | wrong package name; CSOSharp is read-only; CHDStudio is the wrong domain; XISOSharp still takes a NuGet dependency |
| **4. Implement CCI directly inside XISOSharp** | fastest; reuses `Lz4`; no new package | not reusable by SimpleXisoDrive/XISOStudio/VideoGameFileSystemParser; grows XISOSharp's already large surface; no independent versioning |

### 6.2 Recommendation

**Option 1 — standalone `CCISharp` repository at `purelogiccode/CCISharp`.**

- The `CCISharp` package ID is now in use: **1.0.0 is published on NuGet** (nupkg + snupkg). Future versions must use new immutable version numbers.
- Model the repo on `CSharp_ZArchiveSharp`: `CCISharp/`, `CCISharp.Tests/`, `CCISharp.Benchmarks/`, optional `CCISharp.Cli/`, `docs/`, `Directory.Build.props`, `global.json`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `.github/workflows/ci.yml`.
- Target `net8.0;net9.0;net10.0`, MinVer (`v*` tags), `IsTrimmable`/`IsAotCompatible`, XML docs, SourceLink, deterministic builds, symbols (`snupkg`), `EnablePackageValidation`.
- Zero runtime dependencies (own LZ4 block codec). Do **not** add K4os to the shipped package; use K4os only in tests as an independent oracle.
- Unsigned is acceptable: XISOSharp is strong-named but already references unsigned `ZArchiveSharp`. If a signed CCISharp is desired later, add a `CCISharp.snk`.

**Fallback:** if a new repository is not justified now, implement `CciReader`/`CciWriter` inside XISOSharp first (Option 4), then extract the files into CCISharp once the format tests are stable. Do not choose Option 3.

### 6.3 Licensing

- `libCCI` is **GPL-2.0-or-later**; `XboxToolkit` and `Repackinator` are **GPL-3.0**. Do not copy, port or transliterate their code into MIT-licensed CCISharp/XISOSharp.
- `FORMAT.md` is the normative specification. Implement from those format facts and from observed on-disk layouts; use the reference binaries only to produce/verify test data.
- `xdvdfs` is **MIT**, and its open CCI PR #185 is a second clean reference for the on-disk layout. If code is ported from it, retain the MIT attribution in `THIRD-PARTY-NOTICES.md`.
- The bundled LZ4 in libCCI is **BSD-2-Clause**; the LZ4 block codec in XISOSharp/CCISharp is a port/derivative of MIT-licensed `lz4_flex`. Keep the notice.
- Running the GPL `cci_encode`/`cci_extract` tools as an external test oracle is fine; never link or redistribute their code with the MIT package.

---

## 7. CCISharp design

### 7.1 Repository layout

```
CCISharp/
  CCISharp.sln
  Directory.Build.props
  global.json
  LICENSE
  README.md
  THIRD-PARTY-NOTICES.md
  .github/workflows/ci.yml
  CCISharp/
    CCISharp.csproj
    CciFormat.cs
    CciHeader.cs
    CciIndex.cs
    CciSectorCodec.cs
    CciFile.cs            (open/validate/read/extract, split discovery)
    CciStream.cs
    CciWriter.cs
    Lz4.cs
    Models/
      CciError.cs
      CciInfo.cs
      CciOptions.cs
  CCISharp.Tests/
    TempDir.cs
    TestImages.cs
    CciHeaderTests.cs
    CciSectorCodecTests.cs
    CciRoundTripTests.cs
    CciSplitTests.cs
    CciCorruptionTests.cs
    CciReferenceInteropTests.cs
```

Planned additions after v1.0.0: `docs/` (`format.md`, `api.md`, `interop.md`), `WhatsNew.md`, `CCISharp.Benchmarks`, fuzz/large-image tests, and an optional `CCISharp.Cli/` with `pack`, `unpack`, `info`, `validate` (P2).

**Codec duplication note:** CCISharp carries its own copy of the ~400-line LZ4 block codec (ported from XISOSharp's MIT `Lz4.cs`). Do not extract a shared `Lz4Sharp` package yet: XISOSharp's `Lz4` is public API and a third consumer is not on the roadmap. Revisit only if SimpleXisoDrive or VideoGameFileSystemParser needs the codec directly.

### 7.2 On-disk format (normative)

**Authority:** `References/libCCI-0.1.0-1b0087e/FORMAT.md` (commit `1b0087e`). The tables below summarize it; where they disagree, `FORMAT.md` wins.

All integers little-endian.

**Header — 32 bytes**

| Offset | Size | Field | Value / meaning |
|---|---|---|---|
| `0x00` | 4 | Magic | `0x4D494343` (`CCIM`) |
| `0x04` | 4 | HeaderSize | `32` |
| `0x08` | 8 | UncompressedSize | bytes represented by this slice; multiple of 2048 |
| `0x10` | 8 | IndexOffset | absolute offset of the index within this slice |
| `0x18` | 4 | BlockSize | `2048` |
| `0x1C` | 1 | Version | `1` |
| `0x1D` | 1 | IndexAlignment | `2` |
| `0x1E` | 2 | Reserved | `0` |

**Index — `(sectors + 1) × u32` at `IndexOffset`**

- Bit 31: `1` = LZ4-compressed blob, `0` = raw 2048-byte sector.
- Bits 0–30: `fileOffset >> IndexAlignment` (so `fileOffset = (entry & 0x7FFF_FFFF) << 2`).
- Entry `i` is the start of sector `i`; entry `i + 1` is the end. The final entry is the end of the last sector.
- All offsets are 4-byte aligned because the header is 32 bytes and every blob is padded to 4 bytes.

**Sector blob**

- Raw (bit 31 = 0, span = 2048): the 2048 sector bytes verbatim.
- LZ4 (bit 31 = 1): `[padCount:u8][lz4Block][zero padding]`, total span 4-byte aligned.
  - `compressedLength = span - 1 - padCount`.
  - The LZ4 block decodes to exactly 2048 bytes.
- Writer rule (reference `libCCI` `cci_writer.c`): compress only when `compressedLength > 0` and `compressedLength < 2048 - (4 + 4)` (= `< 2040`). Otherwise store raw.
- `padCount = align_up(compressedLength + 1, 4) - (compressedLength + 1)` (0–3).
- Reference encoder uses **LZ4-HC level 12** (`LZ4HC_CLEVEL_MAX`). Any valid LZ4 block is accepted by decoders; byte-for-byte identity with XboxToolkit/Repackinator is not guaranteed across LZ4 implementations.

**Split slices**

- Each slice is an independent container with its own header, sector blobs and index.
- Logical image = concatenation of slice sector ranges, in numeric part order.
- Default split point: **`0xFFBF6000`** (~4 GiB, `4_294_442_752` bytes) — the same point CISO uses and safely below FATX's 32-bit `0xFFFFFFFF` file-size cap. (The reference examples use `4294967296`; see §14.)
- Single-slice output keeps `game.cci`; split output produces `game.1.cci`, `game.2.cci`, … (no unsuffixed file).
- Slice boundary rule (reference `cci_writer.c`): before adding a sector, if `position + (partSectors + 2) * 4 + 2048 > SplitBytes` and the part already has at least one sector, finish the part and start the next one.
- The final index entry (sentinel) holds `indexOffset`; its LZ4 bit is unused.
- The reference implementation writes/reads **single-digit** parts `1..9`, contiguous from `1`. Multi-digit discovery is an optional CCISharp extension; do not rely on it for interop.

**Edge cases**

- Input length not a multiple of 2048: zero-pad the final sector; `CciFile.Length` reports the padded length.
- Empty input: reject (`InvalidSize`); a zero-sector CCI is not valid.
- Version > 1, align != 2, block size != 2048: reject with a specific error; do not guess.

### 7.3 Public API sketch

```csharp
namespace CCISharp;

public enum CciError
{
    None = 0,
    NotFound,
    AccessDenied,
    InvalidHeader,
    UnsupportedHeaderSize,
    UnsupportedBlockSize,
    UnsupportedVersion,
    UnsupportedIndexAlignment,
    InvalidSize,
    InvalidIndex,
    CorruptSector,
    Truncated,
    UnexpectedEndOfStream,
    Io,
    InvalidArgument,
    AlreadyExists,
    Cancelled,
}

public readonly record struct CciHeader(
    ulong UncompressedSize, ulong IndexOffset,
    uint BlockSize, byte Version, byte IndexAlignment);

public sealed record CciPartInfo(
    string Path, ulong StartSector, ulong SectorCount,
    ulong UncompressedSize, long CompressedSize, ulong IndexOffset)
{
    public double Ratio => UncompressedSize == 0 ? 0 : (double)CompressedSize / UncompressedSize;
}

public sealed class CciFile : IDisposable
{
    public CciHeader Header { get; }                    // first slice
    public IReadOnlyList<CciPartInfo> Parts { get; }
    public ulong TotalSectors { get; }
    public long Length { get; }                         // total uncompressed bytes (sector-padded)
    public string? FirstPath { get; }

    public static CciError Open(string path, out CciFile? cci);
    public static CciError Open(string path, CciReaderOptions? options, out CciFile? cci);
    public static CciError Open(Stream stream, bool ownsStream, out CciFile? cci);

    public CciError ReadSector(ulong sector, Span<byte> buffer);
    public CciError Read(ulong offset, Span<byte> buffer);       // byte-granular
    public CciStream OpenStream();
    public CciError ExtractToIso(string outputPath, CciExtractOptions? options = null);
    public CciError Validate(bool deep = false, out CciValidationReport? report);
}

public sealed class CciStream : Stream { /* seekable, read-only, sector cache */ }

public sealed record CciExtractOptions
{
    public bool Overwrite { get; init; }
    public bool Preallocate { get; init; } = true;
    public IProgress<CciProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record CciValidationIssue(int PartIndex, ulong? Sector, string Reason);

public sealed record CciValidationReport(
    bool IsValid,
    IReadOnlyList<CciPartInfo> Parts,
    ulong TotalSectors,
    ulong UncompressedSize,
    long CompressedSize,
    double Ratio,
    IReadOnlyList<CciValidationIssue> Issues);


public sealed class CciWriter
{
    public static CciError Compress(string sourcePath, string outputPath, CciWriteOptions? options = null);
    public static CciError Compress(Stream source, long inputLength, Stream output, CciWriteOptions? options = null); // single slice
    public static Task<CciError> CompressAsync(string sourcePath, string outputPath,
        CciWriteOptions? options = null);
}

public sealed record CciReaderOptions
{
    public FileShare Share { get; init; } = FileShare.Read;
    public bool ValidateIndex { get; init; } = true;
    public int MaxSectorSpan { get; init; } = 1 << 20;
}

public sealed record CciWriteOptions
{
    public int Level { get; init; } = 9;                 // 0 = store, 1..9 = LZ4 acceleration 10-level
    public long SplitBytes { get; init; } = 0xFFBF6000;  // ~4 GiB; 0 = single file
    public int BufferSize { get; init; } = 1 << 20;
    public bool Overwrite { get; init; }
    public IProgress<CciProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public readonly record struct CciProgress(
    long SectorsDone, long TotalSectors, long BytesIn, long BytesOut, int SliceIndex);
```

**Error model:** mirror `CSOSharp` (`CciError` + `TryOpen`-style `out` result). Programmer errors (`null` path, negative buffer) throw standard argument exceptions; format/I/O failures return `CciError`. XISOSharp wraps the error in `XisoFormatException`/`IOException` at its boundary so its public exception contract is unchanged.

**No dependency on XISOSharp.** The integration adapter (`CciBlockDevice`) lives in XISOSharp.

### 7.4 Reader behavior

- Discover slices (reference `cci_file.c` / `FORMAT.md`):
  - `game.<single digit>.cci` → strip the numeric sub-extension, then gather `game.1.cci` … `game.9.cci` contiguously from `1`, stopping at the first missing part.
  - `game.cci` → single slice.
  - If `game.cci` and `game.1.cci` both exist, reading `game.cci` reads only that file; reading `game.1.cci` reads the set.
  - Optional extension: accept multi-digit parts (`game.10.cci`) when no single-digit interpretation applies; document that the reference tool does not create them.
- Load each slice header and index eagerly (4 bytes per sector); slice ranges are defined by each header's `uncompressed_size`, not by file size.
- Validate:
  - magic/header size/block size/version/align;
  - `UncompressedSize > 0`, `% 2048 == 0`;
  - `IndexOffset >= 32`, `IndexOffset + (sectors + 1) * 4 <= fileLength`;
  - first offset `>= 32`; offsets monotonic; last offset `<= IndexOffset`;
  - span `<= MaxSectorSpan` (default 1 MiB) to bound corrupt allocations;
  - compressed span has `padCount < span - 1` and `compressedLength >= 1`.
- Decode:
  - `span == 2048 && !lz4` → raw copy.
  - otherwise → `pad = span[0]`, `compLen = span - 1 - pad`, LZ4 block decode to 2048.
  - A `span == 2048 && lz4` blob is decoded as compressed (do not assume raw just because the span is 2048).
- Random access: global sector → slice via binary search; each slice owns a `FileStream` with `FileOptions.RandomAccess` and a lock.
- Cache: optional small sector cache for sequential streams; `CciFile.ReadSector` remains lock-protected and thread-safe.
- `ExtractToIso`: stream in 1 MiB chunks, `SetLength(totalSectors * 2048)`, `FileOptions.SequentialScan`, progress + cancellation, temp file + move to avoid partial output.
- Memory: `O(sectors)` for the index (~4 bytes/sector, ≈17 MB at 8.5 GiB); no whole-image buffering.

### 7.5 Writer behavior

- Input must be readable; the output must be seekable (or a path). The file-path overload knows the length up front; a stream overload can accept an explicit length and zero-pad the final sector.
- Write a zeroed/placeholder header first; write sector blobs; record `(length, isCompressed)` per sector; write the index; seek back and patch `UncompressedSize` and `IndexOffset`.
- Level mapping: `0` = store all sectors raw; `1..9` = CCISharp quality presets mapped to the available encoder's acceleration (`10 - level`, level 9 = best of the fast codec). This is **not** the same scale as libCCI's LZ4-HC `1..12`; document the difference.
- Encoder strategy: v1 ships the pure-managed fast LZ4 block codec (the lz4_flex port). Output is spec-valid and interoperable, but typically a few percent larger than libCCI's LZ4-HC output. Expose `ICciBlockEncoder` so an HC encoder (bundled or a K4os adapter) can be plugged in later; add an `--best`/level-12 mode once HC is available.
- Document that CCISharp's LZ4 output is spec-valid but **not byte-identical** to Repackinator's/ libCCI's K4os/LZ4-HC encoder.
- Reuse one compression buffer and `ArrayPool<byte>`; no per-sector allocations in steady state.
- Output safety: write to a temporary file and move into place on success; on failure/cancel delete the temp and never leave a partial `.cci`. For split output, finish every slice before renaming any of them, or clean up all parts on failure.
- Overwrite policy: `CciWriteOptions.Overwrite`; default `false` (return `AlreadyExists`). The CLI maps `-y` / `-n`.
- Progress: sectors done, total, bytes in/out, current slice index.
- Async wrapper over `Task.Run` (the library stays synchronous internally, like XISOSharp's `CisoWriter`).

### 7.6 Split policy details

- `SplitBytes <= 0` → single file.
- `SplitBytes < 2048` (but > 0) → `InvalidArgument`.
- The boundary check is conservative: reserve room for the remaining index entries plus the largest possible sector blob. This can make a slice slightly smaller than the nominal split point, never larger.
- Each slice's `UncompressedSize` is its own sector count × 2048.
- Part naming: `game.cci` + split → `game.1.cci` … `game.9.cci` (single-digit, reference-compatible). Generated part extensions are normalized to lowercase `.cci`; input discovery is case-insensitive.
- Reader accepts single-digit parts per the reference; multi-digit parts are an optional extension (see §7.4).

### 7.7 Validation report

`CciFile.Validate` returns:
- header fields and slice count;
- total sectors, compressed bytes, ratio;
- per-slice index monotonicity and bounds;
- per-sector decode check (optional `deep: true`, which reads and decodes every sector);
- list of issues with slice index + sector index + reason.

CLI `cci validate` maps to this report.

### 7.8 Performance targets

- No whole-file buffering; index-only memory.
- Sequential decompression within ~10% of raw ISO read throughput on NVMe for compressible data (measure, do not hard-code).
- Random 2048-byte reads: one seek + one LZ4 block decode; cache the last sector.
- Writer: single-threaded v1; a parallel compress pipeline is a follow-up if benchmarks justify it.
- Trim/AOT clean (`IsTrimmable`, `IsAotCompatible`; no reflection).

### 7.9 Test plan

1. **Header tests** — parse/write every field; reject bad magic/header size/block size/version/align.
2. **Index tests** — encode/decode `u32`, high-bit flag, `>> 2`, monotonic checks, final entry, out-of-range offsets.
3. **Sector codec** — zeros, repeating pattern, random incompressible, highly compressible, exactly-threshold data, 2048-byte raw fallback, `span == 2048` compressed edge case.
4. **Round-trip** — sizes: 1 sector, 2048+1, 1 MiB, 64 MiB, 1 GiB sparse; verify byte equality and padded tail.
5. **Split** — split at small thresholds (e.g., 2100 bytes, 1 MiB), naming, multi-digit parts, concatenation equals the source, each slice validates independently.
6. **Corruption** — truncated header/index, index beyond EOF, non-monotonic index, span too large, bad pad count, LZ4 failure, zero sectors, wrong version.
7. **Fuzz** — 10k mutated inputs; only `CciError` results, no unexpected exceptions, no unbounded allocation.
8. **Oracle** — decode CCISharp output with a minimal independent test decoder built on test-only `K4os.Compression.LZ4`; cross-check against the reference `libCCI` tools (`cci_encode` / `cci_extract` / `cci_info`) built from `References/libCCI-0.1.0-1b0087e` on a **synthetic** image (no copyrighted game data).
9. **Concurrency** — parallel `ReadSector` calls from multiple threads; no data races; `CciStream` documented as not thread-safe.
10. **Large file** — >4 GiB split set without loading the whole image into memory (CI can use a sparse temp file).
11. **Trim/AOT** — analyzers clean; no reflection.

### 7.10 CLI, benchmarks, docs, CI

- **CLI (P2):** `cci pack <input.iso> [output.cci] [--level N] [--split BYTES]`, `cci unpack`, `cci info`, `cci validate`. Useful for interop and CI smoke tests; XISOSharp's CLI remains the primary user surface.
- **Benchmarks:** compress/decompress throughput, random sector reads, split overhead, allocation counts.
- **Docs:** `docs/format.md` (byte tables, citing `libCCI/FORMAT.md`), `docs/api.md`, `docs/interop.md` (libCCI, Repackinator/XboxToolkit, xdvdfs PR #185), README quick start.
- **CI:** build + test on `ubuntu-latest`, `windows-latest`, `macos-latest`; tag-only pack/publish to NuGet with the `nuget` environment secret; `fetch-depth: 0` for MinVer.
- **Reference oracle:** build `References/libCCI-0.1.0-1b0087e` with CMake (bundled LZ4 submodule is present) and use `cci_encode` / `cci_extract` / `cci_info` in BattleTests or manual interop runs; never ship or link the GPL binary.

### 7.11 CCISharp acceptance criteria

- `dotnet test` green on net8.0/net9.0/net10.0 on all three OSes.
- Round-trip of a 1 GiB sparse image and a 64 MiB random image is byte-identical.
- A 4 GiB+ split set validates and decodes without materializing the whole image.
- A `libCCI`-produced fixture (`cci_encode` on a synthetic image) parses correctly; CCISharp-produced CCI parses in `cci_extract`/the independent test decoder.
- Fuzz suite finds no unhandled exception or OOM.
- Package `CCISharp` 1.0.0 packs with README, LICENSE, third-party notices, XML docs and symbols; nupkg + snupkg published.

---

## 8. XISOSharp workstreams

### 8.1 Phase 0 - unified image-source factory (enabler) — done in 1.4.5

**Problem:** container detection is duplicated in `XisoReader.IsCsoPath`, `XisoChecksum.IsCsoPath`, `XisoRepairer`, CLI `IsImage`, GUI drop routing, and output-naming helpers. Adding CCI and split ISO by copying those checks would make it worse.

**Design:**

```csharp
namespace XISOSharp;

public enum XisoImageFormat
{
    Iso,
    Ciso,
    Cci,
    SplitIso,
    SplitCiso,
    SplitCci,
}

public sealed record XisoImageSourceInfo(
    XisoImageFormat Format,
    string DisplayName,
    long Length,
    IReadOnlyList<string> Parts);

public static class XisoImageSource
{
    public static bool TryDetect(string path, out XisoImageSourceInfo? info);
    public static Stream OpenRead(string path, FileShare share = FileShare.Read);
}
```

- `OpenRead` returns a seekable `Stream` for every sector-based format:
  - ISO → `FileStream`;
  - CISO single/split → `BlockDeviceStream(new CisoBlockDevice(...))`;
  - CCI single/split → `BlockDeviceStream(new CciBlockDevice(...))` (new, wraps `CciFile`);
  - split ISO → `SplitIsoStream` (new).
- `CisoReader`/`CisoWriter` public APIs stay unchanged; they become implementation details used by the factory.
- Add `StreamBlockDevice : IBlockDevice` for generic seekable streams (reused by CCI and any future container).
- Replace the duplicated checks with the factory. Keep the old internal helpers as thin wrappers for compatibility.

**Acceptance:** no behavior change; all existing tests green; one detection table test covering every extension/part.

### 8.2 Phase 1 - CCI integration — done in 1.4.5

1. Add `CCISharp` dependency (ProjectReference during development, NuGet `1.0.0` at release).
2. New `XISOSharp/BlockDevice/CciBlockDevice.cs`:
   - wraps `CciFile`;
   - `Length` = total uncompressed bytes;
   - `Read(offset, span)` maps to `CciFile.Read`;
   - `Write` throws `NotSupportedException`.
3. `XisoImageSource` handles `.cci` and `.1.cci`.
4. `XisoReader`:
   - `OpenImageStream` uses the factory;
   - output naming strips `.cci`/`.1.cci` (like `StripCsoSuffix`);
   - `GetVolumeInfo`, `VerifyXiso`, `AuditXiso`, `GetSectorLayout`, extraction and listing work unchanged through the stream/device path.
5. `XisoChecksum` and `XisoRepairer` recognise CCI (checksum reads it; repair refuses in-place with a clear message).
6. CLI:
   - `compress --format cso|cci` (default `cso` keeps existing behavior);
   - `--cci-level 0..9`, `--cci-split BYTES` (mirror `--ciso-*`);
   - `decompress` auto-detects `.cso`/`.cci`;
   - aliases `cci` / `uncci`;
   - help text + flag-contract tests.
7. GUI:
   - format selector on the compress tab;
   - `.cci`/`.1.cci` in file filters and drop routing;
   - `CliCommands.Compress` passes the format.
8. Docs: `docs/compression.md` CCI section, `docs/cli.md`, `docs/api-xisoreader.md`, `docs/xdvdfs-compat.md`, READMEs, release notes.

**Acceptance:**
- `XISOSharp checksum game.cci` == `XISOSharp checksum game.iso`.
- `XISOSharp -x game.cci` extracts byte-identical files.
- `XISOSharp -r game.cci` output is byte-identical to `-r game.iso`.
- `XISOSharp compress --format cci game.iso` produces a CCI readable by CCISharp and a Repackinator-produced fixture is readable by XISOSharp.
- `XISOSharp decompress game.1.cci` reconstructs the original ISO byte-for-byte.
- `--validate`, `--sector-layout`, `--ranges`, and `--filetime` (read) work on CCI inputs; `--set-filetime` and `--repair` refuse compressed containers in place (recompress or decompress first).

### 8.3 Phase 2 - split ISO input — done in 1.4.5

**New types:**
- `SplitIsoFile` (internal static): `IsSplitPath` (`.1.iso`), `OpenParts` (consecutive, multi-digit), `PartPath`.
- `SplitIsoStream : Stream`: read-only, seekable view over the concatenated parts; maps global offsets to `(part, local offset)`.
- `SplitIsoBlockDevice : IBlockDevice`: wraps `SplitIsoStream` (or the parts directly).

**Wiring:**
- `XisoImageSource` detects `.1.iso`.
- `OpenImageStream` returns the split stream.
- `StripCsoSuffix` gets a split-ISO analogue so output directories are named `game`, not `game.1`.
- `XisoRepairer` continues to refuse split parts (in-place writes need a joined file); `Join` remains for materialization.

**Acceptance:**
- Split a known image with `XisoSplitter.Split`, then `-l`, `-t`, `-x`, `--checksum`, `--validate`, `--sector-layout`, `compress` all work directly on `game.1.iso` and match the single-file results.
- A missing middle part produces a clear error, not silent truncation.

### 8.4 Phase 3 - ZAR input — done in 1.4.5

**New APIs in `XisoZarchive`:**
- `ListZar(string zarPath)` → entries (path, size, isDirectory).
- `ExtractZar(string zarPath, string outputDir, bool skipExisting, IProgress<...>, CancellationToken)` — node-walk with `XisoPaths`/`XisoEntryNames` path-safety checks (no `..`, no drive-relative names, no separators in names).
- `ConvertZarToIso(string zarPath, string isoPath, ...)` — v1: extract to a temp directory, `XisoWriter.CreateXiso`, clean up; v2 (follow-up): direct writer from a source-filesystem abstraction.
- `XisoChecksum.ComputeZarChecksum(string zarPath)` — same SHA3-256 tree hash as `ComputeImageChecksum`, so equivalent trees hash equally.

**Wiring:**
- CLI auto-detects `.zar` for `list`, `extract`, `checksum`, `validate` and conversion verbs.
- `validate <zar> <iso>` compares file trees (reuse `XisoValidator`'s tree comparison).
- GUI: ZAR as an input source for extract/list/convert.

**Acceptance:**
- `-l game.zar` matches the tree inside the image.
- `-x game.zar` produces byte-identical files to `-x game.iso`.
- `--checksum game.zar` == `--checksum game.iso` for the same tree.
- `convert game.zar game.iso` produces an image that extracts identically.

### 8.5 Phase 4 — pathological TOC traversal — done in 1.4.5

**Change:** replace the left-subtree recursion in `TraverseXiso` with an explicit stack.

- Preserve traversal order exactly: left subtree → entry → right siblings (in-order).
- Keep the visited-offset set, table bounds, and `MaxTocEntriesPerTable`.
- Split the depth concepts:
  - `MaxTocDepth` (existing, 64) applies to **subdirectory nesting** only in `TraverseXiso`;
  - intra-table tree depth is bounded by `MaxTocEntriesPerTable` and the explicit stack, not by 64.
- Add a hard stack-frame guard (`Constants.MaxTocStackFrames`, 100k) to fail cleanly on absurd inputs rather than grow memory without bound.
- Keep the existing `ContinueOnError` subtree-skip semantics.

**Tests:**
- Left-skewed chain of 4,096 entries; right-skewed chain; alternating; a cyclic left pointer.
- The same left chain audits, maps (`GetXisoRanges`/`GetFileEntries`), repairs a deep
  reserved-attribute bit, salvages to a fresh image, and packs to ZAR.
- Compare list/tree/extract/rewrite output against the same tree written in balanced form.
- Full suite + BattleTests parity must stay green.

**Acceptance:** a 4k left-chain image lists/extracts/rewrites successfully; corrupt cycles still fail with the existing named error.

**Scope note (closed):** `TraverseXiso` (extract/list/tree/rewrite/unpack) was
hardened first. The follow-up is now implemented too: the audit
(`AuditWalk`), sector-range (`XisoRanges.GetValidSectorsCore` /
`CollectFileEntries`), repair (`XisoRepairer.CollectWalk`), salvage
(`XisoSalvager.SalvageWalk`) and ZAR pack (`XisoZarchive.ParseNode`) walks all
use an explicit in-order stack within a table. `MaxTocDepth` bounds real
subdirectory nesting only, so a 4,096-entry left spine audits, maps, repairs,
salvages and packs to ZAR; cycles and out-of-table pointers still hit the
existing named error or report/drop gate.

### 8.6 Phase 5 — batch conversion and naming — done in 1.4.5

- `--batch` / `--batch-recursive` on:
  - `compress` (`.iso`, `.xiso` → `.cso`/`.cci` depending on `--format`);
  - `decompress` (`.cso`, `.1.cso`, `.cci`, `.1.cci` → `.iso`);
  - `convert --format zar` (`.iso` → `.zar`) and `convert --format iso` (`.zar` → `.iso`);
  - `split` (unsplit `.iso`/`.xiso` only; already-split sets are skipped).
- Shared batch behavior: `--jobs <n>` (1..64), `--skip-existing`, `--continue-on-error`, `-y`/`-n`, per-file status and a closing summary.
- Naming template: `--output-name "{name}.xiso.{format}"` with `{name}` (source stem after stripping `.1.cso`/`.1.cci`/`.1.iso`), `{ext}`, `{format}`. Relative templates resolve against each source's directory; an extensionless template gets the output format's extension; subdirectories are created.
- Split sets are collapsed to their first part during discovery; duplicate outputs and outputs that would clobber another batch input are refused before any write.
- A failed conversion removes its partial output (a bad CISO used to leave a truncated `.iso` behind).
- The existing single-source positional behavior and shared-output guard are unchanged.

**Acceptance:** `compress --batch dir --format cci` converts every image; an interrupted run resumes with `--skip-existing`; naming templates are covered by tests. **Done:** `CliBatchConvertTests` (19 tests) covers CISO/CCI compress, decompress, ISO↔ZAR convert, split, recursion, templates (including subdirectories), duplicate-output refusal, skip-existing resume, continue-on-error partial cleanup, and parallel jobs.

### 8.7 Phase 6 — `--json` output — done in 1.5.0

- Scope: `info`, `list`, `tree`, `checksum`, `sector-layout`, `ranges`, `filetime`, `xbe-info`, `xex-info`, `validate`.
- `--json` writes JSON to stdout; all human logs go to stderr.
- Add `JsonOutputContext` (`JsonSerializerContext`, source-generated) to keep trim/AOT compatibility.
- Schema: `{ "schemaVersion": 1, "command": "...", ... }`; arrays for lists; stable names; omit nulls.
- Add golden-schema tests and trim-analyzer checks.
- Enrich `-v` / `--version` (and its JSON form) with XISOSharp, CCISharp, ZArchiveSharp, runtime and commit info — closes extract-xiso #21.

**Acceptance:** `XISOSharp info --json game.iso | ConvertFrom-Json` works; schema snapshots are stable; no reflection-based serialization.

**Implementation (1.5.0):** `XISOSharp.Cli/JsonOutput.cs` defines the versioned DTOs and a source-generated `JsonOutputContext` (camelCase, nulls omitted). `Logger.RouteInfoToError` keeps human progress on stderr while JSON owns stdout; `--json` is accepted by `-i`, `-l`, `-t`, `--checksum`/`checksum`, `--sector-layout`, `--ranges`, `--filetime`, `--xex-info`, `--xbe-info`, `validate` and `-v`/`--version`. `CliJsonTests` covers every payload, stdout/stderr separation, multi-image list documents and CCI input. `-v` now reports XISOSharp, CCISharp, ZArchiveSharp, runtime, OS, architecture and the MinVer commit.

### 8.8 Phase 7 — preallocation and sequential I/O — done in 1.5.0

- Add an optional default interface member to `IFilesystem`:
  ```csharp
  Stream CreateFile(string path, long? expectedLength) => CreateFile(path);
  ```
  `LocalFilesystem` overrides it and calls `SetLength` when the length is known; `MemoryFilesystem` may preallocate.
- `XisoFileCopier` passes the expected size; use `FileOptions.SequentialScan` on source and destination where supported.
- `XisoWriter` pre-sizes the output when the final image size is known before the data phase (inspect the write order; only apply where it cannot truncate already-written metadata).
- No behavior change; benchmark before/after with `XISOSharp.Benchmarks`.

**Acceptance:** extraction/creation output is byte-identical; benchmarks show no regression and a measurable improvement on spinning disks/large files.

**Implementation (1.5.0):** `IFilesystem.CreateFile(path, expectedLength)` is an additive default interface member; `LocalFilesystem` opens with `FileOptions.SequentialScan` and calls `SetLength` before the first write, `MemoryFilesystem` preallocates its buffer, and custom stores keep the old behavior through the default member. `XisoReader.ExtractFile`/`CopyOutFile` pass the entry size, `XisoWriter` sizes the output to the exact post-layout extent before the data phase, and plain-ISO sources open with `SequentialScan`. `PreallocationTests` verifies pre-sizing, the default-member fallback, and that extraction passes each entry's size to the destination.

### 8.9 Phase 8 — explicit sector-layout packing — done in 1.6.0

- `--layout <file>` accepts the JSON emitted by `--sector-layout --json`
  (`XisoLayoutSpec.ParseJson`) or a compact `path=sector` text spec
  (`XisoLayoutSpec.ParseText`; `#`/`;` comments, `0x` hex, optional
  `total-sectors=N`).
- `LayoutPlanner` validates every fixed extent (system area, overlap, bounds,
  too-small extents), assigns the matching nodes their requested sectors, and
  seeds `SectorAllocator` so unplaced entries are allocated first-fit around
  the fixed extents. `TotalSectors` is preserved; `prependSectors` keeps the
  stored sectors partition-relative.
- The writer runs a placement pass after `CalculateDirectoryRequirements` and
  before `CalculateDirectoryOffsets`/the data phase, so table/data writes see
  the final placements.
- Wired through `XisoWriter.CreateXiso`/`CreateXisoAsync`/`PackFromDirectory`/
  `PackFromDirectoryAsync`, `RemapFilesystem.BuildImage`, and the CLI's `-c`,
  `--pack <dir>` and `build-image` (`--layout <file>`).
- Tests: `LayoutPackingTests` (41) covers parsing, byte-identical JSON
  round-trips, fixed/first-fit placement, reserved JSON extents, total-sector
  preservation, prepended partitions, case-insensitive paths, empty entries,
  `build-image`, and CLI usage errors.

---

## 9. File-by-file change map

### CCISharp (new repo)

| File | Purpose |
|---|---|
| `CCISharp/CciHeader.cs` | 32-byte header parse/write/validate |
| `CCISharp/CciIndex.cs` | `(sectors+1) × u32` encode/decode, bounds/monotonic checks |
| `CCISharp/CciSectorCodec.cs` | raw/LZ4 blob encode/decode, padding rule, threshold |
| `CCISharp/Lz4BlockCodec.cs` | pure-managed LZ4 block codec (MIT port; no package dependency) |
| `CCISharp/CciSplitFile.cs` | part discovery/naming, per-slice header/index |
| `CCISharp/CciFile.cs` | open/validate/read/extract, slice table |
| `CCISharp/CciStream.cs` | seekable read-only stream, sector cache |
| `CCISharp/CciWriter.cs` | compress, placeholder header, index, split, progress |
| `CCISharp/Models/*` | errors, options, progress, slice info, validation report |
| `CCISharp.Tests/*` | see §7.9 |
| `CCISharp.Benchmarks/*` | throughput/allocation benchmarks |
| `docs/format.md`, `docs/api.md`, `docs/interop.md` | format + API + interop notes |
| `.github/workflows/ci.yml` | 3-OS build/test, tag pack/publish |

### XISOSharp

| File | Change |
|---|---|
| `XISOSharp/XISOSharp.csproj` | add `CCISharp` package reference (at release) |
| `XISOSharp/XisoImageSource.cs` (new) | format detection + `OpenRead` factory |
| `XISOSharp/BlockDevice/StreamBlockDevice.cs` (new) | `IBlockDevice` over a seekable stream |
| `XISOSharp/BlockDevice/CciBlockDevice.cs` (new) | `IBlockDevice` over `CciFile` |
| `XISOSharp/BlockDevice/SplitIsoBlockDevice.cs` (new, phase 2) | `IBlockDevice` over split parts |
| `XISOSharp/SplitIsoFile.cs` (new, phase 2) | `.1.iso` discovery + `SplitIsoStream` |
| `XISOSharp/XisoReader.cs` | use the factory; strip CCI/split suffixes; keep public API |
| `XISOSharp/XisoChecksum.cs` | use the factory; add `ComputeZarChecksum` |
| `XISOSharp/XisoRepairer.cs` | refuse CCI/split in-place with a clear message |
| `XISOSharp/XisoZarchive.cs` | `ListZar`, `ExtractZar`, `ConvertZarToIso` |
| `XISOSharp/XisoWriter.cs` | layout pre-sizing; sequential output; explicit layout packing (`layout` parameter) |
| `XISOSharp/Models/XisoLayoutSpec.cs` (new, phase 8) | layout JSON/text parsing, `XisoLayoutEntry`, `FromSectorLayout` |
| `XISOSharp/LayoutPlanner.cs` (new, phase 8) | fixed-extent validation/placement + seeded allocator |
| `XISOSharp/DataStructures/OffsetCalcContext.cs` | fixed-start/total-sector context for layout mode |
| `XISOSharp/XisoReader.cs` | pre-sized extraction/copy-out; offset-aware `GetVolumeInfo`/`ListDirectory`/`GetSectorLayout` |
| `XISOSharp/LocalFilesystem.cs` / `MemoryFilesystem.cs` | `CreateFile(path, expectedLength)` pre-sizing + `SequentialScan` |
| `XISOSharp/Interfaces/IFilesystem.cs` | optional `CreateFile(path, expectedLength)` default member |
| `XISOSharp.Cli/Program.cs` | `--format`, `--cci-*`, `decompress` autodetect, batch, `--json`, enriched `--version` |
| `XISOSharp.Cli/JsonOutput.cs` (new, phase 6) | JSON DTOs + source-generated `JsonOutputContext` |
| `XISOSharp.Cli/BatchConversion.cs` (new, phase 5) | batch discovery, naming templates, collision guards, prompts, jobs, summary |
| `XISOSharp.Cli/CliCommands.cs` | GUI command builders for CCI/format/batch |
| `XISOSharp.Gui/**` | format selector, filters, drop routing |
| `XISOSharp.Tests/*` | new CCI/split/ZAR/traversal/batch/JSON tests |
| `XISOSharp.BattleTests/*` | oracle/parity cases for new inputs |
| `docs/*`, `README.md`, `WhatsNew.md`, `docs/release-notes.md` | documentation refresh |

---

## 10. Test plan and quality gates

### 10.1 Test layers

| Layer | What it covers |
|---|---|
| Unit | Header/index/sector codec, padding math, split naming, options validation |
| Integration | CCISharp round-trip, split sets, large sparse images, concurrency |
| XISOSharp parity | CCI/CSO/ISO extraction, rewrite, checksum, validate, sector layout |
| Oracle/interop | Repackinator fixture read by CCISharp; CCISharp output read by independent decoder; xdvdfs checksum equality |
| Robustness | Corruption matrix, fuzz, truncated parts, missing parts, cycles |
| CLI contract | Flags, aliases, batch, JSON schema, exit codes |
| GUI | Format selector, drop routing, self-test (`--self-test`) |
| Performance | BenchmarkDotNet before/after; no >10% regression |

### 10.2 Quality gates (every phase)

- `dotnet build -c Release` clean for the full solution.
- `dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj -c Release` green on net8.0/net9.0/net10.0.
- JetBrains InspectCode: 0 errors, 0 warnings (repo standard).
- `IsTrimmable`/`IsAotCompatible` analyzers clean.
- No new public API without XML docs.
- Docs + release notes updated in the same change.
- No deletion of Release artifacts (per `AGENTS.md`).

### 10.3 Test data

- Synthetic images generated by `XISOSharp.TestDataGenerator` or tiny hand-built sector patterns.
- No copyrighted game data in the repository.
- Repackinator-produced fixtures only for synthetic images; document how to regenerate them.
- Fuzz seeds checked in as small binaries or generated deterministically.

---

## 11. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| CCI format drift / undocumented variants | Low | High | Version field is 1; reject unknown versions; keep a format doc; test against real fixtures |
| GPL contamination from XboxToolkit/Repackinator | Medium | High | Use the MIT xdvdfs PR + observed format; no code copying; third-party notices |
| LZ4 output differs from Repackinator | High (expected) | Low | Interoperability, not byte parity; document; independent decoder tests |
| Split semantics ambiguity (independent slices vs sparse ranges) | Medium | High | Follow XboxToolkit/xdvdfs behavior; test slice-by-slice; document clearly |
| New package/maintenance overhead | Medium | Medium | Template from ZArchiveSharp; shared CI pattern; keep scope tight |
| Traversal refactor regresses rewrite parity | Medium | High | Preserve order; golden corpus; full suite + BattleTests; compare against recursive implementation |
| XISOSharp public API break | Low | High | Additive changes only; keep `CisoReader`/`CisoWriter`; mark new types as additions |
| Strong-name/dependency issues | Low | Low | Unsigned dependency precedent (ZArchiveSharp); sign CCISharp later if needed |
| Large-image memory/time in CI | Medium | Medium | Sparse files; opt-in `[Trait("Category","Large")]` tests; index-only design |
| JSON output breaks AOT/trim | Medium | Medium | Source-generated `JsonSerializerContext`; analyzer gate |

---

## 12. Sequencing and releases

### 12.0 Current state (2026-11-01)

- **Reference:** `References/libCCI-0.1.0-1b0087e` built; `cci_encode`/`cci_extract`/`cci_info` verified.
- **CCISharp 1.0.0:** <https://github.com/purelogiccode/CCISharp> - published to NuGet at <https://www.nuget.org/packages/CCISharp/1.0.0>.
  - `net8.0`/`net9.0`/`net10.0`, zero runtime dependencies, trimmable/AOT.
  - Reader, writer, split sets, seekable stream, ISO extraction, validation.
  - **39 tests green on all three TFMs**, including `libCCI` -> CCISharp and CCISharp -> `libCCI` interop.
  - CI green on Windows/Linux/macOS; tag `v1.0.0` builds/publishes `1.0.0` (nupkg + snupkg).
- **XISOSharp 1.6.0 (in tree, untagged):**
  - `XisoImageSource` factory (ISO/split ISO/CISO/split CISO/CCI/split CCI/ZAR).
  - `CciBlockDevice`, `XisoCci`, `StreamBlockDevice`, `SplitIsoFile`/`SplitIsoStream`.
  - `XisoZarchive.ListZar`/`ExtractZar`/`ConvertZarToIso`; `XisoChecksum.ComputeZarChecksum`.
  - CLI: `compress --format cci`, `--cci-level`/`--cci-split`, `cci`/`uncci`, `convert`, ZAR-aware list/extract/checksum.
  - GUI: CISO/CCI format selector, `.cci` filters/drop routing, ZAR input through Extract.
  - **Full suite green:** 1913 passed / 1 skipped (1914) on net8.0/net9.0; 1975 passed / 1 skipped (1976) on net10.0.
  - **Phase 4:** `TraverseXiso` walks each directory table with an explicit in-order stack; a 4,096-entry left-skewed chain lists/trees/extracts/rewrites (the rewrite checksum matches the balanced form). `Constants.MaxTocStackFrames` backstops the stack; subdirectory nesting still uses `MaxTocDepth`. **Follow-up done:** `AuditWalk`, `XisoRanges.GetValidSectorsCore`/`CollectFileEntries`, `XisoRepairer.CollectWalk`, `XisoSalvager.SalvageWalk` and `XisoZarchive.ParseNode` use the same explicit stack, so the left spine audits/maps/repairs/salvages/packs to ZAR too.
  - **Phase 5:** `compress`/`decompress`/`convert`/`split` accept `--batch`/`--batch-recursive`, `--skip-existing`, `--continue-on-error`, `--jobs 1..64` and `--output-name` templates (`{name}`, `{ext}`, `{format}`); duplicate outputs and input clobbers are refused before writing, and failed conversions remove partial outputs.
  - **Phase 6:** `--json` on read-only verbs with a source-generated `JsonOutputContext`; stdout JSON/stderr logs; `CliJsonTests`.
  - **Phase 7:** `IFilesystem.CreateFile(path, expectedLength)`, `SequentialScan` sources/destinations, writer layout pre-sizing; `PreallocationTests`.
  - **Phase 8:** `XisoLayoutSpec` (JSON/text parsing), `LayoutPlanner`, fixed-extent placement + first-fit allocation, `TotalSectors` preservation, `--layout` on `-c`/`--pack`/`build-image`; `LayoutPackingTests`.
  - **Phase 1 gap closed:** `XisoRanges.GetXisoRanges`/`GetFileEntries` are container-aware (`Stream` overloads + `XisoImageSource`), so `--ranges` reads split ISO/CISO/CCI.
- **Remaining roadmap:** none from this plan (CCISharp benchmarks/docs/fuzz and optional cross-repo consumers remain separate work).

### 12.1 Dependency graph

```
CCISharp bootstrap ──► CCISharp 1.0.0 ─┐
                                       ▼
XISOSharp 1.5.0 (factory + split ISO + CCI + ZAR input + traversal hardening + batch + JSON + preallocation)
                                             │
                                             └─► 1.6.0 (layout packing)
```

### 12.2 Recommended order

| Step | Deliverable | Est. effort | Notes |
|---|---|---|---|
| 0 | Reference copy + oracle build | done | `References/libCCI-0.1.0-1b0087e`; `cci_encode`/`cci_extract`/`cci_info` verified |
| 1 | CCISharp repo bootstrap, header/index/codec, reader | done | 39 tests green on net8/9/10 |
| 2 | CCISharp writer + split + tests/bench/docs | 1.0.0 published; benchmarks/docs/fuzz pending | NuGet `1.0.0` |
| 3 | XISOSharp Phase 0 factory + split ISO input | done (1.4.5) | `XisoImageSource`, `SplitIsoFile`/`SplitIsoStream`, naming/guards/tests |
| 4 | XISOSharp CCI integration (library + CLI + GUI) | done (1.4.5) | `CciBlockDevice`, `XisoCci`, `--format cci`, GUI selector |
| 5 | XISOSharp ZAR input | done (1.4.5) | `ListZar`/`ExtractZar`/`ConvertZarToIso`, `ComputeZarChecksum`, CLI `convert` |
| 6 | Traversal hardening | done (1.4.5) | explicit in-order stack, 4k left-chain tests |
| 7 | Batch convert + naming | done (1.4.5) | `--batch` on compress/decompress/convert/split, templates, jobs, resume |
| 8 | JSON output | done (1.5.0) | `--json` on read-only verbs, `JsonOutputContext`, stderr logs |
| 9 | Preallocation | done (1.5.0) | `CreateFile(path, expectedLength)`, `SequentialScan`, writer pre-sizing |
| 10 | Layout packing | done (1.6.0) | `XisoLayoutSpec`, `LayoutPlanner`, `--layout` on create/pack/build-image, 41 tests |

Effort figures are indicative, not commitments; they exclude review, docs polish and release overhead.

### 12.3 Definition of done per release

- Library API + CLI + GUI wiring (where applicable).
- Tests: unit + integration + parity + CLI contract.
- Docs: user docs, API docs, format matrix, release notes, README updates.
- Full suite green on all TFMs; InspectCode clean.
- Package validated (`dotnet pack`, `EnablePackageValidation`).
- For XISOSharp: `AGENTS.md` release routine followed (tag, CI publish, verify NuGet).

### 12.4 Suggested version labels

- **CCISharp 1.0.0** — read/write/split/validate.
- **XISOSharp 1.5.0** — factory + split ISO + CCI/ZAR input + batch + JSON + preallocation.
- **XISOSharp 1.6.0** — explicit sector-layout packing (`--layout`, `XisoLayoutSpec`).

---

## 13. Non-goals

- Replacing XISOSharp's CISO implementation with `CSOSharp` (CSOSharp is read-only).
- Adding CCI to `CSOSharp`.
- CHD/GOD/STFS containers in XISOSharp core (keep them in SimpleXisoDrive/XISOStudio/VideoGameFileSystemParser).
- Python bindings (`no_std`, `bincode`, NFS/FUSE daemons, Tauri/web UI, CMake fixes).
- Xbox 360 XEX decryption or full XBE editing (out of scope; `GetXexInfo`/`GetXbeInfo` stay read-only).
- Direct optical-drive reading (users can implement `IBlockDevice`).
- Byte-identical output vs Repackinator's CCI encoder (interoperability is the goal).

---

## 14. Open questions

1. **CCISharp location:** standalone repo (recommended) vs second project in XISOSharp vs CHDStudio. Decision needed before step 1.
2. **Signing:** keep CCISharp unsigned (like ZArchiveSharp) or add a strong-name key?
3. **CLI scope:** ship `CCISharp.Cli` in 1.0.0 or defer to 1.1.0?
4. **CCI split default:** FATX caps a file at `0xFFFFFFFF` bytes (4 GiB − 1), so a literal 4 GiB slice is not writable. Use a conservative default (e.g., `0xFFBF6000`, the same ~4 GiB point CISO uses) or `4 GiB − 64 KiB`, and confirm against a real Cerbios/Repackinator set.
5. **ZAR conversion:** v1 via temp directory (simple) vs direct `ISourceFilesystem` writer (clean, more work)?
6. **Layout packing:** is it worth a 1.7.0, or should it stay a documented future item? **Resolved:** implemented in 1.6.0.

---

## Appendix A — upstream issue map (actionable items)

| Upstream | Status in XISOSharp 1.5.0 | Plan |
|---|---|---|
| extract-xiso #25 OOM invalid TOC | covered | keep |
| extract-xiso #21 version command | covered (`-v`/`--version` + JSON) | keep |
| extract-xiso #28 Xbox 360 | covered (XGD offsets, XEX info) | keep |
| extract-xiso #32/#33 Redump offset | covered | keep |
| extract-xiso #60/#61 bulk | covered | `--batch` extended to convert verbs (Phase 5) |
| extract-xiso #63 DVD drive | not planned | `IBlockDevice` extension point |
| extract-xiso #66 audit | covered | keep |
| extract-xiso #67 rewrite name | covered | keep |
| extract-xiso #68 media patch | covered (pattern patch) | optional XBE editor later |
| extract-xiso #70/#74/#81 names | covered | keep |
| extract-xiso #76/#77 edge cases | covered | explicit in-order stack (Phase 4) |
| extract-xiso #85 multiple ISOs | covered by batch | done (Phase 5) |
| extract-xiso #92 empty dirs | covered | keep |
| extract-xiso #95 AVL/tree | covered (pack validates sizes) | keep |
| extract-xiso PR #26 preallocation | covered | Phase 7 done |
| extract-xiso PR #80 traversal | covered | explicit in-order stack (Phase 4) |
| extract-xiso PR #83 symlinks | covered | keep |
| xdvdfs #36 output==input | covered | keep |
| xdvdfs #37/#155 filetime | covered | keep |
| xdvdfs #49 layout | dump + copy-in covered; pack-to-layout not | Phase 8 |
| xdvdfs #60/#117/#129 mount | not planned | SimpleXisoDrive |
| xdvdfs #88 Python bindings | not planned | native ABI later |
| xdvdfs #93/#114 batch | covered | batch conversion (Phase 5) |
| xdvdfs #97 split | covered (Phase 2) | keep |
| xdvdfs #101 deterministic | covered | keep |
| xdvdfs #119 compressed input | CISO covered; CCI/ZAR missing | Phases 1/3 |
| xdvdfs #120 explore | covered | keep |
| xdvdfs #165 copy-in | covered | keep |
| xdvdfs #166 unpack abstraction | covered | keep |
| xdvdfs #185 CCI | **missing** | CCISharp |
| xdvdfs #187/#190 errors/resume | covered | keep |

## Appendix B — CCI quick reference

```
CCI header (32 bytes, LE)
  00 43 43 49 4D       "CCIM"
  04 20 00 00 00       headerSize = 32
  08 <u64>             uncompressedSize (this slice)
  10 <u64>             indexOffset
  18 00 08 00 00       blockSize = 2048
  1C 01                version = 1
  1D 02                indexAlignment = 2
  1E 00 00             reserved

index at indexOffset: (sectors + 1) * u32
  bit31 = LZ4
  bits 0..30 = offset >> 2

sector blob:
  raw:   2048 bytes
  LZ4:   [padCount:u8][lz4 block][zero padding]  (span 4-byte aligned)
  decode: compLen = span - 1 - padCount
```

Reference implementations for format verification only (not code copying):
- **Canonical:** `Team-Resurgent/libCCI` (`FORMAT.md`, `src/cci_*.c`), commit `1b0087e`, local copy `References/libCCI-0.1.0-1b0087e` — GPL-2.0-or-later.
- `xdvdfs` PR #185 (`xdvdfs-cci/src/lib.rs`, MIT).
- Team-Resurgent XboxToolkit `CCIContainerReader` / `CCISectorDecoder` (GPL-3.0 — behavior reference only).
- Repackinator `ContainerUtility.ConvertContainerToCCI` (GPL-3.0 — behavior reference only).

Part discovery (reference): given `name.K.cci` with a single digit `K`, gather `name.1.cci` … `name.9.cci` contiguously from 1 and stop at the first gap; `name.cci` is single-part. Parts are concatenated by sector range, not by file size.

## Appendix C — verification commands (planned)

```powershell
# CCISharp
dotnet test CCISharp.Tests/CCISharp.Tests.csproj -c Release
dotnet pack CCISharp/CCISharp.csproj -c Release -o artifacts

# XISOSharp
dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj -c Release

# CCI round-trip smoke
XISOSharp compress --format cci game.iso game.cci
XISOSharp checksum game.cci
XISOSharp -x game.cci out-cci
XISOSharp -x game.iso out-iso
# compare file trees / hashes

# Split ISO read
XISOSharp split game.iso --size 1073741824
XISOSharp -l game.1.iso
XISOSharp checksum game.1.iso   # must equal the single-image checksum

# ZAR input
XISOSharp -l game.zar
XISOSharp --checksum game.zar   # must equal the ISO checksum
```
