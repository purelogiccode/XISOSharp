# XISOSharp

[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](global.json)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/XISOSharp.svg)](https://www.nuget.org/packages/XISOSharp/)

A **pure C#** port of [extract-xiso](https://github.com/XboxDev/extract-xiso) for Xbox ISO (XISO / XDVDFS) images — **byte-identical** output, no native dependencies, no P/Invoke, just .NET. Beyond the C baseline it merges the archival power of [XboxKit](https://github.com/Deterous/XboxKit) and the modern packing of [xdvdfs](https://github.com/antangelo/xdvdfs) into one trimmable, AOT-compatible library + CLI.

## Projects

| Project | Description |
|---|---|
| [XISOSharp.Core](XISOSharp/) | Core library (`NuGet: XISOSharp`) — full read/write engine, `net8.0`/`net9.0`/`net10.0`, strong-named |
| [XISOSharp.Cli](XISOSharp.Cli/) | CLI project (ships binary `XISOSharp(.exe)`, `AssemblyName XISOSharp.Cli`) — extract-xiso-compatible flags + 35+ extra modes |
| [XISOSharp.Tests](XISOSharp.Tests/) | xUnit suite (1290 tests) — snapshot `test_fixture.iso` + corruption resilience + in-place repair + salvage rebuild + XBE/XEX parsing + disc-format identity + `MemoryBlockDevice` + `xdvdfs-cli` split-CSO interop + extract-xiso 2.7.1 legacy-layout interop + unpack-resume/output-guard/`-d`-edge-case/stream-API/filesystem-destination/explorer/split-join/robustness/remap-escape/symlink coverage |
| [XISOSharp.Benchmarks](XISOSharp.Benchmarks/) | BenchmarkDotNet (AVL, Boyer-Moore, sector math) |
| [XISOSharpTester](XISOSharpTester/) | WPF GUI — batch regression vs `extract-xiso.exe` |
| [XISOSharp.BattleTests](XISOSharp.BattleTests/) | CLI-vs-reference battle harness over a random sample of real ISOs (default 3 of `H:\XBOXTest`, seeded): `extract-xiso.exe` v2.7.1 (`list`/`extract`/`rewrite`), `xdvdfs.exe` 0.8.3 (`checksum`/`md5`/`unpack`/`pack`/`cso` round-trip), `xboxkit.exe` 0.7 (`petrify`/`video`/`random`/`seed`/`trim`/`wipe`/`zar`/`rebuild`) — outputs compared byte-for-byte, per-exe timings reported |

## Documentation

Full docs live in [`docs/`](docs/README.md) — also served as a **Docsify site with a left sidebar** at [`docs/index.html`](docs/index.html) (GitHub Pages):

- [Getting Started](docs/getting-started.md) — install, first extract/create/list
- [CLI Reference](docs/cli.md) — every flag, verb, exit code
- [Archival Workflows](docs/archival.md) — `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`rebuild`
- [Build-Image & Image-Spec](docs/xdvdfs-compat.md#build-image) — ordered `wax` remapping, `xdvdfs.toml`
- [Compression (CISO)](docs/compression.md) — `compress`/`decompress`, `CisoBlockDevice`
- [Checksums](docs/xdvdfs-compat.md#checksum) — SHA3-256 `checksum` vs MD5/SHA-256
- [XISO Format](docs/xiso-format.md) — header, dirtab, AVL, ECMA-119, `0x0000` sentinel
- [Redump & Disc Layouts](docs/redump-workflows.md) — XGD offsets incl. hybrid `0x89D80000`
- [Library Overview](docs/library.md) · [XisoReader](docs/api-xisoreader.md) · [XisoWriter](docs/api-xisowriter.md) · [Utilities](docs/api-utilities.md)

> **Left menu:** open `docs/index.html` locally or via Pages — [`docs/_sidebar.md`](docs/_sidebar.md) is the sidebar.

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

# self-contained trimmed single-file (no runtime needed) for all seven RIDs
./publish-cli.ps1
# binaries land in publish/<rid>/XISOSharp(.exe), ~14 MB each (renamed from the XISOSharp.Cli assembly name)

# or one RID manually (single-file + trimmed come from the csproj defaults)
dotnet publish XISOSharp.Cli -c Release -r linux-x64 --self-contained
# RIDs: win-x86, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 (XboxKit parity, incl. 32-bit)
```

### GUI (desktop app, Avalonia, dark-only)

```bash
# self-contained single-file (no runtime needed) for all six RIDs
./publish-gui.ps1
# binaries land in publish-gui/<rid>/XISOSharp.Gui(.exe), ~80 MB each
```

`XISOSharp.Gui` is a dark-theme front-end that drives the `XISOSharp` CLI as a child
process via the shared core `ProcessRunner` + `ToolLocator` (override → sibling →
`PATH` + `-v` probe; extract/create/rewrite/rebuild/compress/decompress/validate/batch plus
list/tree/info/unpack/copy-out/checksum, live log, cancel, overwrite `-y`/`-n`
switch). Files and folders can be **dragged onto the window**: a single image queues
Extract, a single `.cso` queues Decompress, multiple images queue Rewrite, and a folder
queues Batch (when it contains `*.iso`) or Create. Input==output mistakes are refused
before the CLI runs. It finds the CLI next to itself, on `PATH`, or via the Settings tab
(persisted atomically to `%AppData%/XISOSharp/gui-settings.json`). Picker results marshal
back via `Dispatcher.UIThread`, and commands gate on `CanExecute` while a run is active. Headless helpers:
`XISOSharp.Gui --probe-cli [path]` and `XISOSharp.Gui --self-test [cliPath]`.

## Using the CLI

The CLI binary is `XISOSharp(.exe)`. It is `extract-xiso`-compatible (`-c`/`-x`/`-l`/`-r`/`-d`/`-D`/`-m`/`-q`/`-Q`/`-s`/`-X`/`-h`/`-v`) plus XboxKit + xdvdfs verbs. Flags must precede positionals; `-h`/`-v` exit 0. Help is `-h` ONLY — `--help` is treated as a filename. `-v` still prints the `extract-xiso v2.7.1` baseline banner for compatibility. Double-clicking the exe prints usage and waits for a keypress instead of closing. Every launch also checks the latest GitHub release (daily-cached) and prints an `[UPDATE]` notice with the release URL when a newer `release_<version>_<rid>.zip` is available — opt out with `XISO_NO_UPDATE_CHECK=1`. Full project readme (all args + examples): [`XISOSharp.Cli/`](XISOSharp.Cli/) — deep reference: [`docs/cli.md`](docs/cli.md).

### Basics

```bash
# Extract (auto-detects RAW/GLOBAL/XGD2/XGD3/Hybrid/XGD1)
XISOSharp -d ./out game.iso
XISOSharp --unpack game.iso              # auto-named ./game/
XISOSharp --unpack game.iso ./out

# List / tree / info / audit
XISOSharp -l game.iso
XISOSharp -t game.iso                     # recursive with sizes
XISOSharp -i game.iso /                  # volume + dir entries
XISOSharp --ls game.iso /media           # flat directory
XISOSharp -V game.iso game2.iso         # deep audit (header/tag/cycles/bounds/0x48)

# Create / pack / rewrite
XISOSharp -c ./game_files                # -> ./game_files.iso
XISOSharp -c ./game_files custom.iso
XISOSharp -s -X "**/*.tmp" -X "**/node_modules/**" -c ./src ./out.iso
XISOSharp --pack ./game_files            # dir → create
XISOSharp --pack game.iso                # iso → rewrite (keeps .old)
XISOSharp -r game.iso                    # rewrite optimized (skips if already in!xiso)
XISOSharp -r -D game.iso                 # + delete .old
XISOSharp -c --file-time 0 ./game_files det.iso  # deterministic: byte-identical output

# Copy-out / hash / XEX / XBE / batch
XISOSharp --copy-out game.iso /media ./media_out
XISOSharp --copy-in game.iso ./my-config.ini /config.ini  # patch one file in (keeps .old backup; host dirs rejected fast)
XISOSharp --md5 game.iso                 # or --sha256
XISOSharp --xex-info game360.iso /default.xex
XISOSharp --xbe-info game.iso /default.xbe  # title ID/name, media, region
XISOSharp --sector-layout game.iso          # full sector map (extents, used/free ranges)
XISOSharp --ranges game.iso                 # system vs file sector spans
XISOSharp --is-optimized game.iso           # optimized-tag probe (supports --skip-sectors)
XISOSharp --batch -d ./out ./isos        # all *.iso sorted (extract/list/tree/rewrite/audit only)
XISOSharp --batch --batch-recursive -r ./isos

# Resume an interrupted unpack (skip files already on disk, logged as "skip: <path>")
XISOSharp --skip-existing --unpack game.iso ./out
XISOSharp --skip-existing --batch ./isos -d ./out   # bulk runs resume too
XISOSharp --skip-existing --copy-out game.iso /media ./media_out

# Safety: an -o that points back at the input (or its .old backup) is refused (exit 1)
XISOSharp -r -o game.iso game.iso        # Error: ... is the same file as the input
```

### Audit, repair & salvage (no reference tool does this)

```bash
# Diagnose first: header, tag, full tree walk, sector bounds, cycles, names
XISOSharp -V game.iso                    # Result: PASS, or FAIL + issue list

# Fixable in place (reserved bits, missing tag, separators in names; keeps .old)
XISOSharp --repair game.iso
XISOSharp --dry-run --repair game.iso    # preview only, changes nothing
XISOSharp --repair --no-backup game.iso  # skip the .old backup

# Truncated / structurally damaged: rebuild what is still reachable
XISOSharp --salvage game.iso             # -> game.salvaged.iso (source untouched)
XISOSharp --salvage --repair-out fixed.iso game.iso
```

### Redump & disc offsets

```bash
# Video partition precedes game partition — auto-probed, or explicit
XISOSharp --skip-sectors 129824 -d ./out redump.iso     # GLOBAL/XGD2
XISOSharp -c --prepend-sectors 16640 ./files redump.iso # XGD3
XISOSharp -c --prepend-sectors 283392 ./files hybrid.iso # Hybrid 0x89D80000
XISOSharp -r --skip-sectors 283392 game.iso             # rewrite offset image to bare

# Validate lossless round-trip (flavors imply --validate; mismatch exits 2)
XISOSharp validate --validate-checksums game.redump.iso rebuilt.iso
XISOSharp -r --validate --validate-strict --validate-report report.json game.iso
```

### Archival (Redump lossless, XboxKit parity)

```bash
# Extract components
XISOSharp --video game.redump.iso                  # -> game.video.iso (L0 head + L1 tail)
XISOSharp --random game.iso                        # -> game.filler (gap bytes)
XISOSharp --seed game.iso                          # -> game.seed (XGD1 PRNG brute-force, 4-byte LE)
XISOSharp --wipe -o wiped.iso game.iso             # zero filler gaps
XISOSharp --trim -o trimmed.iso game.iso           # truncate after last extent
XISOSharp --petrify game.iso                       # -> skeleton.iso + .hash (SHA-1 per file)
XISOSharp --update game.redump.iso                 # XGD3 -> su20076000_00000000 (+ zeroes it in video)
XISOSharp --zar -o game.zar game.iso               # ZArchive/zstd
XISOSharp --zar --jobs 4 --policy auto-rename game1.iso game2.iso  # parallel + skip|overwrite|auto-rename

# Aliases (mirrors xboxkit -a/-b/-c)
XISOSharp --all game.redump.iso                    # --random --seed --trim --update --video --wipe
XISOSharp --best game.redump.iso                   # --trim --wipe
XISOSharp --compress game.iso                      # --petrify --update --video --zar

# Security sectors (rebuild only; 4096-sector ranges)
XISOSharp --video game.redump.iso
XISOSharp rebuild --security-sectors sectors.txt -o rebuilt.iso # or:

# Rebuild lossless Redump from components
XISOSharp rebuild game.xiso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso
XISOSharp rebuild game.xiso video.iso seed.bin -o rebuilt.redump.iso          # XGD1 seed variant
XISOSharp rebuild game.xiso video.iso --security-sectors sectors.txt -o rebuilt.redump.iso
```

### Packing & compression (xdvdfs parity)

```bash
# Ordered remapping (wax captures, ! negation, xdvdfs.toml, --dry-run, \: colon escaping)
XISOSharp build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp build-image -D -m "!secret/**" -m "**:/{0}" ./src      # dry-run
XISOSharp build-image -f xdvdfs.toml ./src -O out.iso

# TOML generation
XISOSharp image-spec from -O dist/image.iso -m "bin:/" -m "assets:/{0}" xdvdfs.toml
# -> stdout if specPath omitted

# CISO (v2 LZ4 default, byte-identical to modern xdvdfs compress; v1 DEFLATE via --ciso-version 1)
XISOSharp compress ./game_dir game.cso --ciso-level 9       # 0=store; 1..9 = LZ4 acceleration 10-level
XISOSharp cso game.iso game.cso --ciso-split 0              # single .cso (default splits at ~4 GiB)
XISOSharp decompress game.1.cso game.iso                    # also reads split .1.cso/.2.cso parts
XISOSharp uncso game.cso                                    # decso alias

# Plain-ISO split/join (FATX-friendly) + deterministic checksum
XISOSharp split --size half game.iso                        # -> game.1.iso/game.2.iso
XISOSharp join --output rejoined.iso game.1.iso             # joinsplit alias

# Deterministic image checksum (SHA3-256 over sorted BTreeMap, xdvdfs compat)
XISOSharp checksum game.iso
XISOSharp checksum --silent game1.iso game2.iso            # hex only, multiple images
XISOSharp --checksum game.iso --silent                     # flag form
```

Exit codes: `0` success/`-v`/`-h`/`validate` pass, `1` usage/I/O, `2` validation failure (`validate` command or `-r --validate` on mismatch).

## Using the Library

All in `XISOSharp` namespace (`XISOSharp.Core`). Static `XisoReader`/`XisoWriter` plus archival types (`XisoRedump`, `XisoOperations`, `XisoRanges`, `XisoSkeleton`, `XisoZarchive`, `XgdTables`, `XboxPrng`, `SecuritySectors`), xdvdfs types (`WaxGlob`, `RemapFilesystem`, `XisoChecksum`, `CisoWriter`/`CisoReader`, `BlockDevice/*`), repair types (`XisoRepairer`, `XisoSalvager`), explorer/split/validate (`XisoExplorer`, `XisoSplitter`, `XisoValidator`, `XisoPatcher`), safety types (`UnpackOptions`, `XisoPaths`), typed records (`VolumeInfo`, `EntryInfo`, `AuditResult`, `RepairResult`, `SalvageResult`, `XexInfo`, `XbeInfo`, `ValidationResult`, `ProgressInfo`), `CancellationToken` + `IProgress<ProgressInfo>` + `*Async` everywhere.

### Extract / list / info

```csharp
using XISOSharp;
using XISOSharp.DataStructures; // AvlNode, etc.

// Extract (llCompat auto via tag; pass false for optimized, true for legacy)
int rc = XisoReader.Extract("game.iso", "./out", llCompat: false);
int rc2 = XisoReader.UnpackImage("game.iso", "./out"); // auto IsOptimized, skipSectors aware

// Resume an interrupted unpack: files already on disk with matching sizes are
// skipped (logged as "skip: <path>") instead of rewritten (xdvdfs #190)
var resume = new UnpackOptions { SkipExisting = true };
int rc3 = XisoReader.UnpackImage("game.iso", "./out", options: resume);
XisoReader.CopyOut("game.iso", "/media", "./media_out", resume);

// Unpack without touching the disk (IFilesystem destinations, TODO #7):
// MemoryFilesystem (in-process bytes) or LocalFilesystem/custom stores
var memory = new MemoryFilesystem();
XisoReader.UnpackImage("game.iso", memory);          // lands at the fs root
byte[] xbe = memory.ReadAllBytes("default.xbe");

// Cancellation is honored per entry; extract also reports FileAdded per written file
using var cts = new CancellationTokenSource();
var progress = new Progress<ProgressInfo>(info => { /* ... */ }); // needs using XISOSharp.Models
int rc4 = XisoReader.Extract("game.iso", "./out", llCompat: false,
    cancellationToken: cts.Token, progress: progress);

// List / tree / directory
XisoReader.List("game.iso", llCompat: false);
XisoReader.Tree("game.iso", llCompat: false);
IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory("game.iso", "/");
IReadOnlyList<string> names = XisoReader.ListDirectoryFlat("game.iso", "/media");
EntryInfo? e = XisoReader.GetEntryInfo("game.iso", "/default.xbe");

// Volume & copy-out
VolumeInfo vol = XisoReader.GetVolumeInfo("game.iso"); // IsValid, RootDirSector/Size, DiscLseek, DiscFormat, FileLength
XisoReader.CopyOut("game.iso", "/media", "./media_out");

// Copy-in: patch one host file into the image in place (replace or add;
// keeps game.iso.old backup unless createBackup: false)
XisoReader.CopyIn("game.iso", "./my-config.ini", "/config.ini");

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

### Repair, salvage & executable info (no reference tool does this)

```csharp
// Audit, then fix what is safely patchable in place (keeps game.iso.old)
AuditResult audit = XisoReader.AuditXiso("game.iso");
if (!audit.IsValid)
{
    RepairResult rep = XisoReader.Repair("game.iso"); // reserved bits, tag, separators
    Console.WriteLine($"fixed {rep.Fixed.Count}, remaining {rep.Remaining.Count}");
}

// Truncated / structurally damaged: rebuild every reachable entry into a
// fresh image (source only read, never written; CISO input allowed)
SalvageResult salv = XisoReader.Salvage("game.iso"); // -> game.salvaged.iso
SalvageResult salv2 = XisoReader.Salvage("game.iso", "fixed.iso");
Console.WriteLine($"carried {salv.Copied.Count}, dropped {salv.Skipped.Count}, pass: {salv.Success}");

// XBE (original Xbox): header + certificate of an executable inside the image
XbeInfo? xbe = XisoReader.GetXbeInfo("game.iso", "/default.xbe");
Console.WriteLine($"{xbe?.TitleName} [{xbe?.TitleId:X8}] media 0x{xbe?.AllowedMedia:X8}");

// Split / join FATX-friendly parts (xdvdfs #97)
IReadOnlyList<string> parts = XisoReader.SplitXiso("game.iso", "game", 4L * 1024 * 1024 * 1024);
string rejoined = XisoReader.JoinSplitXiso(parts[0], "rejoined.iso");

// FILETIME volume descriptor (xdvdfs-compatible semantics)
DateTimeOffset stamped = XisoReader.GetFileTime("game.iso");
XisoReader.SetFileTime("game.iso", DateTimeOffset.UtcNow);
```

### Create / rewrite

```csharp
// Simple create (1:1 directory → ISO) — convenience
int rc = XisoWriter.PackFromDirectory("source_dir", "out/game.iso",
    excludePatterns: ["**/*.tmp", "**/node_modules/**"],
    progressCallback: (cur, total) => Console.Write($"\r{cur}/{total}"),
    progress: myProgress); // IProgress<ProgressInfo> FileCount/DirCount/DirAdded/FileAdded/FileProgress/FinishedPacking

// Full control (mirrors extract-xiso.c three-pass layout)
int rc2 = XisoWriter.CreateXiso(
    rootDirectory: "source_dir", outputDirectory: "./out", inRoot: null, sourceStream: null,
    out string? outIsoPath, inName: "game.iso", progressCallback: null,
    prependSectors: 129824, // GLOBAL/XGD2
    excludePatterns: ["**/$SystemUpdate/**"],
    cancellationToken: ct);

// Rewrite optimized (AVL) — in place by default; an explicit -o-equivalent
// outputName pointing at the input (or its .old backup) throws IOException
int rw = XisoReader.Rewrite("game.iso", outputPath: "./out", out string? rewritten,
    outputName: "game.opt.iso");
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
bool zar = XisoZarchive.CreateZar("game.iso", "game.zar");

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

File-by-file against [`References/`](References/) — `extract-xiso v2.7.1` (`extract-xiso.c`, incl. build `202609111233` with the empty-subdirectory rewrite fix), `XboxKit-0.7` (`LibXGD/`), `xdvdfs-0.8.3` (`xdvdfs-core`/`cli`). Single matrix (✅ native, 🟡 partial/opt-in, ❌ absent, — n/a):

| Capability | XISOSharp | `extract-xiso` v2.7.1 | XboxKit 0.7 | `xdvdfs` 0.8.3 |
|---|:---:|:---:|:---:|:---:|
| **Reading** | | | | |
| Extract / Unpack | ✅ | ✅ | ✅ | ✅ |
| List top-level / Tree recursive | ✅ | 🟡 list only | ❌ | ✅ |
| `info` / `ls` / `xex-info` / `xbe-info` | ✅ | ❌ | ❌ | 🟡 `info`/`ls` only |
| Per-file MD5 / SHA-256 | ✅ | ❌ | ❌ | 🟡 MD5 |
| SHA3-256 image checksum (`checksum`) | ✅ | ❌ | ❌ | ✅ |
| `copy-out` single file/dir | ✅ | ❌ | ❌ | ✅ |
| `copy-in` single file (in-place patch + `.old` backup) | ✅ | ❌ | ❌ | ❌ (open #165) |
| Resume interrupted unpack (`--skip-existing` / `UnpackOptions.SkipExisting`) | ✅ | ❌ | ❌ | ❌ (open #190) |
| Deep audit `-V` (header/tag/cycles/bounds/0x48/0x0000) | ✅ | ❌ | ❌ | ❌ |
| In-place repair `--repair` (reserved bits/tag/separators + `.old` backup) | ✅ | ❌ | ❌ | ❌ |
| Salvage rebuild `--salvage` (carry reachable entries into fresh `.iso` + re-audit) | ✅ | ❌ | ❌ | ❌ |
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
| Rewrite byte-parity (`-r` SHA-256 identical on real dumps: DIR/ARC attr defaults + empty-file frontier sectors) | ✅ | ✅ (reference) | ❌ | ❌ |
| Source attribute preservation on rewrite (`--preserve-attrs`, RO/HID/SYS/NOR) | ✅ | ❌ (always normalizes) | ❌ | ❌ |
| Empty-file start sector = allocation frontier (`SectorAllocator` zero-count) | ✅ | ✅ (reference) | ❌ | ❌ |
| Custom `-o` filename | ✅ | ❌ | — | — |
| **Redump / Archival** | | | | |
| `--video` L0 head + L1 tail (PVD `0x832D`) | ✅ | ❌ | ✅ | ❌ |
| `--random` filler gaps (`GetXisoRanges`/`MergeRanges`) | ✅ | ❌ | ✅ | ❌ |
| `--seed` XGD1 PRNG brute-force 4-byte LE | ✅ | ❌ | ✅ | ❌ |
| `--wipe` zero filler | ✅ | ❌ | ✅ | ❌ |
| `--trim` truncate after last extent | ✅ | ❌ | ✅ | ❌ |
| `--petrify` skeleton + SHA-1 per file | ✅ | ❌ | ✅ | ❌ |
| `--update` tail `su20076000_00000000` (XGD3) | ✅ | ❌ | ✅ | ❌ |
| `--zar` ZArchive/zstd (byte-identical to xboxkit: discovery-order name table) | ✅ | ❌ | ✅ | ❌ |
| ZArchive read/write/pack/extract library (`ZArchiveSharp`, pure C#, zero packages, incl. RFC 8878 zstd encoder + decoder) | ✅ | ❌ | ❌ | ❌ |
| `rebuild` from `.zar` sidecar (XboxKit roadmap "coming soon") | ✅ | ❌ | ❌ | ❌ |
| `rebuild` lossless (L0/`l0Padding`+game+`l1Padding`+L1) | ✅ | ❌ | ✅ | ❌ |
| `--security-sectors` `4096`-aligned `sectors.txt` (rebuild only) | ✅ | ❌ | 🟡 `sectors.txt` sidecar | ❌ |
| `--jobs` parallel `--zar` + `--policy` `skip\|overwrite\|auto-rename` | ✅ | ❌ | ❌ | ❌ |
| Aliases `--all`/`--best`/`--compress` | ✅ | ❌ | ✅ | ❌ |
| Wave tables `XISO_OFFSET`/`REDUMP_ISO_LENGTH`/`VIDEO_Lx`/`WAVE_PVD` | ✅ | ❌ | ✅ | ❌ |
| `--skip-sectors` / `--prepend-sectors` arbitrary | ✅ | ❌ | 🟡 built-in tables | ❌ |
| **Packing / xdvdfs** | | | | |
| `build-image` ordered `host/**:image/{n}` + `!` + `{0}` | ✅ | ❌ | ❌ | ✅ |
| `image-spec from` TOML preserve-order | ✅ | ❌ | ❌ | ✅ |
| CISO compress v2 LZ4 (byte-identical `lz4_flex` port, fixed `align 2`) + v1 DEFLATE `align` 0/1/2, threshold `+12` | ✅ | ❌ | ❌ | ✅ |
| `--ciso-level` 0..9 / `--ciso-version 1\|2\|auto` / `--ciso-split` (default split `0xffbf6000`) | ✅ | ❌ | ❌ | ✅ |
| Split CSO output `.1.cso`/`.2.cso`… + split input (`ciso::split` parity, golden-tested vs `xdvdfs-cli 0.8.3` incl. multi-part) | ✅ | ❌ | ❌ | ✅ |
| Split plain ISO `split`/`join`/`joinsplit` (sector-aligned `.1.iso`…, FATX 4G cap) | ✅ | ❌ | ❌ | ❌ |
| `wax` glob `*`/`**`/`?`/`[]`/`{a,b}` | ✅ | ❌ | ❌ | ✅ |
| `xdvdfs.toml` `[map_rules]` | ✅ | ❌ | ❌ | ✅ |
| `--dry-run` preview | ✅ | ❌ | ❌ | ✅ |
| `RemapFilesystem` / `SectorAllocator` | ✅ | ❌ | ❌ | ✅ |
| **API** | | | | |
| Byte-range reads | ✅ | — | 🟡 | ✅ |
| LBA sector reads | ✅ | — | ✅ | ✅ |
| Thread-safe random-access | ✅ | ❌ | ❌ | ✅ |
| `CancellationToken` | ✅ | ❌ | ❌ | ❌ |
| `IProgress<ProgressInfo>` (`FileCount`/`DirCount`/`DirAdded`/`FileAdded`/`FileProgress`/`FinishedPacking`) | ✅ | 🟡 `progress_callback` | ❌ | 🟡 `ProgressInfo` |
| `*Async` (`Task.Run`) | ✅ | ❌ | ❌ | 🟡 `maybe-async` |
| Parallel verify / encode | ✅ verify | ❌ | ❌ | ❌ |
| Typed errors (`XisoFormatException` etc.) | ✅ | ❌ | ❌ | 🟡 `InvalidVolume` |
| `VerifyXiso(IBlockDevice)` overload | ✅ | ❌ | ❌ | ✅ trait |
| Glob `-X` / `WaxGlob` captures | ✅ | 🟡 `-s` only | ❌ | ✅ `wax` |
| **CLI** | | | | |
| `-c` repeatable + `-X` excludes + `-s` | ✅ | 🟡 no `-X` | — | ✅ `pack` |
| `--pack` dir→create / iso→rewrite | ✅ | ❌ | — | ✅ |
| Batch `--batch` sorted + `--batch-recursive` | ✅ | 🟡 explicit args only | ❌ | 🟡 `checksum` multi |
| Input==output safety guard (refuse `-o` onto input/`.old`/split part) | ✅ | — (no `-o` flag) | ❌ | ❌ (open #36) |
| Quiet `-q` / silent `-Q` (`checksum --silent` → hex only) | ✅ | ✅ | 🟡 | ❌ |
| Help `-h` ONLY (`--help` is a filename) / banner `-v` `2.7.1 (01.11.14)` | ✅ | ✅ | 🟡 `-h` only | 🟡 `clap` |
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
dotnet test -c Release                       # 1290 tests (`XISOSharp.Tests`; ZArchiveSharp comes from NuGet)
```

Projects: `XISOSharp.Core` (`net8.0`/`net9.0`/`net10.0`) packs on build; `XISOSharp.Cli` (`net8.0`/`net9.0`/`net10.0`, ships net10.0); `XISOSharp.Tests` (`net8.0`/`net9.0`/`net10.0`); `XISOSharpTester` (`net10.0-windows` WPF). `ZArchiveSharp` (`net8.0`/`net9.0`/`net10.0` ZArchive library) + `ZArchiveSharp.Tests` + `ZArchiveSharp.Benchmarks` moved to the sibling `../CSharp_ZArchiveSharp` repo (own solution); `XISOSharp` consumes the library as NuGet package `ZArchiveSharp` 1.0.2. CI builds on `ubuntu`/`windows`/`macos`.

## Requirements

- .NET 8 SDK or newer (pinned `10.0.301` in `global.json`, `rollForward: latestMinor`)

## License

MIT — see [LICENSE](LICENSE).
