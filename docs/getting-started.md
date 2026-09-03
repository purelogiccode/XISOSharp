# Getting Started

This page gets you from zero to your first XISO extract and creation, using either the
command-line tool or the .NET library.

## Requirements

| Component | Requirement |
|---|---|
| OS | Windows, Linux, or macOS |
| .NET SDK (build from source) | 10.0.301 or newer (see [`global.json`](../global.json)) |
| .NET runtime (run prebuilt binaries) | .NET 8, .NET 9, or .NET 10 |
| Disk space | At least 2× the ISO size for extraction, plus workspace for creation |

> [!NOTE]
> The library targets `net8.0`, `net9.0`, and `net10.0`. The CLI targets `net10.0`
> but can be published as a **self-contained single-file** binary for any platform,
> requiring no installed runtime — see [Building](building.md#publishing).

## Option 1 — The command-line tool

### Build or download

```bash
git clone https://github.com/purelogiccode/XISOSharp.git
cd XISOSharp

# Framework-dependent build (requires the .NET 10 SDK)
dotnet build XISOSharp.Cli -c Release

# Or a self-contained single-file binary for your platform (no runtime needed)
dotnet publish XISOSharp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# Supported RIDs: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 (win-x86 also builds).
# Or publish all six at once: ./publish-cli.ps1  (binaries land in publish/<rid>/)
```

The executable is named **`XISOSharp.Cli`** (Windows: `XISOSharp.Cli.exe`) and lives in
`XISOSharp.Cli/bin/Release/net10.0/`.

### First extraction

```bash
XISOSharp.Cli -d ./extracted game.iso
```

- `-d ./extracted` — output directory (created if missing)
- `game.iso` — any XISO image; GLOBAL/XGD2/XGD3/XGD1 formats are detected automatically

```text
extract-xiso v2.7.1 (01.11.14) for win - written by in <in@fishtank.com>

extracting game.iso:

creating dir1\ (0 bytes) [OK]
...
2 files in game.iso total 5012 bytes
```

### First creation

```bash
XISOSharp.Cli -c ./game_files
```

Creates `game_files.iso` in the current directory from the contents of `./game_files`.
Optionally pass an output name:

```bash
XISOSharp.Cli -c ./game_files my_game.iso          # written to ./my_game.iso
XISOSharp.Cli -c ./game_files ./out/my_game.iso     # name may include a directory
```

### First listing

```bash
XISOSharp.Cli -l game.iso
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

int result = XisoReader.Extract("game.iso", "output_directory", llCompat: false);
if (result == 0)
    Console.WriteLine("Extraction succeeded");
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

### Archival (Redump lossless)

```bash
# Video filler seed wipe trim petrify update zar — XboxKit parity
XISOSharp.Cli --video game.redump.iso              # L0 head + L1 tail via XgdTables
XISOSharp.Cli --random game.iso                    # filler gaps via GetXisoRanges/MergeRanges
XISOSharp.Cli --seed game.iso                      # XGD1 PRNG brute-force (XboxPrng)
XISOSharp.Cli --wipe game.iso -o wiped.iso
XISOSharp.Cli --trim game.iso -o trimmed.iso
XISOSharp.Cli --petrify game.iso                   # skeleton + .hash SHA-1
XISOSharp.Cli --update game.redump.iso             # XGD3 su20076000_00000000
XISOSharp.Cli --zar game.iso -o game.zar           # zstd
XISOSharp.Cli --all game.redump.iso                # all of the above + --video/--wipe

# Lossless rebuild
XISOSharp.Cli rebuild game.xiso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso --security-sectors sectors.txt
XISOSharp.Cli validate game.redump.iso rebuilt.redump.iso --validate-checksums
```

See [Archival Workflows](archival.md).

### Build-Image & CISO & Checksum (xdvdfs parity)

```bash
# Ordered remapping with wax captures + negation + xdvdfs.toml
XISOSharp.Cli build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp.Cli build-image -D -m "!secret/**" -m "**:/{0}" ./src   # --dry-run
XISOSharp.Cli image-spec from -O dist/image.iso -m "bin:/" xdvdfs.toml

# CISO compress/decompress (DEFLATE v1 + LZ4 v2, align 0/1/2)
XISOSharp.Cli compress ./game_dir game.cso --ciso-level 9
XISOSharp.Cli decompress game.cso game.iso

# Deterministic SHA3-256 image checksum (BTreeMap sorted, xdvdfs compat)
XISOSharp.Cli checksum game.iso
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

> **Docs site:** open `docs/index.html` (left sidebar via `_sidebar.md`) or `wiki/Home.md` on GitHub Wiki — both share the same menu.
