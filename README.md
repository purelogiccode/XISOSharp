# XISOSharp

[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](global.json)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/XISOSharp.svg)](https://www.nuget.org/packages/XISOSharp/)

A **pure C#** port of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1 for Xbox ISO (XISO / XDVDFS) images — **byte-identical** output, no native dependencies, no P/Invoke, just .NET. Beyond the C baseline it merges the archival power of [XboxKit 0.7](References/XboxKit-0.7/) and the modern packing of [xdvdfs 0.8.3](References/xdvdfs-0.8.3/) into one trimmable, AOT-compatible library + CLI.

## Projects

| Project | Description |
|---|---|
| [XISOSharp.Core](XISOSharp/) | Core library (`NuGet: XISOSharp`) — full read/write engine, `net8.0`/`net9.0`/`net10.0`, strong-named |
| [XISOSharp.Cli](XISOSharp.Cli/) | CLI `XISOSharp.Cli` (`net10.0`, `AssemblyName XISOSharp.Cli`) — extract-xiso-compatible flags + 20 extra modes |
| [XISOSharp.Tests](XISOSharp.Tests/) | xUnit suite (675 tests) — golden fixtures + `MemoryBlockDevice` |
| [XISOSharp.Benchmarks](XISOSharp.Benchmarks/) | BenchmarkDotNet (AVL, Boyer-Moore, sector math) |
| [XISOSharpTester](XISOSharpTester/) | WPF GUI — batch regression vs `extract-xiso.exe` |
| [XISOSharp.BattleTests](XISOSharp.BattleTests/) | Battle harness vs `References/extract-xiso-build-202505152050/extract-xiso.c` |

## Documentation

Full docs live in [`docs/`](docs/README.md) — also served as a **Docsify site with a left sidebar** at [`docs/index.html`](docs/index.html) (GitHub Pages) and mirrored in [`wiki/`](wiki/Home.md):

- [Getting Started](docs/getting-started.md) — install, first extract/create/list
- [CLI Reference](docs/cli.md) — every flag, verb, exit code
- [Archival Workflows](docs/archival.md) — `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`rebuild`
- [Build-Image & Image-Spec](docs/xdvdfs-compat.md#build-image) — ordered `wax` remapping, `xdvdfs.toml`
- [Compression (CISO)](docs/compression.md) — `compress`/`decompress`, `CisoBlockDevice`
- [Checksums](docs/xdvdfs-compat.md#checksum) — SHA3-256 `checksum` vs MD5/SHA-256
- [XISO Format](docs/xiso-format.md) — header, dirtab, AVL, ECMA-119, `0x0000` sentinel
- [Redump & Disc Layouts](docs/redump-workflows.md) — XGD offsets incl. hybrid `0x89D80000`
- [Library Overview](docs/library.md) · [XisoReader](docs/api-xisoreader.md) · [XisoWriter](docs/api-xisowriter.md) · [Utilities](docs/api-utilities.md)

> **Left menu:** open `docs/index.html` locally or via Pages — [`docs/_sidebar.md`](docs/_sidebar.md) (mirrored as [`wiki/_Sidebar.md`](wiki/_Sidebar.md)) is the sidebar.

## Install

### NuGet (library)

```bash
dotnet add package XISOSharp
# or
Install-Package XISOSharp
```

Package targets `net8.0`, `net9.0`, `net10.0`, zero runtime dependencies (BCL only), strong-named, `IsTrimmable`+`IsAotCompatible`, `snupkg` via SourceLink.

### CLI (tool)

```bash
git clone https://github.com/purelogiccode/XISOSharp.git
cd XISOSharp

# framework-dependent (needs .NET 10 SDK, pinned in global.json)
dotnet build XISOSharp.Cli -c Release
# bin: XISOSharp.Cli/bin/Release/net10.0/XISOSharp.Cli(.exe)

# self-contained trimmed single-file (no runtime needed) for all six RIDs
./publish-cli.ps1
# binaries land in publish/<rid>/XISOSharp.Cli(.exe), ~14 MB each

# or one RID manually (single-file + trimmed come from the csproj defaults)
dotnet publish XISOSharp.Cli -c Release -r linux-x64 --self-contained
# RIDs: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 (win-x86 also builds)
```

### GUI (desktop app, Avalonia, dark-only)

```bash
# self-contained single-file (no runtime needed) for all six RIDs
./publish-gui.ps1
# binaries land in publish-gui/<rid>/XISOSharp.Gui(.exe), ~80 MB each
```

`XISOSharp.Gui` is a dark-theme front-end that drives the `XISOSharp` CLI as a child
process (extract/create/rewrite/rebuild/compress/decompress/validate/batch plus
list/tree/info/unpack/copy-out/checksum, live log, cancel, overwrite `-y`/`-n`
switch). It finds the CLI next to itself, on `PATH`, or via the Settings tab
(persisted to `%AppData%/XISOSharp/gui-settings.json`). Headless helpers:
`XISOSharp.Gui --probe-cli [path]` and `XISOSharp.Gui --self-test [cliPath]`.

## Using the CLI

The CLI binary is `XISOSharp.Cli(.exe)`. It is `extract-xiso`-compatible (`-c`/`-x`/`-l`/`-r`/`-d`/`-D`/`-m`/`-q`/`-Q`/`-s`/`-X`/`-h`/`-v`) plus XboxKit + xdvdfs verbs. Flags must precede positionals; `-h`/`-v` exit 0. `-v` still prints the `extract-xiso v2.7.1` baseline banner for compatibility.

### Basics

```bash
# Extract (auto-detects RAW/GLOBAL/XGD2/XGD3/Hybrid/XGD1)
XISOSharp.Cli -d ./out game.iso
XISOSharp.Cli --unpack game.iso              # auto-named ./game/
XISOSharp.Cli --unpack game.iso ./out

# List / tree / info / audit
XISOSharp.Cli -l game.iso
XISOSharp.Cli -t game.iso                     # recursive with sizes
XISOSharp.Cli -i game.iso /                  # volume + dir entries
XISOSharp.Cli --ls game.iso /media           # flat directory
XISOSharp.Cli -V game.iso game2.iso         # deep audit (header/tag/cycles/bounds/0x48)

# Create / pack / rewrite
XISOSharp.Cli -c ./game_files                # -> ./game_files.iso
XISOSharp.Cli -c ./game_files custom.iso
XISOSharp.Cli -c ./src ./out.iso -s -X "**/*.tmp" -X "**/node_modules/**"
XISOSharp.Cli --pack ./game_files            # dir → create
XISOSharp.Cli --pack game.iso                # iso → rewrite (keeps .old)
XISOSharp.Cli -r game.iso                    # rewrite optimized (skips if already in!xiso)
XISOSharp.Cli -r -D game.iso                 # + delete .old

# Copy-out / hash / XEX / batch
XISOSharp.Cli --copy-out game.iso /media ./media_out
XISOSharp.Cli --md5 game.iso                 # or --sha256
XISOSharp.Cli --xex-info game360.iso /default.xex
XISOSharp.Cli --batch ./isos -d ./out        # all *.iso sorted
XISOSharp.Cli --batch ./isos --batch-recursive -r
```

### Redump & disc offsets

```bash
# Video partition precedes game partition — auto-probed, or explicit
XISOSharp.Cli --skip-sectors 129824 -d ./out redump.iso     # GLOBAL/XGD2
XISOSharp.Cli -c ./files redump.iso --prepend-sectors 16640 # XGD3
XISOSharp.Cli -c ./files hybrid.iso --prepend-sectors 283392 # Hybrid 0x89D80000
XISOSharp.Cli -r --skip-sectors 283392 game.iso             # rewrite offset image to bare

# Validate lossless round-trip
XISOSharp.Cli validate game.redump.iso rebuilt.iso --validate-checksums
XISOSharp.Cli -r --validate --validate-strict --validate-report report.json game.iso
```

### Archival (Redump lossless, XboxKit parity)

```bash
# Extract components
XISOSharp.Cli --video game.redump.iso                  # -> game.video.iso (L0 head + L1 tail)
XISOSharp.Cli --random game.iso                        # -> game.filler (gap bytes)
XISOSharp.Cli --seed game.iso                          # -> game.seed (XGD1 PRNG brute-force, 4-byte LE)
XISOSharp.Cli --wipe game.iso -o wiped.iso             # zero filler gaps
XISOSharp.Cli --trim game.iso -o trimmed.iso           # truncate after last extent
XISOSharp.Cli --petrify game.iso                       # -> skeleton.iso + .hash (SHA-1 per file)
XISOSharp.Cli --update game.redump.iso                 # XGD3 -> su20076000_00000000 (+ zeroes it in video)
XISOSharp.Cli --zar game.iso -o game.zar               # ZArchive/zstd

# Aliases (mirrors xboxkit -a/-b/-c)
XISOSharp.Cli --all game.redump.iso                    # --random --seed --trim --update --video --wipe
XISOSharp.Cli --best game.redump.iso                   # --trim --wipe
XISOSharp.Cli --compress game.iso                      # --petrify --update --video --zar

# Security sectors (4096-sector ranges)
XISOSharp.Cli --video --security-sectors sectors.txt game.redump.iso
XISOSharp.Cli rebuild --security-sectors sectors.txt -o rebuilt.iso # or:

# Rebuild lossless Redump from components
XISOSharp.Cli rebuild game.xiso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso
XISOSharp.Cli rebuild game.xiso video.iso seed.bin -o rebuilt.redump.iso          # XGD1 seed variant
XISOSharp.Cli rebuild game.xiso video.iso --security-sectors sectors.txt -o rebuilt.redump.iso
```

### Packing & compression (xdvdfs parity)

```bash
# Ordered remapping (wax captures, ! negation, xdvdfs.toml, --dry-run)
XISOSharp.Cli build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp.Cli build-image -D -m "!secret/**" -m "**:/{0}" ./src      # dry-run
XISOSharp.Cli build-image -f xdvdfs.toml ./src -O out.iso

# TOML generation
XISOSharp.Cli image-spec from -O dist/image.iso -m "bin:/" -m "assets:/{0}" xdvdfs.toml
# -> stdout if specPath omitted

# CISO (v2 LZ4 default, byte-identical to modern xdvdfs compress; v1 DEFLATE via --ciso-version 1)
XISOSharp.Cli compress ./game_dir game.cso --ciso-level 9       # 0=store; 1..9 = LZ4 acceleration 10-level
XISOSharp.Cli cso game.iso game.cso --ciso-split 0              # single .cso (default splits at ~4 GiB)
XISOSharp.Cli decompress game.1.cso game.iso                    # also reads split .1.cso/.2.cso parts
XISOSharp.Cli uncso game.cso                                    # decso alias

# Deterministic image checksum (SHA3-256 over sorted BTreeMap, xdvdfs compat)
XISOSharp.Cli checksum game.iso
XISOSharp.Cli checksum --silent game1.iso game2.iso            # hex only, multiple images
XISOSharp.Cli --checksum game.iso --silent                     # flag form
```

Exit codes: `0` success/`-v`/`-h`/`validate` pass, `1` usage/I/O, `2` validation failure (`--validate-strict`).

## Using the Library

All in `XISOSharp` namespace (`XISOSharp.Core`). Static `XisoReader`/`XisoWriter` plus archival types (`XisoRedump`, `XisoOperations`, `XisoRanges`, `XisoSkeleton`, `XisoZarchive`, `XgdTables`, `XboxPrng`, `SecuritySectors`), xdvdfs types (`WaxGlob`, `RemapFilesystem`, `XisoChecksum`, `CisoWriter`/`CisoReader`, `BlockDevice/*`), typed records (`VolumeInfo`, `EntryInfo`, `AuditResult`, `ValidationResult`, `XexInfo`, `ProgressInfo`), `CancellationToken` + `IProgress<ProgressInfo>` + `*Async` everywhere.

### Extract / list / info

```csharp
using XISOSharp;
using XISOSharp.DataStructures; // AvlNode, etc.

// Extract (llCompat auto via tag; pass false for optimized, true for legacy)
int rc = XisoReader.Extract("game.iso", "./out", llCompat: false);
int rc2 = XisoReader.UnpackImage("game.iso", "./out"); // auto IsOptimized, skipSectors aware

// List / tree / directory
XisoReader.List("game.iso", llCompat: false);
XisoReader.Tree("game.iso", llCompat: false);
IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory("game.iso", "/");
IReadOnlyList<string> names = XisoReader.ListDirectoryFlat("game.iso", "/media");
EntryInfo? e = XisoReader.GetEntryInfo("game.iso", "/default.xbe");

// Volume & copy-out
VolumeInfo vol = XisoReader.GetVolumeInfo("game.iso"); // IsValid, RootDirSector/Size, DiscLseek, FileLength
XisoReader.CopyOut("game.iso", "/media", "./media_out");

// Hash / audit / validate
byte[]? md5 = XisoReader.ComputeFileHash("game.iso", "/default.xbe", System.Security.Cryptography.HashAlgorithmName.MD5);
var hashes = XisoReader.ComputeDirectoryHashes("game.iso", "/", System.Security.Cryptography.HashAlgorithmName.SHA256);
AuditResult audit = XisoReader.AuditXiso("game.iso"); // header/tag/cycles/bounds/0x48/0x0000
ValidationResult vr = XisoValidator.ValidateConversion("src.iso", "out.iso", verifyChecksums: true);
XisoValidator.LogResult(vr, "src.iso", "out.iso");
XisoValidator.WriteReport(vr, "src.iso", "out.iso", "report.json");

// XEX2 (Xbox 360)
XexInfo? xex = XisoReader.GetXexInfo("game360.iso", "/default.xex");
Console.WriteLine($"{xex?.TitleId:X8} entry 0x{xex?.EntryPoint:X8} region {xex?.Region}");
```

### Create / rewrite

```csharp
// Simple create (1:1 directory → ISO) — convenience
int rc = XisoWriter.PackFromDirectory("source_dir", "out/game.iso",
    excludePatterns: ["**/*.tmp", "**/node_modules/**"],
    progressCallback: (cur, total) => Console.Write($"\r{cur}/{total}"),
    progress: myProgress); // IProgress<ProgressInfo> FileCount/DirCount/DirAdded/FileAdded/FinishedPacking

// Full control (mirrors extract-xiso.c three-pass layout)
int rc2 = XisoWriter.CreateXiso(
    rootDirectory: "source_dir", outputDirectory: "./out", inRoot: null, sourceStream: null,
    out string? outIsoPath, inName: "game.iso", progressCallback: null,
    prependSectors: 129824, // GLOBAL/XGD2
    excludePatterns: ["**/$SystemUpdate/**"],
    cancellationToken: ct);

// Rewrite optimized (AVL) — or PackFromDirectory for iso input
int rw = XisoReader.Rewrite("game.iso", outputDirectory: "./out", deleteOriginal: false, outputName: "game.opt.iso");
var (res, outPath) = await XisoWriter.CreateXisoAsync("source_dir", "./out", null, null, "game.iso", null, ct);
var (res2, out2) = await XisoReader.DecodeXisoAsync("game.iso", "./out", ExtractMode.Rewrite, llCompat: false, ct);
```

### Redump archival

```csharp
using XISOSharp;

// Video (L0 head + L1 tail via XgdTables VIDEO_L*_LENGTH, PVD 0x832D)
bool ok = XisoRedump.TryExtractVideo("game.redump.iso", "game.video.iso", out var videoPath);

// Filler gaps via ranges (sys + file extents)
byte[] filler = XisoOperations.ExtractFiller("game.iso");           // GapBytes = xisoLength - MergeRanges(sys,file)
uint? seed = XisoOperations.ExtractSeed("game.iso");                // XGD1 only, XboxPrng brutal 4-byte LE
bool hasSeed = XisoOperations.TryExtractSeed("game.iso", out uint seedVal);

// Wipe / trim / petrify
int wiped = XisoOperations.WipeFiller("game.iso", "wiped.iso");     // zero filler extents
int trimmed = XisoOperations.TrimXiso("game.iso", "trimmed.iso");   // (last.End+1)*2048
int petr = XisoSkeleton.Petrify("game.iso", "skeleton.iso", "hash.txt"); // zeroed + SHA-1 hex lines

// System update (XGD3 tail scan ABCDABCD)
bool upd = XisoRedump.TryExtractUpdate("game.redump.iso", "su20076000_00000000", "game.video.iso");

// Security sectors (4096-aligned ranges)
int[] sectors = SecuritySectors.Parse("sectors.txt"); // validates 4095 length, sorted

// Rebuild lossless Redump (L0+l0Padding+game+l1Padding+L1, PRNG or filler file)
int rebuilt = XisoRedump.RebuildRedump(
    xisoPath: "game.xiso", videoPath: "game.video.iso",
    fillerOrSeedPath: "filler.bin", // or seed.bin for XGD1
    updatePath: "su20076000_00000000",
    outputRedumpPath: "rebuilt.redump.iso",
    securitySectors: sectors, progress: myProgress, ct: ct);

// Ranges (XboxKit GetValidSectors / GetXISORanges parity)
var (sys, file) = XisoRanges.GetXisoRanges("game.iso", isoOffset: 0);
var merged = XisoRanges.MergeRanges(sys, file);
var files = XisoRanges.CollectFileEntries(File.OpenRead("game.iso"), isoOffset: 0); // sorted by Offset

// ZAR (zstd)
int zar = XisoZarchive.CreateZar("game.iso", "game.zar");

// Tables & PRNG
int videoType = XgdTables.GetVideoType("game.redump.iso"); // via PVD 0x832D → WAVE_PVD
int isoType = XgdTables.GetRedumpIsoTypeBySize(new FileInfo("game.redump.iso").Length);
var prng = new XboxPrng(seedVal); prng.SimulateSectors(100); prng.WriteSectors(stream, 100);
```

### Packing, CISO & checksums (xdvdfs parity)

```csharp
// Build-image ordered remapping (WaxGlob *,**,?,[],{a,b} + {0}/{n} captures, ! negation)
var rules = new List<RemapRule>
{
    new("bin", "/"),
    new("assets/**", "/assets/{1}"), // {1} = first ** capture
    new("!secret/**", ""),            // exclusion (IsExclusion)
};
var preview = RemapFilesystem.DryRunRemap("./src", rules); // HostPath→ImagePath without writing
int built = RemapFilesystem.BuildImage("./src", "out.iso", rules, progress: myProgress);
string toml = RemapFilesystem.GenerateSpecText(rules, "dist/out.iso");
RemapFilesystem.WriteSpec("xdvdfs.toml", rules, "dist/out.iso");
var loaded = RemapFilesystem.ParseSpecFile("xdvdfs.toml"); // preserve-order [map_rules]

// CISO (pure-managed: v2 LZ4 default byte-identical to xdvdfs (lz4_flex port), v1 DEFLATE, threshold +12)
int cso = CisoWriter.CompressToCso("game.iso", "game.cso", level: 9, splitBytes: CisoWriter.DefaultSplitPoint);
int iso = CisoReader.DecompressToIso("game.1.cso", "rebuilt.iso"); // split .N.cso input supported
bool isCso = CisoReader.IsCso("game.cso"); // magic CISO + blockSize 2048 + ver 1/2

// BlockDevice — in-memory golden fixtures without temp files (no_std parity)
using var mem = new MemoryBlockDevice(File.ReadAllBytes("game.iso"));
var (rootSector, rootSize, discLseek) = XisoReader.VerifyXiso(mem, "game.iso");
AuditResult a2 = XisoReader.AuditXiso(mem);
using var cisoDev = new CisoBlockDevice(new FileBlockDevice(File.OpenRead("game.cso"))); // single-sector cache
var files2 = XisoReader.ListDirectory(cisoDev, "/");

// Deterministic image checksum (SHA3-256, SortedDictionary Ordinal, /path UTF-8 + streamed data — xdvdfs compat)
byte[] hash = XisoChecksum.ComputeImageChecksum("game.iso");
string hex = XisoChecksum.ComputeImageChecksumHex("game.iso"); // 64-char lowercase hex
// CLI prints "hex<TAB>path", --silent → hex only
```

### Async, progress & cancellation

```csharp
var cts = new CancellationTokenSource();
var progress = new Progress<ProgressInfo>(info =>
{
    switch (info.Type)
    {
        case ProgressInfoType.FileCount: Console.WriteLine($"{info.Count} files"); break;
        case ProgressInfoType.FileAdded: Console.WriteLine($"added {info.Path} @ {info.Sector}"); break;
        case ProgressInfoType.FinishedPacking: Console.WriteLine("done"); break;
    }
});

// All long-running ops accept CancellationToken + IProgress<ProgressInfo>
var (rc, outPath) = await XisoWriter.CreateXisoAsync("src", "./out", null, null, "game.iso", null, cts.Token, progress: progress);
int ex = await XisoReader.DecodeXisoAsync("game.iso", "./out", ExtractMode.Extract, llCompat: false, cancellationToken: cts.Token);
int rb = await Task.Run(() => XisoRedump.RebuildRedump("a.xiso","v.iso","f.bin",null,"out.iso", null, progress, cts.Token));
```

Errors are typed: `XisoFormatException` (corrupt), `XisoEmptyException` (no files), `XisoFileTooLargeException` (>4 GB, `FileName`/`FileSize`), `ExtractErrorException` (`ErrorCode` `ErrEndOfSector`/`ErrIsoRewritten`/`ErrIsoNoFiles`). `Logger` (`Out`/`Error`/`Quiet`/`RealQuiet`/`MediaEnable`/`RemoveSystemUpdate`/`XboxDiscLseek`) is redirectable for embedding.

## Comparison

File-by-file against [`References/`](References/) — `extract-xiso v2.7.1` (`extract-xiso.c`), `XboxKit-0.7` (`LibXGD/`), `xdvdfs-0.8.3` (`xdvdfs-core`/`cli`). Single matrix (✅ native, 🟡 partial/opt-in, ❌ absent, — n/a):

| Capability | XISOSharp | `extract-xiso` v2.7.1 | XboxKit 0.7 | `xdvdfs` 0.8.3 |
|---|:---:|:---:|:---:|:---:|
| **Reading** | | | | |
| Extract / Unpack | ✅ | ✅ | ✅ | ✅ |
| List top-level / Tree recursive | ✅ | 🟡 list only | ❌ | ✅ |
| `info` / `ls` / `xex-info` | ✅ | ❌ | ❌ | 🟡 `info`/`ls` only |
| Per-file MD5 / SHA-256 | ✅ | ❌ | ❌ | 🟡 MD5 |
| SHA3-256 image checksum (`checksum`) | ✅ | ❌ | ❌ | ✅ |
| `copy-out` single file/dir | ✅ | ❌ | ❌ | ✅ |
| Deep audit `-V` (header/tag/cycles/bounds/0x48/0x0000) | ✅ | ❌ | ❌ | ❌ |
| `validate` + `--validate*` JSON report | ✅ | ❌ | ❌ | ❌ |
| Disc probe RAW/GLOBAL/XGD3/Hybrid/XGD1 (5) | ✅ | 🟡 4 | ✅ +tables | 🟡 4 (`XDVD_OFFSETS`, no Hybrid) |
| Empty-dir sentinel `0xFFFF` + `0x0000` header | ✅ | 🟡 `0xFFFF` only | 🟡 `0xFFFF` only | ✅ |
| Reserved bits `0x08`/`0x40` masked | ✅ | ❌ | ❌ | 🟡 flag only |
| `llCompat` linked-list fix (auto via tag) | ✅ | ✅ | ❌ | ❌ |
| Encoding Latin-1 / WINDOWS_1252 | ✅ | 🟡 raw bytes (`FORCE_ASCII` dead) | 🟡 ASCII | ✅ |
| ECMA-119 descriptors `0x8000` | ✅ | ✅ | ❌ | 🟡 sector 32 |
| Optimized tag `in!xiso` at 31337 | ✅ | ✅ | ❌ | ❌ |
| CISO decompress (DEFLATE v1 + LZ4 v2, single + split parts) | ✅ | ❌ | ❌ | ✅ |
| `.cso`/`.1.cso` input auto-detect in all verbs (`img.rs` parity) | ✅ | ❌ | ❌ | ✅ |
| BlockDevice random-access (File/Memory/Offset/Ciso) | ✅ | ❌ | ❌ | ✅ |
| Track/TOC parsing | ✅* | — | — | 🟡 |
| **Writing** | | | | |
| Write V5 (optimized AVL) | ✅ | ✅ | 🟡 ranges only | ✅ |
| `FileModulus 0x10000` + sector `0xFF` pad | ✅ | ✅ | 🟡 range-copy only | 🟡 `0x00` pad |
| Empty dir → 1 sector `0xFF` sentinel | ✅ | ✅ | ❌ | ✅ |
| `.xbe` media patch `E8…7D→EB` (Boyer-Moore, overlap 7) | ✅ | ✅ | ❌ | ❌ |
| Media patch disable `-m` | ✅ | ✅ | ❌ | ❌ |
| Custom `-o` filename | ✅ | ❌ | — | — |
| **Redump / Archival** | | | | |
| `--video` L0 head + L1 tail (PVD `0x832D`) | ✅ | ❌ | ✅ | ❌ |
| `--random` filler gaps (`GetXisoRanges`/`MergeRanges`) | ✅ | ❌ | ✅ | ❌ |
| `--seed` XGD1 PRNG brute-force 4-byte LE | ✅ | ❌ | ✅ | ❌ |
| `--wipe` zero filler | ✅ | ❌ | ✅ | ❌ |
| `--trim` truncate after last extent | ✅ | ❌ | ✅ | ❌ |
| `--petrify` skeleton + SHA-1 per file | ✅ | ❌ | ✅ | ❌ |
| `--update` tail `su20076000_00000000` (XGD3) | ✅ | ❌ | ✅ | ❌ |
| `--zar` ZArchive/zstd | ✅ | ❌ | ✅ | ❌ |
| ZArchive read/write/pack/extract library (`ZARSharp`, pure C#, zero packages, incl. RFC 8878 zstd decoder) | ✅ | ❌ | ❌ | ❌ |
| `rebuild` from `.zar` sidecar (XboxKit roadmap "coming soon") | ✅ | ❌ | ❌ | ❌ |
| `rebuild` lossless (L0/`l0Padding`+game+`l1Padding`+L1) | ✅ | ❌ | ✅ | ❌ |
| `--security-sectors` `4096`-aligned `sectors.txt` | ✅ | ❌ | 🟡 `sectors.txt` sidecar | ❌ |
| Aliases `--all`/`--best`/`--compress` | ✅ | ❌ | ✅ | ❌ |
| Wave tables `XISO_OFFSET`/`REDUMP_ISO_LENGTH`/`VIDEO_Lx`/`WAVE_PVD` | ✅ | ❌ | ✅ | ❌ |
| `--skip-sectors` / `--prepend-sectors` arbitrary | ✅ | ❌ | 🟡 built-in tables | ❌ |
| **Packing / xdvdfs** | | | | |
| `build-image` ordered `host/**:image/{n}` + `!` + `{0}` | ✅ | ❌ | ❌ | ✅ |
| `image-spec from` TOML preserve-order | ✅ | ❌ | ❌ | ✅ |
| CISO compress v2 LZ4 (byte-identical `lz4_flex` port, fixed `align 2`) + v1 DEFLATE `align` 0/1/2, threshold `+12` | ✅ | ❌ | ❌ | ✅ |
| `--ciso-level` 0..9 / `--ciso-version 1\|2\|auto` / `--ciso-split` (default split `0xffbf6000`) | ✅ | ❌ | ❌ | ✅ |
| Split CSO output `.1.cso`/`.2.cso`… + split input (`ciso::split` parity) | ✅ | ❌ | ❌ | ✅ |
| `wax` glob `*`/`**`/`?`/`[]`/`{a,b}` | ✅ | ❌ | ❌ | ✅ |
| `xdvdfs.toml` `[map_rules]` | ✅ | ❌ | ❌ | ✅ |
| `--dry-run` preview | ✅ | ❌ | ❌ | ✅ |
| `RemapFilesystem` / `SectorAllocator` | ✅ | ❌ | ❌ | ✅ |
| **API** | | | | |
| Byte-range reads | ✅ | — | 🟡 | ✅ |
| LBA sector reads | ✅ | — | ✅ | ✅ |
| Thread-safe random-access | ✅ | ❌ | ❌ | ✅ |
| `CancellationToken` | ✅ | ❌ | ❌ | ❌ |
| `IProgress<ProgressInfo>` (`FileCount`/`DirCount`/`DirAdded`/`FileAdded`/`FinishedPacking`) | ✅ | 🟡 `progress_callback` | ❌ | 🟡 `ProgressInfo` |
| `*Async` (`Task.Run`) | ✅ | ❌ | ❌ | 🟡 `maybe-async` |
| Parallel verify / encode | ✅ verify | ❌ | ❌ | ❌ |
| Typed errors (`XisoFormatException` etc.) | ✅ | ❌ | ❌ | 🟡 `InvalidVolume` |
| `VerifyXiso(IBlockDevice)` overload | ✅ | ❌ | ❌ | ✅ trait |
| Glob `-X` / `WaxGlob` captures | ✅ | 🟡 `-s` only | ❌ | ✅ `wax` |
| **CLI** | | | | |
| `-c` repeatable + `-X` excludes + `-s` | ✅ | 🟡 no `-X` | — | ✅ `pack` |
| `--pack` dir→create / iso→rewrite | ✅ | ❌ | — | ✅ |
| Batch `--batch` sorted + `--batch-recursive` | ✅ | 🟡 explicit args only | ❌ | 🟡 `checksum` multi |
| Quiet `-q` / silent `-Q` | ✅ | ✅ | 🟡 | ❌ |
| Help `-h` / banner `-v` `2.7.1 (01.11.14)` | ✅ | ✅ | 🟡 `-h` only | 🟡 `clap` |
| Exit `0`/`1` + `2` for `validate --strict` | ✅ | 🟡 `0`/`1` only | ❌ | ❌ |
| **Extras** | | | | |
| Extraction to dir | ✅ | ✅ | ✅ | ✅ |
| Platform detection (`OperatingSystem.Is*`) | ✅ | 🟡 `#if` | 🟡 | ✅ |
| Per-hunk CRC / full-image verify | ✅ | — | — | 🟡 SHA3 |
| Multi-target builds | ✅ `net8`/`net9`/`net10` | — | ✅ `net6`–`net10` (`LibXGD` `net20`–`net10`) | — (Rust) |
| Strong-name signing (`snk`) | ✅ | — | ❌ | — |
| Trimmable + AOT (`IsTrimmable`/`IsAotCompatible`) | ✅ | — | ❌ | ✅ native/`no_std` |
| Native dependencies | **none** | none | none (managed NuGet `GrindCore`/`SabreTools`) | `bincode`/`serde`/`clap`/`wax`/`ciso`/`md-5`/`sha3` |
| Distribution | NuGet + `XISOSharp` bin | `cmake` bin | `dotnet` publish + NuGet `LibXGD` | `cargo` crate |

<sub>*XISOSharp `Track/TOC` = `GetVolumeInfo`/`ListDirectory`/`GetXisoRanges` sector map; 🟡 = partial/opt-in/different pad or trimmed-only semantics. xdvdfs CISO = `ciso` crate 0.2 — LZ4 v2 with fixed `align 2`; DEFLATE v1 neither written nor read (`ciso` reader rejects `version != 2`), and no `decompress` verb (`.cso` read via `CSOBlockDevice` only). The original extract-xiso `-p` is parsed but unhandled (dead); `FORCE_ASCII` is a dead macro.</sub>

## Build

```bash
git clone https://github.com/purelogiccode/XISOSharp.git
cd XISOSharp
dotnet build CSharp_XISOSharp.sln            # Debug
dotnet build CSharp_XISOSharp.sln -c Release # Release (packs NuGet)
dotnet test -c Release                       # 724 tests
```

Projects: `XISOSharp.Core` (`net8.0`/`net9.0`/`net10.0`) packs on build; `XISOSharp.Cli` (`net10.0`); `XISOSharp.Tests` (`net10.0`); `XISOSharpTester` (`net10.0-windows` WPF); `ZARSharp` (`net8.0`/`net9.0`/`net10.0` ZArchive library). CI builds on `ubuntu`/`windows`/`macos`.

## Requirements

- .NET 8 SDK or newer (pinned `10.0.301` in `global.json`, `rollForward: latestFeature`)

## License

MIT — see [LICENSE](LICENSE).
