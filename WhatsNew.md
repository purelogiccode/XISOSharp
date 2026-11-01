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
