# Release Notes

## Unreleased

### Library

- **FATX-safe split default**: `Constants.FatxMaxFileSize` (`0xFF000000`,
  4 GiB − 16 MiB) is now the default maximum part size for plain-ISO splits.
  An explicit `--size` above it is still honoured with a warning. CISO/CCI keep
  their `0xffbf6000` xdvdfs-compatible compression default.
- **Parallel CISO compression**: `CisoWriter.CompressToCso`/`CompressStream`
  gain `jobs` overloads (`1..64`) that compress sector batches on a bounded
  worker pool and write payloads in sector order, so output is byte-identical
  to the sequential writer and progress stays ordered. `MaxParallelism` is the
  shared ceiling; CCI remains single-threaded until CCISharp 1.1.
- **Parallel extraction**: `UnpackOptions.MaxDegreeOfParallelism` (`1..64`,
  default `1`) queues independent files during the sequential tree walk and
  copies them on a bounded pool. Output is byte-identical to a sequential run;
  per-file `FileAdded` progress and completion logs stay in traversal order,
  and `SkipExisting`/`ContinueOnError` keep their semantics. Plain ISO sources
  are read positionally through `RandomAccess`; CISO/CCI/GOD/split and
  in-memory sources serialize reads, and destination filesystem calls are
  serialized so `MemoryFilesystem` and other non-thread-safe stores stay safe.
  `UnpackOptions.MaxParallelism` is the shared `64` ceiling.
- **GOD read-only sector input**: `GodBlockDevice` maps a GOD/SVOD header plus
  its `DataNNNN` fragments into the decompressed sector view, so every read
  verb (list, tree, extract, checksum, hash, validate, sector-layout, ranges)
  accepts a GOD container. `GodHeader.TryReadVolume`/`GodVolumeInfo` expose the
  SVOD mapping, and `XisoImageSource` detects both a header file and its
  `.data` folder. In-place repair/copy-in/set-filetime/rewrite refuse GOD and
  ask for a decompress first.
- **GOD title metadata**: `GodHeader`/`GodTitleInfo` read the plaintext
  `XContent` header of a GOD/SVOD container (title name, title ID, disc
  number/count) without mounting it. Names are sanitised for FATX and fall back
  to the hex title ID when empty.
- **XBLA/XBLIG header metadata**: `StfsHeader`/`StfsTitleInfo` read the same
  XContent fields from single-file Xbox 360 STFS packages (XBLA `0x000D0000`,
  XBLIG `0x00000002`), which have no `.data` directory.
- **XBLA/XBLIG package listing and extraction**: `StfsPackage` reads the STFS
  file table, follows contiguous and hash-chain block lists through the
  level-0/level-1/level-2 hash interleave, and exposes `Entries`, `Find`,
  `ReadAllBytes`, `CopyTo` and `ExtractTo`. `StfsPackageInfo` reports the
  `XContent` title fields and the volume layout. `XisoChecksum.ComputeStfsChecksum`
  uses the same sorted-path SHA3-256 tree hash as ISO and ZAR, so equivalent
  trees compare equal. Read-only: STFS writing/rebuilding stays out of scope.
- **Xbox 360 title metadata**: `XexDbdf` reads the plaintext XDBF/XSTR/XSRC
  resources from the XEX header-section table. `XexTitleInfo` carries title
  name, publisher, developer, genre, description and optional thumbnail;
  `GetXexTitleInfo` exposes it directly. `TitleInfo` gains additive `Kind`
  (`Xbe`/`Xex`) and `Xex` properties, and `GetTitleInfo` resolves
  `default.xbe` → `default.xex` → `default_mp.xbe`.
- **XBE image assets**: `XbeAssets` reads the logo bitmap and the
  `$$XTIMAGE`/`$$XSIMAGE` sections. DXT1/3/5, BGR24/BGRA32 and XPR0 payloads
  are decoded to PNG by the new BCL-only `DxtDecoder`/`PngWriter`; DDS is
  passed through or generated. `XbeInfo` gains `HasLogoBitmap`,
  `HasTitleImage` and `HasSaveImage` init properties.

### CLI

- **GOD input**: every read verb accepts a GOD header or its `.data` folder;
  `decompress` writes a plain ISO (named after the header title) and `convert`
  accepts GOD → `.iso`/`.cci`/`.cso`. Redump modes and in-place write verbs
  refuse GOD with a "decompress first" message.
- **XBLA/XBLIG STFS packages**: `-l`, `-t`, `-x`, `--unpack`, `checksum` and
  `-l/--json` accept a single-file XBLA/XBLIG STFS package and read its file
  table directly. Extraction honours `-d` and `--skip-existing`; `checksum`
  uses the ISO/ZAR-compatible tree hash. `compress`, `decompress`, `convert`,
  rewrite and the raw redump modes refuse STFS with an "extract first" message.
- **`title-info`** accepts a GOD header or its `.data` folder, a single-file
  XBLA/XBLIG STFS package, or an XISO image's `default.xbe`/`default.xex`/
  `default_mp.xbe` and reports the available title name, title ID, disc and
  version fields in text and `--json` form. Xbox 360 titles also report
  publisher, developer, genre, description and thumbnail presence.
- **`--xbe-image <image> <path> --type logo|title|save -o out.png|out.dds`**
  extracts XBE artwork. `--xbe-info --json` adds `hasLogoBitmap`,
  `hasTitleImage` and `hasSaveImage`; `--xex-info` prints the XDBF title name,
  publisher, developer, genre and description when present.
- **`--delete-source`** on batch `compress`, `decompress`, `convert` and
  `split`: deletes each source only after a successful conversion, requires
  `-y`/`--yes` (refused with `-n`/`--no`), removes every part of a split set,
  and reports the deleted count in the batch summary.
- **`--jobs` on CISO compression**: `compress`/`cso --jobs 1..64` compresses
  sectors on a bounded worker pool (default `1`); output is byte-identical to
  the sequential writer. With `--batch`, `--jobs` still selects concurrent
  conversions; single-file CCI compression is single-threaded.
- **`--jobs` on extraction**: `-x` and `--unpack` accept `--jobs 1..64` (default
  `1`) to copy independent files in parallel. Output is byte-identical, per-file
  `FileAdded` progress stays in traversal order, and `--skip-existing`/
  `--continue-on-error` work unchanged. ZAR/STFS unpacking and non-extract modes
  reject the flag with a clear message.
- `split` warns when an explicit `--size` exceeds the conservative FATX cap.

### Dependencies

- `CCISharp` 1.0.1: the CCI reader now rejects oversized or hostile indexes
  with `InvalidIndex` instead of allocating a buffer sized by the part file.
  No API change.

### Packaging

- The CLI is no longer published as a NuGet dotnet tool (`XISOSharp.Tool`).
  `XISOSharp` (the library) remains the only NuGet package; get the CLI from
  the GitHub release bundles (`release_<version>_<rid>.zip`) or build it from
  source with `publish-cli.ps1`.

## 1.5.0

**World-class library release.** This merges everything from the unreleased
1.5.0/1.6.0 tree (CCI, split ISO, ZAR input, traversal hardening, batch
conversion, JSON, preallocation and explicit layout packing) with the Phase 9
productization work: an open-once `XisoImage` facade, full-image
CRC32/MD5/SHA-1/SHA-256/SHA3-256, `.hash` verification, title metadata,
Windows long-path/reserved-name hardening, ZAR extraction caps, mutation
fuzzing, AOT smoke, 3-OS tests, a `dotnet tool`, SBOM and provenance.

Full suite green on all three targets: **1979 passed / 1 opt-in skip (1980) on
net8.0/net9.0** and **2043 passed / 1 opt-in skip (2044) on net10.0**.

### Phase 9 — world-class library

- **`XisoImage`** (`XISOSharp`): `Open`/`OpenAsync`, `List`/`ListFlat`,
  `ReadFile`/`OpenReadStream`, `Extract`/`ExtractAsync`, `Validate`,
  `GetTitleInfo`, `ComputeImageHash`, `IDisposable`/`IAsyncDisposable`.
- **Full-image hashes**: `XisoImageHash.Compute` streams CRC32, MD5, SHA-1,
  SHA-256 and SHA3-256 in one pass through the decompressed view.
- **`verify-hashes`**: verifies a `--petrify`/XboxKit `.hash` sidecar for ISO,
  CISO, CCI, split ISO and ZAR inputs.
- **Title metadata**: `title-info` / `XisoReader.GetTitleInfo` find
  `default.xbe` and report title name, ID, region, disc and version.
- **Hardening**: Windows `longPathAware` manifests, reserved device-name
  rejection/sanitization, ZAR extraction byte/entry caps, CISO malformed-index
  and alignment validation (found by the new mutation fuzzer).
- **Quality gates**: 3-OS test matrix, `dotnet format` gate, AOT publish smoke,
  mutation fuzz job, 70% line / 60% branch coverage floors, package validation
  against 1.4.4 with a reviewed suppression baseline.
- **Packaging**: `XISOSharp.Tool` dotnet tool, SBOM + build provenance on
  releases, community files and a console sample.

### Layout packing (Phase 8)

Sector-layout packing release (PlanToEnhance Phase 8), plus the Phase 4 TOC
hardening follow-up. Images can now be repacked at
an explicit layout: `XisoLayoutSpec` accepts the JSON emitted by
`--sector-layout --json` (or a compact `path=sector` text spec), fixes the sectors of
matching files and directory tables, and allocates every other entry first-fit around
the reserved extents. `-c`, `--pack <dir>` and `build-image` gain `--layout <file>`.
The audit, sector-range, repair, salvage and ZAR walks now use the same explicit
in-order table traversal as extract/list/rewrite, so `MaxTocDepth` bounds real
subdirectory nesting only. The raw-sector redump modes also reject compressed
and split containers up front, and the GUI ZAR tab converts a `.zar` back to a
plain ISO. ZAR extraction is now depth-bounded, pre-sized and exact-length, and
CISO compression reads split/container sources through the decompressed view
instead of a joined temporary ISO.

Full suite green on all three targets: **1979 passed / 1 opt-in skip (1980) on
net8.0/net9.0** and **2043 passed / 1 opt-in skip (2044) on net10.0**.

### Library

- **`XisoLayoutSpec` / `XisoLayoutEntry`** (`XISOSharp.Models`):
  `FromSectorLayout`, `Load` (JSON/text auto-detect), `Parse`, `ParseJson` and
  `ParseText`. JSON accepts the full `--sector-layout` payload, a bare `entries`
  array, camelCase or PascalCase names, and `startSector`/`sector` aliases; the
  text form supports `#`/`;` comments, `total-sectors=N` and `0x` hex sectors.
- **`XisoWriter` layout packing**: `CreateXiso`, `CreateXisoAsync`,
  `PackFromDirectory`, `PackFromDirectoryAsync` and `RemapFilesystem.BuildImage`
  accept an optional `layout`. A two-pass planner validates every fixed extent
  (overlap, reserved system area, bounds, too-small extents), assigns the requested
  sectors, then seeds `SectorAllocator` so unplaced entries fill gaps first-fit.
- **Layout fidelity**: a JSON layout round-trips an unchanged source tree
  byte-for-byte; `TotalSectors` is preserved (subject to the writer's final 64 KiB
  alignment); partition-relative sectors stay correct under `prependSectors`.
- **Strict/lenient paths**: the compact text form rejects paths missing from the
  source tree; JSON extra entries are reserved (with a warning) so a subset repack
  cannot reuse their bytes.

### TOC hardening (Phase 4 follow-up)

- `XisoReader.AuditWalk`, `XisoRanges.GetValidSectorsCore`/`CollectFileEntries`,
  `XisoRepairer.CollectWalk`, `XisoSalvager.SalvageWalk` and
  `XisoZarchive.ParseNode` now walk each directory table with an explicit
  in-order stack (left subtree, entry, right siblings) instead of recursing on
  the left child. `MaxTocDepth` (64) bounds real subdirectory nesting only.
- A 4,096-entry left-skewed table now audits, maps (`--ranges`,
  `GetFileEntries`), repairs a reserved-attribute bit on its deepest entry,
  salvages into a fresh image and packs to ZAR; cycles and out-of-table
  pointers still hit the existing named errors or report/drop gates.
- The visited-offset set and `MaxTocEntriesPerTable` / `MaxTocStackFrames`
  backstops are unchanged; a shared (DAG) subtree now reports the same cycle
  error the reader has always raised.

### ZAR validation (Phase 3 follow-up)

- `XisoValidator.ValidateConversion` accepts a ZArchive (`.zar`) on either side:
  the tree comes from `XisoZarchive.ListZar`, and `--validate-checksums` hashes
  each name-tree entry through the new `XisoZarchive.ComputeFileHash` (no
  extraction to disk). ISO↔ZAR conversions can now be verified directly.
- The CLI `validate` verb accepts `.zar` (including `--json` and
  `--validate-report`), matching the `-l`/`-t`/`-x`/`checksum` ZAR input support.
- A `.zar` handed to a sector-only verb (`-i`, `--ls`, `-V`, `--filetime`,
  `--sector-layout`, `--ranges`, `--repair`, `--copy-in`/`--copy-out`, …) now
  reports a clean `Error:` naming the archive verbs instead of an unhandled
  `NotSupportedException` stack trace.

### ZAR extraction hardening (Phase 3/7 follow-up)

- `XisoZarchive.ExtractZar` now bounds directory nesting at
  `Constants.MaxTocDepth`, matching `ListZar` and `ComputeZarChecksum`; a
  malicious or corrupt archive fails with a named `InvalidDataException`
  instead of recursing until the stack overflows.
- Each extracted destination is pre-sized from the name tree and copied with
  `XisoFileCopier.CopyExact`, so a truncated entry throws
  `TruncatedCopyException` instead of silently producing a short file. Failed
  or canceled entries delete their partial output (Windows-safe: the handle is
  closed first).

### Direct CISO input (Phase 2 follow-up)

- `CisoWriter.CompressToCso` reads split ISO, CISO and CCI sources through the
  seekable decompressed view from `XisoImageSource` instead of writing a joined
  temporary ISO first. Split output still goes through `CisoSplitOutput`.
- The split-output collision guard now compares every source part (not just the
  entry path), so recompressing a split CISO/ISO set cannot truncate one of its
  own parts.

### CLI

- `--layout <file>` on `-c <dir>`, `--pack <dir>` and `build-image`; rejected for
  rewrite/extract/list modes and for `--pack <iso>`.
- Help text and the misplaced-flag guard cover `--layout`.
- `--sector-layout --json game.iso > layout.json` followed by
  `-c game_dir out.iso --layout layout.json` reproduces the source layout.
- The raw-sector redump modes (`--video`, `--random`, `--seed`, `--wipe`,
  `--trim`, `--petrify`, `--update`, `--zar`) refuse `.cso`/`.cci`, split ISO
  and `.zar` inputs with a clear "decompress/convert/join first" message
  instead of parsing container bytes as an XISO.
- Expected overwrite refusals (`-n`, redirected stdin) stay in the local log
  and no longer reach the bug-report API.
- CCI split sets opened through any numbered part (`.2.cci` … `.9.cci`) name
  outputs after the game: extraction creates `game/`, rewrite writes `game.iso`,
  and `compress` derives `game.cci` instead of refusing the source as its own
  output. Plain ISO/CISO sets still strip only `.1`, and `archive.1.zar` keeps
  its number.

### GUI

- The ZAR tab is bidirectional: a `.zar` source runs `convert` and writes a
  plain ISO (output auto-derived next to the archive); an image source keeps the
  existing pack + overwrite/skip/auto-rename behavior. The output picker and
  labels follow the detected direction, and the self-test covers the new argv.

### Tests

- `LayoutPackingTests` (41): JSON/text parsing and validation, `FromSectorLayout`,
  byte-identical JSON round-trips, fixed-extent placement, first-fit gaps, reserved
  unmatched JSON extents, total-sector preservation, prepended partitions, case-
  insensitive paths, empty files/directories, `build-image`, and CLI
  `-c`/`--pack`/`build-image` end-to-end plus usage errors.
- `SectorAllocatorTests` (+7 `SeedUsedRanges` cases): one-pass bulk seeding
  sorts/coalesces, rejects overlaps and out-of-bounds ranges, refuses to seed after
  allocations, handles null and zero-count ranges.
- `PathologicalTocTests` (+5): the 4,096-entry left spine audits and maps, repairs
  its deepest entry, salvages to a passing image, and packs to a valid ZAR.
- `ZarValidateTests` (10): ZAR↔ISO/CCI tree comparison, per-entry SHA-256/MD5
  hashing (case-insensitive paths, missing/directory paths), missing-file and
  checksum-mismatch detection, CLI `validate --json`/`--validate-report`, and a
  clean sector-only rejection for `.zar` inputs.
- `ZarInputTests` (+1): a 65-level name tree is rejected by both `ListZar` and
  `ExtractZar` with the depth message; the existing list/extract/checksum/
  convert tests still pass with pre-sized, exact-length extraction.
- `CisoTests` (+1): recompressing a CISO through `CompressToCso` now reads the
  decompressed view directly and round-trips to the same ISO hash.
- `RedumpInputGuardTests` (11): every redump mode refuses `.cci`/`.cso`, split
  ISO and `.zar` inputs with the documented message, while a plain ISO still
  runs. `CliCommandsTests.Convert`, `MainViewModelTests` and the GUI self-test
  cover the ZAR→ISO direction/argv; `OverwritePromptExtraTests` asserts expected
  refusals carry `NoBugReport`. `CisoAutoDetectTests.ReaderVerbs_CsoMatchIso`
  and the split-ISO reader test now compare `--ranges` and sector-layout output.
- `XisoImageSourceTests` (+7 part-suffix cases), `CciAutoDetectTests.SecondCciPart_NamesOutputsAfterGameStem`
  and `XisoRepairerRefusalTests.Salvage_DefaultOutputPath_Naming` cover numbered
  CCI part naming: extraction, rewrite, recompress and salvage from `.2.cci` all
  use the game stem; `.10.cci`/`.0.cci` stay ordinary names, and `.1.zar` keeps
  its number.
- **net8.0/net9.0 - 1980 tests (1979 passed, 1 opt-in skip)**;
  **net10.0 - 2044 tests (2043 passed, 1 opt-in skip, adds the GUI tests)**.

### JSON and I/O efficiency (Phase 7)

Machine-readable-output and I/O-efficiency release. Read-only verbs gain a
stable `--json` schema (source-generated, trim/AOT-safe) with human logs moved
to stderr, and extraction/creation now pre-size known-length destinations and
use sequential-scan hints. `-v`/`--version` also reports the CCISharp,
ZArchiveSharp, runtime and commit metadata.

Full suite green on all three targets: **1860 passed / 1 opt-in skip (1861) on
net8.0/net9.0** and **1922 passed / 1 opt-in skip (1923) on net10.0**.

### Library

- **Preallocation / sequential I/O** (PlanToEnhance Phase 7): new additive
  `IFilesystem.CreateFile(string path, long? expectedLength)` default member.
  `LocalFilesystem` opens with `FileOptions.SequentialScan` and applies
  `SetLength` before the first write; `MemoryFilesystem` preallocates its
  buffer; custom stores keep the old behavior through the default member.
  `XisoReader.ExtractFile`/`CopyOutFile` pass each entry's size, and plain-ISO
  sources open with `SequentialScan`.
- **`XisoWriter` pre-sizing**: after the sector layout is computed, the output
  is sized once to the exact post-layout extent (plus any prepended partition)
  before the data phase. The final 64 KiB alignment padding and image bytes are
  unchanged.
- **Offset-aware read helpers**: new overloads
  `GetVolumeInfo(string, int?)`, `GetVolumeInfo(Stream, string, int?)`,
  `ListDirectory(string, string, int?)`, `ListDirectory(Stream, string, string, int?)`
  and `GetSectorLayout(string, int?)` probe the XISO header at an explicit
  Redump/XGD partition offset instead of the automatic candidates.
- **Container-aware ranges**: `XisoRanges.GetXisoRanges`/`GetFileEntries` gained
  `Stream` overloads and open through `XisoImageSource`, so `--ranges` and
  range-based archival helpers read split ISO, CISO and CCI inputs.
- **`Logger.RouteInfoToError`**: optional switch that sends `Log`/`LogLine`/
  `Flush` to `Error`, keeping stdout free for machine-readable output.

### CLI

- **`--json`** on `-i`, `-l`, `-t`, `--checksum`/`checksum`,
  `--sector-layout`, `--ranges`, `--filetime`, `--xex-info`, `--xbe-info`,
  `validate` and `-v`/`--version`. Every document carries
  `"schemaVersion": 1` and a `command`; list/tree wrap results in an `images`
  array (one document for multiple inputs), checksum returns a `checksums`
  array. JSON goes to stdout; banners/progress/warnings/errors go to stderr.
- Serialization uses the source-generated `JsonOutputContext` (camelCase, nulls
  omitted), so the trimmed single-file CLI has no reflection fallback.
- `--json` is rejected for mutating modes (extract, rewrite, create,
  compress/decompress, redump) with a clear error.
- **Version enrichment**: `-v` keeps its historical first line, then prints
  XISOSharp, CCISharp, ZArchiveSharp, runtime, OS, architecture and the MinVer
  commit. `-v --json` emits the same data as a JSON document.
- Help text documents `--json`, `--version`, and the new read-only schema.

### Tests

- `CliJsonTests` (12): every JSON payload parses, stdout/stderr are separated,
  multi-image list output stays one document, CCI input matches the ISO tree,
  checksum matches the library digest, `validate` reports summaries, version
  metadata is populated, and `--json` rejects extract mode.
- `XbeInfoTests`/`XexInfoTests`: JSON payloads for `--xbe-info`/`--xex-info`
  (plus the existing end-to-end text checks).
- `PreallocationTests` (5): `LocalFilesystem` pre-sizing, empty default,
  `MemoryFilesystem` capacity behavior, the default interface-member fallback,
  and extraction passing each entry size to a custom `IFilesystem`.
- `CliHelpFlagTests` now asserts the enriched version output while keeping the
  first-line banner contract.
- **net8.0/net9.0 - 1861 tests (1860 passed, 1 opt-in skip)**;
  **net10.0 - 1923 tests (1922 passed, 1 opt-in skip, adds the GUI tests)**.

## 1.4.5

Container-input, traversal-hardening and batch-conversion release. XISOSharp now
reads **CCI/CCIM** images (via the new `CCISharp` 1.0.0 dependency), split
plain-ISO sets (`game.1.iso` …), and **ZArchive** (`.zar`) inputs, and exposes
one unified detection/open layer (`XisoImageSource`) for every sector-based
format. The CLI gains `compress --format cci`, `--cci-level`/`--cci-split`,
`cci`/`uncci` aliases, `convert` for ZAR ↔ ISO, ZAR-aware
list/extract/checksum, and `--batch` conversion on `compress`, `decompress`,
`convert` and `split`; the GUI gains a CISO/CCI format selector and routes
`.cci`/`.zar` drops to the matching tab. The reader's directory walk is now an
explicit in-order stack, so a pathological left-skewed table can no longer
exhaust the process stack or trip the subdirectory depth cap.

Full suite green on all three targets: **1841 passed / 1 opt-in skip (1842) on
net8.0/net9.0** and **1903 passed / 1 opt-in skip (1904) on net10.0**.

### Library

- **`XisoImageSource`** (`XisoImageFormat`, `XisoImageSourceInfo`): extension-led
  detection with a `CISO`/`CCIM` magic sniff fallback; `OpenRead`/`OpenBlockDevice`
  return the decompressed/concatenated view for ISO, split ISO, CISO, CCI and
  their split variants. Plain files remain ISO candidates.
- **CCI/CCIM**: `XisoCci` (`CompressToCci`, `DecompressToIso`,
  `DeriveDefaultCciPath`, `DeriveDefaultIsoPath`), `CciBlockDevice`, and
  `CciNaming`; backed by [CCISharp](https://github.com/purelogiccode/CCISharp)
  1.0.0 (MIT, pure managed, LZ4). Single-part and `.1.cci` … `.9.cci` sets are
  supported.
- **Split ISO input**: `SplitIsoFile`/`SplitIsoStream` concatenate
  `<base>.1.iso`, `.2.iso`, … (multi-digit indices included); a missing middle
  part raises `InvalidDataException` instead of silently truncating.
- **ZAR input**: `XisoZarchive.ListZar`, `ExtractZar`, `ConvertZarToIso`;
  `XisoChecksum.ComputeZarChecksum`/`ComputeZarChecksumHex` produce the same
  SHA3-256 tree hash as the equivalent ISO. Unsafe entry names are rejected.
- **Unified readers**: `XisoReader.OpenImageStream` now routes CCI and split ISO
  through the factory; `XisoChecksum` opens every format through
  `OpenBlockDevice`. Output naming strips `.cso`/`.cci`/`.1.*` suffixes.
- **In-place guards**: `Repair`, `CopyIntoImage` and `SetFileTime` refuse CISO
  and CCI containers with a clear "decompress first" error, even when the file
  is missing or corrupt.
- **Recompression**: `CisoWriter.CompressToCso` and `XisoCci.CompressToCci`
  accept split ISO/CISO/CCI sources by materializing the decompressed image to a
  temporary ISO; directory sources still pack then compress.
- `StreamBlockDevice` exposes any seekable stream as an `IBlockDevice`.
- **Pathological TOC traversal**: `TraverseXiso` now walks each directory table
  with an explicit in-order stack instead of left-subtree recursion. `MaxTocDepth`
  bounds subdirectory nesting only; intra-table depth is bounded by
  `MaxTocEntriesPerTable` and `Constants.MaxTocStackFrames` (100k). A 4,096-entry
  left-skewed chain lists/trees/extracts/rewrites, and the rewrite checksum
  matches the balanced form. `ContinueOnError` still skips just the corrupt
  subtree.
- Runtime dependencies are now `ZArchiveSharp` 1.4.0 and `CCISharp` 1.0.0.

### CLI

- `compress --format cso|cci` (default `cso`), `--cci-level 0..9`,
  `--cci-split <bytes>`; `-l`/`--level` and `--split` remain format-neutral.
- `cci` alias for `compress --format cci`; `uncci` joins `decompress`.
- Format-specific flag validation: `--ciso-*` is rejected for CCI output and
  `--cci-*` for CISO output.
- `decompress` auto-detects CISO vs CCI (extension plus magic sniff).
- `convert <src.zar|src.iso> <out.iso|out.zar>`; `-l`, `-t`, `-x`, `--unpack`
  and `checksum` accept `.zar` directly.
- **Batch conversion**: `--batch <dir>` / `--batch-recursive` on `compress`,
  `decompress`, `convert` and `split`; `--skip-existing` resumes, `--continue-on-error`
  keeps going after a failed file (partial outputs are removed), `--jobs 1..64`
  converts in parallel, and `--output-name` supports `{name}`/`{ext}`/`{format}`.
  Duplicate outputs and outputs that would clobber another input are refused
  before any write. `convert --batch --format iso|zar` covers both directions.
- Help text and the GUI self-test cover the new flags.

### GUI

- Compress tab: **Format** selector (CISO/CCI); output/save dialog and split
  placeholder follow the selected format.
- Picker filters and drop routing include `.cci`/`.1.cci`; a dropped `.zar`
  opens the Extract tab for list/extract/checksum, and compressed drops route to
  Decompress.
- Decompress picker accepts CISO and CCI; collision guards use the matching
  default output naming.

### Tests

- `XisoImageSourceTests`: detection table for ISO/XISO/IMG/CISO/split CISO/
  CCI/split CCI/split ISO, CCI round-trip, cross-part seeks, magic sniffing,
  suffix stripping and missing files.
- `CciAutoDetectTests`: list/tree/extract/unpack/rewrite/checksum parity,
  default output naming, CCI decompression and the in-place refusal contracts.
- `SplitIsoAutoDetectTests`: extract/list/checksum parity, split CISO/CCI
  recompression, default `game/` naming and missing-middle-part errors.
- `CliCciTests`: CCI compress/decompress, split sets, aliases, list and
  format-specific flag rejection.
- `ZarInputTests`: ZAR listing/extraction/checksum/convert and CLI wiring.
- `PathologicalTocTests`: 4,096-entry left-skewed, right-skewed, alternating and
  balanced tables; list/tree/extract/rewrite parity, a left self-cycle, and
  continue-on-error subtree skipping.
- `CliBatchConvertTests`: CISO/CCI batch compress, batch decompress, ISO to ZAR
  convert, batch split, recursive discovery, naming templates (including
  subdirectories), duplicate-output refusal, `--skip-existing` resume,
  `--continue-on-error` partial-output cleanup, and parallel `--jobs`.
- **net8.0/net9.0 - 1842 tests (1841 passed, 1 opt-in skip)**;
  **net10.0 - 1904 tests (1903 passed, 1 opt-in skip, adds the GUI tests)**.

## 1.4.4

Extraction-cleanup and static-analysis release. A failed or cancelled extraction no
longer leaves a truncated destination file behind: `ExtractFile`/`CopyOutFile`
delete the partial output through the new `IFilesystem.DeleteFile` member, which
custom destination stores can override (the default keeps the previous no-op
behavior). The solution is clean under JetBrains InspectCode - 0 errors and 0
warnings across every project - and dead members found by the inspection were
removed from the Tester. Full suite green on all three targets: 1760 passed / 1
opt-in skip (1761) on net8.0/net9.0, 1821 passed / 1 opt-in skip (1822) on net10.0.

### Library

- **Partial-destination cleanup**: a copy that throws, reports truncation, or is
  cancelled removes the destination it wrote, so callers (drag-and-drop prep,
  batch extraction, resume runs) can never mistake a partial file for a complete
  one. Cleanup is best-effort and never masks the extraction error.
- **`IFilesystem.DeleteFile`**: new member with a default no-op implementation -
  existing custom stores still compile and keep their partial content if they
  cannot delete. `LocalFilesystem` deletes from disk (a missing file is a no-op);
  `MemoryFilesystem` removes the stored snapshot.
- Static-analysis cleanup across the library, CLI, GUI, Tester, benchmarks and
  tests: redundant default arguments, redundant casts and assignments, dead null
  guards, unused fields/properties, and simplified nullability in
  `Logger.Out`/`Logger.Error` (assigning `null` now stores `TextWriter.Null`, so
  the getters never return `null`). No output or format changes.

### Tests

- `ExtractRobustnessTests`: a truncated copy and a mid-write failure both leave no
  destination file, including through a custom `IFilesystem`.
- `XisoFilesystemTests`: `LocalFilesystem.DeleteFile` removes files and treats a
  missing path as a no-op.
- Suite: **net8.0/net9.0 - 1761 tests (1760 passed, 1 opt-in skip)**;
  **net10.0 - 1822 tests (1821 passed, 1 opt-in skip, adds the GUI tests)**.

## 1.4.3

Split CISO part sets honor the caller-chosen `FileShare` when opening images.
`CisoSplitFile.OpenParts` was hardcoded to `FileShare.Read`, so
`XisoReader.OpenImageStream(path, FileShare.ReadWrite)` and
`XisoExplorerOptions.Share` reached plain ISOs and single-file `.cso` containers
but not the individual `*.1.cso` part files - a split set held open by a writer
(sync client, in-progress copy, antivirus) still failed with a sharing violation
on Windows. The share mode now threads to every part. Defaults are unchanged
(`FileShare.Read`). Full suite green on all three targets: 1758 passed / 1
opt-in skip (1759) on net8.0/net9.0, 1819 passed / 1 opt-in skip (1820) on
net10.0.

### Library

- **Split CISO share mode**: `CisoSplitFile.OpenParts(firstPart, share)`
  accepts a share mode, and `CisoBlockDevice` threads the caller's `FileShare`
  through to every part file. `XisoExplorerOptions.Share` now covers plain
  ISOs, single `.cso` containers, and split `*.1.cso` part sets alike.

### Tests

- `CisoOpenParts_HonorsCallerShareMode` pins the share plumbing, including
  that the default stays read-shared; `SplitCso_ExplorerWithReadWriteShare_
  CoexistsWithWriterHoldingPart` covers the end-to-end `XisoExplorer` flow with
  a writer holding the first part.

## 1.4.2

Deep-review hardening and GUI experience release. An 80-item review of the whole
repository (path escapes, patch-layout collisions, CWD leaks, partial artifacts,
CLI contract mismatches, vacuous tests, doc drift) is resolved, together with
the follow-up regressions found while re-reviewing those fixes and a final pass
over the fix commits. The desktop app gains Donate/About/Exit header actions, F8
window screenshots, and a startup update prompt backed by the same `UpdateCore`
+ 24-hour cache the CLI uses. Namespace note: `UnpackOptions`,
`ProcessRunResult`, `ExplorerNode`, and `XisoExplorerOptions` moved from
`XISOSharp` to `XISOSharp.Models` — add `using XISOSharp.Models;` when
upgrading from 1.4.1. Targets remain `net8.0` / `net9.0` / `net10.0`; full
suite green on all three (1818 tests on net10.0: 1817 passed, 1 opt-in skip;
1757 on net8.0/net9.0: 1756 passed, 1 opt-in skip). The suite runs fully
sequentially (assembly-wide `DisableTestParallelization`) because create/extract
mutate the process CWD.

### Library

- **Path safety**: crafted image names (separators, Windows drive-relative
  colons, any trailing dot/space) are rejected at extract on Windows or
  sanitized by repair/salvage; listing, tree, rewrite, and ZAR packing accept
  every structurally valid name on every host, matching the reference tool
  (the colon stays valid on Unix where the writer/patcher accept it).
- **Patching**: the allocator reserves the optimized tag (sector 15), the
  ECMA-119 descriptor pair (16-17), and the layout-tool signature sector — also
  for header-only volumes; sector counts use 64-bit math; the root table size
  is recorded even when the table stays in place; a child offset that would
  encode the `0xFFFF` empty-child sentinel fails instead of truncating.
- **Walkers**: all child offsets are bounded by the recorded table size, the
  all-zero empty-table sentinel is honored, `.`/`..` records are skipped
  consistently, truncated names fail instead of truncating the walk (including
  `CollectFileEntries`), and zero-size directory entries list as empty.
- **Writing/containers**: CWD is restored on every path; a cwd source
  (`-c .` / `-c ..`) defaults its output next to the source instead of
  aborting on the same-leaf guard; relative `-d` resolves against the caller
  CWD; failed/cancelled runs delete partial `.iso`/`.cso`/`.zar` output
  (including the `build-image` remap writer, and closing split-part handles
  first on Windows) but never a pre-existing file the call did not create;
  CISO headers/indexes are validated; split part naming handles dotted bases;
  `Latin1Encoding` range checks are overflow-safe.
- **Repair/audit**: a missing optimized tag is written at the disc offset for
  prepended images; path and device audits agree on trailing magic, empty-image
  tag probing, and an oversized root table (audit now matches extraction).
- `XisoPaths` resolves both sides of an input==output check against one CWD
  snapshot; `XisoExplorer.Dispose` serializes with keep-open reads.

### CLI

- `--silent` is order-independent in every position, including with `-v`/
  `--help`, and still requires `--checksum`; `--file-time`/`--preserve-attrs`/
  `--jobs`/`--policy` are rejected outside their modes, and `--jobs` is rejected
  with `--compress` or a multi-mode redump run where it cannot take effect;
  `--wipe`/`--trim` honor `-o`; `--is-optimized` honors `--skip-sectors`;
  `--ciso-split` refuses a source named like its own first part.
- `-D` deletes `.old` only after a fully successful (validated) rewrite, and a
  failed file no longer suppresses a later file's success or keeps its backup.
- Help shows `-o <output>` for the redump modes instead of a positional second
  file (which is parsed as another input).
- stdout/stderr are always UTF-8 instead of the host console code page, so
  Latin-1 filenames (`café.txt`) render and pipe identically on every platform
  (matching xdvdfs's UTF-8 output); the battle harness now decodes each tool
  with its real encoding (extract-xiso Latin-1; xdvdfs, xboxkit, and the CLI
  UTF-8) so non-ASCII names compare equal instead of collapsing to `U+FFFD`.

### GUI

- Donate/About/Exit header actions with an About dialog; F8 screenshots;
  startup update prompt (subscribed before probing so a fast show cannot hang
  it); tooltips.
- Drag-and-drop routing fixed (only `*.iso` folders go to Batch; `.zar` drops
  go to Rebuild in single- and multi-item drops); Wipe/Trim pass `-o` before
  the image; `CliStatus` updates on the UI thread; settings persist again;
  screenshot environment warnings stay local and the fallback folder is tried
  for security exceptions.

### Logging, bug reports, telemetry

- Shared Serilog pipeline; expected operational warnings are tagged
  `NoBugReport`; the shared API key is double-Base64 encoded
  (`ApiKeyStore`); the dedupe map is bounded; `ProcessRunner` falls back to a
  stable working directory when the inherited CWD was deleted.

### Tester

- List comparison parses the real `\name (N bytes)` format and decodes
  extract-xiso's Latin-1 output; two empty parses fail instead of passing;
  remaining dead wrappers (`RunQuietAsync`) removed.

### Docs & build

- Drift fixed (test counts, `--help`, Release packing, battle default, coverage
  OS/figures, Tester CI, sequential-suite description, missing scripts,
  `ConversionPlan.md`, namespace samples); publish scripts stage into `%TEMP%`
  and merge instead of deleting the protected `publish*/` trees, and
  `publish-cli.ps1 -Zip` requires an exact tag or `-Version <x.y.z>` so a
  pre-release run cannot overwrite a shipped `release_*.zip`; `docs/` serves
  Pages (Docsify `_sidebar.md`) and syncs to the wiki (`Home.md` +
  `_Sidebar.md`) via `.github/workflows/wiki.yml`.

## 1.4.1

Dependency and legal maintenance release. `ZArchiveSharp` moves 1.3.0 → 1.4.0
(drop-in: upstream rewrote its pipeline layer as original MIT work with no
public-surface, wire-format, or CLI changes), and the ZArchiveSharp notice is
refreshed — the library, including the `ZArchiveSharp.Pipeline` pack/extract
layer XISOSharp uses, is fully MIT. No XISOSharp API or CLI behavior changes;
targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1427 tests: 1426 passed, 1 opt-in skip, 0 failed).

### Dependencies

- `ZArchiveSharp` 1.3.0 → 1.4.0. Upstream highlights: original MIT pipeline
  layer (`ZarPipeline`, `ZarPackEngine`, `ProcessRunner`), stricter per-item
  batch isolation, and a robust stderr drain that no longer stalls on an
  inherited pipe. The public surface is unchanged, so ZAR output, CLI
  semantics, and exit codes are untouched.

### License

- `LICENSE` item 4 records ZArchiveSharp as fully MIT as of v1.4.0
  (Copyright (c) 2026 PureLogicCode.com; lead developer Peterson Fernandes),
  including `ZArchiveSharp.Pipeline`.
- The packaged library license (`XISOSharp/LICENSE`) is synced with the root
  `LICENSE`, so the NuGet package now carries the full third-party notices
  (extract-xiso BSD-4-clause, xdvdfs, XboxKit, ZArchiveSharp).

### Docs

- Root `README.md` and `docs/building.md` dependency references updated
  1.3.0 → 1.4.0.

## 1.4.0

Integrity-audit release. The CLI's `-V` audit no longer fails a structurally
sound image just because the optimized tag is missing: it reports the tag
(`Optimized: yes/no`) and passes raw/unconverted dumps, while structural damage
still fails. Audits now accept `.cso` images (previously compressed bytes were
read as sectors), the library gains a `requireOptimizedTag` overload plus the
`AuditResult.IsOptimized` property, and empty (zero-root) images report tag
presence instead of always `false`. Targets remain `net8.0` / `net9.0` /
`net10.0`; full suite green on all three (1427 tests: 1426 passed, 1 opt-in
skip, 0 failed).

### Library

#### Audit control and tag reporting

- New overloads `AuditXiso(string isoPath, bool requireOptimizedTag)` and
  `AuditXiso(IBlockDevice dev, string isoName, bool requireOptimizedTag)`.
  With `requireOptimizedTag: false` a missing tag is not an audit issue, so raw
  (unconverted) images audit as valid; the 1-arg overloads keep the original
  strict behavior (`true`).
- New `AuditResult.IsOptimized` property reports whether the tag was found.
  The empty/zero-root early-return paths previously skipped the tag probe, so
  an image whose root fields are zero but whose tag is intact reported `false`;
  both the path and block-device overloads now probe and report it.
- `AuditXiso(string)` now opens through `OpenImageStream` (CISO-aware) instead
  of a plain `FileStream`, matching `GetVolumeInfo`. A `.cso` input used to be
  audited as compressed bytes, producing bogus tree/bounds issues.

### CLI

- `-V` is an integrity audit: it prints `Optimized: yes/no`, a missing tag no
  longer fails the run, and raw/unconverted images exit 0. Use
  `--is-optimized` for the strict tag-only probe or `--repair` to write a
  missing tag. Help text updated.

### Tests

- 1427 tests across `net8.0` / `net9.0` / `net10.0`: 1426 passed, 1 opt-in skip
  (`XISO_UPDATE_FIXTURE=1` fixture validator), 0 failed.
- New: `CliAuditTests` (3 end-to-end `-V` runs: optimized, raw passing with
  `Optimized: no`, corrupt tree failing), `AuditXisoTests` integrity/tag/CISO
  cases (4, incl. plain-vs-CISO parity), and device empty-root tag coverage.

### Dependencies

- `coverlet.collector` 10.0.1 → 10.1.0 (Tests). No library dependency changes.

### Docs

- `README.md`, `XISOSharp/README.md`, `XISOSharp.Cli/README.md`, `docs/cli.md`,
  and `docs/api-xisoreader.md` document the integrity audit, the
  `requireOptimizedTag` overload, and `IsOptimized`; `docs/troubleshooting.md`
  and `docs/getting-started.md` refreshed; suite counts updated.
- Release bundles now include `WhatsNew.md` next to `README.md` and `LICENSE`.

## 1.3.0

Correctness and maintenance release. The rewrite `-o` output name is now
honored for real — including rooted/absolute paths — and a rewrite that fails
to write reports an error instead of exiting 0. Wrapped exceptions carry their
cause, the reference-binary interop tests resolve the current `References/`
drop again, and the bug-report wire format is locked by tests. Targets remain
`net8.0` / `net9.0` / `net10.0`; full suite green on all three (1419 tests:
1418 passed, 1 opt-in skip, 0 failed).

### Library

#### Rewrite output naming

- `XisoWriter.CreateXiso` ignored `inName` in rewrite mode, so
  `XisoReader.Rewrite(..., outputName:)` (CLI `-o`) had no effect on the output
  filename. The name is now used verbatim: relative names resolve against the
  `-d` directory, and rooted names are kept intact. The Windows drive-letter
  strip used for legacy names no longer applies to a rooted `-o`, which turned
  `C:\out\game.iso` into `:\out\game.iso` and failed with a path-syntax error.
- `XisoReader.DecodeXiso` (and therefore `Rewrite`) now returns the writer's
  non-zero result when the output cannot be written. Previously the writer
  logged the failure but the rewrite still reported success and returned 0.
- Regression tests: `Rewrite_WithAbsoluteOutputName_UsesProvidedName`,
  `Rewrite_UnwritableOutput_ReturnsError`.

#### Exception causality

- Wrapped rethrows embed the caught exception as `InnerException` (Meziantou
  MA0054): `ProcessRunner` timeout → `TimeoutException`, `XisoReader` truncated
  file copy → `IOException`, GUI `MainViewModel` output-verification failures →
  `InvalidOperationException`. Message text is unchanged, so existing
  diagnostics and exception filters keep working.

### CLI

- Rewrite (`-r`) now checks the decode result: a failed rewrite exits 1 and
  does not print `successfully rewritten` (the input stays at `<name>.old`,
  exactly as before the fix).
- `-o` accepts relative and rooted paths and uses them as-is.

### Logging

- `UpdateChecker` debug-logs every previously silent catch (offline checks and
  API/cache failures stay below `Warning`, so they never file bug reports); GUI
  `CliLocator.ProductVersion` debug-logs unreadable CLI metadata.
- `BugReporter` extracts `ComposeReport` and exposes `BuildExceptionBlock` to
  tests; new `BugReportFormatTests` (5) lock the Environment / Error /
  Exception sections required by the bug-report service.

### Tests

- 1419 tests across `net8.0` / `net9.0` / `net10.0`: 1418 passed, 1 opt-in skip
  (`XISO_UPDATE_FIXTURE=1` fixture validator), 0 failed.
- `TestConditions` resolves the compiled `extract-xiso.exe` from the current
  `References/` drop (canonical `References/extract-xiso.exe`, dated drop root,
  or in-tree CMake build) instead of the removed `202505152050` artifact path;
  the legacy `llCompat` interop tests run and pass again.
- `RegenerateFixtureIso_WhenRequested` renamed to
  `ValidateFixtureIso_WhenRequested` (it validates only; regeneration stays
  manual) — `docs/testing.md` updated.

### Dependencies

- `Meziantou.Analyzer` 3.0.257 → 3.0.290 (all projects), `Avalonia` 12.1.2 →
  12.1.3 (GUI), `QuestPDF` 2026.8.0 → 2026.9.1 (Tester),
  `Microsoft.NET.Test.Sdk` 18.10.0 → 18.10.1 (Tests). No library dependency
  changes.

### Docs

- Badge rows (CI, NuGet version/downloads, release, .NET, platform/RIDs,
  license, docs site, per-project) added to the root, library, CLI, Tests,
  Tester, and docs READMEs; suite counts refreshed; `-o` rooted-path behavior
  documented in the CLI reference.

## 1.2.0

Additive release for VFS-consumer parity (SimpleXisoDrive/Dokan): the probe and
read stack now accepts the rebuilt "sector-0" XISO layout (volume descriptor at
the very start of the image instead of partition sector 32), and the in-place
patcher, sector ranges, and ZAR packing understand it end-to-end. Targets remain
`net8.0` / `net9.0` / `net10.0`; full suite green on all three (1412 tests:
1408 passed, 4 pre-existing skips, 0 failed).

### Library

#### Rebuilt sector-0 XISO support

- `VerifyXiso` (stream and block-device overloads), `GetVolumeInfo`,
  `GetFileTimeRaw`/`SetFileTime` (both overloads), and `OffsetBlockDevice.Probe`
  detect a volume descriptor at absolute offset 0. Such images report
  `DiscLseek = 0` and `DescriptorSector = 0`; sector numbers stay
  partition-relative, so `XisoExplorer`, directory listing, and bounded file
  reads work unchanged on them.
- `XisoReader.TryFindHeaderBase` resolves the descriptor base for callers that
  take a partition offset (standard sector-32 candidate first, then sector-0).
- `GetSectorLayout` marks the detected descriptor sector as used instead of
  hardcoding partition sector 32, so `SectorAllocator.FromLayout` can no longer
  hand out the live header to the in-place patcher.
- `PatchVolumeHeaderRoot` writes the relocated root pointer to the active header
  base (`DiscLseek + DescriptorSector * 2048`), fixing a silent-corruption path
  where `CopyIn` on a sector-0 image overwrote the descriptor or left the header
  pointing at the old root table.
- `XisoRanges.GetXisoRanges`/`GetFileEntries` and `XisoZarchive.CreateZar`
  resolve the descriptor with the same helper; invalid images keep the
  historical fallback base and failure behavior.
- `XisoRedump.RebuildRedump` rejects sector-0 inputs with an explicit error
  (repack to the standard sector-32 layout first) because a verbatim embed would
  place the descriptor at partition sector 0; `HasXisoMagic` recognizes sector-0
  images so they keep working as `.zar` sidecar inputs.

#### Prompt cancellation in `XboxPrng.TryGetSeed`

- The brute-force seed search now checks the cancellation token inside each
  worker's candidate chunk. A canceled search stops promptly instead of grinding
  through the rest of the chunk — removing the coverage-instrumented stall that
  tripped CI's 15-minute blame-hang watchdog on the net8.0 test host.

### Tests

- New `XisoRebuiltSector0Tests` (12 tests): probe/verify/volume-info parity,
  explorer listing + `OpenReadStream`, filetime read/write, block-device probe,
  sector layout used/free ranges, in-place add/replace/root-table-move, and
  `GetXisoRanges`/`CreateZar` round-trips on sector-0 images.

### Docs

- `docs/xiso-format.md` documents the rebuilt sector-0 variant; the
  `api-xisoreader.md` probe table, `DescriptorSector` contract, and the
  library/CLI READMEs cover the accepted layout and the Redump rebuild
  restriction.

### CI

- Test runs always publish a `.trx` artifact for post-mortems and skip
  hang-dump collection (`--blame-hang-dump-type none`); the instrumented
  seed-search stall that aborted the net8.0 job is gone (windows tests dropped
  from ~15 min to ~1.5 min per TFM).

## 1.1.0

Additive release for VFS-consumer parity (SimpleXisoDrive/Dokan): the public API
gains bounded in-place file reads, a keep-open explorer mode, descriptor
creation-time/sector surfacing, `FileShare` control, and Windows attribute
mapping — no behavior change to existing APIs. Targets remain `net8.0` /
`net9.0` / `net10.0`; full suite green on all three (1400 tests: 1396 passed,
4 pre-existing skips, 0 failed).

### Library

#### Read file bytes in place — no extraction

- `XisoExplorer.OpenReadStream(string)` / `OpenReadStream(ExplorerNode)` return a
  read-only, seekable `Stream` over a file's data extent inside the image. Reads
  are clamped to the entry's `FileSize` (not the image length), so a corrupt TOC
  cannot leak the next file's sectors; seeking at/past the end reads 0 bytes per
  `Stream` conventions; CISO/split-CISO images work transparently through the
  decompressed block device. No copy is made, so 4 GB files stream in place.
- `XisoReader.ReadFileBytes(isoPath, internalPath, Span<byte>, fileOffset)` and
  the stream overload are one-shot conveniences over the same bounds; the stream
  overload leaves the caller's stream open, and missing paths/directories throw
  `InvalidDataException`.
- A stateless explorer's read stream owns the image handle (dispose it); a
  keep-open explorer's read streams stay valid until the explorer is disposed.

#### Keep-open explorer mode

- New `XisoExplorerOptions { KeepOpen, Share }` and
  `XisoExplorer(string, XisoExplorerOptions)`: `KeepOpen` holds one image stream
  for the explorer's lifetime (metadata calls use the held stream), serializes
  operations with an internal lock, and `Dispose()` closes the stream — the
  mount/VFS shape that avoids re-opening the image for every lookup.
  `KeepOpen: false` stays byte-for-byte identical to 1.0.2.

#### FileShare control on image opens

- `XisoReader.OpenImageStream(path, FileShare)` and
  `XisoExplorerOptions.Share`: plain-ISO streams pass the share mode through
  (default `FileShare.Read`, unchanged), and CISO inputs thread it to the
  container file. `FileShare.ReadWrite` lets a mounted image coexist with AV
  scanners or sync clients that open the `.iso` for write.

#### Volume descriptor timestamps and sector

- `VolumeInfo` gains `CreationTime` (`DateTimeOffset?`, `null` when invalid;
  raw 0 maps to 1601-01-01), `FileTimeRaw`, and `DescriptorSector`
  (partition-relative sector `32` for every supported layout — the partition
  shift is `DiscLseek`; −1 when invalid) — all populated by the same
  probe `GetVolumeInfo` already performs, so no second open is needed.
  `GetVolumeInfo` agrees with `GetFileTime` for the same image.

#### Windows attribute mapping

- New `XisoAttributes.ToWindowsFileAttributes(byte)`: pure bit math mapping the
  raw XDVDFS attribute byte to `System.IO.FileAttributes` (`ReadOnly` always
  set; `Directory`/`Hidden`/`System`/`Archive` OR'd in; `Normal` when nothing
  else applies; reserved bits masked), matching SimpleXisoDrive's locked
  expectations.

### GUI (Avalonia)

- Modern dark theme: left navigation rail with an accent pill, card layout,
  green accent palette, rounded inputs and log console, and a dark native
  title bar on Windows.
- New **ZAR** tab: packs an ISO/XISO/Redump image into a `.zar` (ZArchive/zstd,
  loadable in Xenia canary) with overwrite/skip/auto-rename collision policies;
  drop routing and tab-order shortcuts account for the new tab.

### Fixes

- `XisoExplorer.OpenReadStream(string)` and the keep-open constructor no longer
  leak the image handle when a lookup or probe throws.
- `BoundedSubStream` rejects seeks before the window start with `IOException`
  (per `Stream` conventions) instead of reading preceding image bytes.
- `ReadFileBytes` validates the path even when the buffer is empty.
- `GetVolumeInfo` skips probe candidates past EOF instead of aborting, so a
  trimmed XGD3 image (smaller than the global candidate offset) is detected.

### Docs

- Library/Getting-Started quick samples for the VFS use case, and the Utilities
  page documents explorer options, the read-bounds contract, and
  `XisoAttributes`.

### Dependencies

- `ZArchiveSharp` NuGet dependency updated 1.0.2 → 1.3.0 (mount-friendly reader
  API, specific open-failure reasons; additive, no wire-format changes).

## 1.0.2

Release tag [`1.0.2`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1338 tests: 1334 passed, 4 pre-existing skips, 0 failed).

### CLI

#### `-v` banner is XISOSharp-branded

`-v` (and the usage header) now prints
`XISOSharp v<version> for <win|linux|macos|cross-platform> - https://github.com/purelogiccode/XISOSharp`
instead of the `extract-xiso` compatibility line. The version is the MinVer
build stamp with `+build` metadata trimmed, so it matches the package/assembly
version. `Constants.ExisoVersion` is kept for provenance and the on-disk
optimized tag (`in!xiso!2.7.1 (01.11.14)`) is unchanged, so image compatibility
is unaffected. The extract-xiso BSD-4-clause acknowledgement remains in the
shipped `LICENSE`.

#### Automatic update check

Every interactive launch compares the running version against the latest
GitHub release — at most one request per 24 hours, cached at
`%LocalAppData%/XISOSharp/update-check.json`. A newer release prints an
`[UPDATE]` notice with the release page and the matching
`release_<version>_<rid>.zip` asset, and on an interactive console you are
offered to open the release page in your browser. The check is skipped for
`-q`/`-Q`/`-v` runs and test hosts, never fails the run, and can be disabled
with `XISO_NO_UPDATE_CHECK=1`.

#### New inspection verbs

`--sector-layout` (volume summary, per-file extents, used/free ranges),
`--ranges` (system/bone vs file sector ranges), and `--is-optimized`
(optimized-tag probe, `--skip-sectors` aware).

#### Interactive launch pause

Double-clicking `XISOSharp.exe` with no arguments prints usage and waits for a
keypress instead of closing the console window. Scripts, pipes, test hosts, and
`XISO_NO_PAUSE=1` are never blocked.

#### Warning+ logs forwarded to the bug-report service

The shared Serilog pipeline in the CLI, GUI, and Tester forwards
Warning-and-above events to the bug-report API with environment, error, and
exception sections; opt out with `XISO_DISABLE_BUGREPORT=1`.

### GUI (Avalonia)

- **CLI discovery fix for single-file bundles**: `ToolLocator` now also probes
  the directory of the real executable (`Environment.ProcessPath`) after
  `AppContext.BaseDirectory`, since self-extracting bundles run from
  `%TEMP%\.net\...`. This resolves the "CLI not found" state when the GUI runs
  from a published bundle.
- The legacy `XISOSharp.Cli` file-name fallback was removed; resolution is
  `XISOSharp(.exe)` only.
- Status bar and log show a branded product label (`XISOSharp <version>`) read
  from the CLI binary's version metadata instead of echoing the `-v` banner.
- The app icon is embedded in both the executable and the window.
- Framework-dependent publish builds and stages the CLI beside the GUI
  automatically (previous attempts failed with `NETSDK1151`).

### Library

- `ToolLocator` is now the shared resolver/probe used by the GUI, Tester, and
  BattleTests: explicit override → app directory → process directory → `PATH`,
  with a bounded, tree-killed `-v` probe via `ProcessRunner`, plus a new
  `ToolLocatorTests` suite.
- `Constants.Banner` reports the XISOSharp product version; the extract-xiso
  baseline constant is retained for provenance.

### Repository & docs

- GitHub Pages publishes the Docsify documentation, and a workflow syncs the
  wiki (sidebar included).
- `LICENSE` now carries the full third-party notices (extract-xiso BSD-4-clause,
  xdvdfs, XboxKit, ZArchiveSharp); distributed bundles include `LICENSE` and
  `README.md`.
- Docs and readmes refreshed for the branded banner, the new inspection verbs,
  update checks, the GUI bundle layout, and the shared tool locator.

### Dependencies

No library dependency changes: `ZArchiveSharp` stays at 1.0.2, analyzer/Roslynator
versions are unchanged from 1.0.1.

## 1.0.1

Release tag [`1.0.1`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1290 tests: 1286 passed, 4 pre-existing skips, 0 failed).

### Fixes

#### Rewrite keeps zero-size directories as directories

Optimizing (`-r` / `Rewrite`) an image containing a zero-size directory entry —
seen in the wild in e.g. Marvel vs Capcom 2 — used to write that entry back as a
**file** (ARC attribute, file data) instead of a **directory**, breaking the game.

Cause: `XisoReader.TraverseXiso` in `GenerateAvl` mode only recursed into
subdirectories with `fileSize > 0` and left `AvlNode.Subdirectory` as `null` for
zero-size ones; the writer treats `null` as "file". The fix assigns
`AvlNode.EmptySubdirectory`, matching upstream `extract-xiso` build
`202609111233`, which fixed the same bug in `traverse_xiso()` (subdirectory left
`NULL` instead of `EMPTY_SUBDIRECTORY`). See [Rewrite mode](api-xisowriter.md#rewrite-mode)
and [XISO Format](xiso-format.md#directory-tables-and-the-avl-tree).

#### SHA3-256 checksum works on OSes without native SHA3 support

`XisoChecksum.ComputeImageChecksum` used only the BCL
`IncrementalHash` SHA3-256, which throws `PlatformNotSupportedException` on OSes
without a SHA3 provider (Windows 10 CNG, OpenSSL 1.x) — all checksum tests
failed there. New pure-managed FIPS 202 SHA3-256 (`XISOSharp/Sha3.cs`, no new
dependencies, trim/AOT-safe) is now used as a fallback whenever
`SHA3_256.IsSupported` is false; digests are identical either way (verified
against OpenSSL 3.0, including rate-boundary sizes). See
[Checksums](xdvdfs-compat.md#checksum).

### Dependencies

- `ZArchiveSharp` **1.0.1 → 1.0.2**, now consumed purely as a NuGet package —
  the sibling-checkout `ProjectReference` fallback was removed, so no
  side-by-side `CSharp_ZArchiveSharp` clone is needed to build. See
  [Building](building.md).
- `Meziantou.Analyzer` 3.0.235 → 3.0.236 (all projects), `Avalonia.Diagnostics`
  11.3.21 → 11.3.22 (GUI, Debug-only). Dev-only, no runtime impact.

### Documentation

- Checksum docs ([xdvdfs Compat](xdvdfs-compat.md#checksum),
  [XisoReader](api-xisoreader.md#checksum-sha3-256),
  [Utilities](api-utilities.md)) describe the BCL/managed-fallback behavior.
- Rewrite docs ([XisoWriter](api-xisowriter.md#rewrite-mode)) and the format
  reference ([XISO Format](xiso-format.md#directory-tables-and-the-avl-tree))
  document the zero-size-directory handling and upstream parity.
- Reference/build docs updated for the NuGet-only ZArchiveSharp consumption and
  the `extract-xiso-build-202609111233` reference drop.
- Library README intro no longer pins a single extract-xiso version number.

### Internal (no behavior change)

Solution-wide Roslynator formatting cleanup (line wrapping, `Fill(0)` →
`Clear()`, internal `Sha3_256` → `Sha3256` rename).

## 1.0.0

Initial NuGet release: pure-C# port of `extract-xiso` v2.7.1 (byte-identical
output) extended with XboxKit archival workflows and xdvdfs packing
(build-image remapping, CISO, SHA3-256 checksums). See the
[README](README.md#feature-highlights) for the full feature list.
