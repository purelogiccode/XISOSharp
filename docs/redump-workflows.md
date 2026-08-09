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
XISOSharp probes these offsets (in order) during verification:

| Layout | Offset (bytes) | Offset (sectors) | Notes |
|---|---|---|---|
| RAW (bare XISO) | `0x00000000` | 0 | Plain XISO, header at `0x10000` |
| GLOBAL / XGD2 | `0x0FD90000` | 129,824 (`0x1FB20`) | Retail / Xbox Live layout |
| XGD3 | `0x02080000` | 16,640 (`0x4100`) | Xbox 360 (XGD3) |
| XGD1 | `0x18300000` | 198,144 (`0x30600`) | Original Xbox (XGD1) |

> [!NOTE]
> XboxKit additionally documents an "XGD2-Hybrid" offset `0x89D80000`. XISOSharp does
> not probe it; use `--skip-sectors` for such images.

## Auto-detection

For most dumps no flag is needed: `VerifyXiso` probes the header magic
(`MICROSOFT*XBOX*MEDIA`) at `0x10000`, then at each known offset above, and selects the
matching layout. Every subsequent sector read adds the detected offset.

`--skip-sectors` exists for the cases probing cannot cover — most importantly XGD2,
whose **video partition size varies between discs**, so the game partition does not
always sit at exactly `0x0FD90000`.

## Skip sectors (reading offset images)

```bash
extract-xiso --skip-sectors <N> -d ./out image.iso
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
extract-xiso -c ./game_files redump.iso --prepend-sectors <N>
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
extract-xiso --skip-sectors 129824 -d ./extracted game.redump.iso

# 2. Rebuild a Redump-style image at the same offset
extract-xiso -c ./extracted rebuilt.iso --prepend-sectors 129824

# 3. Prove the conversion is lossless
extract-xiso validate game.redump.iso rebuilt.iso --validate-checksums
```

### Optimize an offset image in place

```bash
# Reads at the skip offset, writes a bare optimized XISO
# (validation flags cannot be combined with --skip-sectors)
extract-xiso -r --skip-sectors 129824 game.redump.iso
```

### Offset images with exclusion and media patching

```bash
extract-xiso -s -X "**/*.tmp" -c ./files custom.iso --prepend-sectors 16640   # XGD3 offset
```

## Sector math reference

| Desired layout | `--skip-sectors` / `--prepend-sectors` value |
|---|---|
| RAW | 0 |
| GLOBAL / XGD2 (`0x0FD90000`) | 129,824 |
| XGD3 (`0x02080000`) | 16,640 |
| XGD1 (`0x18300000`) | 198,144 |

Compute your own: `sectors = byteOffset / 2048`. For example a game partition at byte
offset `0x12340000` → `0x12340000 / 2048 = 149,120` sectors.

See also: [CLI Reference](cli.md) · [XISO Format](xiso-format.md) ·
[Validation](validation.md) · [FAQ](faq.md)
