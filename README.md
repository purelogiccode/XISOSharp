# XISOSharp

A **pure C#** implementation of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1 — the tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

This project is a **direct rewrite** of the original C codebase into idiomatic, managed C#. No native dependencies, no P/Invoke — just .NET.

## Projects

| Project | Description |
|---|---|
| [XISOSharp.Core](XISOSharp.Core/) | Core class library with the complete XISO read/write engine |
| [XISOSharp.Cli](XISOSharp.Cli/) | CLI tool compatible with the original extract-xiso |
| [XISOSharp.Tests](XISOSharp.Tests/) | Unit tests for the core library |
| [XISOSharpTester](XISOSharpTester/) | WPF GUI for batch regression testing against the C tool |

## Documentation

The full documentation (repository wiki) lives in [`docs/`](docs/README.md):

- [Getting Started](docs/getting-started.md) — install, first extract/create/list
- [CLI Reference](docs/cli.md) — every command, flag, and exit code
- [Library API](docs/library.md) — XisoReader, XisoWriter, and utilities
- [XISO Format](docs/xiso-format.md) — the on-disk format
- [Redump & Disc Layouts](docs/redump-workflows.md) — XGD offsets, video partitions
- [FAQ](docs/faq.md) · [Troubleshooting](docs/troubleshooting.md) · [Contributing](docs/contributing.md)

## Features

- **Create** XISO images from a directory
- **Extract** XISO contents to a directory
- **List** files inside an XISO
- **Tree** — recursive file listing with sizes and totals
- **Rewrite** an XISO to optimize its filesystem layout
- **Info** — display volume metadata and directory entry details
- **Copy-out** — extract individual files or directories without full unpack
- **Hash** — compute MD5 or SHA-256 hashes of files within an XISO
- **Audit** — deep integrity verification: header, tree, sector bounds, cycle detection
- Supports **GLOBAL**, **XGD2**, **XGD3**, and **XGD1** disc formats
- **Skip/Prepend sectors** — read Redump-style images where a video partition precedes the game partition (`--skip-sectors`), and write images with room for one (`--prepend-sectors`)
- **Exclude patterns** — omit files/folders when creating an image (`-X <glob_pattern>`, repeatable; `-s` implicitly excludes `$SystemUpdate`)
- Automatic `.xbe` media-enable patching
- Async APIs for non-blocking I/O
- Strong-named assembly
- Targets .NET 8, .NET 9, and .NET 10

## NuGet Package

The core library is available as the **`XISOSharp`** NuGet package.

### Install

```
dotnet add package XISOSharp
```

Or via the NuGet Package Manager:

```
Install-Package XISOSharp
```

### Package Manager UI

Search for `XISOSharp` in the NuGet Package Manager in Visual Studio and install it.

## Usage

### Extract an XISO

```csharp
using XISOSharp;

int result = XisoReader.Extract("game.iso", "output_directory");
```

### List contents of an XISO

```csharp
using XISOSharp;

int result = XisoReader.List("game.iso");
```

### Create an XISO from a directory

```csharp
using XISOSharp;

int result = XisoWriter.CreateXiso("source_directory", "output.iso");
```

### Rewrite an XISO (optimize layout)

```csharp
using XISOSharp;

int result = XisoReader.Rewrite("game.iso", outputDirectory: null, deleteOriginal: false);
```

### Async create

```csharp
using XISOSharp;

var (result, outputPath) = await XisoWriter.CreateXisoAsync("source_directory", "output.iso");
```

## Build

Open `CSharp_XISOSharp.sln` in Visual Studio or run:

```
dotnet build
```

## Comparison with Related Projects

This project was compared file-by-file against the three reference implementations shipped in
[`References/`](References/):

* [`extract-xiso-build-202505152050`](References/extract-xiso-build-202505152050/) — C port of **extract-xiso v2.7.1** (`extract-xiso.c`, `CMakeLists.txt`, `win32/`)
* [`XboxKit-0.7`](References/XboxKit-0.7/) — C# archival toolkit (`XboxKit/`, `LibXGD/`) for lossless Redump ↔ XISO/ZAR conversion
* [`xdvdfs-0.8.3`](References/xdvdfs-0.8.3/) — Rust `xdvdfs-core` + `xdvdfs-cli` (+ `xdvdfs-desktop`/`xdvdfs-web`)

The tables below are derived from those sources and from [`XISOSharp.Core/`](XISOSharp.Core/) + [`XISOSharp.Cli/Program.cs`](XISOSharp.Cli/Program.cs).

### 1 — Project overview

| Dimension | **XISOSharp (this repo)** | **extract-xiso v2.7.1 (C)** | **XboxKit 0.7** | **xdvdfs 0.8.3 (Rust)** |
|---|---|---|---|---|
| **Language / Runtime** | C# 14, .NET 8/9/10, `Nullable`, strong-named, trimmable, AOT-compatible, SourceLink + MinVer | C89/C99, single `extract-xiso.c` ~2 800 LOC, no headers | C# (.NET) `LibXGD` library + `XboxKit` console + native `ZArchive`/zstd | Rust (workspace, `no_std` + `alloc` optional), `maybe-async` |
| **Platforms** | `win-x64`/`linux-x64`/`osx-x64`/`osx-arm64`, banner via `OperatingSystem.Is*()` (`Constants.Banner` at `XISOSharp.Core/Constants.cs:181`) | `#if _WIN32/__LINUX__/__DARWIN__/__FREEBSD__/__OPENBSD__`, shims `win32/getopt.c`/`dirent.c`/`asprintf.c` | .NET cross-platform (Windows-centric), `FileStream`-based | Cross-platform CLI + Tauri desktop + WASM web, `cargo install xdvdfs-cli` |
| **Build** | `dotnet build`, `CSharp_XISOSharp.sln`, `Directory.Build.props`, NuGet `XISOSharp` + `snupkg` | `cmake .. && make`, `CMakeLists.txt: cmake_minimum_required 3.5` | `dotnet build` (`LibXGD.csproj`/`XboxKit.csproj`) + CMake for `ZArchive` | `cargo build`, `Cargo.toml` workspace `v0.8.3`, `flake.nix`/`default.nix` |
| **Dependencies (runtime)** | **Zero** — only BCL (`System.Buffers.Binary`, `System.Security.Cryptography`), build-time `SourceLink.GitHub`+`MinVer` | None — `time.h`/`fcntl.h`/`sys/stat.h` only | `LibXGD`, `ZArchive` (zstd), `SabreTools.Wrappers.XboxISO` | `bincode`, `serde`, `encoding_rs` (WINDOWS_1252), `clap`, `wax` (glob), `ciso`, `md-5`, `sha3` |
| **Distribution** | NuGet `XISOSharp`, CLI `XISOSharp.Cli` (`AssemblyName extract-xiso`) | Binary `extract-xiso` in `build/` | Binary `xboxkit.exe` | Crates `xdvdfs` + `xdvdfs-cli`, GitHub releases (`xdvdfs` binary) |
| **License** | MIT | BSD-3-clause variant by `in@fishtank.com` (`LICENSE.TXT`) | (per `README.md` in `XboxKit-0.7`, lossless archival tool) | MIT/Apache-2.0 (per `LICENSE` in `xdvdfs-0.8.3`) |
| **Primary goal** | **Byte-identical** managed rewrite of `extract-xiso` + modern library/CLI extensions | Create/list/extract/rewrite XISO (game partition) | **Lossless** Redump ↔ XISO/ZAR archival with deduplication | Modern XDVDFS filesystem library (embeddable, `no_std`) |

### 2 — Disc layout & Redump support

| Capability | **XISOSharp** | **extract-xiso** | **XboxKit** | **xdvdfs** |
|---|---|---|---|---|
| **Header magic** | `MICROSOFT*XBOX*MEDIA` (20 B) at `0x10000`, trailing magic after `FILETIME`+`0x7C8` — all 4 probe (`XisoReader.VerifyXiso` at `XISOSharp.Core/XisoReader.cs:53`) | Same, nested `lseek`/`read`/`memcmp` chain in `extract-xiso.c:verify_xiso` | `XDVDFS.IsValidXISO` checks magic at `offset+XISO_HEADER_OFFSET`; higher-level uses file-length tables | `VolumeDescriptor::deserialize` at `32*2048`, checks `magic0 && magic1` (`layout.rs`/`read.rs`) |
| **Fixed disc offsets probed** | `0` (raw/trimmed), `0x0FD90000` GLOBAL/XGD2, `0x02080000` XGD3, `0x18300000` XGD1 (`Constants.cs:127-136`) — order `0→GLOBAL→XGD3→XGD1` | Identical (`#define GLOBAL_LSEEK_OFFSET 0x0FD90000ul` etc. at `extract-xiso.c:447`) | Full table: `XISO_OFFSET [0x18300000, 0x0FD90000, 0x89D80000, 0x02080000]` = XGD1/XGD2/Hybrid/XGD3 + `REDUMP_ISO_LENGTH[9]` + `VIDEO_Lx[19]` (`LibXGD/XGD.cs:11`) — **only one that knows `0x89D80000` hybrid** | **None** — expects already-trimmed game partition, `BlockDeviceRead::read(offset,buf)` with `sector*2048` |
| **Arbitrary Redump video partition** | ✅ `--skip-sectors N` (read) + `--prepend-sectors N` (write), `VerifyXiso(..., skipSectors)` skips probing, sector numbers stay partition-relative, tag written at `prependOffset+31337` | ❌ fixed 4 only; hybrid/non-standard Redumps fail | ✅ true dual-partition: PFI vs SS layerbreak, joins `L0` head + `L1` tail, derives wave from PVD at `0x832D` via `WAVE_PVD[24]`, splits `l0Padding`/`l1Padding` | ❌ bare partition only |
| **Security sectors (mastering errors)** | Not modelled (XISO contains only game partition) | Not modelled | ✅ reads external `sectors.txt` (`4096`-sector ranges), zeroes in Redump, skips via `XboxPRNG.SimulateSectors` (`XGD.cs:209`) | Not modelled |
| **Hybrid `0x89D80000`** | Only via `--skip-sectors 283392` (documented in `docs/redump-workflows.md`) | Not supported | Native (`XGD.cs:11`) | Not applicable |

### 3 — Filesystem implementation

| Capability | **XISOSharp** | **extract-xiso** | **XboxKit** | **xdvdfs** |
|---|---|---|---|---|
| **Sector / alignment** | `2048` B sector, `0x10000` file modulus, `RootDirectorySector 0x108`, dir-entry `14 B + name` padded to `*4`, sector-boundary pad, image padded to `0x10000` | Same (`#define XISO_SECTOR_SIZE 2048`, `XISO_FILE_MODULUS 0x10000` etc.) | Same via `XDVDFS.SECTOR_SIZE`, merges `sysRanges/fileRanges` via `MergeRanges`/`GetValidSectors` | Same `SectorAllocator` (`32*2048`), `dirtab: pad to sector`, volume at `32*2048` with `0x00` zero-pad (not `0xFF`) |
| **Empty-directory sentinel** | `0xFFFF` (`PadShort`) **and** `0x0000` + 12-byte `0x00` header (`Constants.IsEmptyDirectoryHeader` at `Constants.cs:61`, `XisoReader.TraverseXiso` at `XisoReader.cs:231`) — xdvdfs compat; `0x0000` with non-zero tail treated as real `left=0` | `0xFFFF` only (`extract-xiso.c:1384`, `traverse_xiso`) | `0xFFFF` only (`XDVDFS.GetValidSectors`) — would mis-detect xdvdfs empty dirs | `0xFFFF` and `0x0000` (`read_dirent` `[0xFF;0xE]`/`[0x00;0xE]`, `left/right !=0 && !=0xFFFF`) — XISOSharp matches this |
| **AVL tree** | Case-insensitive `AvlTree.cs` (port of `avl_compare_key`/`avl_insert`/`AvlTraverseDepthFirst`), balanced BST, `EmptySubdirectory` sentinel | Same (`avl_compare_key` via `strcasecmp`/`_stricmp`, `avl_left_grown` etc.) | File-extent ranges (`GetXISORanges`) sorted by offset, not AVL rebuild | `write::avl` + `DirectoryEntryTableWriter` (`write/dirtab.rs`) |
| **`llCompat` linked-list fix** | Preserved (`TraverseXiso` at `XisoReader.cs:315`): `if(llCompat) rOffset sector rounding`; auto-selected via optimized-tag (`llCompat = !IsOptimized`) | Same (`traverse_xiso` `in_ll_compat`, first non-zero left clears flag, right fix) — callers pass `!optimized` for list/extract, `true` for rewrite | No `llCompat` | No `llCompat` |
| **Filename encoding** | `Latin1Encoding` (Windows-1252) (`Latin1Encoding.cs`) | `char*` filesystem bytes, `FORCE_ASCII` on Linux/FreeBSD | `Encoding.ASCII` | `encoding_rs::WINDOWS_1252` (`read.rs`) |
| **ECMA-119 / ISO9660 descriptors** | ✅ `XisoWriter.WriteVolumeDescriptors` at `XISOSharp.Core/XisoWriter.cs:1000` (`0x8000 CD001`, volume space LE+BE, set size, creation dates) | ✅ `write_volume_descriptors` (`extract-xiso.c:212x`) | Not emitted per-XISO | Only `VolumeDescriptor` at sector 32 |
| **Optimized tag** | `in!xiso!2.7.1 (01.11.14)` at `31337`, prefix `in!xiso` (`Constants.cs:29`), `IsOptimized` at `XisoReader.cs:633`, rewrite skip, prepend-aware | Same (`XISO_OPTIMIZED_TAG_OFFSET 31337`, `XISO_OPTIMIZED_TAG "in!xiso!" v`) | Not used | Not used |

### 4 — CLI commands & flags

| Flag / Mode | **XISOSharp** (`Program.cs`) | **extract-xiso** (`GETOPT_STRING "c:d:Dhlmp:qQrsvx"` at `extract-xiso.c:503`) | **XboxKit** (`Options.cs` + README) | **xdvdfs** (`src/main.rs` `Cmd` enum) |
|---|---|---|---|---|
| `-c <dir> [name]` create | ✅ repeatable, `-X` excludes, `-s` → `**/$SystemUpdate/**`, `XisoWriter.CreateXiso` | ✅ `strdup`+`argv[optind]` peek, `create_xiso` | Not as flag — rebuild mode is positional `<input.xiso> [files...]` | `pack <source> [image]` dir branch (via `StdFilesystem`) |
| `--pack <input> [name]` | ✅ dir→create / file→rewrite alias (`TranslatePackInput` at `Program.cs:1014`) | — | — | `pack` also handles both |
| `-x` extract (default) | ✅ explicit, default when no mode | ✅ `x_seen` guard, default `extract=true` | Extract mode `xboxkit.exe [options] <input.iso>` | `unpack <image> [dest]` |
| `--unpack <file> [dest]` | ✅ auto `IsOptimized`, `--skip-sectors` aware (`RunUnpackMode` at `Program.cs:966`) | — | — | `unpack` (same) |
| `-l` list (top-level) | ✅ `XisoReader.List` | ✅ `k_list` | — | `ls <image> [path]` default `/` |
| `-t` tree (recursive) | ✅ `XisoReader.Tree` | — (plain `extract-xiso` has no tree) | — | `tree <image>` |
| `-i <file> [path]` info | ✅ `GetVolumeInfo` + `ListDirectory` (sector/size/attrs/left/right) | — | — | `info <image> [file]` |
| `--ls <file> [path]` | ✅ `ListDirectoryFlat` | — | — | `ls` (same) |
| `--xex-info <file> <path>` | ✅ `GetXexInfo` (big-endian XEX2 header, media types bitmask) (`XexInfo.cs`) | — | — | — |
| `--md5 <file> [path]` | ✅ `ComputeFileHashes` (MD5) | — | — | `md5 <image> [path]` (`md-5` crate) |
| `--sha256 <file> [path]` | ✅ `ComputeFileHash(SHA256)` | — | — | — (`checksum` is **SHA3-256** deterministic over `BTreeMap<path,data>`, not per-file) |
| `--copy-out <iso> <path> <dest>` | ✅ file or directory recursive | — | `Wrapper.ExtractGamePartition` extracts all (not single arbitrary path) | `copy-out <image> <src> <dest>` |
| `-r` rewrite (optimize) | ✅ `XisoReader.DecodeXiso(..., Rewrite)`, `IsOptimized` check, `.old` dance, `-D` delete | ✅ `rename(argv[i],".old")`, `decode_xiso(buf, k_rewrite, true)` | Partial — `ProcessXISO` wipe/trim path only | `pack` via `XDVDFSFilesystem` (repack) |
| `-V` audit | ✅ `AuditXiso` deep check (header, tag, sector bounds, cycles, reserved `0x48`, dir size) (`XisoReader.cs:1032`) | — | — | — |
| `validate <src> <out>` + `--validate*` | ✅ `XisoValidator.ValidateConversion` (counts/paths/sizes + optional SHA-256), JSON `--validate-report`, exit `2` on fail | — | — | — |
| `--batch <dir>` (+ `--batch-recursive`) | ✅ case-insensitive `*.iso` scan (`ExpandIsoFiles` at `Program.cs:1099`), sorted, works with extract/list/tree/rewrite/audit | — (loop `for(i=optind;i<argc;++i)` handles multiple explicit files only) | — | `checksum [images...]` takes multiple images but no `--batch` scan |
| `-h` / `-v` | ✅ help to stderr / banner `v2.7.1 (01.11.14)` to stdout | ✅ `usage()` macro / `printf banner` | — | `xdvdfs --help` per subcommand (`clap`) |
| `-d <dir>` | ✅ extract/rewrite output dir | ✅ `path` strdup, `mkdir`/`chdir` | (`-o --output` = game files, different meaning) | — (dest is positional) |
| `-D` / `-m` / `-q`/`-Q` / `-s` | ✅ delete old / disable `.xbe` patch / quiet/silent / skip `$SystemUpdate` | ✅ identical | `q --quiet` only | — |
| `-o <filename>` (rewrite custom name) | ✅ `outputName` | — | `-o` means extraction output dir | — |
| `--skip-sectors N` | ✅ non-negative int, forbidden with `-c`, allowed with `-i`/hash/copy/audit not allowed | — | — | — |
| `--prepend-sectors N` | ✅ requires `-c` or `-r` | — | Rebuild rebuilds via `L0`/`L1` tables instead | — |
| `-X <glob>` exclude | ✅ repeatable, `GlobMatcher.cs` (`* ? ** [] [!] \`, `/` sep, anchored `**/`, case-insensitive) | — (`-s` only) | — | Only via `build-image` ordered map rules (`wax` glob `host/**:image/{1}`, `!excluded`, `{n}`) |

### 5 — Library / API surface

| Capability | **XISOSharp** (`XISOSharp.Core`) | **extract-xiso** | **XboxKit (`LibXGD`)** | **xdvdfs (`xdvdfs-core`)** |
|---|---|---|---|---|
| **API style** | Static `XisoReader`/`XisoWriter`/`XisoValidator` + typed `VolumeInfo`/`EntryInfo`/`AuditResult`/`ValidationResult`/`XexInfo`, `CancellationToken`, `IProgress<ProgressInfo>` (`Types.cs`), async `*Async` via `Task.Run` | Single TU globals (`s_total_bytes`, `s_xbox_disc_lseek` etc.), `int verify_xiso(int fd, int32_t*, ...)` / `create_xiso` / `traverse_xiso`, progress `progress_callback(xoff_t cur,xoff_t total)` | `XDVDFS` (`GetValidSectors`, `GetXISORanges`, `ProcessXISO`, `CollectFileEntries`), `XGD` (`GetWave`, `ExtractVideo`, `RebuildRedump`, tables), `Utils`/`XboxPRNG`/`ZArchive` | Traits `BlockDeviceRead`/`BlockDeviceWrite`, `Filesystem<H,FE,HE>` (`StdFilesystem`, `XDVDFSFilesystem`, `RemapOverlayFilesystem`), `VolumeDescriptor`/`DirectoryEntryDiskNode`/`DirectoryEntryTableWriter`/`SectorAllocator`, `maybe-async` |
| **Hashing** | `ComputeFileHash`/`ComputeDirectoryHashes` — **MD5** + **SHA-256** (`System.Security.Cryptography.IncrementalHash`), lowercase hex + two spaces | None | Skeleton hashes **SHA-1** per file via `GetFileEntries` + `hashWriter` hex+path | `md5` subcommand (**MD5** per-file) + `checksum` (**SHA3-256** combined over sorted paths) |
| **Audit / deep validation** | `AuditXiso` (`XisoReader.cs:1032`) — header magic, optimized tag at `31337`, root bounds, cycle `HashSet<long>`, sector overflow, reserved attrs `0x48`, dir-size, filename sep | None (only `s_warned` warning counter) | `Validate()` stub only | Only `InvalidVolume`/`DoesNotExist` errors |
| **Validation / report** | `XisoValidator.ValidateConversion` (missing/extra/size/checksum `SHA-256`, case-insensitive dict, `WriteReport` JSON + `LogResult` at `XisoValidator.cs:254`) | None | None | None |
| **Media-enable `.xbe` patch** | ✅ `BoyerMoore.cs` (`E8 CA FD FF FF 85 C0 7D → EB` at byte 7, overlap `Length-1`, `Logger.MediaEnable` toggle `-m`) (`XisoWriter.cs:543`) | ✅ `boyer_moore_init`/`search`/`done` in `write_file` | None | None |
| **Glob / remapping** | `GlobMatcher` (`* ? ** [] \`), anchored vs `**/`, trailing `/`→`/**` | `-s` substring `"$SystemUpdate"` only | None | `wax` crate via `RemapOverlayFilesystem`, `!negation` + `{n}` substitution + `xdvdfs.toml` (`build-image`/`image-spec from`) |
| **Archival extras** | Only `$SystemUpdate` filtering | Same | ✅ **random filler** (`--random` → `.filler`), **seed** (`--seed` brute-force `XboxPRNG`), **wipe** (`--wipe` zero gaps), **trim** (`--trim` truncate), **petrify/skeleton** (`--petrify` zeroed XISO + `.hash`), **ZArchive** (`--zar` zstd) | ✅ **CISO** `compress` (`ciso` crate, `SectorLinearBlockDevice`, `SplitOutput` parts) |
| **Video ISO (Redump)** | Reconstruction only via `--prepend-sectors` round-trip (documented `redump-workflows.md`); no `L0`/`L1` split | None | ✅ `XGD.ExtractVideo` (`L0` head + `L1` tail) + update extraction (`--update` zeros update in video ISO for XGD3) | None |
| **Progress / cancellation** | `ProgressCallback(long cur,long total)` + structured `IProgress<ProgressInfo>` (`FileCount`/`DirCount`/`DirAdded`/`FileAdded`/`FinishedPacking`) + `CancellationToken` throughout `DecodeXiso`/`CreateXiso` | `progress_callback` param (mostly `nil`) | Console `[INFO]` only | `ProgressInfo::{DiscoveredDirectory/FileCount/DirCount/DirAdded/FileAdded/FinishedPacking}` (+ CISO `SectorCount/Finished`), `maybe-async` but no cancellation token |
| **Batch / multi-image summary** | Per-ISO `"\nN files in <path> total M bytes"` + batch `"\nN files in K xiso's total M bytes"` + `WARNING` banner (`Logger.TotalBytesAllIsos`) | Same counters `s_total_files_all_isos`/`s_total_bytes_all_isos` | Single-image only | `checksum` loops `Vec<String> images` individually |

### 6 — What XISOSharp uniquely adds vs. all three

* **Full CLI parity with `extract-xiso v2.7.1` plus 10 extra modes** (`-t`/`-i`/`--ls`/`--xex-info`/`--md5`/`--sha256`/`-V`/`--copy-out`/`--unpack`/`validate`) — none exist in the C tool.
* **Arbitrary Redump reconstruction** without hard-coded wave tables: `--skip-sectors`/`--prepend-sectors` handle any video-partition size, including hybrid `0x89D80000` (XboxKit) and future layouts.
* **xdvdfs compatibility**: empty-directory `0x0000` sentinel + 12-byte header peek — extract-xiso and XboxKit both miss this.
* **Managed safety & ergonomics**: typed exceptions (`XisoFormatException`/`XisoEmptyException`/`XisoFileTooLargeException`), `CancellationToken`, `IProgress<ProgressInfo>`, `*Async` methods, strong-named trimmable library — none of the other tools expose a consumable .NET/Rust-style library with cancellation and progress.
* **Post-conversion assurance**: `--validate`/`--validate-checksums`/`--validate-strict`/`--validate-report` with JSON and exit-code `2` — absent in all three references.
* **Glob excludes** (`-X`) for creation — absent in C (`-s` only) and XboxKit, and coarser than xdvdfs `build-image` ordered map rules.

### 7 — What the references have that XISOSharp intentionally does **not**

| Feature | Where it lives | Why XISOSharp omits it |
|---|---|---|
| **Full Redump video/filler/seed/skeleton/ZAR pipeline** (16 `L0`/`L1` wave tables, `REDUMP_ISO_LENGTH[9]`, PVD wave inference at `0x832D`, `XISO_LENGTH[4]`, `GetValidSectors`/`ProcessXISO` filler ranges, `XboxPRNG` seed extraction, `RebuildRedump` with `l0Padding`/`l1Padding` and `prng.WriteSectors`) | XboxKit `LibXGD/XGD.cs:11` + `LibXGD/XDVDFS.cs:ProcessXISO` | Out of scope — XISOSharp is a **game-partition (XDVDFS)** tool. Use XboxKit for lossless archival collections; use XISOSharp for XISO create/extract/validate workflows. Redump round-trip is via explicit `--skip-sectors`/`--prepend-sectors`. |
| **`build-image` path remapping** (`xdvdfs.toml` `[map_rules]`, ordered `host/**:image/{1}` with `{0}`/`{1}` captures, `!excluded`, `--dry-run`, `image-spec from`) | xdvdfs `xdvdfs-cli/src/cmd_build_image.rs` + `xdvdfs-core/src/write/fs.rs:RemapOverlayFilesystem` | Out of scope — XISOSharp creation is **1:1** directory ↔ image. Use xdvdfs for arbitrary host→image remapping and CISO. |
| **CISO (`.cso`) compression** (`ciso` crate, `SectorLinearBlockDevice` → `CisoSectorInput` → `SplitOutput`) | xdvdfs `compress` (`cmd_compress.rs`) | Not implemented — use xdvdfs `compress` for CISO. |
| **ZArchive/ZAR** (zstd game-file archive, skeleton SHA-1 hashes) | XboxKit `--zar`/`--petrify` (`LibXGD/ZArchive.cs`) | Not implemented — use XboxKit for ZAR. |
| **FTP / Darwin burn / hybrid `0x89D80000` built-in** | extract-xiso historical `generate_avl_tree_remote` / Darwin `#if __DARWIN__` (removed in XboxDev fork) | Not ported — network/burn paths were already absent in the XboxDev `v2.7.1` baseline. |
| **Web / desktop front-ends** | xdvdfs `xdvdfs-web` (WASM) + `xdvdfs-desktop` (Tauri) | Out of scope — XISOSharp ships a WPF batch tester (`XISOSharpTester/`) for regression vs. the C tool instead. |

> **Rule of thumb:** for *playable* XISO workflows (create, extract, list, tree, rewrite, info, copy-out, hash, audit, validate) use **XISOSharp**; for *archival* lossless Redump ↔ XISO/ZAR with filler/seed/video/skeleton use **XboxKit**; for *embedded* no_std, WASM, or CISO/`build-image` remapping use **xdvdfs**; for the *historical* single-file C baseline see **extract-xiso**.

## Requirements

- .NET 8 SDK or later

## License

MIT
