# Archival Workflows (Redump)

XboxKit's value is **lossless** round-trip: Redump full-disc images ↔ trimmed XISO. XISOSharp implements the entire pipeline in pure C# (port of `References/XboxKit-0.7/LibXGD/XGD.cs:11`, `LibXGD/XDVDFS.cs`, `LibXGD/XboxPRNG.cs`, `LibXGD/ZArchive`).

- [Concepts](#concepts)
- [Video partition](#video)
- [Filler / random](#random)
- [Seed (XGD1 PRNG)](#seed)
- [Wipe](#wipe)
- [Trim](#trim)
- [Petrify / skeleton](#petrify)
- [System update (XGD3)](#update)
- [ZArchive / ZAR](#zar)
- [Security sectors](#security-sectors)
- [Aliases --all / --best / --compress](#aliases)
- [Rebuild](#rebuild)
- [API surface](#api-surface)
- [Examples](#examples)

## Concepts

A Redump dump is `L0` video head + `l0Padding` zeroes + **game partition** (XDVDFS, what XISOSharp extracts) + `l1Padding` + `L1` video tail (optionally `l1Trimmed + updateFS + lastSector` for XGD3). Tables live in `XISOSharp.Core/XgdTables.cs` (verbatim `XISO_OFFSET`/`REDUMP_ISO_LENGTH[9]`/`VIDEO_L*_LENGTH[19]`/`WAVE_PVD[24]` + PVD at `0x832D` via `GetVideoType`/`GetRedumpIsoTypeBySize`). Ranges live in `XISOSharp.Core/XisoRanges.cs` (`GetXisoRanges`/`GetValidSectors`/`MergeRanges`), seeded gaps in `XboxPrng.cs`, skeleton in `XisoSkeleton.cs`, `SecuritySectors.cs` parses `sectors.txt`.

```
Redump: [ L0 (VIDEO_L0_LENGTH[wave]) | l0Padding | game partition (fileRanges + gaps) | l1Padding | L1 (VIDEO_L1_LENGTH[wave]) ]
                                                                  └─ l1 split for XGD3: l1Trimmed | su20076000_00000000 FS | last sector
```

`GetXisoRanges(fs, isoOffset, quiet)` walks `cur = isoOffset+rootOffset+childOffset`, `left==0xFFFF` sentinel, collecting `SysRanges` (directory tables) + `FileRanges` (`fileOffset/fileSize`). `MergeRanges(a,b)` coalesces sorted distinct ranges — filler = `xisoLength - merged`. All sector math is `SectorSize 2048`, image padded to `FileModulus 0x10000`.

## Video

```bash
XISOSharp.Cli --video <redump.iso> [video.iso]
```

`XisoRedump.TryExtractVideo(redumpPath, outputVideoPath, out outPath)` — head `VIDEO_L0_LENGTH[videoType]` at `0` + tail `VIDEO_L1_LENGTH[videoType]` at `isoSize-L1`, streamed `64*SectorSize` chunks via `Logger`. Gracefully fails (warning) when `GetVideoType == -1`. As sidecar of `--all`/`--best`/`--compress` (see aliases). XGD3 `su…` is **not** in video when `--update` also extracts it (zeroed in video for dedup).

## Random

```bash
XISOSharp.Cli --random <input.iso> [filler.bin]
```

`XisoOperations.ExtractFiller(isoPath, isoOffset)` — bytes **not** in `SysRanges ∪ FileRanges` after `MergeRanges`, i.e. gaps. Validates `filler % SectorSize==0`. Mirrors `XDVDFS.GetValidSectors` → `ProcessXISO` filler path.

## Seed

```bash
XISOSharp.Cli --seed <input.iso> [seed.bin]
```

XGD1 only. `XisoOperations.TryExtractSeed` + `XboxPrng.BruteForceSeed(ReadOnlySpan<byte> fillerSample)` / `SimulateSectors` / `WriteSectors` — RC4-like PRNG (port of `XboxPRNG.cs`). Brute-forces 4-byte LE seed from first filler gap; gate `GetXisoType==0` (XGD1). Writes 4-byte LE seed to `*.seed`.

## Wipe

```bash
XISOSharp.Cli --wipe <input.iso> -o <wiped.xiso>
```

`XisoOperations.WipeFiller` → `ProcessWipe` — walks `currentByte < xisoLength`, writing zeroes for filler extents instead of original/PRNG bytes. Part of `--best` (`-twx`). Improves compression for emulator use.

## Trim

```bash
XISOSharp.Cli --trim <input.iso> [trimmed.xiso]
```

`XisoOperations.TrimXiso` — truncate after last file extent (`ranges.Max(End)+1)*SectorSize`), already `FileModulus`-aligned collection via `MergeRanges`. `FileStream.SetLength(trimmedLen)`.

## Petrify

```bash
XISOSharp.Cli --petrify <input.iso> [skeleton.xiso] [hashFile]
```

`XisoSkeleton.Petrify` — XISO with file extents zeroed + SHA-1 hex per file (`CollectFileEntries` sorted by `Offset`, `SHA1` streaming `sector*SectorSize+isoOffset`, line `hex + " " + path`). Skeleton = copy XISO with `WriteZeroes` over `FileRanges`. Mirrors `ProcessXISO(skeleton:true, hashWriter)`.

## Update

```bash
XISOSharp.Cli --update <redump.iso> [updateFile]
```

`XisoRedump.TryExtractUpdate(redumpPath, outputUpdatePath, outputVideoPath)` — extracts `su20076000_00000000` from video `L1` tail and zeroes it in output `video.iso` for dedup (XGD3 only, `GetVideoType` 17/18). Heuristic `FindUpdateOffset` tail scan `ABCDABCD`, `l1Trimmed = L1 - suSize - SectorSize`. Warns no-op on XGD1/2.

## ZAR

```bash
XISOSharp.Cli --zar <input.iso|redump.iso> [output.zar]
XISOSharp.Cli rebuild <game.zar> [video.iso] [filler|seed] [su...] -o <redump.iso>
```

`XisoZarchive.CreateZar` — packs the XISO file tree with the XboxKit header layout using
raw (uncompressed) blocks, valid per the ZArchive spec (no native deps, trimmable/AOT-safe).
Full read/write/pack/extract support lives in the `ZARSharp` project: a pure-C# port of
`References/ZArchive-0.1.2` with an in-repo RFC 8878 zstd decoder (zero packages), so
reference `zarchive.exe` archives (zstd-compressed) open transparently.

## Security sectors

```bash
XISOSharp.Cli --security-sectors <sectors.txt> <redump.iso> --video ...
XISOSharp.Cli rebuild ... --security-sectors <sectors.txt> -o <redump.iso>
```

`SecuritySectors.cs` parses `start-end` lines (`start-end` where `end-start==4095`, `4096`-sector ranges), sorted `int[]`. Overrides built-ins per XGD type, zeroes in Redump, skipped via `XboxPrng.SimulateSectors` in rebuild.

## Aliases

```bash
XISOSharp.Cli --all <redump.iso>       # == --random --seed --trim --update --video --wipe (+ --xiso)
XISOSharp.Cli --best <redump.iso>      # == --trim --wipe --xiso  (mirrors XboxKit -b / -twx)
XISOSharp.Cli --compress <input.iso>   # == --petrify --update --video --zar (mirrors -c / -puvz)
```

`Program.cs:482` expands `allMode`/`bestMode`/`compressAlias` + `RunRedumpBatch:693` dispatches batch with `-o` single-file guard. Aliases match XboxKit `-a` (`-rstuvwx`) / `-b` / `-c`.

## Rebuild

```bash
XISOSharp.Cli rebuild <xiso|game.zar> [video.iso] [filler|seed] [su20076000_00000000] -o <redump.iso>
XISOSharp.Cli <input.xiso> [files...]   # XboxKit compat alias (no flags)
```

`XisoRedump.RebuildRedump(xisoPath, videoPath, fillerOrSeedPath, updatePath, outputRedumpPath, securitySectors, progress, ct)` — faithful `RebuildRedump` port: `GetXISORanges(xisoFS,0,quiet)` → `MergeRanges` → sector walk with `XboxPRNG` fallback. Validates `l0Padding = xisoOffset - L0 >=0`, `l1Padding = (redumpLen-L1)-(xisoOffset+xisoLength)`, last-sector split for XGD3 updates. Pads via `WriteZeroes`. Checks `currentByte==xisoLength`.

A `.zar` sidecar may stand in for `<xiso>` (XboxKit roadmap "ZArchive rebuild is coming soon!",
beyond parity): a single embedded XISO image is used verbatim (byte-identical rebuild stays
possible), otherwise the archived file tree is extracted to a temp dir and repacked via
`XisoWriter.PackFromDirectory` — file data is exact, directory layout is regenerated, so the
rebuilt Redump matches only if no gaps depend on the original layout (filler still covers gaps).
Temp files are deleted afterwards; corrupt archives fail with an error instead of rebuilding.

Positional file mode (`rebuild <xiso> [files...]`) expands via `Program.cs:RunRebuildMode:1259` (files may appear before `-o`), matching XboxKit `xboxkit.exe <input.xiso> [files...]`.

Workflow:

```bash
# Full archival export
XISOSharp.Cli --all game.redump.iso

# Lossless rebuild (video + filler/seed + update)
XISOSharp.Cli rebuild game.xiso game.video.iso game.filler su20076000_00000000 -o rebuilt.redump.iso

# XGD1 seed variant
XISOSharp.Cli rebuild game.xiso game.video.iso seed.bin -o rebuilt.redump.iso

# From a .zar sidecar instead of the XISO
XISOSharp.Cli rebuild game.zar game.video.iso game.filler su20076000_00000000 -o rebuilt.redump.iso

# Validate
XISOSharp.Cli validate game.redump.iso rebuilt.redump.iso --validate-checksums
```

## API surface

| Type | Members |
|---|---|
| `XgdTables` | `XISO_OFFSET`, `REDUMP_ISO_LENGTH`, `VIDEO_L0_LENGTH`, `VIDEO_L1_LENGTH`, `WAVE_PVD`, `GetVideoType(path)`, `GetRedumpIsoTypeBySize(long)`, `GetWave` PVD `0x832D` |
| `XisoRanges` | `GetXisoRanges(string isoPath, long isoOffset)`, `GetXisoRanges(FileStream, long, bool quiet)`, `MergeRanges`, `GetValidSectors`, `CollectFileEntries` sorted by `Offset` |
| `XboxPrng` | `BruteForceSeed(ReadOnlySpan<byte>)`, `SimulateSectors(long count)`, `WriteSectors(Stream, long sectorCount)` |
| `XisoRedump` | `TryExtractVideo`, `TryExtractUpdate`, `RebuildRedump` |
| `XisoOperations` | `ExtractFiller`, `TryExtractSeed`/`ExtractSeed`, `WipeFiller`, `TrimXiso`, `WipeAndTrim` |
| `XisoSkeleton` | `Petrify(skeletonPath, hashPath)`, `CollectFileEntries` |
| `XisoZarchive` | `CreateZar` |
| `SecuritySectors` | `Parse(string path)` → `int[]`, validation `4095` length |
| CLI | `rebuild` verb `RunRebuildMode`, `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`--all`/`--best`/`--compress` → `RunRedumpBatch`, `--security-sectors` threaded |

## Examples

```bash
# Redump → components (one pass)
XISOSharp.Cli --all H:\dumps\game.redump.iso

# Components → Redump (with update extraction)
XISOSharp.Cli --update game.redump.iso su.bin
XISOSharp.Cli --video game.redump.iso game.video.iso
XISOSharp.Cli --random game.xiso game.filler
XISOSharp.Cli rebuild game.xiso game.video.iso game.filler su.bin -o rebuilt.redump.iso
XISOSharp.Cli validate game.redump.iso rebuilt.redump.iso --validate-checksums

# Trim for emulator
XISOSharp.Cli --best game.iso          # trimmed + wiped
XISOSharp.Cli --trim game.iso -o small.xiso
```

See also: [CLI](cli.md) · [Redump & Disc Layouts](redump-workflows.md) · [Compression](compression.md) · [Library](library.md)
