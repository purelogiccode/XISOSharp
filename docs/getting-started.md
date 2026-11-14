# Getting Started

This page gets you from zero to your first XISO extract and creation, using either the
command-line tool or the .NET library.

## Requirements

| Component | Requirement |
|---|---|
| OS | Windows, Linux, or macOS |
| .NET SDK (build from source) | 11.0.100 or newer (see [`global.json`](../global.json)) |
| .NET runtime (run prebuilt binaries) | .NET 11 (Desktop Runtime for the GUI) |
| Disk space | At least 2× the ISO size for extraction, plus workspace for creation |

> [!NOTE]
> The library and CLI target `net8.0`, `net9.0`, `net10.0` and `net11.0` with
> C# 15; the GUI and internal tools target `net11.0`. The CLI can be published
> as a **self-contained single-file** binary for any platform, requiring no
> installed runtime - see [Building](building.md#publishing).

## Option 1 — The command-line tool

### Build or download

```bash
git clone https://github.com/purelogiccode/XISOSharp.git
cd XISOSharp

# Framework-dependent build (requires the .NET 11 SDK)
dotnet build XISOSharp.Cli -c Release

# Or a self-contained single-file binary for your platform (no runtime needed)
dotnet publish XISOSharp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# Supported RIDs: win-x86, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
# Or publish all seven at once: ./publish-cli.ps1  (binaries land in publish/<rid>/)
```

A framework-dependent `dotnet build` produces `XISOSharp.Cli(.exe)` in
`XISOSharp.Cli/bin/Release/net11.0/`; a self-contained `dotnet publish` (or
`./publish-cli.ps1`, whose output lands in `publish/<rid>/`) ships the same tool
renamed to **`XISOSharp`** (Windows: `XISOSharp.exe`).

> [!TIP]
> Double-clicking `XISOSharp.exe` in Explorer prints the usage text and keeps the
> window open until you press a key. The pause only happens on an interactive
> no-argument launch — scripts and pipes are never blocked (`XISO_NO_PAUSE=1`
> disables it explicitly).

### Prebuilt bundles

Every release publishes six framework-dependent bundles: Windows
`release_<version>_<rid>.zip` (`win-x64`, `win-arm64`), Linux
`release_<version>_<rid>.tar.gz` (`linux-x64`, `linux-arm64`) and macOS
`release_<version>_<rid>.dmg` (`MacOsX-x64`, `MacOsX-arm64`). Extract the
archive or mount the disk image, then run `XISOSharp` (`XISOSharp.exe` on
Windows) or `XISOSharp.Gui`. The bundles need the **.NET 11 runtime** (Desktop
Runtime for the GUI); nothing else is bundled.

### Package managers and container

The repository ships generated winget, Scoop and Homebrew manifests under
[`packaging/`](../packaging) plus a container image:

```powershell
# winget: install from the checked-in manifest (requires LocalManifestFiles)
winget settings --enable LocalManifestFiles
winget install --manifest .\packaging\winget\manifests\p\purelogiccode\XISOSharp\2.0.1

# Scoop: the repository root is a bucket
scoop bucket add xisosharp https://github.com/purelogiccode/XISOSharp
scoop install xisosharp/xisosharp
```

```bash
# Homebrew cask (local file; a tap can host packaging/homebrew/xisosharp.rb)
brew install --cask ./packaging/homebrew/xisosharp.rb

# Container image
docker run --rm -v "$PWD:/work" -w /work ghcr.io/purelogiccode/xisosharp:latest -v
```

`scripts/update-packaging.ps1 -Version <version>` refreshes the manifests and
their SHA-256 values from a published GitHub release. See
[Shell Completions & Man Page](shell-completions.md) for the tab-completion
scripts and `xisosharp(1)`.

### First extraction

```bash
XISOSharp -d ./extracted game.iso
```

- `-d ./extracted` — output directory (created if missing)
- `game.iso` — any XISO image; GLOBAL/XGD2/XGD3/Hybrid/XGD1 and rebuilt sector-0 formats are detected automatically

```text
XISOSharp v1.4.4 for win - https://github.com/purelogiccode/XISOSharp

extracting game.iso:

creating dir1\ (0 bytes) [OK]
...
2 files in game.iso total 5012 bytes
```

If the run is interrupted (Ctrl+C, power loss), re-run it with `--skip-existing`
to resume instead of starting over — files already on disk with matching sizes are
skipped (`skip: <path>`), only the missing ones are written:

```bash
XISOSharp --skip-existing -d ./extracted game.iso
```

### First creation

```bash
XISOSharp -c ./game_files
```

Creates `game_files.iso` in the current directory from the contents of `./game_files`.
Optionally pass an output name:

```bash
XISOSharp -c ./game_files my_game.iso          # written to ./my_game.iso
XISOSharp -c ./game_files ./out/my_game.iso     # name may include a directory
```

A current-directory source defaults its output next to the source instead, so it
never lands inside the directory being packed:

```bash
cd ./game_files
XISOSharp -c .                                  # ../game_files.iso
```

### First listing

```bash
XISOSharp -l game.iso
```

Lists the top-level entries. Use `-t` for a recursive listing with sizes, or see the
[CLI Reference](cli.md) for all modes.

## Option 2 — The .NET library

The core engine is published to NuGet as the **`XISOSharp`** package.

### Install

```bash
dotnet add package XISOSharp
```

or via the NuGet Package Manager in Visual Studio.

### Extract

```csharp
using XISOSharp;
using XISOSharp.Models; // UnpackOptions

int result = XisoReader.Extract("game.iso", "output_directory", llCompat: false);
if (result == 0)
    Console.WriteLine("Extraction succeeded");

// Resume an interrupted run: skip files already on disk with matching sizes
int resumed = XisoReader.UnpackImage("game.iso", "output_directory",
    options: new UnpackOptions { SkipExisting = true });

// Copy independent files with four workers (output is byte-identical)
int parallel = XisoReader.UnpackImage("game.iso", "output_directory",
    options: new UnpackOptions { MaxDegreeOfParallelism = 4 });
```

### Create

```csharp
int result = XisoWriter.CreateXiso(
    rootDirectory: "source_directory",
    outputDirectory: "output_directory",
    inRoot: null,            // null = build the tree from the file system
    sourceStream: null,      // only used in rewrite mode
    out string? outIsoPath,
    inName: "game.iso",
    progressCallback: null);
```

### List

```csharp
int result = XisoReader.List("game.iso", llCompat: false);
```

> [!TIP]
> `llCompat` controls the directory right-offset calculation. Pass `true` for images
> that were **not** written by extract-xiso-family tools (non-optimized layout); pass
> `false` for optimized images. The CLI decides automatically by probing the
> optimized-tag marker — see [XISO Format](xiso-format.md#optimized-tag).

### Virtual filesystem (VFS mounter / Dokan)

Mounting an image as a drive letter calls the same paths over and over, so use the
keep-open explorer: one held image stream, serialized operations, and bounded
per-file read streams that never extract to disk.

```csharp
using XISOSharp;
using XISOSharp.Models; // VolumeInfo

using var explorer = new XisoExplorer(isoPath,
    new XisoExplorerOptions { KeepOpen = true, Share = FileShare.ReadWrite });
VolumeInfo vol = explorer.Volume;                       // CreationTime, DescriptorSector, DiscFormat
ExplorerNode? node = explorer.GetNode("/sub/file.bin"); // size, sector, attrs, is-dir
using Stream data = explorer.OpenReadStream(node);      // .cso-aware, bounded to node.Size
int read = data.Read(buffer);                           // Dokan ReadFile hot path
FileAttributes attrs = XisoAttributes.ToWindowsFileAttributes(node.Attributes);
```

### Archival (Redump lossless)

```bash
# Video filler seed wipe trim petrify update zar — XboxKit parity
XISOSharp --video game.redump.iso              # L0 head + L1 tail via XgdTables
XISOSharp --random game.iso                    # filler gaps via GetXisoRanges/MergeRanges
XISOSharp --seed game.iso                      # XGD1 PRNG brute-force (XboxPrng)
XISOSharp --wipe -o wiped.iso game.iso
XISOSharp --trim -o trimmed.iso game.iso
XISOSharp --petrify game.iso                   # skeleton + .hash SHA-1
XISOSharp --update game.redump.iso             # XGD3 su20076000_00000000
XISOSharp --zar -o game.zar game.iso           # zstd
XISOSharp --zar --jobs 4 --policy auto-rename game1.iso game2.iso  # parallel + skip|overwrite|auto-rename
XISOSharp --all game.redump.iso                # all of the above + --video/--wipe

# Lossless rebuild (security sectors are rebuild-only)
XISOSharp rebuild game.xiso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso --security-sectors sectors.txt
XISOSharp validate --validate-checksums game.redump.iso rebuilt.redump.iso
```

See [Archival Workflows](archival.md).

### Build-Image & CISO & Checksum (xdvdfs parity)

```bash
# Ordered remapping with wax captures + negation + xdvdfs.toml
XISOSharp build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp build-image -D -m "!secret/**" -m "**:/{0}" ./src   # --dry-run
XISOSharp image-spec from -O dist/image.iso -m "bin:/" xdvdfs.toml

# CISO compress/decompress (DEFLATE v1 + LZ4 v2, align 0/1/2)
XISOSharp compress ./game_dir game.cso --ciso-level 9 --ciso-version 2 --ciso-split 0
XISOSharp decompress game.cso game.iso   # uncso/decso aliases; split .1.cso input accepted

# GOD/SVOD read-only input (header file or its .data folder)
XISOSharp -l "4D5307E6"                   # list/extract/checksum/hash/validate
XISOSharp decompress "4D5307E6" game.iso  # title-named ISO by default
XISOSharp convert "4D5307E6" game.cci     # or .cso

# XBLA/XBLIG STFS package (single-file; read-only)
XISOSharp -l game.stfs                    # list the STFS file table
XISOSharp -x -d game game.stfs            # extract every package file
XISOSharp checksum game.stfs              # same tree hash as an equivalent ISO/ZAR

# Deterministic SHA3-256 image checksum (BTreeMap sorted, xdvdfs compat)
XISOSharp checksum game.iso
```

See [Build-Image](xdvdfs-compat.md#build-image) · [Compression](compression.md) · [Checksums](xdvdfs-compat.md#checksum).

## Next steps

- [CLI Reference](cli.md) — every flag and mode (archival + xdvdfs verbs)
- [Library Overview](library.md) — architecture and API highlights (`IBlockDevice`, `XisoChecksum`, `RemapFilesystem`)
- [Redump & Disc Layouts](redump-workflows.md) — XGD offsets (incl. hybrid `0x89D80000`), video partitions
- [Archival Workflows](archival.md) — video/filler/seed/wipe/trim/petrify/update/ZAR/rebuild
- [xdvdfs Compat](xdvdfs-compat.md) — build-image, CISO, BlockDevice, checksum
- [XISO Format](xiso-format.md) — empty-dir `0x0000` sentinel + reserved bits
- [FAQ](faq.md) — common questions

> **Docs site:** open `docs/index.html` (left sidebar via `_sidebar.md`).
