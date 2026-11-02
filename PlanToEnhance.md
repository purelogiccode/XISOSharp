# Plan to Enhance XISOSharp

**Status:** CCISharp 1.0.1 published on NuGet (2026-11-01; 1.0.0 was the first release, 1.0.1 adds hostile-index hardening). XISOSharp **1.5.0** (published 2026-11-01) implements the Phase 0 image-source factory, Phase 1 CCI integration, Phase 2 split-ISO input, Phase 3 ZAR input (including `validate`/`--validate-checksums`), Phase 4 pathological-TOC traversal hardening (extract/list/rewrite plus the audit, sector-range, repair, salvage and ZAR follow-up walkers), Phase 5 batch conversion/naming, Phase 6 `--json` output, Phase 7 preallocation/sequential I/O, and Phase 8 explicit sector-layout packing (library + CLI + tests); follow-ups make CISO compression read split/container sources directly and harden ZAR extraction (depth bound, pre-sized exact copies, partial cleanup). **Phase 10 Tier A is the 1.6.0 work:** A1 (Xbox 360 XDBF/XSRC title metadata), A2 (XBE logo/title/save image assets), A3 (FATX cap), A4 (`--delete-source`) and A5 (GOD title naming, extended to single-file XBLA/XBLIG STFS package headers) are implemented. **Tier B (1.7.0) is in progress:** B1's read-only GOD/SVOD sector input is implemented in-repo (`GodBlockDevice`, `GodHeader.TryReadVolume`, `XisoImageSource` detection, CLI `decompress`/`convert`), and full XBLA/XBLIG single-file STFS package listing/extraction is implemented in-repo (`StfsPackage`, `StfsEntry`, `StfsPackageInfo`, `XisoChecksum.ComputeStfsChecksum`, `StfsPackage.Validate` hash-tree verification, CLI `-l`/`-t`/`-x`/`--unpack`/`-V`/`checksum`); extracting the readers into the standalone `StfsSharp` package remains.
**Baseline:** XISOSharp `1.4.4` (tag), extract-xiso build `202609111233`, xdvdfs `0.8.3`
**Reference:** CCI spec/implementation copied to `References/libCCI-0.1.0-1b0087e` (commit `1b0087e`, GPL-2.0-or-later; reference only) and verified to build/round-trip.
**CCISharp repo:** <https://github.com/purelogiccode/CCISharp> — package <https://www.nuget.org/packages/CCISharp/1.0.1>.
**Date:** 2026-10-31
**Scope:** turn XISOSharp into the most complete original-Xbox disc-image library by closing the four format/robustness gaps, adding a dedicated `CCISharp` package, and adding three quality-of-life improvements. This document records the plan and its implementation status.
**Phase 10 (2026-11-01):** cross-reference harvest from the four new reference projects (`Xbox360Convert`, `XboxToolkit`, `XDVDFS-TR`, `XeCrypt`) — Tier A for `1.6.0`, Tier B for `1.7.0`; see §16.

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
- `XISOSharp.Tests` - current suite: 2098 passed / 1 opt-in skip on net8.0/net9.0; 2162 passed / 1 opt-in skip on net10.0.

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

- The `CCISharp` package ID is now in use: **1.0.1 is published on NuGet** (nupkg + snupkg; 1.0.0 was the first release). Future versions must use new immutable version numbers.
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
- Package `CCISharp` 1.0.0/1.0.1 packs with README, LICENSE, third-party notices, XML docs and symbols; nupkg + snupkg published.

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
- `ComputeFileHash(string zarPath, string internalPath, HashAlgorithmName)` — per-entry MD5/SHA-256 without extraction, so validation can compare checksums.

**Wiring:**
- CLI auto-detects `.zar` for `list`, `extract`, `checksum`, `validate` and conversion verbs.
- `validate <zar> <iso>` compares file trees (reuse `XisoValidator`'s tree comparison), including `--validate-checksums` and `--validate-report`.
- GUI: ZAR as an input source for extract/list/convert (the ZAR tab packs an
  image into `.zar` and converts a `.zar` source back to ISO).

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

### 8.9 Phase 8 — explicit sector-layout packing — done in 1.5.0

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

### 12.0 Current state (2026-11-02)

- **Reference:** `References/libCCI-0.1.0-1b0087e` built; `cci_encode`/`cci_extract`/`cci_info` verified.
- **CCISharp 1.0.1:** <https://github.com/purelogiccode/CCISharp> - published to NuGet at <https://www.nuget.org/packages/CCISharp/1.0.1>.
  - `net8.0`/`net9.0`/`net10.0`, zero runtime dependencies, trimmable/AOT.
  - Reader, writer, split sets, seekable stream, ISO extraction, validation.
  - **43 tests green on all three TFMs**, including `libCCI` -> CCISharp and CCISharp -> `libCCI` interop.
  - CI green on Windows/Linux/macOS; tag `v1.0.0` builds/publishes `1.0.0` (nupkg + snupkg).
- **XISOSharp 1.5.0 (published 2026-11-01):**
  - `XisoImageSource` factory (ISO/split ISO/CISO/split CISO/CCI/split CCI/ZAR).
  - `CciBlockDevice`, `XisoCci`, `StreamBlockDevice`, `SplitIsoFile`/`SplitIsoStream`.
  - `XisoZarchive.ListZar`/`ExtractZar`/`ConvertZarToIso`/`ComputeFileHash`; `XisoChecksum.ComputeZarChecksum`.
  - CLI: `compress --format cci`, `--cci-level`/`--cci-split`, `cci`/`uncci`, `convert`, ZAR-aware list/extract/checksum/validate.
  - GUI: CISO/CCI format selector, `.cci` filters/drop routing, ZAR input through Extract, and `.zar` → ISO conversion on the bidirectional ZAR tab.
  - **Full suite green:** 1979 passed / 1 skipped (1980) on net8.0/net9.0; 2043 passed / 1 skipped (2044) on net10.0 at the 1.5.0 tag. Current tree counts are recorded in §1.3.
  - **Phase 4:** `TraverseXiso` walks each directory table with an explicit in-order stack; a 4,096-entry left-skewed chain lists/trees/extracts/rewrites (the rewrite checksum matches the balanced form). `Constants.MaxTocStackFrames` backstops the stack; subdirectory nesting still uses `MaxTocDepth`. **Follow-up done:** `AuditWalk`, `XisoRanges.GetValidSectorsCore`/`CollectFileEntries`, `XisoRepairer.CollectWalk`, `XisoSalvager.SalvageWalk` and `XisoZarchive.ParseNode` use the same explicit stack, so the left spine audits/maps/repairs/salvages/packs to ZAR too.
  - **Phase 5:** `compress`/`decompress`/`convert`/`split` accept `--batch`/`--batch-recursive`, `--skip-existing`, `--continue-on-error`, `--jobs 1..64` and `--output-name` templates (`{name}`, `{ext}`, `{format}`); duplicate outputs and input clobbers are refused before writing, and failed conversions remove partial outputs.
  - **Phase 6:** `--json` on read-only verbs with a source-generated `JsonOutputContext`; stdout JSON/stderr logs; `CliJsonTests`.
  - **Phase 7:** `IFilesystem.CreateFile(path, expectedLength)`, `SequentialScan` sources/destinations, writer layout pre-sizing; `PreallocationTests`.
  - **Phase 8:** `XisoLayoutSpec` (JSON/text parsing), `LayoutPlanner`, fixed-extent placement + first-fit allocation, `TotalSectors` preservation, `--layout` on `-c`/`--pack`/`build-image`; `LayoutPackingTests`.
  - **Phase 9:** `XisoImage` facade, full-image hashes, `verify-hashes`, `title-info`, long-path/reserved-name hardening, ZAR caps, fuzzing, AOT smoke, 3-OS CI and package validation; see §15.
  - **Phase 2 follow-up:** `CisoWriter.CompressToCso` compresses split ISO/CISO/CCI sources from the decompressed seekable view instead of materializing a joined temporary ISO; the split-output guard checks every source part.
  - **Phase 3/7 follow-up:** `XisoZarchive.ExtractZar` bounds directory nesting at `MaxTocDepth`, pre-sizes destinations from the name tree, copies exactly the declared length (`TruncatedCopyException` on short data), and deletes partial output on failure/cancel.
  - **Phase 1 gap closed:** `XisoRanges.GetXisoRanges`/`GetFileEntries` are container-aware (`Stream` overloads + `XisoImageSource`), so `--ranges` reads split ISO/CISO/CCI.
  - **Container guard:** the raw-sector redump modes (`--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`) refuse `.cso`/`.cci`, split ISO and `.zar` inputs with a decompress/convert/join message instead of parsing container bytes as an XISO; `RedumpInputGuardTests` covers every mode.
  - **CCI part naming:** a split CCI opened through `.2.cci` … `.9.cci` names extraction/rewrite/compress outputs after the game (`game/`, `game.iso`, `game.cci`); `StripImageSuffix` strips a part marker only for split-capable extensions (`.1` for ISO/CISO, any digit for CCI, never for `.zar`).
- **XISOSharp 1.6.0 (complete, untagged):**
  - **B1 partial (read-only GOD/SVOD input):** `GodBlockDevice` maps the SVOD hash-table interleave and `DataNNNN` fragments to a read-only `IBlockDevice`; `GodHeader.TryReadVolume`/`GodVolumeInfo` expose the mapping; `XisoImageSource` detects a header file or its `.data` folder, so every read verb (list/tree/extract/checksum/hash/validate/sector-layout/ranges) works on GOD. CLI `decompress` writes ISO (title-named) and `convert` accepts GOD → ISO/CCI/CISO; redump modes and in-place repair/copy-in/set-filetime/rewrite/split refuse GOD with a "decompress first" message. Tests: `GodBlockDeviceTests` (11).
  - **A3:** `Constants.FatxMaxFileSize` (`0xFF000000`, 4 GiB − 16 MiB) is the plain-ISO split default; explicit `--size` values above it warn but are honoured. CISO/CCI keep `0xffbf6000` as their xdvdfs-compatible compression default.
  - **A4:** batch `compress`/`decompress`/`convert`/`split` accept `--delete-source` (requires `-y`; refused with `-n`); deletion happens only after a successful conversion, split sets delete every part, and the summary reports the deleted count.
  - **A5:** `GodHeader`/`GodTitleInfo` read the plaintext GOD/SVOD `XContent` header (title name, title ID, disc number/count); `title-info` accepts a GOD header or its `.data` folder in text and `--json` form, with FATX-safe name sanitisation and a hex-title-ID fallback. **Extension:** `StfsHeader`/`StfsTitleInfo` read the same XContent fields from single-file XBLA (`0x000D0000`) and XBLIG (`0x00000002`) STFS packages, which have no `.data` folder.
  - **A1:** `XexDbdf` reads the plaintext XDBF/XSTR/XSRC resources from the XEX header-section table; `XexTitleInfo` carries title name, publisher, developer, genre, description and optional thumbnail. `TitleInfo` gains `Kind` (`Xbe`/`Xex`) and `Xex` init properties without changing the positional record; `GetTitleInfo` resolves `default.xbe` → `default.xex` → `default_mp.xbe`; `title-info`/`--xex-info` print and serialize the 360 fields.
  - **A2:** `XbeAssets` reads the logo bitmap and `$$XTIMAGE`/`$$XSIMAGE` sections; `DxtDecoder`/`PngWriter` decode DXT1/3/5, BGR24/BGRA32 and XPR0 payloads to PNG, and DDS is passed through or generated. `XbeInfo` gains `HasLogoBitmap`/`HasTitleImage`/`HasSaveImage`; `--xbe-image` extracts logo/title/save as PNG or DDS.
  - **Parallel CISO compression:** `CisoWriter.CompressToCso`/`CompressStream` gain a `jobs` overload (1..64) that compresses sector batches on a bounded worker pool and writes payloads in sector order, so output stays byte-identical; `compress`/`cso --jobs <n>` exposes it (single-file mode). CCI stays single-threaded until CCISharp 1.1.
  - **Parallel extraction:** `UnpackOptions.MaxDegreeOfParallelism` (1..64) queues independent files during the sequential tree walk and copies them on a bounded pool; output is byte-identical, `FileAdded` progress and completion logs stay in traversal order, and `-x`/`--unpack --jobs <n>` exposes it. CISO/CCI/GOD/split and in-memory sources serialize reads, and destination filesystem calls are serialized so non-thread-safe stores stay correct. `ParallelExtractionTests`.
  - **Safe-extract name policy:** `ExtractNamePolicy` (`Reject`/`Sanitize`/`Skip`) and `UnpackOptions.NamePolicy`; sanitized names are collision-safe (`~N`), a skipped directory omits its subtree, and `StfsPackage.ExtractTo`/`XisoZarchive.ExtractZar` have policy overloads. CLI `--name-policy` on `-x`/`--unpack`; `ExtractNamePolicyTests`.
  - **STFS hash-tree validation:** `StfsPackage.Validate` hashes every logical data block against its level-0 SHA-1 entry, walks each file's block chain, verifies the level-0/1/2 tables, top hash and header hash, and returns a `StfsValidationReport`; CLI `-V`/audit accepts XBLA/XBLIG packages. `StfsPackageTests` adds valid/corrupt data/table/top/header/chain coverage and the fuzzer runs `Validate`.
  - **Writer concurrency:** `XisoWriter.CreateXiso`/`PackFromDirectory` now resolve the source once and walk it with absolute `HostPath`s, keep byte/file counters in `WriteTreeContext`, and take no process-wide `CreateLock`; concurrent creates run independently and never change the process CWD. Caller-supplied trees without `HostPath` are resolved once against the caller's CWD. `PackFromDirectoryTests` adds an overlap test (a second create finishes while the first is blocked in its progress callback), a relative-source test and a prebuilt-tree test.
   - **Reader CWD removal:** `DecodeXisoCore` routes extraction through a destination `IFilesystem` (a caller-supplied store, or a `LocalFilesystem` rooted at the output directory), so `Extract`/`UnpackImage`/`DecodeXiso` never call `Directory.SetCurrentDirectory` and concurrent extracts are independent. Log/progress text keeps the historical host-separator prefix through a separate display path. `UnpackImageTests` covers CWD stability with and without `-d`, two concurrent extracts and a mid-run CWD change.
- **Remaining roadmap:** Tier B B1's read-only GOD/SVOD sector input is implemented in-repo (`GodBlockDevice`, `GodHeader.TryReadVolume`, CLI `decompress`/`convert`), and XBLA/XBLIG STFS package listing/extraction and SHA-1 hash-tree validation are implemented in-repo (`StfsPackage`, `StfsEntry`, `StfsPackageInfo`, `ComputeStfsChecksum`, `StfsPackage.Validate`, CLI read verbs and `-V`); extracting both readers into the standalone `StfsSharp` package remains for `1.7.0`, as do the unchecked §15 quality items. CCISharp benchmarks/docs/fuzz and optional cross-repo consumers remain separate work.

### 12.1 Dependency graph

```
CCISharp bootstrap ──► CCISharp 1.0.0/1.0.1 ─┐
                                             ▼
XISOSharp 1.5.0 (factory + split ISO + CCI + ZAR input + traversal hardening
                 + batch + JSON + preallocation + layout packing)
                                             │
                                             ├─► 1.6.0 (Phase 10 Tier A: A1–A5)
                                             │
                                             └─► 1.7.0 (StfsSharp + read-only GOD)
```

### 12.2 Recommended order

| Step | Deliverable | Est. effort | Notes |
|---|---|---|---|
| 0 | Reference copy + oracle build | done | `References/libCCI-0.1.0-1b0087e`; `cci_encode`/`cci_extract`/`cci_info` verified |
| 1 | CCISharp repo bootstrap, header/index/codec, reader | done | 39 tests green on net8/9/10 |
| 2 | CCISharp writer + split + tests/bench/docs | 1.0.0 published; 1.0.1 hardened the index path (cap, checked cast, hostile tests) and made libCCI interop a real CI gate; benchmarks/docs/fuzz pending | NuGet `1.0.1` |
| 3 | XISOSharp Phase 0 factory + split ISO input | done (1.4.5) | `XisoImageSource`, `SplitIsoFile`/`SplitIsoStream`, naming/guards/tests |
| 4 | XISOSharp CCI integration (library + CLI + GUI) | done (1.4.5) | `CciBlockDevice`, `XisoCci`, `--format cci`, GUI selector |
| 5 | XISOSharp ZAR input | done (1.4.5) | `ListZar`/`ExtractZar`/`ConvertZarToIso`, `ComputeZarChecksum`, CLI `convert` |
| 6 | Traversal hardening | done (1.4.5) | explicit in-order stack, 4k left-chain tests |
| 7 | Batch convert + naming | done (1.4.5) | `--batch` on compress/decompress/convert/split, templates, jobs, resume |
| 8 | JSON output | done (1.5.0) | `--json` on read-only verbs, `JsonOutputContext`, stderr logs |
| 9 | Preallocation | done (1.5.0) | `CreateFile(path, expectedLength)`, `SequentialScan`, writer pre-sizing |
| 10 | Layout packing | done (1.5.0) | `XisoLayoutSpec`, `LayoutPlanner`, `--layout` on create/pack/build-image, 41 tests |

Effort figures are indicative, not commitments; they exclude review, docs polish and release overhead.

### 12.3 Definition of done per release

- Library API + CLI + GUI wiring (where applicable).
- Tests: unit + integration + parity + CLI contract.
- Docs: user docs, API docs, format matrix, release notes, README updates.
- Full suite green on all TFMs; InspectCode clean.
- Package validated (`dotnet pack`, `EnablePackageValidation`).
- For XISOSharp: `AGENTS.md` release routine followed (tag, CI publish, verify NuGet).

### 12.4 Suggested version labels

- **CCISharp 1.0.1** — read/write/split/validate, with hardened index parsing.
- **XISOSharp 1.5.0** — factory + split ISO + CCI/ZAR input + traversal hardening + batch + JSON + preallocation + explicit sector-layout packing (published 2026-11-01).
- **XISOSharp 1.6.0** — Phase 10 Tier A: FATX cap (A3), `--delete-source` (A4), GOD title naming (A5), 360 title metadata (A1), XBE image assets (A2).
- **XISOSharp 1.7.0** — `StfsSharp` + read-only GOD input (Phase 10 Tier B).

---

## 13. Non-goals

- Replacing XISOSharp's CISO implementation with `CSOSharp` (CSOSharp is read-only).
- Adding CCI to `CSOSharp`.
- CHD, and STFS/CON/LIVE/PIRS **writing or rebuilding**, in XISOSharp core (CHD stays in SimpleXisoDrive/XISOStudio; STFS write belongs in a future container package). **Read-only GOD input is the one exception:** it arrives in `1.7.0` through the `StfsSharp` package (Phase 10, §16.3) so every existing read verb works on GOD without new format code in XISOSharp.
- Web/WASM builds, browser samples and a Tauri/web UI — owner decision 2026-11-01: XISOSharp ships no web surface (`xdvdfs-web` parity is not a goal).
- Python bindings, a native ABI (C/C++/Rust/Python export layer), `no_std`, `bincode`, NFS/FUSE daemons and CMake fixes — owner decision 2026-11-01: XISOSharp stays a managed .NET library and ships no binding layer.
- Xbox 360 XEX decryption or full XBE editing (out of scope; `GetXexInfo`/`GetXbeInfo` stay read-only).
- Executable signature verification (XBE certificate RSA signature, XEX hash tree) — owner decision 2026-11-01: `--validate` stays structural, with no authenticity or tamper reporting.
- Direct optical-drive reading — DVD/BD drive access, SCSI/IOCTL passthrough, raw ripping and drive-speed control — owner decision 2026-11-01: **permanent non-goal**. Users who need it implement `IBlockDevice` over their own drive reader.
- Byte-identical output vs Repackinator's CCI encoder (interoperability is the goal).

---

## 14. Open questions

1. **CCISharp location:** standalone repo (recommended) vs second project in XISOSharp vs CHDStudio. Decision needed before step 1.
2. **Signing:** keep CCISharp unsigned (like ZArchiveSharp) or add a strong-name key?
3. **CLI scope:** ship `CCISharp.Cli` in 1.0.0 or defer to 1.1.0?
4. **CCI split default:** FATX caps a file at `0xFFFFFFFF` bytes (4 GiB − 1), so a literal 4 GiB slice is not writable. Use a conservative default (e.g., `0xFFBF6000`, the same ~4 GiB point CISO uses) or `4 GiB − 64 KiB`, and confirm against a real Cerbios/Repackinator set. **Resolved 2026-11-02 for plain ISO:** `Constants.FatxMaxFileSize` = `0xFF000000` (4 GiB − 16 MiB) is the split default; CISO/CCI keep `0xffbf6000` as their xdvdfs-compatible compression default (below `0xFFFFFFFF`, above the conservative FATX cap).
5. **ZAR conversion:** v1 via temp directory (simple) vs direct `ISourceFilesystem` writer (clean, more work)?
6. **Layout packing:** is it worth a 1.7.0, or should it stay a documented future item? **Resolved:** implemented in 1.5.0.

---

## Appendix A — upstream issue map (actionable items)

| Upstream | Status in XISOSharp 1.5.0 | Plan |
|---|---|---|
| extract-xiso #25 OOM invalid TOC | covered | keep |
| extract-xiso #21 version command | covered (`-v`/`--version` + JSON) | keep |
| extract-xiso #28 Xbox 360 | covered (XGD offsets, XEX info) | keep |
| extract-xiso #32/#33 Redump offset | covered | keep |
| extract-xiso #60/#61 bulk | covered | `--batch` extended to convert verbs (Phase 5) |
| extract-xiso #63 DVD drive | excluded | permanent non-goal — users implement `IBlockDevice` (§13) |
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
| xdvdfs #88 Python bindings | excluded | permanent non-goal (§13) |
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

---

## 15. Phase 9 — World-class 1.5.0 checklist

**Status:** **released** 2026-11-01. Linux full suite 2015 passed / 24 skipped, Windows 2043 passed / 1 skipped, macOS CI green, NativeAOT smoke green, NuGet `XISOSharp` 1.5.0 + `XISOSharp.Tool` 1.5.0 published, GitHub release 1.5.0 published with six bundles + SPDX SBOM. `[x]` = implemented and verified in this tree; `[ ]` = pending; `[-]` = evaluated and deliberately excluded (reason recorded).

**Versioning note (2026-11-02):** `1.5.0` is published (2026-11-01). The next release is **`1.6.0`** (Phase 10 Tier A); do not reuse the `1.5.0` version or the `1.6.0` label for a different scope.

### 15.1 Release, packaging, versioning

- [x] `release.yml` builds the six RID bundles, enforces the exact five-file layout, smoke-tests `win-x64`/`linux-x64`/`MacOsX-arm64`, and creates the GitHub release (dry-run verified).
- [x] NuGet publish is gated on `pack` and runs only on tags (`ci.yml`).
- [x] `AGENTS.md` release routine and bundle recipe document the automated path.
- [x] Merge the tree's 1.5.0/1.6.0 notes into one `## 1.5.0` release section and refresh `WhatsNew.md` for the new features.
- [x] Publish `1.5.0`: tag, NuGet nupkg/snupkg + `XISOSharp.Tool` (the tool package was dropped after 1.5.0), GitHub release with six bundles + SPDX SBOM; verified `api.nuget.org/v3-flatcontainer` and the release assets.
- [-] Add a `dotnet tool` package (`XISOSharp.Tool`, command `xisosharp`) and a tag-only publish job. **Added in 1.5.0 and removed for 1.6.0**: the CLI ships only inside the six RID bundles; the NuGet feed carries the library alone.
- [x] Add SBOM generation (CycloneDX/SPDX) to the release workflow.
- [x] Add GitHub build-provenance attestations for the six bundles.
- [ ] Add optional NuGet author-signing and Windows Authenticode/macOS notarization steps gated on repository secrets.

### 15.2 Library API and architecture

- [x] `IFilesystem` destination abstraction (`LocalFilesystem`, `MemoryFilesystem`, custom stores).
- [x] `IBlockDevice` source abstraction (`File`, `Memory`, `Offset`, `Stream`, `Ciso`, `Cci`).
- [x] `XisoImageSource` unified detection/opening for ISO, split ISO, CISO, CCI and ZAR.
- [x] Structured `IProgress<ProgressInfo>` and `CancellationToken` on the main read/write paths.
- [x] Add `XisoImage` — a disposable, thread-safe, open-once facade (`Open`, `Info`, `List`, `ReadFile`, `Extract`, `Validate`, `Dispose`, async wrappers).
- [~] Remove `Directory.SetCurrentDirectory` from `XisoWriter`/`XisoReader` and replace the process-wide `CreateLock` with per-operation state; walk sources through `IFilesystem`. **Writer done:** `XisoWriter.CreateXiso`/`PackFromDirectory` resolve the source (and caller-supplied tree file names) to absolute host paths, keep byte/file counters in `WriteTreeContext`, and take no process-wide lock, so concurrent creates run independently and never change the CWD (`PackFromDirectoryTests`). **Reader done (2026-11-02):** `DecodeXisoCore` routes every extraction through a destination `IFilesystem` — a caller-supplied store, or a `LocalFilesystem` rooted at the output directory — so `Extract`/`UnpackImage`/`DecodeXiso` never change the process CWD and concurrent extracts are independent (`UnpackImageTests` CWD + parallel coverage). Log/progress text keeps its historical host-separator prefix via a separate display path. **Remaining:** walking local create sources through `IFilesystem`.
- [ ] Make logging per-call (`XisoOptions`/`ILogger`-style sink) while keeping the existing static `Logger` as a compatibility facade.
- [x] Add an opt-in bounded parallel extraction path (`UnpackOptions.MaxDegreeOfParallelism`). **Done:** the tree walk queues independent files and a bounded pool copies them; output is byte-identical, `FileAdded`/completion logs stay in traversal order, and CISO/CCI/GOD/split plus non-thread-safe destination stores are handled (serialized reads/filesystem calls). CLI `-x`/`--unpack --jobs 1..64`; `ParallelExtractionTests`.
- [x] Enable `PackageValidationBaselineVersion` (1.4.4) with a reviewed compatibility-suppression file.
- [x] Add a public-API snapshot/approval test.
- [x] Publish a .NET support policy (`net8` until EOL, `net10` LTS; `net9` EOL May 2026) and decide whether to add `netstandard2.0`.
- [ ] Generate an API reference site with DocFX from the XML docs.

### 15.3 Verification, CI and quality gates

- [x] Full-solution build on Ubuntu/Windows/macOS.
- [x] Windows test suite on `net8.0`/`net9.0`/`net10.0`.
- [x] InspectCode 2026.2.3.1 gate at 0 warnings + SARIF upload.
- [x] Dependency audit (`dotnet list package --vulnerable`).
- [x] CodeQL, Dependency Review and OpenSSF Scorecard workflows.
- [x] TestData fixture stamp so parallel TFM hosts share one ISO.
- [x] Run the full test suite on Ubuntu (verified locally in WSL: 2014 passed / 24 skipped) and macOS (CI matrix; same Unix code paths).
- [x] Add `dotnet format --verify-no-changes` to CI and fix the current whitespace drift.
- [x] Add a trimmed/AOT publish smoke app and run it on Linux in CI.
- [x] Add a mutation-fuzz harness (`XISOSharp.Fuzz`) covering XISO TOC, CISO, CCI, ZAR, XBE/XEX and Redump offsets; run it in CI (2 minutes per push).
- [ ] Add property-based round-trip tests (pack/extract, split/join, compress/decompress, ISO↔ZAR).
- [x] Add a public-API breaking-change gate and a `PackageValidationBaselineVersion`.
- [x] Raise the coverage floor to 70% lines / 60% branches (actual 1.5.0 tree: 71.5% line).
- [x] Run the battle/interop harness in CI against a public synthetic/homebrew corpus (not copyrighted game data).

### 15.4 Performance

- [x] Pre-sized extraction/copy-out and `SequentialScan` hints.
- [x] Writer layout pre-sizing.
- [ ] Add end-to-end BenchmarkDotNet suites: create, extract, checksum, CISO compress/decompress, CCI compress/decompress, large-image I/O. **Partial:** `XisoEndToEndBenchmarks` covers pack, open/list, hash and CISO compress/decompress; extract, CCI and large-image I/O remain.
- [x] Add bounded parallel CISO sector compression and a `--jobs` option on `compress`/`cso`. **Done:** `CisoWriter.CompressToCso(..., jobs: n)` / `CompressStream(..., jobs: n)` compress batches of sectors on a bounded pool and write payloads in sector order, so output is byte-identical to the sequential writer; CLI `compress`/`cso --jobs 1..64` (single-file mode), `CisoTests` and `CliBatchConvertTests` cover byte identity, v1/v2, split output, ordered progress and flag rejection.
- [ ] Add parallel CCI sector compression (CCISharp 1.1) and a `--jobs` option. **Partial:** `--jobs` is accepted for CCI only in `--batch` mode (concurrent conversions); single-file CCI compression stays single-threaded until CCISharp ships a parallel encoder.
- [x] Add bounded parallel extraction (independent files) with ordered progress. **Done:** `UnpackOptions.MaxDegreeOfParallelism` (1..64) queues files during the tree walk and copies them on a bounded pool; `FileAdded` progress and completion logs stay in traversal order, output is byte-identical, and `-x`/`--unpack --jobs` exposes it. `ParallelExtractionTests` covers ISO/CISO/split sources, `MemoryFilesystem`, resume, continue-on-error, fail-fast and CLI contracts.
- [ ] Add a sparse/large-file output strategy (avoid zero-filling 8 GB images where the OS supports sparse files).
- [ ] Publish a benchmark comparison against extract-xiso, xdvdfs and XboxKit with methodology and hardware.

### 15.5 Robustness and security

- [x] Path-traversal/unsafe name rejection and sanitization.
- [x] TOC cycles/out-of-table offsets/entry caps/depth caps.
- [x] Input==output and split-part collision guards.
- [x] ZAR extraction depth bound, pre-sizing and exact-length copies.
- [x] Add Windows long-path support (`longPathAware` manifest + `\\?\` path handling) and tests.
- [x] Add Windows reserved-device-name handling (`CON`, `NUL`, `COM1`… `LPT9`, with extensions) and tests.
- [x] Add explicit ZAR extraction caps (`MaxTotalBytes`, `MaxEntries`, compression-ratio guard) and tests.
- [x] Add a safe-extract policy (reject / sanitize / skip) for hostile names. **Done:** `ExtractNamePolicy` + `UnpackOptions.NamePolicy` (reject default), collision-safe `~N` sanitization, directory-subtree skipping, `StfsPackage.ExtractTo`/`XisoZarchive.ExtractZar` overloads, CLI `--name-policy`, and `ExtractNamePolicyTests`.
- [x] Fuzz the XBE/XEX parsers and CCI/CISO index decoders.
- [x] Add memory/entry caps and malformed-index/alignment validation for CISO on hostile inputs.
- [x] Add memory/entry caps for CCI indexes on hostile inputs (CCISharp 1.0.1: 64 MiB cap, checked cast, hostile-header tests).
- [x] Add STFS hash-tree verification (per-block SHA-1, level-0/1/2 tables, top hash and header hash) and expose it through `StfsPackage.Validate` and the `-V` audit. **Done:** `StfsValidationReport`/`StfsValidationIssue`, `StfsPackageTests.Validate_*`, fuzzer coverage.
- [x] Document a threat model and a “hostile image” test corpus.

### 15.6 Feature completeness (beyond the references)

- [x] extract-xiso parity (list/extract/create/rewrite/media patch/skip/legacy layout/byte parity).
- [x] xdvdfs 0.8.3 parity (ls/tree/info/md5/checksum/copy-out/unpack/pack/build-image/image-spec/compress/CISO).
- [x] XboxKit 0.7 parity (video/update/random/seed/wipe/trim/petrify/ZAR/rebuild/aliases/security sectors).
- [x] CCI/CCIM read/write/split/validate through CCISharp.
- [x] Split ISO input.
- [x] ZAR input for list/extract/checksum/validate/convert.
- [x] GOD/SVOD read-only sector input (`GodBlockDevice`, `GodHeader.TryReadVolume`, all read verbs, CLI decompress/convert); standalone `StfsSharp` package extraction remains.
- [x] XBLA/XBLIG single-file STFS package listing/extraction (`StfsPackage`, `StfsEntry`, `StfsPackageInfo`, `XisoChecksum.ComputeStfsChecksum`, CLI `-l`/`-t`/`-x`/`--unpack`/`checksum`) and hash-tree validation (`StfsPackage.Validate`, `-V`).
- [x] Add `verify-hashes <image> <hash-file>` to verify a rebuilt image against the `.hash` sidecar emitted by `--petrify` (works for ISO/CISO/CCI/ZAR).
- [x] Add `hash <image>` with streaming CRC32/MD5/SHA1/SHA256/SHA3-256, `--json`, and per-file/full-image modes.
- [x] Add `--title-info`/`GetTitleInfo` that finds `default.xbe`, parses the certificate and reports title name/ID/region/disc.
- [~] Add `--jobs` to extraction (`-x`, `--unpack`) and CISO/CCI compression. **CISO done:** `compress`/`cso --jobs 1..64` compresses sectors on a bounded pool with byte-identical output. **Extraction done:** `UnpackOptions.MaxDegreeOfParallelism` and `-x`/`--unpack --jobs 1..64` queue independent files and copy them on a bounded pool with ordered `FileAdded` progress; single-file CCI compression remains pending (CCISharp 1.1 work).
- [ ] Add CCI LZ4-HC level 12 encoding in CCISharp for byte-parity with libCCI/Repackinator (keep the fast encoder as default).
- [ ] Add optional multi-digit CCI part discovery (`game.10.cci`) while keeping reference-compatible single-digit output.
- [-] XRD Redump metadata generation — deferred: XboxKit 0.7's implementation is itself a stub (zeroed hashes, TODO certificates). Implement only with a complete XRD spec and a verified oracle, not by porting the stub.
- [-] GOD/STFS/CON containers — non-goal for the XISO library; belongs in a separate Xbox 360 container package. **Amended 2026-11-01:** read-only GOD input is planned for `1.7.0` via `StfsSharp` (Phase 10, §16.3); STFS writing/rebuild stays out. **A5 (2026-11-02):** header-only GOD title metadata and single-file XBLA/XBLIG STFS package header metadata are implemented in `1.6.0` (`GodHeader`/`GodTitleInfo`, `StfsHeader`/`StfsTitleInfo`, `title-info`). **2026-11-02:** full XBLA/XBLIG package listing/extraction is implemented in-repo (`StfsPackage`, CLI read verbs); STFS writing/rebuild remains a non-goal.

### 15.7 Documentation, samples, community

- [x] Docsify site + GitHub Wiki mirror.
- [x] `docs/library.md`, API pages, CLI reference, archival, compression, format, testing, troubleshooting.
- [ ] DocFX API reference published to Pages.
- [x] `XISOSharp.Sample` console app (open/list/read/extract/create/validate/verify).
- [-] `samples/XISOSharp.WasmSample` — excluded 2026-11-01 by owner decision: XISOSharp ships no web/WASM surface (see §13 and §16.7).
- [x] Root `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `CODEOWNERS`, `SUPPORT.md`, PR template.
- [ ] Public synthetic/homebrew fixture corpus and a regeneration script.
- [x] “Known limitations” page (long paths, GOD/STFS, XRD, net9 EOL, unsigned bundles).
- [x] Refresh README/AGENTS/docs/release notes for 1.5.0.

### 15.8 Distribution and supply chain

- [-] `dotnet tool` package (added in 1.5.0, removed for 1.6.0).
- [ ] winget/scoop/brew manifests (generated from release assets).
- [ ] Shell completions and a man page.
- [x] SBOM per release.
- [x] Build-provenance attestations.
- [ ] NuGet signing (conditional on a certificate secret).
- [ ] Windows Authenticode signing + macOS notarization (conditional on secrets).
- [ ] Optional Docker image (`ghcr.io/purelogiccode/xisosharp`).

### 15.9 Cross-repo work (CCISharp / ZArchiveSharp)

- [ ] CCISharp 1.1: LZ4-HC encoder, multi-digit parts, own fuzz harness, benchmarks, format/API/interop docs (1.0.1 already shipped index hardening, real libCCI interop in CI, and format/coverage/package-baseline gates).
- [ ] ZArchiveSharp: fuzz harness and benchmark coverage for ZAR decode.
- [ ] SimpleXisoDrive/XISOStudio: consume the new `XisoImage` facade (optional, after 1.5.0).

### 15.10 Explicit non-goals

- [-] CHD containers.
- [-] Xbox 360 XEX decryption or XBE editing.
- [-] Executable signature verification (XBE certificate / XEX hash tree) — **permanent non-goal** (owner decision 2026-11-01).
- [-] FUSE/Dokan mounting in the core library (SimpleXisoDrive owns it).
- [-] Web/WASM builds, browser samples and a Tauri/web UI — **permanent non-goal** (owner decision 2026-11-01).
- [-] Python bindings / native ABI — **permanent non-goal** (owner decision 2026-11-01); XISOSharp remains a managed .NET library.
- [-] Direct optical-drive reading — **permanent non-goal** (owner decision 2026-11-01); users implement `IBlockDevice` over their own drive reader.
- [-] Bit-exact CCI output until CCISharp ships LZ4-HC.
- [-] STFS/CON/LIVE/PIRS **writing**, package rebuild and re-signing; XRD (see 15.6).
- [~] GOD **read-only** input — **B1 partial done:** `GodBlockDevice`/`GodHeader.TryReadVolume`/`XisoImageSource` detect and read GOD/SVOD in-repo, and the CLI `decompress`/`convert` verbs are wired. Extracting the reader into the standalone `StfsSharp` package is pending; GOD writing stays a non-goal. **A5 done in `1.6.0`:** header-only title metadata (`title-info`) for GOD and XBLA/XBLIG packages.
- [x] Full XBLA/XBLIG single-file STFS package listing/extraction (file table, hash tree, data blocks) through `StfsPackage`; `StfsHeader` remains the header-only metadata shortcut.

### 15.11 Verification commands for this phase

```powershell
dotnet build CSharp_XISOSharp.sln -c Release -m:1
dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj -c Release
dotnet format whitespace CSharp_XISOSharp.sln --verify-no-changes --no-restore
dotnet pack XISOSharp/XISOSharp.csproj -c Release -o artifacts
dotnet publish XISOSharp.AotSmoke/XISOSharp.AotSmoke.csproj -c Release -r linux-x64
dotnet run --project XISOSharp.Benchmarks -c Release -- --filter "*EndToEnd*"
dotnet run --project XISOSharp.Fuzz -c Release -- --seconds 60
```

### 15.12 Progress log

| Date | Change | Commit |
|---|---|---|
| 2026-11-01 | Phase 9 plan added; 1.5/1.6 notes merged into 1.5.0 | — |
| 2026-11-01 | Phase 9 implementation: `XisoImage`, full-image hashes, `verify-hashes`, `title-info`, CISO hardening (fuzzer found an `OverflowException`), ZAR caps, reserved names/long paths, `dotnet tool`, AOT smoke, mutation fuzzer, 3-OS CI, format/coverage gates, SBOM/provenance, community files, sample, end-to-end benchmarks, support policy, threat model, public-API snapshot, synthetic battle-interop. Full suite 1979/1 (net8/9), 2043/1 (net10); Linux 2015/24; NativeAOT smoke green. | — |
| 2026-11-01 | **Released 1.5.0**: tag pushed, CI green on Ubuntu/Windows/macOS + AOT + fuzz + battle-interop + InspectCode, NuGet `XISOSharp` 1.5.0 + `XISOSharp.Tool` 1.5.0 published, GitHub release published with six RID bundles + SPDX SBOM. | `1.5.0` |
| 2026-11-01 | **CCISharp 1.0.1**: 64 MiB index cap (checked cast; hostile headers return `InvalidIndex` instead of throwing from `ArrayPool.Rent`), overflow-free index length, hostile-input tests, libCCI interop in CI with checked-in oracles, format + 65/55 coverage + package-validation gates, pinned actions. XISOSharp now references 1.0.1. | `v1.0.1` |
| 2026-11-02 | **Parallel CISO compression** (§15.4): `CisoWriter.CompressToCso`/`CompressStream` gained `jobs` overloads (1..64) that compress sector batches on a bounded worker pool and write payloads in sector order, so output is byte-identical; `compress`/`cso --jobs <n>` wired in single-file mode, CCI `--jobs` rejected with a single-threaded message, help/docs/tests updated. | — |
| 2026-11-02 | **Parallel extraction** (§15.4/§15.6): `UnpackOptions.MaxDegreeOfParallelism` (1..64) queues independent files during the sequential tree walk and copies them on a bounded pool with ordered `FileAdded` progress; `-x`/`--unpack --jobs <n>` wired, ZAR/STFS reject it, `ParallelExtractionTests` (18 tests) covers ISO/CISO/split sources, `MemoryFilesystem`, resume, continue-on-error, fail-fast and CLI contracts. | — |
| 2026-11-02 | **Writer concurrency** (§15.2, TODO #22): `XisoWriter` uses absolute source paths and per-operation `WriteTreeContext` counters instead of `Directory.SetCurrentDirectory` and the process-wide `CreateLock`; concurrent creates run independently, output stays byte-identical, caller-supplied trees resolve against the caller CWD without chdir, `PackFromDirectoryTests` adds overlap/relative-source/prebuilt-tree coverage, docs updated. Legacy `XisoReader.Extract` CWD path remains. | — |
| 2026-11-02 | **Reader CWD removal** (§15.2): `DecodeXisoCore` routes extraction through a destination `IFilesystem` (custom store or a `LocalFilesystem` rooted at the output directory), so `Extract`/`UnpackImage`/`DecodeXiso` never call `Directory.SetCurrentDirectory`; log/progress paths keep the historical host-separator prefix through a separate display path, and `UnpackImageTests` adds CWD-stability, concurrent-extract and mid-run-CWD-change coverage. | — |

---

## 16. Phase 10 — cross-reference harvest (Xbox360Convert / XboxToolkit / XDVDFS-TR / XeCrypt)

**Status:** proposed 2026-11-01; Tier A (`1.6.0`) is complete (A1–A5 implemented 2026-11-02); Tier B ships in **`1.7.0`**. Scope is deliberately narrow: only items judged *worthy and useful* are included, and every rejected candidate is recorded in §16.7 with a reason so it is not re-litigated.

**References reviewed:** `References/Xbox360Convert-1.0.1`, `References/XboxToolkit-main`, `References/XDVDFS-TR-0.8.4-TR`, `References/XeCrypt-main`, re-checked against `extract-xiso-build-202609111233`, `libCCI-0.1.0-1b0087e`, `XboxKit-0.7` and `xdvdfs-0.8.3`.

### 16.1 Review outcome — why the scope is small

| Reference | License | New capability vs the 1.5.0 tree | Taken |
|---|---|---|---|
| `extract-xiso-build-202609111233` | BSD-style | none — parity baseline and oracle | no |
| `libCCI-0.1.0-1b0087e` | GPL-2.0-or-later | none — CCISharp 1.0.1 implements `FORMAT.md` | no |
| `Xbox360Convert-1.0.1` | GPL-3.0 (no own license file; links XboxToolkit) | GOD input, FATX split point, delete-after-convert, GOD title naming | **4 small items** |
| `XboxKit-0.7` | permissive | none — full CLI/redump parity already shipped | no |
| `XboxToolkit-main` | GPL-3.0-only | GOD/STFS read, XEX XDBF metadata, XBE image assets | **3 items** |
| `xdvdfs-0.8.3` | MIT | none — parity baseline | no |
| `XDVDFS-TR-0.8.4-TR` | MIT | none functional (see note) | no |
| `XeCrypt-main` | MIT | XBE certificate signature format, STFS hash trees | no — signature verification excluded (§16.7) |

**XDVDFS-TR note.** It was diffed file by file against `xdvdfs-0.8.3`. The only
additions are `xdvdfs-cci` (already covered by CCISharp), `xdvdfs-fsd` (FUSE + NFS
daemon), `xdvdfs-web` (WASM) and internal refactors (`layout/*` split into
modules, `FilesystemCopier`/`RWCopier`, `progress_info`, error enums).
`xdvdfs-core/src/checksum.rs` is algorithmically identical, so checksum parity is
unaffected. Two notes for other repositories, not this one: `xdvdfs-fsd` proves an
NFS mount path for SimpleXisoDrive, and `xdvdfs-web` shows an upstream WASM build
exists — **excluded here by owner decision** (§16.7).

**Licensing rule for this phase.** `XboxToolkit` is GPL-3.0-only and
`Xbox360Convert` has no license file and links it, so both are *behaviour
oracles* only: no code, no transliteration, no linked binaries. Every item below
is implemented from on-disk format facts and verified against synthetic fixtures.
`XeCrypt` is MIT and was reviewed for the XBE certificate signature layout, but
**no item in this phase uses it** — signature verification was dropped (§16.7).

### 16.2 Tier A — `1.6.0`

#### A1. Xbox 360 title metadata (`title-info` on 360 discs)

**Status:** implemented 2026-11-02 (`XexDbdf`, `XexTitleInfo`, `TitleInfo.Kind`/`Xex`, `GetXexTitleInfo`, `title-info`/`--xex-info` text + JSON; tests in `XexTitleInfoTests`).

**Problem.** `XisoReader.GetTitleInfo` resolves only `/default.xbe`, so on a 360
XISO it returns `null` and the CLI prints *"no root default.xbe with an XBEH
certificate"*. `GetXexInfo` parses the fixed header, security info (image size,
load address, region, allowed media), execution info (media ID, version, title
ID, platform, disc number/count) and file-format info — and stops there: no
header section table, no XDBF/XSRC strings.

**Change.**
- Parse the XEX header section table (count at `0x14`, 8-byte entries at `0x18`)
  and read the XDBF/XSRC resource: title name, publisher, developer, genre,
  description, optional thumbnail. XDBF sits in a **plaintext** header section —
  no decryption, no keys.
- New `XexTitleInfo` record. `TitleInfo` gains `Kind` (`Xbe`/`Xex`) and
  `XexTitleInfo? Xex` as **non-positional init properties**, so the positional
  record signature and the 1.4.4 `PackageValidationBaselineVersion` stay intact.
- `GetTitleInfo` resolution order: `/default.xbe` → `/default.xex` →
  `/default_mp.xbe` → `null`.
- CLI `title-info` auto-detects both kinds; `--json` gains additive nullable
  fields (nulls omitted) and keeps `schemaVersion: 1`.
- `--xex-info` prints the resolved title name/publisher when XDBF is present.

**Acceptance.** `title-info` on a 360 XISO reports name, title ID, region, disc
and version; output for OG XISOs is byte-identical to today; `CliJsonTests`
snapshots updated; a synthetic XDBF fixture round-trips.

#### A2. XBE image assets (`--xbe-image`)

**Status:** implemented 2026-11-02 (`XbeAssets`, `XbeImage`, `DxtDecoder`, `PngWriter`, `XbeInfo` presence flags, `--xbe-image`; tests in `XbeAssetsTests`).

**Problem.** `--xbe-info` reports the certificate only; the artwork is unreachable.

**Format facts** (confirmed in the reference, reimplemented from the layout):
- title image = `$$XTIMAGE` section: a 54-byte **DDS** header followed by a
  128×128 image (49,152 bytes for BGR24; DXT1/3/5 variants exist);
- save image = `$$XSIMAGE` section, same shape;
- logo = the raw 100×17 32-bpp buffer addressed by the header's
  `LogoBitmapAddress`/`LogoBitmapSize`.

**Change.**
- New `XbeAssets` API: read logo / `$$XTIMAGE` / `$$XSIMAGE` from an image path
  or `Stream`, section-walk bounded like the existing XBE reader.
- DXT1/DXT3/DXT5 + BGR24 decode and a minimal BCL-only PNG writer
  (`System.IO.Compression`); DDS passthrough for lossless archival.
- `XbeInfo` gains `HasLogoBitmap` / `HasTitleImage` / `HasSaveImage`
  (additive init properties).
- CLI: `--xbe-image <image> <path> --type logo|title|save -o out.(png|dds)`.
- **No new package dependency** — `SixLabors.ImageSharp` is not added.

**Acceptance.** Title, save and logo extract from a synthetic XBE to valid PNG
and DDS; `--json` reports the presence flags; a missing section is a clean error,
not an exception.

#### A3. One FATX file-size constant

**Status:** implemented 2026-11-02 (`Constants.FatxMaxFileSize`, plain-ISO split default and an explicit `--size` warning; tests in `XisoSplitTests`).

**Problem.** `XisoSplitter.DefaultPartSizeBytes` is `4294967296` — exactly 4 GiB.
FATX cannot represent a 4 GiB file, and a part can be exactly that size, so a
"FATX-safe" split can currently produce an unwritable part. `CisoWriter` uses
`0xffbf6000` and Xbox360Convert uses `0xFF000000` (4 GiB − 16 MiB); three
different values for one limit (§14 Q4 already raised this for CCI).

**Change.**
- `Constants.FatxMaxFileSize = 0xFF000000` (4,278,190,080 = 4 GiB − 16 MiB)
  becomes the plain-ISO split default.
- CISO/CCI keep `0xffbf6000` for `xdvdfs` byte-parity, asserted to be below the
  cap and documented as the compression default rather than the FATX limit.
- An explicit `--size` above the cap is still honoured, with a warning.

**Acceptance.** A 4 GiB sparse image splits into parts strictly below 4 GiB;
split/join round-trips are unchanged; existing split tests updated for the new
default.

#### A4. `--delete-source` for batch conversion

**Status:** implemented 2026-11-02 (`BatchOptions.DeleteSource`, `BatchConversion.SourceFiles`, summary count; tests in `CliBatchConvertTests`).

**Problem.** Xbox360Convert has `-d`; XISOSharp's batch verbs have no equivalent,
so library cleanup needs a second pass.

**Change.** `--delete-source` on batch `compress`, `decompress`, `convert` and
`split`:
- requires `-y` (refused with `-n` or an interactive prompt);
- deletes only after a fully successful conversion;
- never deletes an input that is another input's output;
- a split set deletes every part.

**Acceptance.** Success deletes, failure keeps, `-n` refuses, and the batch
summary reports deleted counts.

#### A5. GOD title naming (header only, no mount)

**Status:** implemented 2026-11-02 (`GodHeader`, `GodTitleInfo`, `title-info` text/JSON, FATX-safe name + title-ID fallback; tests in `GodHeaderTests`). **Extended 2026-11-02:** `StfsHeader`/`StfsTitleInfo` read single-file XBLA/XBLIG STFS package headers (same XContent layout, no `.data` folder); tests in `StfsHeaderTests`.

**Problem.** A GOD container is a header file named by content hash next to a
`<name>.data/` directory. Its title name (`0x411`, UTF-16, `0x80` bytes), title ID
(`0x360`) and disc number/count (`0x366`/`0x367`) are readable without decoding a
single sector.

**Change.** `title-info` accepts either the header path or its `.data` folder and
reports title name, title ID, disc number and disc count from the `XContent`
header, with FATX-safe name sanitisation and a fallback to the hex title ID when
the name is empty. Implemented as the first slice of B1 so it ships on its own.

**Acceptance.** A synthetic GOD header reports the expected name/disc; a non-GOD
file produces the existing clean error.

**Scope note (2026-11-02).** A5 itself is **header metadata only** and does not
list or extract package contents. Full single-file STFS package reading (file
table, hash tree and data blocks) is now implemented separately in
`StfsPackage`/CLI read verbs; extracting it into `StfsSharp` remains future
work.

### 16.3 Tier B — `1.7.0`

#### B1. `StfsSharp` package + read-only GOD source

**Status (2026-11-02):** **partial.** Read-only GOD/SVOD sector input is
implemented in-repo: `GodBlockDevice` (SVOD hash-table interleave, fragment
roll-over, security-sector mapping), `GodHeader.TryReadVolume`/`GodVolumeInfo`,
`XisoImageFormat.God`, `XisoImageSource` detection for a header file or its
`.data` folder, and CLI `decompress`/`convert`. Every read verb works; in-place
write verbs and redump modes refuse GOD. XBLA/XBLIG single-file STFS package
listing/extraction is also implemented in-repo (`StfsPackage`, `StfsEntry`,
`StfsPackageInfo`, `XisoChecksum.ComputeStfsChecksum`, `StfsPackage.Validate`
SHA-1 hash-tree verification, CLI read verbs and `-V` audit); the
standalone `StfsSharp` package extraction remains.

**Problem.** `XisoImageSource` covers ISO, split ISO, CISO, split CISO, CCI, split
CCI and ZAR — **not GOD**, the dominant 360 digital format. GOD is a sector image
(SVOD), so it fits `IBlockDevice` naturally and every existing verb would work
unchanged.

**Change.**
- New MIT package/repo `purelogiccode/StfsSharp`, shaped exactly like `CCISharp`
  (`net8.0`/`net9.0`/`net10.0`, MinVer, `IsTrimmable`/`IsAotCompatible`, zero
  runtime dependencies, XML docs, nupkg + snupkg, 3-OS CI):
  `XContent` header, STFS block chains and hash tables, SVOD `.data` mapping,
  `StfsFile`/`StfsStream`, `StfsValidationReport`. **Read-only** — no block
  hashing, no signing, no rebuild.
- Mapping facts to implement (from the on-disk layout, not the GPL code):
  `0x14388` sectors per data file, `0x198` sectors per data block, one
  `0x1000`-byte hash block per data block, base address `0x12000`
  (`0x2000` for enhanced GDF), and sectors `0x20`/`0x21` living in `Data0000`.
- XISOSharp: `XisoImageFormat.God`, `GodBlockDevice : IBlockDevice`, detection of
  a header path **and** a `.data` folder path, naming through
  `StripImageSuffix`.
- CLI: every read verb (`-l`, `-t`, `-x`, `checksum`, `hash`, `validate`,
  `--sector-layout`, `--ranges`, `title-info`) accepts GOD with no per-verb
  changes; `decompress`/`convert` gain `god → iso` and `god → cci`; write paths
  (`--repair`, `--set-filetime`, redump modes) refuse with the existing
  "decompress first" message.

**Scope reversal.** §13, §15.6 and §15.10 are amended to *"GOD/STFS **read-only**
via StfsSharp; STFS write/rebuild, CON/LIVE/PIRS re-signing and XEX decryption
remain non-goals."*

**Acceptance.** `checksum game.god` == `checksum game.iso` for the same tree;
`-x game.god` is byte-identical to `-x game.iso`; a synthetic multi-`Data` set
validates and reads without materialising the image; corrupt hash tables fail
with a named error, not an exception.

**Scope note (2026-11-02).** B1 covers GOD/SVOD sector input and XBLA/XBLIG
single-file STFS package listing/extraction. The 1.6.0 `StfsHeader` remains the
header-only metadata shortcut; the full `StfsPackage` reader adds the file
table, hash-chain block lists and file data. Extracting both readers into the
standalone `StfsSharp` package remains future work.

### 16.4 File-by-file change map

| File | Change |
|---|---|
| `XISOSharp/Models/TitleInfo.cs` | additive `Kind`, `Xex` properties; `Xbe` nullable for XEX titles |
| `XISOSharp/Models/TitleKind.cs` (new) | `Xbe`/`Xex` enum |
| `XISOSharp/Models/XexTitleInfo.cs` (new) | XDBF-derived 360 metadata |
| `XISOSharp/XexDbdf.cs` (new) | XEX header-section table + XDBF/XSTR/XSRC parser |
| `XISOSharp/XisoReader.cs` | `GetTitleInfo` xbe→xex→mp resolution; `GetXexTitleInfo`; XBE image-presence flags |
| `XISOSharp/Models/XbeInfo.cs` | additive image-presence properties |
| `XISOSharp/XbeAssets.cs` (new) | logo / `$$XTIMAGE` / `$$XSIMAGE` extraction |
| `XISOSharp/Models/XbeImage.cs` (new) | extracted image record (`Raw`/`Png`/`Dds`) |
| `XISOSharp/DxtDecoder.cs` (new) | DXT1/3/5 + BGR24/BGRA32 decode |
| `XISOSharp/PngWriter.cs` (new) | BCL-only PNG encode |
| `XISOSharp/Constants.cs` | `FatxMaxFileSize` (A3) |
| `XISOSharp/XisoSplitter.cs` | FATX-safe default part size (A3) |
| `XISOSharp/CisoWriter.cs` / `XisoCci.cs` | document compression split defaults as non-FATX limits (A3) |
| `XISOSharp/GodHeader.cs` (new) | GOD `XContent` title parser (A5); SVOD volume/fragment parser `TryReadVolume` (B1 partial) |
| `XISOSharp/Models/GodTitleInfo.cs` (new) | GOD title metadata record (A5) |
| `XISOSharp/Models/GodVolumeInfo.cs` (new) | GOD SVOD mapping record (B1 partial) |
| `XISOSharp/BlockDevice/GodBlockDevice.cs` (new) | read-only `IBlockDevice` over GOD/SVOD fragments (B1 partial) |
| `XISOSharp/StfsHeader.cs` (new) | single-file XBLA/XBLIG `XContent` title parser (A5 extension) |
| `XISOSharp/Models/StfsTitleInfo.cs` (new) | XBLA/XBLIG title metadata record + kind enum (A5 extension) |
| `XISOSharp/StfsPackage.cs` (new) | read-only STFS file table, hash-chain block lists, `ReadAllBytes`/`CopyTo`/`ExtractTo`, `Validate` SHA-1 hash-tree verification |
| `XISOSharp/Models/StfsEntry.cs` / `StfsPackageInfo.cs` (new) | package entry and volume/title metadata records |
| `XISOSharp/Models/StfsValidationReport.cs` / `StfsValidationIssue.cs` (new) | hash-tree validation result and per-area issue records |
| `XISOSharp/XisoChecksum.cs` | `ComputeStfsChecksum`/`ComputeStfsChecksumHex` (ISO/ZAR-compatible tree hash) |
| `XISOSharp/XisoRepairer.cs` / `XisoPatcher.cs` / `XisoSplitter.cs` / `XisoReader.cs` | STFS extract-first guards for in-place writes and split/rewrite |
| `XISOSharp/XisoImageFormat.cs` / `XisoImageSource.cs` | `God` + detection (header and `.data` folder) (B1) |
| `XISOSharp/BlockDevice/GodBlockDevice.cs` (new) | `IBlockDevice` over `StfsFile` (B1) |
| `XISOSharp.Cli/Program.cs` | `--delete-source` (A4), GOD + XBLA/XBLIG `title-info` (A5), 360 `title-info`/`--xex-info` (A1), `--xbe-image` (A2), GOD `decompress`/`convert` and redump/in-place guards (B1 partial), STFS `-l`/`-t`/`-x`/`--unpack`/`-V`/`checksum`/`--json` and container/redump/rewrite guards |
| `XISOSharp.Cli/JsonOutput.cs` | `JsonGodTitleInfo` + `JsonStfsTitleInfo` (A5), 360 `title-info`/`--xex-info` fields (A1), `xbe-info` presence flags (A2) |
| `XISOSharp.Gui/**` | 360 title panel, XBE image preview, GOD/STFS filter/drop routing (future; A1/A2 and STFS are library + CLI) |
| `XISOSharp.Tests/*` | XDBF, XBE assets, DXT/PNG, FATX cut, delete-source, GOD + XBLA/XBLIG header metadata, STFS package listing/extraction and hash-tree validation (`StfsPackageTests`) |
| `XISOSharp.TestDataGenerator/*` | synthetic XDBF, XBE-with-images, GOD set, STFS package (`SyntheticStfsPackage`) |
| `docs/*`, `WhatsNew.md`, `docs/release-notes.md`, READMEs | feature + limitation docs |

### 16.5 Test plan and quality gates

- Synthetic fixtures only; no copyrighted game data and no GPL binaries.
- `dotnet test` green on `net8.0`/`net9.0`/`net10.0`; InspectCode 0 warnings;
  `dotnet format whitespace --verify-no-changes`.
- Trim/AOT analyzers clean (XDBF, DXT and PNG code are pure managed,
  reflection-free).
- Public API additive only; `PackageValidationBaselineVersion` stays green.
- New JSON fields covered by `GodHeaderTests` (GOD), `StfsHeaderTests` (XBLA/XBLIG) and `CliJsonTests` (existing payloads).
- STFS package fixtures are synthetic (`SyntheticStfsPackage`); `StfsPackageTests` covers the file table, contiguous/hash-chain blocks, writable primary/secondary tables and the level-1 boundary, and the fuzzer mutates a base package through `StfsPackage.Open`/`Validate`/`ReadAllBytes`/`ExtractTo`/`ComputeStfsChecksum`.
- Fuzzer seeds: XDBF, XBE section table, DXT block, GOD `XContent`/block map (XDBF/XBE/DXT have synthetic coverage; the GOD header + fragment mapping is now in `XISOSharp.Fuzz`).

### 16.6 Companion repositories

`CSharp_SimpleXisoDrive` (mount) and `CSharp_XISOStudio` (GUI) are first-class
consumers of this phase. Both currently pin **XISOSharp 1.4.4** and drive
everything through the static `XisoReader.*` APIs plus `IBlockDevice`, so the
items above are designed to reach them without new format code:

| Repo | Consumption today | Work |
|---|---|---|
| `CSharp_SimpleXisoDrive` | `XisoReader.GetVolumeInfo`/`ListDirectory`/`GetEntryInfo`/`OpenImageStream`, `IRawImageSource`, ZArchiveSharp, CHDSharp, Dokan + LibFuseSharp | upgrade to 1.6/1.7, adopt `XisoImage`; **GOD mount arrives free** through `XisoImageSource.OpenRead` |
| `CSharp_XISOStudio` | `XisoReader.*`, `IBlockDevice` (`ChdBlockDevice`), `XisoZarchive.CreateZar`, `XisoImageExplorer`, SharpCompress | upgrade; add GOD to the open dialog, a 360 title panel and an XBE image preview; keep CHD and ZIP in the app |

Explicitly **not** in XISOSharp (companion responsibilities): Dokan/FUSE/NFS
mounting, CHD, ZIP input (XISOStudio already ships SharpCompress), texture
viewers. Cross-repo work is tracked in §15.9.

### 16.7 Evaluated and rejected (recorded so it is not re-litigated)

| Item | Source | Why not |
|---|---|---|
| WASM / web sample, Tauri or browser UI | `xdvdfs-web` | **Excluded by owner decision (2026-11-01).** §13 and §15.7 updated. |
| Python bindings / native ABI (C/C++/Rust export layer) | `xdvdfs` Python bindings | **Excluded by owner decision (2026-11-01)** — permanent, not "later". §13, §15.10 and Appendix A updated. |
| XPR / DDS / DXT **texture conversion** | XboxToolkit | Not a disc-image concern and needs ImageSharp; DXT is decoded only for XBE title images (A2). |
| BIOS boot-logo encode/decode | XboxToolkit | Belongs to a BIOS toolkit, not an XISO library. |
| Marketplace metadata fetch | XboxToolkit | Network + XML, not archival. |
| STFS write / package rebuild | XboxToolkit, Xbox360Convert | Block hashing + signing + private keys; B1 is read-only by design. |
| XexTool re-sign / unrestrict | Xbox360Convert | External binary, private keys, legally sensitive. |
| XEX decryption / decompression | XboxToolkit (native `libXexUnpack`) | Non-goal, and the native binary cannot be shipped. |
| XGD-shaped image synthesis from a folder | XboxToolkit `ISOFormat.Xbox360` | Cannot produce a bootable disc (no video partition, no security sectors); `--prepend-sectors` + `rebuild` already cover the real workflows. |
| `ContainerBuilder` / XGD header writer | XboxToolkit | Weaker than `LayoutPlanner` + `SectorAllocator` (single XGD3 default, no layout support). |
| FUSE/NFS daemon | `xdvdfs-fsd` | SimpleXisoDrive owns mounting. |
| ZIP input | Xbox360Convert | New dependency; XISOStudio already has SharpCompress. |
| Direct optical-drive reading (DVD/BD, SCSI/IOCTL, ripping) | `extract-xiso` #63 | **Excluded by owner decision (2026-11-01)** — permanent. §13, §15.10 and Appendix A updated. |
| XBE/XEX executable signature verification | XboxToolkit, `XeCrypt` | **Excluded by owner decision (2026-11-01)** — not doing signature verification; `--validate` stays structural. §13 and §15.10 updated. |

### 16.8 Sequencing

| Step | Deliverable | Version | Est. |
|---|---|---|---|
| 1 | A3 FATX cap + A4 `--delete-source` | `1.6.0` | 0.5 d |
| 2 | A5 GOD title naming | `1.6.0` | 0.5 d |
| 3 | A1 360 title metadata | `1.6.0` | 1–2 d |
| 4 | A2 XBE image assets | `1.6.0` | 1–2 d |
| 5 | B1 `StfsSharp` + read-only GOD | `1.7.0` | 1–2 wk |
| 6 | Companion upgrades (SimpleXisoDrive, XISOStudio) | after `1.7.0` | cross-repo |
| 7 | Full XBLA/XBLIG STFS package listing/extraction (file table, hash tree, data blocks) | done in-repo (`StfsPackage`) | full reader + CLI read verbs in this tree; `StfsSharp` extraction remains |

### 16.9 Progress log

| Date | Change | Commit |
|---|---|---|
| 2026-11-01 | Phase 10 scope agreed from the reference review: 5 items accepted, GOD read-only proposed; owner exclusions recorded (WASM/web, Python bindings/native ABI, direct optical-drive reading, executable signature verification); §13/§15.6/§15.7/§15.10 and Appendix A amended | — |
| 2026-11-02 | Phase 10 Tier A partial: A3 FATX cap (`Constants.FatxMaxFileSize`), A4 `--delete-source`, A5 GOD header title naming (`GodHeader`/`GodTitleInfo`, `title-info` text/JSON) extended to single-file XBLA/XBLIG STFS headers (`StfsHeader`/`StfsTitleInfo`); plan/document drift corrected (1.5.0 release state, counts, benchmark scope, coverage floor) | — |
| 2026-11-02 | Phase 10 Tier A complete: A1 XEX header-section table + XDBF/XSTR/XSRC parser (`XexDbdf`, `XexTitleInfo`, `GetXexTitleInfo`, `TitleInfo.Kind`/`Xex`, `default.xbe`→`default.xex`→`default_mp.xbe`, 360 `title-info`/`--xex-info` text+JSON); A2 XBE logo/`$$XTIMAGE`/`$$XSIMAGE` extraction (`XbeAssets`, `XbeImage`, DXT1/3/5 + BGR24/BGRA32 + XPR0 decode, BCL PNG writer, DDS passthrough/generation, `XbeInfo` presence flags, `--xbe-image`); synthetic XDBF/XBE fixtures and CLI tests | — |
| 2026-11-02 | Phase 10 Tier B partial: read-only GOD/SVOD sector input (`GodBlockDevice`, `GodHeader.TryReadVolume`, `GodVolumeInfo`, `XisoImageFormat.God`, `XisoImageSource` header/`.data` detection, CLI `decompress`/`convert`, in-place guards); `GodBlockDeviceTests` (11); GOD header/fragment target added to `XISOSharp.Fuzz`; docs/release notes updated. Standalone `StfsSharp` extraction and full XBLA/XBLIG package listing remain. | — |
| 2026-11-02 | XBLA/XBLIG STFS package listing/extraction: `StfsPackage`/`StfsEntry`/`StfsPackageInfo` read the STFS file table and hash-chain block lists (read-only and writable primary/secondary tables, level-1 boundary), `XisoChecksum.ComputeStfsChecksum` adds the ISO/ZAR-compatible tree hash, CLI `-l`/`-t`/`-x`/`--unpack`/`checksum`/`--json` accept packages, container/redump/rewrite verbs refuse them, `SyntheticStfsPackage` fixtures, `StfsPackageTests` (11), fuzzer target; docs/plan/release notes updated. | — |
| 2026-11-02 | Safe-extract name policy: `ExtractNamePolicy` (`Reject`/`Sanitize`/`Skip`), `UnpackOptions.NamePolicy`, collision-safe `~N` renaming, skipped directory subtrees, `StfsPackage.ExtractTo` and `XisoZarchive.ExtractZar` policy overloads, CLI `--name-policy` on `-x`/`--unpack`, `ExtractNamePolicyTests`, docs/release notes updated. | — |
| 2026-11-02 | STFS hash-tree validation: `StfsPackage.Validate` verifies every logical data block against its level-0 SHA-1 entry, walks each file's block chain, checks level-0/1/2 tables, the top hash and the header hash; `StfsValidationReport`/`StfsValidationIssue`; `-V` audit accepts XBLA/XBLIG packages; `SyntheticStfsPackage` now writes real SHA-1 tables; `StfsPackageTests` validation/corruption coverage and fuzzer target; docs/plan/release notes updated. | — |
