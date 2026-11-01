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
