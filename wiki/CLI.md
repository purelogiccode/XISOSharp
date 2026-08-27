# CLI Reference

The `extract-xiso` command-line tool mirrors the original C tool's interface and adds a
number of modern conveniences — **Redump archival** (XboxKit parity) and **xdvdfs parity**
(`build-image`, CISO, checksum). This page is the complete reference: syntax, modes,
options, validation flags, exit codes, and batch behavior.

- [Syntax](#syntax)
- [Modes](#modes)
- [Options](#options)
- [Validation flags](#validation-flags)
- [Mode combinations and restrictions](#mode-combinations-and-restrictions)
- [Exit codes](#exit-codes)
- [Batch / multi-ISO processing](#batch--multi-iso-processing)
- [Optimized-tag detection](#optimized-tag-detection)
- [Examples](#examples)
- [Notes](#notes)

## Syntax

```
extract-xiso [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
extract-xiso [options] -c <dir> [name] [-c <dir> [name]] ...
extract-xiso validate <source.iso> <output.iso> [options]
extract-xiso rebuild <xiso> [video.iso] [filler|seed] [su20076000_00000000] -o <redump.iso>
extract-xiso build-image [sourceDir] [output.iso] -m "host:image" [-f <toml>] [-O output] [-D|--dry-run]
extract-xiso image-spec from -O <out> -m "host:image" ... [specPath]
extract-xiso compress|cso <sourceDir|image.iso> [output.cso] [--ciso-level 0..9] [--ciso-split N]
extract-xiso decompress|uncso|decso <cso> [output.iso]
extract-xiso checksum [--silent] <image> [images...]
extract-xiso --checksum <image> [--silent]            # flag form
```

- Flags must precede positional arguments; the first non-flag token ends option parsing (except verbs above which are detected as first token).
- Flags are matched exactly — combined shorts such as `-lr` are **not** supported.
- An unknown flag is treated as a filename (it will then fail with an "open error").
- `-h` prints help; `-v` prints the banner (`extract-xiso v2.7.1 (01.11.14)`); both exit 0.
- With no arguments at all, usage is printed and the tool exits 1.

## Modes

Modes are mutually exclusive unless noted as aliases. If no mode is given, **extract** is the default.

| Flag | Description |
|---|---|
| `-c <dir> [name]` | **Create** an ISO from the contents of `<dir>`. Optional `name` overrides the output filename (may include a path). Repeatable for batch creation. Excludes `-X` patterns; with `-s`, `$SystemUpdate` is skipped automatically. |
| `--pack <input> [name]` | **Pack** a directory into an ISO (1:1 mapping; `name` defaults to the directory name and may include a path), or **repack** an existing ISO in place (rewrite mode, source renamed to `.old`). Translates internally to create or rewrite mode. Already-optimized images are skipped. |
| `-x` | **Extract** (explicit; the default mode). |
| `--unpack <file> [dest]` | **Unpack** the whole image to `dest`, or to a directory named after the ISO (minus `.iso`) in the current directory when omitted. Detects the optimized layout automatically; supports `--skip-sectors`. |
| `-l` | **List** the top-level entries of each ISO (non-recursive). |
| `-t` | **Tree** — recursive listing with full paths, sizes, and totals. |
| `-i <file> [path]` | **Info** — volume descriptor metadata plus per-entry details (sector, size, attributes, left/right child offsets). `path` defaults to `/`. |
| `--ls <file> [path]` | **List directory** — entry names of a directory (default `/`), **without recursion**. Prints one name per line; `/path: empty directory` when empty. Mirrors `ls` on the image. |
| `--xex-info <file> <path>` | **XEX info** — parse and display the Xbox 360 XEX2 executable header of a `.xex` file inside the image (module flags, entry point, image base/size, region, media types, media/title ID, version, disc, encryption/compression). |
| `--md5 <file> [path]` | Compute **MD5** hashes of files **inside** the image. No `path` → hash every file in the image; directory → recursive; file → single hash. Output: lowercase hex + two spaces + path. |
| `--sha256 <file> [path]` | Compute **SHA-256** hashes of files inside the image (same semantics as `--md5`). |
| `-V <file1.xiso> ...` | **Audit** — deep integrity check of one or more images: header, tree walk, sector bounds, cycle detection, reserved attribute bits `0x48` masked, `0x0000` sentinel, optimized tag. Prints `Files checked` / `Dirs checked` / `Result: PASS|FAIL (N issue(s))`. |
| `--batch <dir>` | Process **all `.iso` files** in `<dir>` instead of explicit filenames. Sorted for deterministic order. Works with extract, list, tree, rewrite (`-r`), and audit (`-V`), and `checksum`; rejected with single-ISO modes and explicit filenames. |
| `--batch-recursive` | With `--batch`, search subdirectories recursively. |
| `--copy-out <iso> <path> <dest>` | Copy a single file **or an entire directory** out of an ISO to `<dest>`. |
| `-r` | **Rewrite** each ISO as an optimized ISO (see [Optimized-tag detection](#optimized-tag-detection)). Already-optimized images are skipped. |
| `validate <src> <out>` | Standalone **validation** command — must be the **first** token. See [Validation](validation.md). |
| `--video` | **Redump:** extract video partition (`L0` head + `L1` tail) via `XisoRedump.TryExtractVideo` + `XgdTables` wave tables; writes `*.video.iso`. Fails gracefully when `videoType==-1`. See [Archival](archival.md#video). |
| `--random` | **Redump:** extract random filler/padding (`XisoOperations.ExtractFiller` via `GetXisoRanges`/`MergeRanges`); writes `*.filler`. See [Archival](archival.md#random). |
| `--seed` | **Redump:** extract XGD1 RNG seed (brute-force `XboxPrng`, XGD1 only); writes `*.seed`. See [Archival](archival.md#seed). |
| `--wipe` | **Redump:** zero filler gaps (`XisoOperations.WipeFiller` → `ProcessWipe`); writes `*.wiped.xiso`. Part of `--best`. See [Archival](archival.md#wipe). |
| `--trim` | **Redump:** truncate after last file extent (`ranges[^1].End+1 * SectorSize`); writes `*.trim.xiso`. See [Archival](archival.md#trim). |
| `--petrify` | **Redump:** skeleton — XISO with file extents zeroed + SHA-1 per file (`XisoSkeleton.Petrify`, `CollectFileEntries` sorted); writes skeleton + `*.hash`. See [Archival](archival.md#petrify). |
| `--update` | **Redump:** extract system update `su20076000_00000000` from XGD3 video `L1` tail (`XisoRedump.TryExtractUpdate`, `FindUpdateOffset` `ABCDABCD`); warns on XGD1/2. See [Archival](archival.md#update). |
| `--zar` | **Redump:** create ZArchive/zstd (`XisoZarchive.CreateZar`, skeleton+update+video sidecars). See [Archival](archival.md#zar). |
| `--all` | Alias: `--random --seed --trim --update --video --wipe` (→ `--xiso` as batch). Mirrors XboxKit `-a`. |
| `--best` | Alias: `--trim --wipe` (XISO). Mirrors XboxKit `-b`. |
| `--compress` | Alias: `--petrify --update --video --zar`. Mirrors XboxKit `-c`. Also see xdvdfs `compress`. |
| `--security-sectors <file>` | External `sectors.txt` (`SecuritySectors.cs`, `4096`-sector `start-end` ranges, `4095` validated, sorted `int[]`) threaded through rebuild/video. Repeatable. |
| `rebuild <xiso> [video.iso] [filler|seed] [su…] -o <redump.iso>` | **Rebuild** Redump ISO (`XisoRedump.RebuildRedump`): `L0`+`l0Padding`+game partition scan (filler/PRNG + security-sector zero-skip) + `l1Padding`+`L1` (optionally `l1Trimmed+updateFS+lastSector`). Positional alias `extract-xiso <input.xiso> [files...]` also accepted. See [Archival](archival.md#rebuild). |
| `build-image [sourceDir] [output.iso] -m "host:image" [-f <toml>] [-O output] [-D\|--dry-run]` | **xdvdfs parity:** ordered `wax` remapping (`RemapFilesystem`, `WaxGlob` `*`/`**`/`?`/`[]`/`{a,b}` + `{0}` whole + `{n}` groups, `!negation` first-wins, suffix re-add), `xdvdfs.toml` `[map_rules]`, `--dry-run` via `DryRunRemap` → `CreateFromRemapTree` (`IsRemap` skips CWD). See [xdvdfs Compat](xdvdfs-compat.md#build-image). |
| `image-spec from -O <out> -m "host:image" ... [specPath]` | **xdvdfs parity:** TOML generation (`GenerateSpecText` preserve-order `[metadata] output` + `[map_rules]`), stdout when `specPath` omitted. See [xdvdfs Compat](xdvdfs-compat.md#image-spec). |
| `compress\|cso <src> [out.cso] [--ciso-level 0..9] [--ciso-split N]` | **CISO** compress: `CisoWriter.CompressToCso` pure-managed BCL DEFLATE v1 `0x80000000` + LZ4 v2, `align` 0 (<2 GB)/1 (<4 GB)/2 (else), `threshold +12`, reader `CisoBlockDevice`. Use on `sourceDir` or `image.iso`. `--ciso-split` warns-ignored (compat). See [Compression](compression.md). |
| `decompress\|uncso\|decso <cso> [out.iso]` | **CISO** decompress: `CisoReader.DecompressToIso` handle both versions + random-access. |
| `checksum [--silent] <image> [images...]` / `--checksum <image> [--silent]` | **SHA3-256** image checksum (`XisoChecksum.ComputeImageChecksum`, `SortedDictionary Ordinal` `/path` UTF-8 + streamed data, `xdvdfs` compat). Prints `hex tab path` (silent → hex only). Also `flag` form `--checksum` supports multiple ISOs. See [xdvdfs Compat](xdvdfs-compat.md#checksum). |

## Options

| Flag | Description |
|---|---|
| `-d <directory>` | Extract mode: output directory (created if missing). Rewrite mode: directory for the rewritten ISO. Ignored by list/tree. |
| `-D` | Rewrite mode: delete the `.old` source file after a successful rewrite. |
| `-m` | Disable automatic `.xbe` media-enable patching during create/rewrite (not recommended). |
| `-o <filename>` | Rewrite/rebuild/compress output filename (default: original name with `.iso`/`.cso` extension). For `rebuild` must be `-o <redump.iso>`; for `compress` optional positional. |
| `-q` | Quiet — suppress all non-error output. |
| `-Q` | Silent — suppress all output, including errors. |
| `-s` | Skip `$SystemUpdate` entries. On create this is equivalent to `-X "**/$SystemUpdate/**"`; on extract/rewrite it filters `$SystemUpdate` paths while reading. |
| `-X <glob_pattern>` | **Create mode only.** Exclude files/directories matching the glob pattern. Repeatable. See [Exclude patterns](#exclude-patterns). `WaxGlob` engine also supports `{0}`/`{n}` captures for `build-image`. |
| `--skip-sectors N` | Treat the image as if the XISO filesystem starts `N` sectors (2048 bytes each) into the file — for Redump images with a video partition. Valid in extract, list, tree, rewrite, unpack, video, audit where noted. See [Redump & Disc Layouts](redump-workflows.md). |
| `--prepend-sectors N` | Write the output image with `N` empty sectors before the XISO filesystem, reserving room for a video partition. Valid in create (`-c`) and rewrite (`-r`) modes. See [Redump & Disc Layouts](redump-workflows.md). |
| `--ciso-level 0..9` | CISO compression level (`compress`/`cso`): `0` NoCompression, `1..3` Fastest, `4..6` Optimal, `7..9` SmallestSize (default `9`). Maps to `CompressionLevel` for BCL DEFLATE. |
| `--ciso-split <bytes>` | Compatibility shim for xdvdfs `SplitOutput`; warns and is ignored (C# writes single `.cso`). |
| `-p` | (Hidden) Print usage and exit 1. |

### Exclude patterns

`-X` accepts shell-style glob patterns matched against paths relative to the source
root, using `/` as the separator. Matching is case-insensitive.

| Pattern | Matches |
|---|---|
| `*.tmp` | `.tmp` files **at the root only** (no `**/` prefix → anchored to root) |
| `**/*.tmp` | `.tmp` files at any depth |
| `**/node_modules/**` | any `node_modules` directory (and everything below it) |
| `screenshots/**` | the root-level `screenshots` directory and its contents |
| `**/$SystemUpdate/**` | `$SystemUpdate` directories at any depth (what `-s` implies) |

Supported syntax: `*` (within one segment), `?` (one character), `**` (zero or more
segments as a complete segment), `[abc]`/`[a-z]`/`[!abc]` character classes, and
`\x` escapes. A trailing `/` is equivalent to `/**`.

For `build-image`, the same engine (`WaxGlob`) additionally supports **capture groups**
`{0}` (whole match) and `{1..n}` (per `*`/`**` segment) and ordered evaluation with
`!` negation — see [xdvdfs Compat — Build-Image](xdvdfs-compat.md#build-image).

## Validation flags

These integrate with `-r` or the standalone `validate` command — see
[Validation](validation.md) for the full picture.

| Flag | Description |
|---|---|
| `--validate` | After a rewrite, compare the source and output file trees (counts, paths, sizes). |
| `--validate-checksums` | Also verify SHA-256 checksums per file (slower). |
| `--validate-strict` | Exit with code 2 on any mismatch. |
| `--validate-report <file>` | Write the validation result as a JSON report. |

## Mode combinations and restrictions

Enforced at parse time; violations print an error and exit 1:

| Combination | Result |
|---|---|
| `--skip-sectors` with `-c` | Error |
| `--prepend-sectors` without `-c` or `-r` | Error |
| `--skip-sectors`/`--prepend-sectors` with `-i`, hash, `--copy-out`, `-V`, `validate`, or `--validate*` | Error |
| `-X` without `-c` | Error |
| `-c` with extra positional arguments | Usage error |
| No positional arguments in a non-create/non-verb mode | Usage error |
| `--ciso-level` / `--ciso-split` without `compress`/`cso` | Warn or error (level requires compress) |
| `--security-sectors` without archival verbs (`--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`rebuild`/`--all`/`--best`/`--compress`) | Ignored (no effect) |

Flags that take arguments (`-c`, `-d`, `-o`, `-X`, `--skip-sectors`, `--prepend-sectors`,
`--validate-report`, `--security-sectors`, `--ciso-level`, `--ciso-split`, `-f`, `-m`, `-O`) consume the next token. For `-c`, the optional `name` is consumed
only when the next token does not start with `-`.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (all modes); `-v`; `-h`; `validate` passed; auditing all images; extracting an image with no files (`ErrIsoNoFiles` is treated as success); create succeeded; checksum matched. |
| `1` | Any error: usage, invalid flag values, mode conflicts, file open failures, per-ISO exceptions, invalid ISO, `validate` exceptions, CISO header errors. |
| `2` | Validation failure: `validate` command when the conversion does not pass, or `-r --validate-strict` on mismatch. |

> [!NOTE]
> `err` is a single accumulator across a batch: a later ISO can overwrite an earlier
> exit code. Errors on one ISO do **not** stop processing of the remaining ISOs. One
> exception: an existing `<name>.iso.old` during rewrite is logged as an error but does
> **not** change the exit code — the ISO is skipped and processing continues.

## Batch / multi-ISO processing

- `-l`, `-t`, `-x`, `-r`, `-V`, and `checksum` accept **multiple** ISO files.
- `-i`, `--md5`, `--sha256`, `--copy-out`, and `validate` operate on a single ISO.
- `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`--all`/`--best`/`--compress` are batch modes (run via `RunRedumpBatch`) with `-o` single-file guard.
- Per-ISO counters reset for each image; cumulative counters (`TotalFilesAllIsos`,
  `TotalBytesAllIsos`) span the whole run.
- Per-ISO summary (on success):
  - tree: `N files, M bytes`
  - others: `N files in <path> total M bytes`
  - checksum: `hex tab path`
- Batch summary when more than one ISO was processed: `N files in K xiso's total M bytes`.
- If any warning was issued: `WARNING: Warning(s) were issued during execution--review stderr!`

## Optimized-tag detection

Before processing each ISO the tool reads the **optimized tag** — the 24-byte string
`in!xiso!2.7.1 (01.11.14)` at byte offset 31337 (minimum 7-byte prefix `in!xiso` is
sufficient). Consequences:

- **Rewrite mode**: an optimized image is skipped with "already optimized, skipping...".
  Otherwise the source is renamed to `<name>.iso.old` (aborting if that file already
  exists) and a new optimized ISO is written; `-D` deletes the `.old` afterwards.
- **Extract/list/tree/checksum**: the tag selects the directory right-offset calculation
  (`llCompat = !optimized`). Images without the tag use the legacy linked-list-compatible
  layout; images with the tag use the optimized layout.
- With `--prepend-sectors`, the tag shifts together with the game partition.

## Examples

```bash
# Extract one ISO to a specific directory
extract-xiso -d ./out game.iso

# Unpack the whole image (auto-named output directory)
extract-xiso --unpack game.iso

# Unpack to a specific destination
extract-xiso --unpack game.iso ./out

# Extract several ISOs (default mode)
extract-xiso game1.iso game2.iso game3.iso

# Create with a custom name, skipping $SystemUpdate and temp files
extract-xiso -s -X "**/*.tmp" -c ./game_files custom_name.iso

# Create a Redump-style image (game partition at the XGD2 offset)
extract-xiso -c ./game_files redump.iso --prepend-sectors 129824

# Pack a directory into an ISO (alias-style convenience)
extract-xiso --pack ./game_files

# Repack an existing ISO in place (optimizes it, keeping a .old copy)
extract-xiso --pack game.iso

# Extract a Redump image whose game partition starts at a nonstandard offset
extract-xiso --skip-sectors 129824 -d ./out redump.iso

# Optimize (rewrite) an ISO, then validate the result
extract-xiso -r --validate --validate-strict game.iso

# Validate two images against each other
extract-xiso validate source.iso rebuilt.iso --validate-checksums --validate-report report.json

# Deep-audit several images
extract-xiso -V game1.iso game2.iso

# Batch-process every ISO in a directory (recursive)
extract-xiso -r --batch ./isos --batch-recursive
extract-xiso --batch ./isos -d ./extracted

# Hash all files in an image with SHA-256
extract-xiso --sha256 game.iso

# List the root directory of an image (non-recursive)
extract-xiso --ls game.iso

# List a subdirectory
extract-xiso --ls game.iso /media

# Show the Xbox 360 executable header of a game
# (title ID, entry point, region, media types, ...)
extract-xiso --xex-info game360.iso /default.xex

# Copy one directory out of an image
extract-xiso --copy-out game.iso /media ./media_out

# --- Archival (Redump) ---

# Extract video partition (writes game.video.iso)
extract-xiso --video game.redump.iso

# Extract filler + seed, wipe & trim
extract-xiso --random game.redump.iso
extract-xiso --seed game.redump.iso          # XGD1 only
extract-xiso --wipe game.redump.iso -o wiped.xiso
extract-xiso --trim game.redump.iso -o trimmed.xiso
extract-xiso --all game.redump.iso           # all-of-the-above + video/wipe
extract-xiso --best game.redump.iso          # trim + wipe

# Petrify + update + zar
extract-xiso --petrify game.iso              # skeleton + .hash (SHA-1)
extract-xiso --update game.redump.iso        # XGD3 su20076000_00000000
extract-xiso --zar game.iso -o game.zar

# Rebuild Redump from components (lossless round-trip)
extract-xiso rebuild x.iso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso
extract-xiso rebuild x.iso video.iso --security-sectors sectors.txt -o rebuilt.redump.iso

# With security sectors (4096-sector ranges)
extract-xiso --video --security-sectors sectors.txt game.redump.iso

# --- xdvdfs parity ---

# Ordered remapping (wax captures, negation, dry-run, xdvdfs.toml)
extract-xiso build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
extract-xiso build-image -D -m "!secret/**" -m "**:/{0}" ./src
extract-xiso build-image -f xdvdfs.toml ./src -O out.iso

# Generate TOML spec
extract-xiso image-spec from -O dist/image.iso -m "bin:/" -m "assets:/{0}" xdvdfs.toml

# CISO compress / decompress (DEFLATE v1 + LZ4 v2)
extract-xiso compress ./game_dir game.cso --ciso-level 9
extract-xiso cso game.iso game.cso
extract-xiso decompress game.cso game.iso
extract-xiso uncso game.cso

# SHA3-256 image checksum (deterministic, BTreeMap sorted)
extract-xiso checksum game.iso
extract-xiso checksum --silent game1.iso game2.iso
extract-xiso --checksum game.iso
```

## Notes

- The banner is `extract-xiso v2.7.1 (01.11.14) for <win|linux|macos|cross-platform> - written by in <in@fishtank.com>`.
- `-v` prints the banner to stdout even under `-Q`; usage (`-h`) goes to stderr and is
  never suppressed by quiet modes.
- Info/hash/copy-out/audit/validate/checksum dispatch happens **before** the batch loop, so those
  modes ignore additional positional arguments beyond what they document.
- Archival verbs (`--video`/`--random`/…/`rebuild`) dispatch via `RunRedumpBatch` with
  shared `ExpandIsoFiles` + `securitySectors` handling.
- xdvdfs verbs (`build-image`/`image-spec`/`compress`/`checksum`) are detected as first token and handled before `getopt` parsing.
- Output progress uses carriage returns when writing to a terminal and newlines when
  stdout is redirected, so logs stay readable in CI.

See also: [Getting Started](getting-started.md) · [Validation](validation.md) ·
[Redump & Disc Layouts](redump-workflows.md) · [Archival](archival.md) ·
[xdvdfs Compat](xdvdfs-compat.md) · [Compression](compression.md) · [FAQ](faq.md) ·
[Troubleshooting](troubleshooting.md)
