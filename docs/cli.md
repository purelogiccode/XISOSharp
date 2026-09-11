# CLI Reference

The `XISOSharp` command-line tool mirrors the original C tool's interface and adds a
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
XISOSharp [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
XISOSharp [options] -c <dir> [name] [-c <dir> [name]] ...
XISOSharp validate <source.iso> <output.iso> [options]
XISOSharp rebuild <xiso|game.zar> [video.iso] [filler|seed] [su20076000_00000000] -o <redump.iso>
XISOSharp build-image [sourceDir] [output.iso] -m "host:image" [-f <toml>] [-O output] [-D|--dry-run]
XISOSharp image-spec from -O <out> -m "host:image" ... [specPath]
XISOSharp compress|cso <sourceDir|image.iso> [output.cso] [--ciso-level 0..9] [--ciso-version 1|2|auto] [--ciso-split bytes]
XISOSharp decompress|uncso|decso <cso|.1.cso> [output.iso]
XISOSharp checksum [--silent] <image> [images...]
XISOSharp --checksum <image> [--silent]            # flag form
XISOSharp split [--size <bytes|half>] [--output <base>] <image> [images...]
XISOSharp join [--output <file>] <first.1.iso> [...]
```

- Flags must precede positional arguments; the first non-flag token ends option parsing (except verbs above which are detected as first token).
- Flags are matched exactly — combined shorts such as `-lr` are **not** supported.
- An unknown flag is treated as a filename (it will then fail with an "open error"); a *known* flag in a filename slot fails fast with `must come before ISO filenames` (see [Misplaced flags](#misplaced-flags-upstream-61)).
- `-h` prints help; `-v` prints the banner (`extract-xiso v2.7.1 (01.11.14)`); both exit 0. Help is `-h` ONLY — `--help` is treated as a filename (it will fail with an "open error"), never as help.
- With no arguments at all, usage is printed and the tool exits 1. On an interactive
  double-click launch (Explorer, no redirection) the window additionally waits for a
  keypress (`Press any key to exit...`) instead of closing; scripts, pipes, and
  `XISO_NO_PAUSE=1` never pause.

## Modes

Modes are mutually exclusive unless noted as aliases. If no mode is given, **extract** is the default.

Image inputs accept `.cso`/`.1.cso` files directly (auto-detected by extension, `xdvdfs img.rs` parity): `extract`, `--unpack`, `-l`/`-t`, `--pack` (iso→rewrite), `-r`, and `checksum` all operate on the decompressed view, with outputs named after the game stem (`game.cso` → `game.iso`, extract dir `game/`).

| Flag | Description |
|---|---|
| `-c <dir> [name]` | **Create** an ISO from the contents of `<dir>`. Optional `name` overrides the output filename (may include a path). Repeatable for batch creation. Excludes `-X` patterns; with `-s`, `$SystemUpdate` is skipped automatically. |
| `--pack <input> [name]` | **Pack** a directory into an ISO (1:1 mapping; `name` defaults to the directory name and may include a path), or **repack** an existing ISO in place (rewrite mode, source renamed to `.old`). Translates internally to create or rewrite mode. Already-optimized images are skipped. |
| `-x` | **Extract** (explicit; the default mode). |
| `--unpack <file> [dest]` | **Unpack** the whole image to `dest`, or to a directory named after the ISO (minus `.iso`) in the current directory when omitted. Detects the optimized layout automatically; supports `--skip-sectors` and `--skip-existing` (resume). |
| `-l` | **List** the top-level entries of each ISO (non-recursive). |
| `-t` | **Tree** — recursive listing with full paths, sizes, and totals. |
| `-i <file> [path]` | **Info** — volume descriptor metadata (incl. detected disc format: `RAW` / `GLOBAL (XGD2)` / `XGD3` / `XGD2 Hybrid` / `XGD1`) plus per-entry details (sector, size, attributes, left/right child offsets). `path` defaults to `/`. |
| `--ls <file> [path]` | **List directory** — entry names of a directory (default `/`), **without recursion**. Prints one name per line; `/path: empty directory` when empty. Mirrors `ls` on the image. |
| `--xex-info <file> <path>` | **XEX info** — parse and display the Xbox 360 XEX2 executable header of a `.xex` file inside the image (module flags, entry point, image base/size, region, media types, media/title ID, version, disc, encryption/compression). |
| `--xbe-info <file> <path>` | **XBE info** — parse and display the original-Xbox XBEH header + certificate of a `.xbe` file inside the image (base, entry point, sections, title ID/name, media, region, ratings, disc, version). No reference tool offers this. |
| `--md5 <file> [path]` | Compute **MD5** hashes of files **inside** the image. No `path` → hash every file in the image; directory → recursive; file → single hash. Output: lowercase hex + two spaces + path. |
| `--sha256 <file> [path]` | Compute **SHA-256** hashes of files inside the image (same semantics as `--md5`). |
| `-V <file1.xiso> ...` | **Audit** — deep integrity check of one or more images: header, tree walk, sector bounds, cycle detection, reserved attribute bits `0x48` masked, `0x0000` sentinel, optimized tag. Prints `Files checked` / `Dirs checked` / `Result: PASS|FAIL (N issue(s))`. |
| `--repair <file>` | **Repair** — fix the audit's safely-patchable issues in place: reserved attribute bits, missing optimized tag, separators in filenames. Writes `<file>.old` backup first unless `--no-backup`. Prints `Fixed:` / `Result: PASS|FAIL`. Truncation/structural issues are reported, never patched. See [Repair](api-xisoreader.md#repair). |
| `--dry-run` | With `--repair`, preview the fixes without changing anything (exit mirrors the audit: `1` when fixes would apply). Rejected without `--repair`. |
| `--salvage <file>` | **Salvage** — rebuild a readable image from a corrupt one: carry every entry reachable without tripping a truncation/structural gate into `<file>` without its extension plus `.salvaged.iso` (CISO input allowed; source never modified). Prints `Carried:` / `Dropped:` / `Result: PASS|FAIL`. See [Salvage](api-xisoreader.md#salvage). |
| `--repair-out <path>` | With `--salvage`, write the rebuilt image to `<path>` instead of the default (existing files follow the `-y`/`-n` convention). Rejected without `--salvage`. |
| `--batch <dir>` | Process **all `.iso` files** in `<dir>` instead of explicit filenames. Sorted for deterministic order. Works with extract, list, tree, rewrite (`-r`), and audit (`-V`) only; rejected with single-ISO modes, redump modes, and explicit filenames. |
| `--batch-recursive` | With `--batch`, search subdirectories recursively. Rejected without `--batch`. |
| `--copy-out <iso> <path> <dest>` | Copy a single file **or an entire directory** out of an ISO to `<dest>`. Supports `--skip-existing` (resume) and `--continue-on-error`. |
| `--copy-in <iso> <host> <path>` | Copy a host file **into** an ISO, modifying it in place: replaces `<path>` when it exists, adds it as a new file otherwise. A host directory is rejected fast (`InvalidDataException`, nothing written). Writes an `<iso>.old` backup first unless `--no-backup`. See [CopyIn](api-xisoreader.md#copyin). |
| `--no-backup` | With `--copy-in` or `--repair`, skip the `<iso>.old` backup (rejected without either). |
| `-r` | **Rewrite** each ISO as an optimized ISO (see [Optimized-tag detection](#optimized-tag-detection)). Already-optimized images are skipped. Output is **byte-identical** to `extract-xiso -r` (SHA-256 verified on real dumps): dirent attributes are re-encoded with the DIR/ARC defaults; pass `--preserve-attrs` to keep the source bits instead. |
| `validate <src> <out>` | Standalone **validation** command — must be the **first** token. See [Validation](validation.md). |
| `--video` | **Redump:** extract video partition (`L0` head + `L1` tail) via `XisoRedump.TryExtractVideo` + `XgdTables` wave tables; writes `*.video.iso`. Fails gracefully when `videoType==-1`. See [Archival](archival.md#video). |
| `--random` | **Redump:** extract random filler/padding (`XisoOperations.ExtractFiller` via `GetXisoRanges`/`MergeRanges`); writes `*.filler`. See [Archival](archival.md#random). |
| `--seed` | **Redump:** extract XGD1 RNG seed (brute-force `XboxPrng`, XGD1 only); writes `*.seed`. See [Archival](archival.md#seed). |
| `--wipe` | **Redump:** zero filler gaps (`XisoOperations.WipeFiller` → `ProcessWipe`); writes `*.wiped.xiso`. Part of `--best`. See [Archival](archival.md#wipe). |
| `--trim` | **Redump:** truncate after last file extent (`ranges[^1].End+1 * SectorSize`); writes `*.trim.xiso`. See [Archival](archival.md#trim). |
| `--petrify` | **Redump:** skeleton — XISO with file extents zeroed + SHA-1 per file (`XisoSkeleton.Petrify`, `CollectFileEntries` sorted); writes skeleton + `*.hash`. See [Archival](archival.md#petrify). |
| `--update` | **Redump:** extract system update `su20076000_00000000` from XGD3 video `L1` tail (`XisoRedump.TryExtractUpdate`, `FindUpdateOffset` `ABCDABCD`); warns on XGD1/2. See [Archival](archival.md#update). |
| `--zar` | Create ZArchive/zstd (`XisoZarchive.CreateZar` → `ZArchiveSharp.ZArchiveWriter`, L6 blocks + raw fallback; standalone `--zar <iso> [out.zar]` or Redump-batch zar of the XISO component). Load the result directly in Xenia canary. See [Archival](archival.md#zar). |
| `--jobs <n>` | With a lone `--zar` over several inputs, pack up to `<n>` archives in parallel (default `1`). |
| `--policy <p>` | `--zar` overwrite handling without prompting: `skip` \| `overwrite` \| `auto-rename`. |
| `--all` | Alias: `--random --seed --trim --update --video --wipe` (→ `--xiso` as batch). Mirrors XboxKit `-a`. |
| `--best` | Alias: `--trim --wipe` (XISO). Mirrors XboxKit `-b`. |
| `--compress` | Alias: `--petrify --update --video --zar`. Mirrors XboxKit `-c`. Also see xdvdfs `compress`. |
| `--security-sectors <file>` | Rebuild-only override of the security sector ranges (`start-end` lines, `4096` sectors each, `4095` validated, sorted `int[]`). Alias: `--sectors`. Rejected outside the `rebuild` verb. |
| `rebuild <xiso\|game.zar> [video.iso] [filler\|seed] [su…] -o <redump.iso>` | **Rebuild** Redump ISO (`XisoRedump.RebuildRedump`): `L0`+`l0Padding`+game partition scan (filler/PRNG + security-sector zero-skip) + `l1Padding`+`L1` (optionally `l1Trimmed+updateFS+lastSector`). `<xiso>` accepts a `.zar` sidecar (single embedded XISO verbatim, else tree repacked). Positional alias `XISOSharp <input.xiso> [files...]` also accepted. See [Archival](archival.md#rebuild). |
| `build-image [sourceDir] [output.iso] -m "host:image" [-f <toml>] [-O output] [-D\|--dry-run] [--file-time <value>]` | **xdvdfs parity:** ordered `wax` remapping (`RemapFilesystem`, `WaxGlob` `*`/`**`/`?`/`[]`/`{a,b}` + `{0}` whole + `{n}` groups, `!negation` first-wins, suffix re-add), `xdvdfs.toml` `[map_rules]`, `--dry-run` via `DryRunRemap` → `CreateFromRemapTree` (`IsRemap` skips CWD). In `-m` rules the separator is the first unescaped `:` — write `\:` for a literal colon, `\\` for a backslash. See [xdvdfs Compat](xdvdfs-compat.md#build-image). |
| `image-spec from -O <out> -m "host:image" ... [specPath]` | **xdvdfs parity:** TOML generation (`GenerateSpecText` preserve-order `[metadata] output` + `[map_rules]`), stdout when `specPath` omitted. See [xdvdfs Compat](xdvdfs-compat.md#image-spec). |
| `compress\|cso <src> [out.cso] [--ciso-level 0..9] [--ciso-version 1\|2\|auto] [--ciso-split bytes]` | **CISO** compress: `CisoWriter.CompressToCso` — v2 (default) LZ4 sectors with fixed `align 2`, byte-identical to modern `xdvdfs compress` (pure-managed `lz4_flex` port); v1 BCL DEFLATE `0x80000000` with dynamic `align` 0/1/2; threshold `+12`. Use on `sourceDir` or `image.iso`. Output splits at `0xffbf6000` (~4 GiB) into `.1.cso`/`.2.cso`… parts (xdvdfs `SplitOutput` parity); `--ciso-split 0` writes a single `.cso`. See [Compression](compression.md). |
| `decompress\|uncso\|decso <cso\|.1.cso> [out.iso]` | **CISO** decompress: `CisoReader.DecompressToIso` handles both versions, single files and split `.N.cso` parts. |
| `checksum [--silent] <image> [images...]` / `--checksum <image> [--silent]` | **SHA3-256** image checksum (`XisoChecksum.ComputeImageChecksum`, `SortedDictionary Ordinal` `/path` UTF-8 + streamed data, `xdvdfs` compat). `.cso` / split `.1.cso` inputs are auto-detected by extension and read through `CisoBlockDevice` (`img.rs::open_image` parity), hashing the decompressed view — result identical to the source ISO. Prints `hex tab path` (`--silent` → hex only). Also `flag` form `--checksum` supports multiple ISOs (`--silent` requires `--checksum` in flag form). See [xdvdfs Compat](xdvdfs-compat.md#checksum). |
| `split [--size <bytes\|half>] [--output <base>] <image> [images...]` | **Split** a plain ISO into sector-aligned `<base>.1.iso`, `<base>.2.iso`, … parts (`XisoSplitter.Split`, TODO #17 / xdvdfs #97 — the plain-ISO counterpart to CSO `--ciso-split`). `--size` accepts bytes with `K`/`M`/`G` suffixes or `half`/`halves`; default 4G (FATX cap). Base defaults to the input stem. Existing parts are refused; partial parts are removed on failure/cancel. |
| `join\|joinsplit [--output <file>] <first.1.iso> [...]` | **Reassemble** split parts (`XisoSplitter.Join`). Output defaults to the part stem + `.iso` and must not exist; the result is validated as an XISO afterwards. |

## Options

| Flag | Description |
|---|---|
| `-d <directory>` | Extract mode: output directory (created if missing). Rewrite mode: directory for the rewritten ISO. Ignored by list/tree. Tolerant of batch-script artifacts: trailing separators, UNC paths, spaces — see [Destination directory edge cases](#destination-directory-edge-cases--d). |
| `-D` | Rewrite mode: delete the `.old` source file after a successful rewrite. Also `build-image --dry-run` alias (`-D`/`--dry-run`/`--dryrun` preview host→image pairs without writing). |
| `-m` | In create or rewrite mode, disable automatic `.xbe` media enable patching (not recommended). |
| `-o <filename>` | Rewrite/rebuild/compress output filename (default: original name with `.iso`/`.cso` extension). For `rebuild` must be `-o <redump.iso>`; for `compress` optional positional. |
| `-q` | Quiet — suppress all non-error output. |
| `-Q` | Silent — suppress all output, including errors. |
| `-s` | Skip `$SystemUpdate` entries. On create this is equivalent to `-X "**/$SystemUpdate/**"`; on extract/rewrite it filters `$SystemUpdate` paths while reading. |
| `-X <glob_pattern>` | **Create mode only.** Exclude files/directories matching the glob pattern. Repeatable. See [Exclude patterns](#exclude-patterns). `WaxGlob` engine also supports `{0}`/`{n}` captures for `build-image`. |
| `-y`, `--yes` | Always overwrite output files without prompting (`rebuild`, rewrite `-o`, `compress`, `decompress`, redump batch outputs). |
| `-n`, `--no` | Never overwrite: refuse when an output file exists (prints `[ERROR] File already exists`, skips the operation). Cannot be combined with `-y`. |
| `--skip-sectors N` | Treat the image as if the XISO filesystem starts `N` sectors (2048 bytes each) into the file — for Redump images with a video partition. Valid in extract, list, tree, rewrite (`-r`), `--unpack`, `--filetime`, `--set-filetime`, and `--is-optimized` modes. Rejected with `-c` and with `-i`, `--ls`, `--xex-info`, `--xbe-info`, `--md5`/`--sha256`, `--copy-out`, `--copy-in`, `-V`, `validate`/`--validate*`, redump verbs, `checksum`, `--sector-layout`, and `--ranges`. See [Redump & Disc Layouts](redump-workflows.md). |
| `--prepend-sectors N` | Write the output image with `N` empty sectors before the XISO filesystem, reserving room for a video partition. Valid in create (`-c`) and rewrite (`-r`) modes. See [Redump & Disc Layouts](redump-workflows.md). |
| `--preserve-attrs` | Rewrite (`-r`) mode: re-encode the source dirent attribute bits (RO/HID/SYS/NOR) into the output instead of the extract-xiso parity default (DIR/ARC defaults, `dir=0x10`/`file=0x20`). Default off so rewritten images stay byte-identical to `extract-xiso -r`. |
| `--file-time <value>` | Fixed FILETIME for the volume descriptor on create (`-c`), `--pack`, and `build-image` (values: ISO-8601, decimal raw, `0x` hex, `'now'`, `'0'`). `'0'` writes the xdvdfs deterministic timestamp so identical input produces byte-identical output. See [XisoWriter API](api-xisowriter.md#deterministic-output). |
| `--filetime <image>` | Show the FILETIME volume-descriptor field (ISO-8601 + raw u64; `0` = 1601-01-01, xdvdfs compatible). See [FILETIME](api-xisoreader.md#filetime). |
| `--set-filetime <image> <value>` | Set the FILETIME field (values: ISO-8601, decimal raw, `0x` hex, `'now'`, or `'0'`). Cannot be combined with other modes. See [FILETIME](api-xisoreader.md#filetime). |
| `--sector-layout <image>` | **Sector layout** — full on-disk map (`XisoReader.GetSectorLayout`): volume summary (format, root dir sector/size, total sectors), per-file extents (`path`, start sector, sector count, bytes), and used/free sector ranges (inclusive spans). Offset-0 only (`--skip-sectors` rejected). |
| `--ranges <image>` | **Sector ranges** (`XisoRanges.GetXisoRanges`): system (bone) vs file sector ranges as inclusive `start-end` spans, with counts. Offset-0 only (`--skip-sectors` rejected). |
| `--is-optimized <image>` | Print `<iso>: optimized` or `<iso>: not optimized` (`XisoReader.IsOptimizedImage` tag probe at offset 31337); supports `--skip-sectors`. Always exits 0 on a readable image. |
| `--skip-existing` | In extract, `--unpack`, and `--copy-out` modes, skip files already on disk with matching sizes (logged as `skip: <path>`) instead of overwriting them. Re-run an interrupted unpack to resume it; pairs with `--batch`. See [Resume interrupted unpacks](#resume-interrupted-unpacks). |
| `--continue-on-error` | In extract, `--unpack`, and `--copy-out` modes, log per-file failures (`Error: Failed to extract ...`) and continue with the next entry instead of aborting. An uncreatable directory skips its subtree. The run still ends with a `Failed to unpack image` summary and a non-zero exit code. See [Extraction robustness](#extraction-robustness). |
| `--ciso-level 0..9` | CISO compression level (`compress`/`cso`, default `9`). v1: maps to `CompressionLevel` for BCL DEFLATE (`0` NoCompression, `1..3` Fastest, `4..6` Optimal, `7..9` SmallestSize). v2: `0` = store all plain, `1..9` = LZ4 acceleration `10 - level` (level 9 byte-identical to xdvdfs). |
| `--ciso-version 1\|2\|auto` | CISO payload codec (`compress`/`cso`). Default `2` (LZ4, `align 2` — modern xdvdfs parity); `1` = classic DEFLATE. |
| `--ciso-split <bytes>` | Split point for `.1.cso`/`.2.cso`… output (`compress`/`cso`). Default `0xffbf6000` (~4 GiB, xdvdfs `SplitOutput`); `0` = single `.cso`. |
| `-p` | (Hidden) Print usage and exit 1. |

### Overwrite behavior

File-producing verbs (`rebuild`, rewrite with `-o`, `compress`, `decompress`, and the
redump batch flags `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`)
check their output path before writing (`XboxKit/Helpers.cs::ConfirmOverwrite` parity):
an existing output prints `[WARNING] File already exists` and prompts
`Would you like to overwrite? (Y/N)` — only `Y`/`YES` (case-insensitive) proceeds.
`-y`/`--yes` skips the prompt (always overwrites); `-n`/`--no` refuses without
prompting (the operation is skipped; batch runs continue with the remaining outputs
but exit 1). Per-file extract/unpack outputs are not gated. For `compress` with split
output the first part (`<base>.1.cso`) is the probe path; rewrite checks `-o` before
moving the input aside to `<name>.old`.

### Input==output safety guard

Separately from the prompt above, an output that points back at one of its inputs is
**refused outright** (exit 1, before any prompt, move, or write) — it can never be
what you meant:

- rewrite `-o` equal to the input (`omit -o to rewrite in place`), or to the
  `<name>.old` backup the rewrite itself needs;
- any other single-input `-o` (wipe/trim/compress/decompress/…) equal to the input;
- `rebuild -o` equal to any component (xiso, video, filler/seed) or the sectors file;
- `compress` whose split output parts (`.1.cso`/`.2.cso`…) would overwrite the source.

The only same-path write allowed is the one with explicit in-place semantics and no
`-o`: `TrimXiso(input, input)` (safe `SetLength` truncation), and rewrite without
`-o` (which works via the `.old` rename). Diagnostics name both sides, e.g.
`Error: rewrite output game.iso is the same file as the input; omit -o to rewrite in place`.

### Resume interrupted unpacks

An unpack killed mid-run (Ctrl+C, power loss, disk full) leaves a partial destination.
Re-run the same command with `--skip-existing` to resume: every file already on disk
**with the same byte size** is left untouched and logged as `skip: <path>`; missing
files — and short files from torn writes — are written normally.

```bash
XISOSharp --unpack game.iso ./out                 # interrupted halfway
XISOSharp --skip-existing --unpack game.iso ./out # resumes: skips done files
XISOSharp --skip-existing --batch ./isos -d ./out # bulk runs resume per image
```

Notes:

- Size is the identity signal: XISO stores no per-file timestamps, so a same-size
  file is assumed to be a complete earlier write (even if its content differs — the
  flag means "don't touch what's there").
- Cancellation is honored per entry, so Ctrl+C stops promptly; the working directory
  is always restored.
- `--skip-existing` is rejected outside extract / `--unpack` / `--copy-out` (exit 1).

### Extraction robustness

A damaged image or a hostile destination no longer dies with a bare OS error
(upstream xdvdfs #187: the web unpacker could only report `Failed to create
file X`). Every per-file failure throws `ExtractFileException`, which names the
entry, its sector, and expected vs actual bytes, with the OS error as the inner
exception:

```bash
XISOSharp -x -d ./out game.iso
# Error: Failed to extract "./out/videos/intro.wmv" (sector 1234, 20000 bytes) -> "intro.wmv": could not create output file: ...
```

Extracted files are integrity-checked two ways: an entry whose data range lies
past the end of the image is refused before its destination is created, and the
bytes on disk are re-statted after the copy — a short image (truncated
download, torn file, entry pointing past the end) fails the file with
`ErrFileTruncated` instead of leaving a short file behind. (The old code merely
warned — and spun forever on a 0-byte read at end of image.)

With `--continue-on-error`, a failed file is logged and skipped while the rest
of the image still extracts; an uncreatable directory skips its whole subtree.
The run still ends with a summary naming every failure, and a non-zero exit:

```bash
XISOSharp --continue-on-error --unpack game.iso ./out
# Error: Failed to extract ... (logged per file as it happens)
# Failed to unpack image "game.iso": 2 file(s) failed:
#   Failed to extract ...
```

Notes:

- Structural corruption (an unreadable directory table) still aborts immediately:
  after a mid-table failure the stream position is unknowable, so continuing
  siblings would be unsound. Table hardening is TODO #16.
- Failed files are excluded from the file/byte totals and `FileAdded` progress.
- `--continue-on-error` is rejected outside extract / `--unpack` / `--copy-out`
  (exit 1), and pairs with `--skip-existing` and `--batch`.

### Destination directory edge cases (`-d`)

Batch scripts build `-d` values by concatenation, so the usual artifacts are
tolerated (upstream #61):

- Trailing separators (`-d .\new\game\`, doubled, or mixed `/\`) are stripped —
  including the filesystem-root case (`-d C:\` stays `C:\`, never the
  drive-relative `C:` that `Path.Combine` would mis-resolve).
- UNC destinations (`-d \\server\share\dir`), `\\?\`-prefixed paths, and
  directories with spaces all work; an unreachable host fails fast with an
  `IOException` instead of hanging.
- An empty `-d` (`-d "%UNSET_VAR%"`) is rejected with
  `Output path must not be empty` (exit 1).
- A `-d` pointing at an existing *file* fails with an `IOException`; the
  process working directory is restored either way.

### Misplaced flags (upstream #61)

`extract-xiso game.iso -d ./new/` never worked upstream: `getopt` stops at
the first filename, so `-d` was opened as an image (`open error: -d`). This
CLI keeps the flags-first contract, but a known flag spelling in a filename
slot now fails fast with a named error instead of a bogus open attempt:

```bash
XISOSharp game.iso -d ./new/
# Error: -d must come before ISO filenames (e.g. -x -d <value> game.iso); a flag after the first filename is read as a filename
```

The check is skipped when the token exists on disk, so a file literally
named like a flag keeps working.

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
[Validation](validation.md) for the full picture. They require `-r` (rewrite)
or `validate` mode (mismatch exits 2); the flavor flags imply `--validate`.

| Flag | Description |
|---|---|
| `--validate` | After a rewrite, compare the source and output file trees (counts, paths, sizes). |
| `--validate-checksums` | Also verify SHA-256 checksums per file (slower). Implies `--validate`. |
| `--validate-strict` | Fail with exit code 2 on any mismatch (retained for parity; mismatches now exit 2 with or without it). Implies `--validate`. |
| `--validate-report <file>` | Write the validation result as a JSON report. Implies `--validate`. |

## Mode combinations and restrictions

Enforced at parse time; violations print an error and exit 1:

| Combination | Result |
|---|---|
| `--skip-sectors` with `-c` | Error |
| `--prepend-sectors` without `-c` or `-r` | Error |
| `--preserve-attrs` without `-r` | Error |
| `--skip-sectors`/`--prepend-sectors` with `-i`, `--ls`, `--xex-info`, `--xbe-info`, hash, `--copy-out`, `--copy-in`, `-V`, `validate`/`--validate*`, redump verbs, `checksum`, `--sector-layout`, or `--ranges` | Error |
| `--validate`/`--validate-checksums`/`--validate-strict`/`--validate-report` without `-r` or `validate` | Error |
| `-X` without `-c` | Error |
| `--skip-existing` without extract/`--unpack`/`--copy-out` (e.g. with `-l`, `-t`, `-r`, `-c`, redump verbs) | Error |
| `--continue-on-error` without extract/`--unpack`/`--copy-out` | Error |
| `--batch` without extract/list/tree/rewrite/audit (e.g. with `--copy-out`, `--copy-in`, `validate`, redump, `checksum`, `--filetime`, `--sector-layout`, `--ranges`, `--is-optimized`) | Error |
| `--batch-recursive` without `--batch` | Error |
| `--no-backup` without `--copy-in` or `--repair` | Error |
| `--dry-run` without `--repair` | Error |
| `--repair-out` without `--salvage` | Error |
| `--silent` without `--checksum` (flag form) | Error |
| `--filetime`/`--set-filetime` with redump or `--batch` | Error |
| `-c` with extra positional arguments | Usage error |
| `-y` with `-n` | Error (`[ERROR] Cannot use both --no (-n) and --yes (-y)`) |
| No positional arguments in a non-create/non-verb mode | Usage error |
| `--ciso-level` / `--ciso-version` / `--ciso-split` without `compress`/`cso` | Error (verb-only flags, parsed by `RunCompressMode`) |
| `--security-sectors`/`--sectors` outside the `rebuild` verb | Error (`only supported by the rebuild verb`) |

Flags that take arguments (`-c`, `-d`, `-o`, `-X`, `--skip-sectors`, `--prepend-sectors`,
`--validate-report`, `--security-sectors`/`--sectors`, `--ciso-level`, `--ciso-version`, `--ciso-split`, `--jobs`, `--policy`, `--repair-out`, `-f`, `-m`/`--map`, `-O`/`--output`) consume the next token. For `-c`, the optional `name` is consumed
only when the next token does not start with `-`.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (all modes); `-v`; `-h`; `validate` passed; auditing all images; extracting an image with no files (`ErrIsoNoFiles` is treated as success); create succeeded; checksum matched. |
| `1` | Any error: usage, invalid flag values, mode conflicts, file open failures, per-ISO exceptions, invalid ISO, `validate` exceptions, CISO header errors. |
| `2` | Validation failure: `validate` command when the conversion does not pass, or `-r --validate` on mismatch (`--validate-strict` retained for parity; mismatches exit 2 with or without it). |

> [!NOTE]
> `err` is a single accumulator across a batch: a later ISO can overwrite an earlier
> exit code. Errors on one ISO do **not** stop processing of the remaining ISOs. One
> exception: an existing `<name>.iso.old` during rewrite is logged as an error but does
> **not** change the exit code — the ISO is skipped and processing continues.

## Batch / multi-ISO processing

- `-l`, `-t`, `-x`, `-r`, `-V`, and `checksum` accept **multiple** ISO files.
- `-i`, `--md5`, `--sha256`, `--copy-out`, `--copy-in`, and `validate` operate on a single ISO.
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
XISOSharp -d ./out game.iso

# Unpack the whole image (auto-named output directory)
XISOSharp --unpack game.iso

# Unpack to a specific destination
XISOSharp --unpack game.iso ./out

# Resume an interrupted unpack (skips files already on disk)
XISOSharp --skip-existing --unpack game.iso ./out

# Extract several ISOs (default mode)
XISOSharp game1.iso game2.iso game3.iso

# Create with a custom name, skipping $SystemUpdate and temp files
XISOSharp -s -X "**/*.tmp" -c ./game_files custom_name.iso

# Create a Redump-style image (game partition at the XGD2 offset)
XISOSharp -c --prepend-sectors 129824 ./game_files redump.iso

# Pack a directory into an ISO (alias-style convenience)
XISOSharp --pack ./game_files

# Repack an existing ISO in place (optimizes it, keeping a .old copy)
XISOSharp --pack game.iso

# Extract a Redump image whose game partition starts at a nonstandard offset
XISOSharp --skip-sectors 129824 -d ./out redump.iso

# Optimize (rewrite) an ISO, then validate the result
XISOSharp -r --validate --validate-strict game.iso

# Validate two images against each other
XISOSharp validate --validate-checksums --validate-report report.json source.iso rebuilt.iso

# Deep-audit several images
XISOSharp -V game1.iso game2.iso

# Batch-process every ISO in a directory (recursive)
XISOSharp -r --batch ./isos --batch-recursive
XISOSharp --batch ./isos -d ./extracted
XISOSharp --skip-existing --batch ./isos -d ./extracted  # resume interrupted bulk extract

# Hash all files in an image with SHA-256
XISOSharp --sha256 game.iso

# List the root directory of an image (non-recursive)
XISOSharp --ls game.iso

# List a subdirectory
XISOSharp --ls game.iso /media

# Sector map, sector ranges, optimized-tag probe
XISOSharp --sector-layout game.iso
XISOSharp --ranges game.iso
XISOSharp --is-optimized game.iso

# Show the Xbox 360 executable header of a game
# (title ID, entry point, region, media types, ...)
XISOSharp --xex-info game360.iso /default.xex

# Show the original-Xbox executable header + certificate
# (title ID/name, media, region, ...)
XISOSharp --xbe-info game.iso /default.xbe

# Copy one directory out of an image
XISOSharp --copy-out game.iso /media ./media_out

# Patch one file into an image (replaces /config.ini; keeps game.iso.old backup)
XISOSharp --copy-in game.iso ./my-config.ini /config.ini

# --- Archival (Redump) ---

# Extract video partition (writes game.video.iso)
XISOSharp --video game.redump.iso

# Extract filler + seed, wipe & trim
XISOSharp --random game.redump.iso
XISOSharp --seed game.redump.iso          # XGD1 only
XISOSharp --wipe -o wiped.xiso game.redump.iso
XISOSharp --trim -o trimmed.xiso game.redump.iso
XISOSharp --all game.redump.iso           # all-of-the-above + video/wipe
XISOSharp --best game.redump.iso          # trim + wipe

# Petrify + update + zar
XISOSharp --petrify game.iso              # skeleton + .hash (SHA-1)
XISOSharp --update game.redump.iso        # XGD3 su20076000_00000000
XISOSharp --zar -o game.zar game.iso

# Rebuild Redump from components (lossless round-trip)
XISOSharp rebuild x.iso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso
XISOSharp rebuild x.iso video.iso --security-sectors sectors.txt -o rebuilt.redump.iso
XISOSharp rebuild game.zar video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso   # .zar sidecar as <xiso>

# With security sectors (rebuild only; 4096-sector ranges)
XISOSharp rebuild x.iso video.iso filler.bin su20076000_00000000 --security-sectors sectors.txt -o rebuilt.redump.iso

# --- xdvdfs parity ---

# Ordered remapping (wax captures, negation, dry-run, xdvdfs.toml)
XISOSharp build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp build-image -D -m "!secret/**" -m "**:/{0}" ./src
XISOSharp build-image -f xdvdfs.toml ./src -O out.iso

# Generate TOML spec
XISOSharp image-spec from -O dist/image.iso -m "bin:/" -m "assets:/{0}" xdvdfs.toml

# CISO compress / decompress (DEFLATE v1 + LZ4 v2)
XISOSharp compress ./game_dir game.cso --ciso-level 9
XISOSharp cso game.iso game.cso --ciso-version 2 --ciso-split 0
XISOSharp decompress game.cso game.iso
XISOSharp uncso game.cso
XISOSharp decso game.cso

# SHA3-256 image checksum (deterministic, BTreeMap sorted)
XISOSharp checksum game.iso
XISOSharp checksum --silent game1.iso game2.iso
XISOSharp --checksum game.iso
```

## Notes

- The banner is `extract-xiso v2.7.1 (01.11.14) for <win|linux|macos|cross-platform> - written by in <in@fishtank.com>`.
- `-v` prints the banner to stdout even under `-Q`; usage (`-h`) goes to stderr and is
  never suppressed by quiet modes.
- Info/hash/copy-out/copy-in/audit/validate/checksum/filetime dispatch happens **before** the batch loop, so those
  modes ignore additional positional arguments beyond what they document.
- Archival verbs (`--video`/`--random`/…/`--zar`/`--all`/`--best`/`--compress`) dispatch via `RunRedumpBatch` with
  shared `ExpandIsoFiles` (`--jobs`/`--policy` thread through the `--zar` path); `rebuild` has its own parser with `--security-sectors`/`--sectors`.
- xdvdfs verbs (`build-image`/`image-spec`/`compress`/`decompress`/`checksum`/`split`/`join`) are detected as first token and handled before `getopt` parsing.
- Output progress uses carriage returns when writing to a terminal and newlines when
  stdout is redirected, so logs stay readable in CI.

See also: [Getting Started](getting-started.md) · [Validation](validation.md) ·
[Redump & Disc Layouts](redump-workflows.md) · [Archival](archival.md) ·
[xdvdfs Compat](xdvdfs-compat.md) · [Compression](compression.md) · [FAQ](faq.md) ·
[Troubleshooting](troubleshooting.md)
