# What's New in 1.8.0

_Release date: 2026-11-07._

XISOSharp 1.8.0 is the hardening release: custom source filesystems, per-call
logging, sparse pre-sized outputs, deterministic property coverage, benchmark
comparisons, a public synthetic corpus and a new Xbox 360 GUI tab. No read/write
behavior changed and no public API was removed; the package validates against
1.7.0.

## CCISharp 1.1

XISOSharp now integrates **CCISharp 1.1**:

- CCI levels `10..12` select the managed LZ4-HC encoder (level 12 is the
  highest effort); levels `0..9` keep the existing fast encoder and output.
- Single-file `compress --format cci --jobs 1..64` compresses sectors on a
  bounded worker pool with byte-identical output.
- CCI readers accept multi-digit parts (`game.10.cci`) when the contiguous set
  starts at `game.1.cci`; the writer keeps the `.1.cci` … `.9.cci` layout.
- `XisoCci.CompressToCci` gains a `jobs` overload. Existing code and read verbs
  are unchanged.

## Library

- **Pack from any source store.** `ISourceFilesystem` is the read-side
  counterpart to `IFilesystem`; `MemoryFilesystem` implements both. New
  `XisoWriter.CreateXiso`/`PackFromDirectory` overloads pack a custom store
  without host source paths and produce a byte-identical image.
- **Per-call logging.** `IXisoLogger`, `XisoLogOptions` and `XisoLogScope` route
  one operation's output to an isolated sink (also via `UnpackOptions.Log`).
  Concurrent operations no longer have to share the static `Logger` writers.
- **Sparse outputs.** Pre-sized writer/extraction outputs use sparse files on
  NTFS and Unix; CISO/CCI decompression and ZAR extraction skip all-zero
  chunks. Dense behavior remains the fallback.
- **Property-style tests.** Seeded round-trip coverage for pack/extract,
  split/join, CISO, CCI and ISO/ZAR at boundary sizes (empty, one byte, sector,
  sector+1, compression threshold).
- **Benchmarks.** Extract, CCI compress/decompress and a 64 MiB random-read case
  join the end-to-end suite; [Benchmarks](docs/benchmarks.md) records the
  in-process and CLI-comparison numbers with a reproducible script.
- **Support policy.** `net10.0` is the primary target; `net8.0`/`net9.0` are
  compatibility-only through 1.x and will be removed in 2.0.0.

## Xbox 360 GUI

- New **Xbox 360** tab: open GOD/SVOD headers or `.data` folders,
  XBLA/XBLIG STFS packages and images; list, tree, extract, unpack and audit
  (`-V`) with the same CLI verbs as the command line.
- **Title panel** parses `title-info --json` for XBE/XEX/GOD/STFS.
- **XBE preview** renders the logo, `$$XTIMAGE` title and `$$XSIMAGE` save
  asset, with a clean message when an asset is absent or unsupported.
- Drag/drop routes GOD/STFS inputs to the new tab.
- Extraction and CISO/CCI compression expose `--jobs 1..64`; CCI levels
  `10..12` (LZ4-HC) are selectable.

## Docs and test infrastructure

- The DocFX API reference is live and CI now fails on broken local documentation
  links.
- `XISOSharp.TestDataGenerator --corpus` generates the public synthetic corpus
  (XISO, split ISO, CISO, CCI, ZAR, GOD/SVOD, XBLA/XBLIG STFS, XBE/XEX and corrupt
  variants) with a SHA-256 manifest; tests and BattleTests consume it.
- Package validation baseline moved to 1.7.0; the 1.4.4-era suppressions were
  retired.

# What's New in 1.7.0

_Release date: 2026-11-07._

XISOSharp 1.7.0 extracts the GOD/SVOD and single-file XBLA/XBLIG STFS readers
into the standalone MIT package
[`StfsSharp`](https://www.nuget.org/packages/StfsSharp) 1.0.0. The existing
XISOSharp API is preserved through thin facades, so `title-info`, `list`,
`extract`, `checksum` and `-V` output is unchanged.

- **StfsSharp dependency**: `XISOSharp` now references `StfsSharp` 1.0.0 from
  NuGet. `GodHeader`, `StfsHeader`, `StfsPackage` and `GodBlockDevice` map the
  StfsSharp models/readers to the existing public records and interfaces.
- **Standalone readers**: `StfsSharp.GodVolume`/`GodVolumeStream` expose a GOD
  container as a seekable read-only stream; `StfsSharp.StfsPackage` adds
  `ReadFileChunks`, extraction progress and warning channels.
- **Facade removal plan**: the XISOSharp facades stay through 1.x and are
  removed in 2.0.0, after which consumers use `StfsSharp` directly.
- **Tests**: the moved unit tests now run in the StfsSharp repository; XISOSharp
  keeps integration tests, including facade parity checks. The XISOSharp suite
  is **2101 passed / 1 opt-in skip** on net8.0/net9.0 and **2165 passed / 1
  opt-in skip** on net10.0.
- **Verification**: InspectCode 0 warnings, `dotnet format` clean, package
  validation green, and the real XBLA/XBLIG/GOD corpus opens and validates
  through both the StfsSharp API and the XISOSharp CLI.

# What's New in 1.6.0

_Release date: 2026-11-06._

XISOSharp 1.6.0 is the Phase 10 Tier A release (A1-A5), plus the first slice
of Tier B: read-only GOD/SVOD sector input and XBLA/XBLIG STFS package
listing/extraction. Full suite green: **2098 passed / 1 opt-in skip on
net8.0/net9.0** and **2162 passed / 1 opt-in skip on net10.0**.

- **Concurrent creation and extraction**: `XisoWriter.CreateXiso`/`PackFromDirectory`
  use absolute source paths and per-operation progress state, so concurrent
  creates run independently and never change the process working directory. A
  caller-supplied `inRoot` without host paths is resolved once against the
  caller's CWD. `XisoReader.Extract`/`UnpackImage` extract through a rooted
  `LocalFilesystem` instead of `Directory.SetCurrentDirectory`, so concurrent
  extracts are independent too; `UnpackImage(..., IFilesystem)` still accepts any
  custom destination.
- **Xbox 360 title metadata**: `title-info` and `--xex-info` read the plaintext
  XDBF/XSTR/XSRC resources from `default.xex` and report the title name,
  publisher, developer, genre, description, thumbnail presence and XEX header
  fields (`XexTitleInfo`, `GetXexTitleInfo`). `GetTitleInfo` resolves
  `default.xbe` → `default.xex` → `default_mp.xbe`, and `TitleInfo` now exposes
  `Kind` (`Xbe`/`Xex`) and `Xex`.
- **XBE image assets**: `XbeAssets` and `--xbe-image` extract the Microsoft
  logo, `$$XTIMAGE` title image and `$$XSIMAGE` save image as PNG or DDS.
  DXT1/3/5, BGR24/BGRA32 and XPR0 payloads are decoded with a BCL-only PNG
  writer; `XbeInfo` reports `HasLogoBitmap`/`HasTitleImage`/`HasSaveImage`.
- **GOD read-only input**: `GodBlockDevice` maps a GOD/SVOD header and its
  `DataNNNN` fragments into the sector view, so list/tree/extract/checksum/
  hash/validate/sector-layout/ranges accept a GOD container. `decompress`
  writes a plain ISO and `convert` accepts GOD → ISO/CCI/CISO; in-place
  repair/copy-in/set-filetime/rewrite and split refuse GOD with a
  "decompress first" message.
- **GOD title metadata**: `title-info` reads a GOD header or its `.data`
  folder and reports title name, title ID, disc number and disc count without
  mounting the container (`GodHeader`/`GodTitleInfo`).
- **XBLA/XBLIG header metadata**: `StfsHeader`/`StfsTitleInfo` read the same
  fields from single-file XBLA/XBLIG STFS packages, which have no `.data`
  folder.
- **XBLA/XBLIG package contents**: `StfsPackage` reads the STFS file table and
  file data behind the hash tables, so `-l`, `-t`, `-x`, `--unpack`, `checksum`
  and `-l/--json` accept an XBLA/XBLIG package directly. `XisoChecksum.ComputeStfsChecksum`
  uses the same sorted-path SHA3-256 tree hash as ISO and ZAR. STFS writing
  stays out of scope.
- **STFS hash-tree validation**: `StfsPackage.Validate` checks every data
  block against its SHA-1 hash entry, walks each file's block chain, verifies
  the level-0/1/2 hash tables, the top hash and the header hash, and returns a
  `StfsValidationReport`. `-V`/audit now accepts an XBLA/XBLIG package and
  reports `Files checked` / `Blocks checked` / `Hash tables` / `Result`.
- **FATX-safe split default**: plain-ISO splits now default to
  `Constants.FatxMaxFileSize` (`0xFF000000`, 4 GiB − 16 MiB); an explicit
  larger `--size` warns.
- **Parallel CISO compression**: `compress`/`cso --jobs 1..64` (and
  `CisoWriter.CompressToCso(..., jobs: n)` / `CompressStream(..., jobs: n)`)
  compresses sectors on a bounded worker pool. Batches are compressed in
  parallel and written in sector order, so the output is byte-identical to the
  sequential writer and progress stays ordered. CCI compression remains
  single-threaded (CCISharp 1.1 work).
- **Parallel extraction**: `-x`/`--unpack --jobs 1..64` (and
  `UnpackOptions.MaxDegreeOfParallelism`) queues independent files during the
  tree walk and copies them on a bounded pool. Output is byte-identical,
  `FileAdded` progress and completion logs stay in traversal order, and
  `--skip-existing`/`--continue-on-error` keep working. Plain ISOs are read
  positionally; CISO/CCI/GOD/split and in-memory sources serialize reads, and
  destination filesystem calls are serialized so non-thread-safe stores stay
  correct.
- **Safe-extract name policy**: `ExtractNamePolicy` (`Reject` default,
  `Sanitize`, `Skip`) and `UnpackOptions.NamePolicy`, plus
  `StfsPackage.ExtractTo`/`XisoZarchive.ExtractZar` overloads. `Sanitize`
  replaces unsafe characters with `_` and adds `~N` on collisions; `Skip` omits
  the entry (and a skipped directory's subtree). The CLI exposes
  `--name-policy reject|sanitize|skip` on `-x`/`--unpack`.
- **`--delete-source`**: batch `compress`/`decompress`/`convert`/`split` can
  delete each source after a successful conversion (requires `-y`; split sets
  delete every part; the summary reports the count).

# What's New in 1.5.0

XISOSharp 1.5.0 is the first public release since 1.4.4 and merges the
1.5.0 development work (CCI, split ISO, ZAR input, traversal hardening, batch
conversion, JSON, preallocation and explicit layout packing) with the Phase 9
"world-class library" work.

## World-class library (Phase 9)

- **`XisoImage`**: disposable, open-once facade over ISO/split ISO/CISO/CCI with
  `List`, `ListFlat`, `ReadFile`, `OpenReadStream`, `Extract`, `Validate`,
  `GetTitleInfo`, `ComputeImageHash` and async wrappers.
- **Full-image hashes**: `XisoImageHash.Compute` streams CRC32, MD5, SHA-1,
  SHA-256 and SHA3-256 in one pass; the CLI exposes `hash <image>`.
- **`verify-hashes`**: verify a `--petrify`/XboxKit `.hash` sidecar for ISO,
  CISO, CCI, split ISO and ZAR.
- **`title-info`**: find the root `default.xbe` and report title name, ID,
  region, disc and version.
- **Hardening**: Windows long-path manifests, reserved device-name
  rejection/sanitization, ZAR extraction byte/entry caps, and CISO
  alignment/index validation (the new mutation fuzzer found an
  `OverflowException` on a corrupt index).
- **Quality**: 3-OS tests, formatting gate, AOT publish smoke, mutation fuzzing,
  75% line / 60% branch coverage floors, package validation against 1.4.4.
- **Packaging**: SBOM + build provenance, community files and a console sample;
  the CLI ships as GitHub release bundles.

## Layout packing (Phase 8)

XISOSharp can now **pack to an explicit sector layout** (PlanToEnhance Phase 8).
Take the JSON from `--sector-layout --json` (or write a compact `path=sector`
text spec), and `-c`, `--pack <dir>` or `build-image` will place matching files
and directory tables at exactly those sectors while allocating the rest
first-fit around them. A round-trip of an unchanged tree is byte-identical.

The Phase 4 TOC hardening follow-up also lands here: the audit, sector-range,
repair, salvage and ZAR walks use the same explicit in-order table traversal as
extract/list/rewrite, so `MaxTocDepth` bounds real subdirectory nesting only.
The raw-sector redump modes now refuse container inputs up front, the GUI ZAR
tab converts a `.zar` back to a plain ISO, ZAR extraction is depth-bounded and
pre-sized, and CISO compression reads split/container sources directly.

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1979 passed / 1 opt-in skip (1980) on net8.0/net9.0** and **2043 passed / 1
opt-in skip (2044) on net10.0**.

## Highlights

- **`XisoLayoutSpec`**: parse the `--sector-layout --json` payload or a compact
  `path=sector` text file (`#`/`;` comments, `0x` hex, optional `total-sectors=N`).
- **Fixed-extent packing**: matching files/tables land at their requested sectors;
  unlisted entries are allocated first-fit from `0x108` around the fixed extents.
- **Layout fidelity**: `TotalSectors` is preserved; `prependSectors` keeps layout
  sectors partition-relative; an unchanged tree repacks byte-for-byte.
- **Fail-fast validation**: overlaps, reserved system-area extents (`[0, 33)`),
  out-of-range extents and too-small extents throw `InvalidDataException` before
  anything is written. JSON extra entries are reserved; text extra entries are
  rejected as typos.
- **All create paths**: `CreateXiso`, `CreateXisoAsync`, `PackFromDirectory`,
  `PackFromDirectoryAsync`, `RemapFilesystem.BuildImage`, and the CLI's `-c`,
  `--pack <dir>` and `build-image`.
- **Every table walker hardened**: a 4,096-entry left-skewed table audits, maps,
  repairs, salvages and packs to ZAR instead of failing at `MaxTocDepth`.
- **ZAR validation**: `validate game.zar game.iso` (and the reverse) compares the
  archive's name tree with the image, with `--validate-checksums` hashing each
  entry through `XisoZarchive.ComputeFileHash` without extracting. `.zar` on a
  sector-only verb is now a clean error instead of a stack trace.
- **ZAR extraction hardening**: the extract walk is bounded by `MaxTocDepth`
  like list/checksum, each destination is pre-sized from the name tree and
  copied exactly (a truncated entry fails instead of silently producing a short
  file), and a failed or canceled entry no longer leaves a partial file behind.
- **Direct container compression**: `CisoWriter.CompressToCso` reads split ISO
  and CISO/CCI inputs through the seekable decompressed view instead of writing
  a joined temporary ISO first; the split-output guard checks every source part,
  not just the entry path.
- **GUI ZAR round-trip**: the ZAR tab detects a `.zar` source and converts it
  back to a plain ISO through the CLI's `convert` verb; packing an image keeps
  the overwrite/skip/auto-rename policies.
- **Redump modes refuse containers**: `--video`, `--random`, `--seed`, `--wipe`,
  `--trim`, `--petrify`, `--update` and `--zar` now reject `.cso`/`.cci`, split
  ISO and `.zar` inputs with a "decompress/convert/join first" message instead
  of parsing container bytes as an XISO.
- **CCI part naming**: a split CCI opened through `.2.cci` … `.9.cci` now names
  outputs after the game (`game/`, `game.iso`) and recompresses to `game.cci`
  instead of the numbered part; plain ISO/CISO sets still strip only `.1`.
- **Quieter bug reports**: expected overwrite refusals (`-n`, redirected stdin)
  stay in the local log and no longer file bug reports.

## CLI

```bash
# Capture a layout, then repack at it
XISOSharp --sector-layout --json game.iso > game.layout.json
XISOSharp -c ./game_files game.iso --layout game.layout.json

# Compact text form
cat > layout.txt <<'EOF'
total-sectors=512
/readme.txt=400
/sub/game.bin=0x190
EOF
XISOSharp --pack ./game_files game.iso --layout layout.txt

# build-image remapping also honours a layout
XISOSharp build-image -m "**:{0}" ./src out.iso --layout layout.txt

# Validate an ISO → ZAR (or ZAR → ISO) conversion without extracting
XISOSharp validate --validate-checksums game.zar game.iso
```

- `--layout` is create-only: it is rejected with rewrite/extract/list modes and
  with `--pack <iso>` (a rewrite has no host files to place).
- Flags still precede positional arguments.

## Library

- `XisoLayoutSpec.FromSectorLayout(SectorLayout)`, `.Load(path)`, `.Parse(text)`,
  `.ParseJson(json)`, `.ParseText(text)`.
- `XisoWriter.CreateXiso`/`PackFromDirectory`/`...Async` and
  `RemapFilesystem.BuildImage` accept `XisoLayoutSpec? layout = null`.
- `LayoutPlanner` (internal) validates and seeds `SectorAllocator`; no public API
  break for existing callers (all new parameters are optional).

## Tests

New `LayoutPackingTests` (41) covers JSON/text parsing, byte-identical JSON
round-trips, fixed/first-fit placement, reserved JSON extents, total-sector
preservation, prepended partitions, case-insensitive paths, empty entries,
`build-image`, and CLI `-c`/`--pack`/`build-image` with usage errors. Seven new
`SectorAllocatorTests.SeedUsedRanges` cases cover the one-pass bulk seeding used
by large layouts. Five new `PathologicalTocTests` prove the 4,096-entry left
spine audits/maps, repairs its deepest entry, salvages and packs to ZAR. Ten new
`ZarValidateTests` cover ZAR↔ISO/CCI tree and checksum validation, per-entry
`ComputeFileHash`, CLI `validate --json`/`--validate-report`, and the clean
sector-only rejection for `.zar` inputs. Eleven `RedumpInputGuardTests` prove
every redump mode refuses container inputs while a plain ISO still runs;
`CliCommandsTests.Convert`, `MainViewModelTests` and the GUI self-test cover the
ZAR→ISO direction/argv, and `OverwritePromptExtraTests` asserts expected
refusals carry `NoBugReport`. Seven `XisoImageSourceTests` suffix cases plus
`CciAutoDetectTests.SecondCciPart_NamesOutputsAfterGameStem` and
`XisoRepairerRefusalTests.Salvage_DefaultOutputPath_Naming` cover numbered CCI
part naming for extraction, rewrite, recompress and salvage. One new
`ZarInputTests` case rejects a 65-level tree in both list and extract, and one
new `CisoTests` case recompresses a CISO through the direct decompressed view.
Net8/net9: **1979 passed / 1 skipped (1980)**; net10:
**2043 passed / 1 skipped (2044)**.

---

# What's New in 1.5.0 (since 1.4.5)

XISOSharp now speaks **JSON** on every read-only verb, and extraction/creation
sizes destinations before writing them. `--json` gives scripts a stable,
source-generated schema (`schemaVersion: 1`) while human logs move to stderr;
`-v`/`--version` reports the full component/runtime/commit stack. The new
preallocation and sequential-scan path keeps large extractions from growing
files sector by sector, and the writer sizes the image once the layout is
known.

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1860 passed / 1 opt-in skip (1861) on net8.0/net9.0** and **1922 passed / 1
opt-in skip (1923) on net10.0**.

## Highlights

- **`--json` for read-only verbs**: `-i`, `-l`, `-t`, `checksum`,
  `--sector-layout`, `--ranges`, `--filetime`, `--xex-info`, `--xbe-info`,
  `validate`, and `-v`/`--version`.
- **Clean stdout/stderr split**: JSON owns stdout; banners, progress, warnings
  and errors go to stderr (`Logger.RouteInfoToError`).
- **Stable schema**: `{ "schemaVersion": 1, "command": "...", ... }`; list/tree
  use an `images` array so multi-image runs stay one document; checksum uses a
  `checksums` array.
- **Trim/AOT-safe serialization**: source-generated `JsonOutputContext`, no
  reflection fallback.
- **Preallocation**: `IFilesystem.CreateFile(path, expectedLength)`, disk
  `SetLength` before writes, in-memory capacity reservation, writer output
  sized once from the computed layout.
- **Sequential I/O**: `FileOptions.SequentialScan` on plain-ISO sources and
  extraction/writer destinations.
- **Version enrichment**: XISOSharp, CCISharp, ZArchiveSharp, runtime, OS,
  architecture and MinVer commit (`-v`, `--version`, `-v --json`).

## CLI

```bash
XISOSharp -i --json game.iso
XISOSharp -l --json game.iso | ConvertFrom-Json
XISOSharp -t --json game.iso
XISOSharp checksum --json game.iso game.cci
XISOSharp --sector-layout --json game.iso
XISOSharp --ranges --json game.iso
XISOSharp --filetime --json game.iso
XISOSharp validate --json game.iso rewritten.iso
XISOSharp -v --json
```

- Flags still precede positional arguments (`-i --json game.iso`, not
  `-i game.iso --json`).
- `--json` is rejected for mutating modes (extract, rewrite, create,
  compress/decompress, redump) with a clear error.
- `-v` keeps its historical first line for scripts, then prints the component
  details.

## Library

- `IFilesystem.CreateFile(string path, long? expectedLength)` - additive default
  member; `LocalFilesystem` and `MemoryFilesystem` override it.
- `Logger.RouteInfoToError` - route informational output to `Logger.Error`.
- `GetVolumeInfo`/`ListDirectory`/`GetSectorLayout` overloads accepting
  `skipSectors` for explicit Redump/XGD partition offsets.
- `XisoRanges.GetXisoRanges`/`GetFileEntries` `Stream` overloads + container-aware
  `string` overloads (split ISO/CISO/CCI).
- `XisoReader.ExtractFile`/`CopyOutFile` and `XisoWriter` pre-size known-length
  destinations; plain-ISO opens use `SequentialScan`.

## Tests

New `CliJsonTests` (12) and `PreallocationTests` (5), plus `--xbe-info`/
`--xex-info` JSON cases; `CliHelpFlagTests` now covers the enriched version
banner. Net8/net9: **1860 passed / 1 skipped (1861)**; net10: **1922 passed /
1 skipped (1923)**.

---

# What's New in 1.4.5 (since 1.4.4)

XISOSharp now reads **CCI/CCIM** images, **split plain-ISO** sets, and
**ZArchive** (`.zar`) inputs, all through one unified image-source layer.
`compress` can write CCI (`--format cci`), `decompress` auto-detects CISO vs
CCI, and a new `convert` verb bridges ZAR and ISO. The container verbs also
accept `--batch`, so whole folders convert in one command. CCI support comes
from the standalone MIT [CCISharp](https://github.com/purelogiccode/CCISharp)
1.0.0 package (pure managed, no native code).

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1841 passed / 1 opt-in skip (1842) on net8.0/net9.0** and **1903 passed / 1
opt-in skip (1904) on net10.0**.

## Highlights

- **CCI/CCIM read + write**: single `game.cci` and split `game.1.cci` …
  `game.9.cci` sets; random access, extraction, validation and recompression.
- **Split ISO input**: `game.1.iso` … is read as one concatenated, seekable
  image. A missing middle part is a clear error, never silent truncation.
- **ZArchive input**: `-l`, `-t`, `-x`, `--unpack`, `checksum` and `convert`
  work directly on `.zar`; the tree checksum matches the equivalent ISO.
- **One detection table**: `XisoImageSource` replaces scattered `.cso` checks
  and resolves renamed containers by `CISO`/`CCIM` magic.
- **Format-aware CLI/GUI**: `compress --format cso|cci`, `cci`/`uncci`
  aliases, CCI-level/split flags, a GUI Format selector, and `.cci` filters.
- **Pathological TOC hardening**: the directory walk uses an explicit in-order
  stack, so a 4,096-entry left-skewed table lists/extracts/rewrites instead of
  hitting the depth cap or overflowing the stack.
- **Batch conversion**: `compress`, `decompress`, `convert` and `split` accept
  `--batch`/`--batch-recursive`, `--skip-existing`, `--continue-on-error`,
  `--jobs 1..64` and `--output-name` templates (`{name}`, `{ext}`, `{format}`).

## Library

- `XisoImageSource` / `XisoImageFormat` / `XisoImageSourceInfo` -
  `TryDetect`, `OpenRead`, `OpenBlockDevice`, `StripImageSuffix`.
- `XisoCci` - `CompressToCci`, `DecompressToIso`, default-path helpers.
- `CciBlockDevice` - `IBlockDevice` over CCISharp's random-access reader.
- `SplitIsoFile` / `SplitIsoStream` - split ISO discovery and concatenation.
- `XisoZarchive.ListZar` / `ExtractZar` / `ConvertZarToIso`.
- `XisoChecksum.ComputeZarChecksum` / `ComputeZarChecksumHex`.
- `StreamBlockDevice` - any seekable stream as an `IBlockDevice`.
- `XisoReader.OpenImageStream` and `XisoChecksum` now use the factory, so every
  reader verb sees the decompressed/concatenated view.
- `Repair`, `CopyIntoImage` and `SetFileTime` refuse CISO/CCI in place with a
  clear "decompress first" message (even for a missing or corrupt container).
- `CisoWriter.CompressToCso` and `XisoCci.CompressToCci` accept split ISO,
  CISO and CCI sources by materializing a temporary ISO first.
- `TraverseXiso` uses an explicit in-order stack (`Constants.MaxTocStackFrames`);
  `MaxTocDepth` now bounds subdirectory nesting only.
- Runtime dependencies: `ZArchiveSharp` 1.4.0 and `CCISharp` 1.0.0.

## CLI

```bash
XISOSharp compress --format cci game.iso game.cci --cci-level 9 --cci-split 0
XISOSharp cci game.iso game.cci --cci-split 0
XISOSharp decompress game.1.cci game.iso
XISOSharp uncci game.cci game.iso
XISOSharp convert game.zar game.iso
XISOSharp -l game.zar
XISOSharp checksum game.zar
XISOSharp compress --batch ./isos --format cci --cci-split 0 -y
XISOSharp decompress --batch ./compressed --skip-existing -y
XISOSharp convert --batch ./isos --format zar -y
XISOSharp split --batch ./isos --size half -y
```

- `--format cso|cci` (default `cso`), `--cci-level 0..9`, `--cci-split bytes`.
- `-l`/`--level` and `--split` stay format-neutral; `--ciso-*` is rejected for
  CCI output and `--cci-*` for CISO output.
- `decompress` detects CISO vs CCI by extension and magic.
- `convert` supports `.zar -> .iso` and `.iso -> .zar`, and
  `convert --batch --format iso|zar` converts a whole directory.
- `--batch` on `compress`/`decompress`/`convert`/`split` scans the verb's own
  inputs, collapses split sets to their first part, refuses duplicate outputs
  and input clobbers before writing, and removes partial outputs on failure.

## GUI

- Compress tab has a **Format** selector (CISO/CCI); the save dialog and split
  placeholder follow the selection.
- Picker filters and drag-and-drop include `.cci`/`.1.cci` and `.zar`.
- A dropped `.zar` opens the Extract tab (list/extract/checksum); compressed
  images route to Decompress.
- Decompress collision guards use CCI-aware default output naming.

## Tests

New suites: `XisoImageSourceTests`, `CciAutoDetectTests`,
`SplitIsoAutoDetectTests`, `CliCciTests`, `ZarInputTests`,
`PathologicalTocTests` and `CliBatchConvertTests`. They cover the detection
table, CCI round-trips, split recompression, ZAR conversion/checksum, CLI
aliases, format-specific flag contracts, pathological left/right/alternating TOC
chains, and batch conversion with naming templates, resume, continue-on-error
and parallel jobs.
