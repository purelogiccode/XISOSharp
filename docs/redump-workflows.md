# Redump & Disc Layouts

Xbox game discs are **multi-partition**. Understanding the physical layout is the key to
working with Redump dumps and to the `--skip-sectors` / `--prepend-sectors` options.

- [Anatomy of an Xbox disc](#anatomy-of-an-xbox-disc)
- [Known disc offsets](#known-disc-offsets)
- [Auto-detection](#auto-detection)
- [Skip sectors (reading offset images)](#skip-sectors-reading-offset-images)
- [Prepend sectors (writing offset images)](#prepend-sectors-writing-offset-images)
- [Workflows](#workflows)
- [Sector math reference](#sector-math-reference)

## Anatomy of an Xbox disc

Every official Xbox game disc contains two independent filesystems:

1. **Video partition** — a standard DVD-Video filesystem (a DVD player sees a movie,
   not a game).
2. **Game partition** — the XDVDFS / XISO filesystem containing the game.

An Xbox console's DVD drive applies an internal address offset so it always sees the
game partition as starting at sector 0. Tools that dump only the game partition produce
the plain "XISO" files that are common in emulation. **Redump dumps** are full linear
disc images: video partition first, then the game partition.

A 2048-byte **sector** is the addressing unit of the XDVDFS filesystem; all sector
numbers stored inside an image are **partition-relative** (offset 0 = the start of the
game partition, not the start of the file).

## Known disc offsets

The `disc lseek` is the byte offset of the game partition **within the dump file**.
XISOSharp probes these offsets (in order) during verification — now **5** including
Hybrid natively (`XgdTables.cs`):

| Layout | Offset (bytes) | Offset (sectors) | Notes |
|---|---|---|---|
| RAW (bare XISO) | `0x00000000` | 0 | Plain XISO, header at `0x10000` |
| GLOBAL / XGD2 | `0x0FD90000` | 129,824 (`0x1FB20`) | Retail / Xbox Live layout (`XgdTables.XISO_OFFSET[1]`) |
| XGD3 | `0x02080000` | 16,640 (`0x4100`) | Xbox 360 (XGD3) |
| **Hybrid (XGD2-Hybrid)** | `0x89D80000` | 283,392 (`0x45300`) | **Native** (`Constants.Xgd2HybridLseekOffset`, probe #4) — also reachable via `--skip-sectors 283392` |
| XGD1 | `0x18300000` | 198,144 (`0x30600`) | Original Xbox (XGD1) |

> [!NOTE]
> Hybrid `0x89D80000` was previously only reachable via `--skip-sectors`; XISOSharp now
> probes it natively (order `0 → GLOBAL → XGD3 → Hybrid → XGD1`, keep `skipSectors` override).
> See [`XgdTables`](archival.md#xgd-tables) for the full `REDUMP_ISO_LENGTH[9]` + `VIDEO_Lx[19]` wave tables.

## Auto-detection

For most dumps no flag is needed: `VerifyXiso` probes the header magic
(`MICROSOFT*XBOX*MEDIA`) at `0x10000`, then at each known offset above, and selects the
matching layout. Every subsequent sector read adds the detected offset.

`--skip-sectors` exists for the cases probing cannot cover — most importantly XGD2,
whose **video partition size varies between discs**, so the game partition does not
always sit at exactly `0x0FD90000`.

## Skip sectors (reading offset images)

```bash
XISOSharp.Cli --skip-sectors <N> -d ./out image.iso
```

`N` is the number of 2048-byte sectors to skip from the start of the file before the
XISO filesystem begins. The header must then be at `N × 2048 + 0x10000`.

- Valid in **extract, list, tree, and rewrite** modes.
- When `N` is supplied, offset **probing is skipped** — the value is authoritative.
- Negative values are rejected; `N = 0` checks only the RAW offset (probing is still
  skipped when the flag is supplied explicitly).
- Combine with `-r` to rewrite an offset image into a bare optimized XISO.

> [!NOTE]
> If the game partition happens to sit at a known offset, auto-detection already works
> and `--skip-sectors` is unnecessary. Use it when the video partition size is
> nonstandard (e.g. a custom XGD2 dump).

## Prepend sectors (writing offset images)

```bash
XISOSharp.Cli -c --prepend-sectors <N> ./game_files redump.iso
```

Writes the image with `N` zero-filled sectors **before** the XISO filesystem, reserving
room for a video partition. The sector numbers stored in directory entries remain
partition-relative — only physical positions shift — so the resulting file matches the
layout of a real dump and is readable by other Xbox tools.

- Valid in **create (`-c`) and rewrite (`-r`)** modes.
- Choose `N` so the filesystem lands at a known offset to keep auto-detection working
  (see the [sector math reference](#sector-math-reference)).
- The prepended area is zero-filled; the ECMA-119 volume descriptors describe the whole
  file (placeholder + game partition), matching real disc behavior.

## Workflows

### Round-trip: Redump → bare XISO → Redump

```bash
# 1. Extract the game partition from a Redump dump
XISOSharp.Cli --skip-sectors 129824 -d ./extracted game.redump.iso

# 2. Rebuild a Redump-style image at the same offset
XISOSharp.Cli -c --prepend-sectors 129824 ./extracted rebuilt.iso

# 3. Prove the conversion is lossless
XISOSharp.Cli validate --validate-checksums game.redump.iso rebuilt.iso
```

### Optimize an offset image in place

```bash
# Reads at the skip offset, writes a bare optimized XISO
# (validation flags cannot be combined with --skip-sectors)
XISOSharp.Cli -r --skip-sectors 129824 game.redump.iso
```

### Offset images with exclusion and media patching

```bash
XISOSharp.Cli -s -X "**/*.tmp" -c --prepend-sectors 16640 ./files custom.iso   # XGD3 offset
```

## Advanced: archival pipeline (video / filler / seed / wipe / trim / petrify / update / ZAR)

For the full lossless Redump ↔ XISO pipeline (XboxKit parity) — extracting `L0`/`L1` video
heads/tails, filler gaps via `GetXisoRanges`/`MergeRanges`, XGD1 PRNG seed brute-force (`XboxPrng`),
wiping, trimming, skeleton petrify (`SHA-1`), XGD3 update `su20076000_00000000`, ZArchive/zstd,
`sectors.txt` (`SecuritySectors`), batch aliases `--all`/`--best`/`--compress`, and verb `rebuild` —
see **[Archival Workflows](archival.md)**.

Quick taste:

```bash
XISOSharp.Cli --all game.redump.iso                  # video+filler+seed+trim+update+wipe in one pass
XISOSharp.Cli rebuild game.xiso video.iso filler.bin su20076000_00000000 -o game.redump.iso --security-sectors sectors.txt
XISOSharp.Cli rebuild game.zar video.iso filler.bin su20076000_00000000 -o game.redump.iso   # .zar sidecar as <xiso>
XISOSharp.Cli --zar -o game.zar game.iso
```

## Sector math reference

| Desired layout | `--skip-sectors` / `--prepend-sectors` value |
|---|---|
| RAW | 0 |
| GLOBAL / XGD2 (`0x0FD90000`) | 129,824 |
| XGD3 (`0x02080000`) | 16,640 |
| **Hybrid** (`0x89D80000`) | **283,392** |
| XGD1 (`0x18300000`) | 198,144 |

Compute your own: `sectors = byteOffset / 2048`. For example a game partition at byte
offset `0x12340000` → `0x12340000 / 2048 = 149,120` sectors.

See also: [CLI Reference](cli.md) · [XISO Format](xiso-format.md) ·
[Validation](validation.md) · [FAQ](faq.md)
