# What's New in 1.6.0 (since 1.5.0)

XISOSharp can now **pack to an explicit sector layout** (PlanToEnhance Phase 8).
Take the JSON from `--sector-layout --json` (or write a compact `path=sector`
text spec), and `-c`, `--pack <dir>` or `build-image` will place matching files
and directory tables at exactly those sectors while allocating the rest
first-fit around them. A round-trip of an unchanged tree is byte-identical.

The Phase 4 TOC hardening follow-up also lands here: the audit, sector-range,
repair, salvage and ZAR walks use the same explicit in-order table traversal as
extract/list/rewrite, so `MaxTocDepth` bounds real subdirectory nesting only.
The raw-sector redump modes now refuse container inputs up front, and the GUI
ZAR tab converts a `.zar` back to a plain ISO.

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1936 passed / 1 opt-in skip (1937) on net8.0/net9.0** and **2000 passed / 1
opt-in skip (2001) on net10.0**.

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
- **GUI ZAR round-trip**: the ZAR tab detects a `.zar` source and converts it
  back to a plain ISO through the CLI's `convert` verb; packing an image keeps
  the overwrite/skip/auto-rename policies.
- **Redump modes refuse containers**: `--video`, `--random`, `--seed`, `--wipe`,
  `--trim`, `--petrify`, `--update` and `--zar` now reject `.cso`/`.cci`, split
  ISO and `.zar` inputs with a "decompress/convert/join first" message instead
  of parsing container bytes as an XISO.
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
refusals carry `NoBugReport`.
Net8/net9: **1936 passed / 1 skipped (1937)**; net10:
**2000 passed / 1 skipped (2001)**.

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
