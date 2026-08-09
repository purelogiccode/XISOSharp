# CLI Reference

The `extract-xiso` command-line tool mirrors the original C tool's interface and adds a
number of modern conveniences. This page is the complete reference: syntax, modes,
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
```

- Flags must precede positional arguments; the first non-flag token ends option parsing.
- Flags are matched exactly — combined shorts such as `-lr` are **not** supported.
- An unknown flag is treated as a filename (it will then fail with an "open error").
- `-h` prints help; `-v` prints the banner (`extract-xiso v2.7.1 (01.11.14)`); both exit 0.
- With no arguments at all, usage is printed and the tool exits 1.

## Modes

Modes are mutually exclusive. If no mode is given, **extract** is the default.

| Flag | Description |
|---|---|
| `-c <dir> [name]` | **Create** an ISO from the contents of `<dir>`. Optional `name` overrides the output filename (may include a path). Repeatable for batch creation. Excludes `-X` patterns; with `-s`, `$SystemUpdate` is skipped automatically. |
| `-x` | **Extract** (explicit; the default mode). |
| `-l` | **List** the top-level entries of each ISO (non-recursive). |
| `-t` | **Tree** — recursive listing with full paths, sizes, and totals. |
| `-i <file> [path]` | **Info** — volume descriptor metadata plus per-entry details (sector, size, attributes, left/right child offsets). `path` defaults to `/`. |
| `--md5 <file> [path]` | Compute **MD5** hashes of files **inside** the image. No `path` → hash every file in the image; directory → recursive; file → single hash. Output: lowercase hex + two spaces + path. |
| `--sha256 <file> [path]` | Compute **SHA-256** hashes of files inside the image (same semantics as `--md5`). |
| `-V <file1.xiso> ...` | **Audit** — deep integrity check of one or more images: header, tree walk, sector bounds, cycle detection, reserved attribute bits, optimized tag. Prints `Files checked` / `Dirs checked` / `Result: PASS|FAIL (N issue(s))`. |
| `--copy-out <iso> <path> <dest>` | Copy a single file **or an entire directory** out of an ISO to `<dest>`. |
| `-r` | **Rewrite** each ISO as an optimized ISO (see [Optimized-tag detection](#optimized-tag-detection)). Already-optimized images are skipped. |
| `validate <src> <out>` | Standalone **validation** command — must be the **first** token. See [Validation](validation.md). |

## Options

| Flag | Description |
|---|---|
| `-d <directory>` | Extract mode: output directory (created if missing). Rewrite mode: directory for the rewritten ISO. Ignored by list/tree. |
| `-D` | Rewrite mode: delete the `.old` source file after a successful rewrite. |
| `-m` | Disable automatic `.xbe` media-enable patching during create/rewrite (not recommended). |
| `-o <filename>` | Rewrite mode: custom output filename (default: original name with `.iso` extension). |
| `-q` | Quiet — suppress all non-error output. |
| `-Q` | Silent — suppress all output, including errors. |
| `-s` | Skip `$SystemUpdate` entries. On create this is equivalent to `-X "**/$SystemUpdate/**"`; on extract/rewrite it filters `$SystemUpdate` paths while reading. |
| `-X <glob_pattern>` | **Create mode only.** Exclude files/directories matching the glob pattern. Repeatable. See [Exclude patterns](#exclude-patterns). |
| `--skip-sectors N` | Treat the image as if the XISO filesystem starts `N` sectors (2048 bytes each) into the file — for Redump images with a video partition. Valid in extract, list, tree, and rewrite modes. See [Redump & Disc Layouts](redump-workflows.md). |
| `--prepend-sectors N` | Write the output image with `N` empty sectors before the XISO filesystem, reserving room for a video partition. Valid in create (`-c`) and rewrite (`-r`) modes. See [Redump & Disc Layouts](redump-workflows.md). |
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
| No positional arguments in a non-create mode | Usage error |

Flags that take arguments (`-c`, `-d`, `-o`, `-X`, `--skip-sectors`, `--prepend-sectors`,
`--validate-report`) consume the next token. For `-c`, the optional `name` is consumed
only when the next token does not start with `-`.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (all modes); `-v`; `-h`; `validate` passed; auditing all images; extracting an image with no files (`ErrIsoNoFiles` is treated as success); create succeeded. |
| `1` | Any error: usage, invalid flag values, mode conflicts, file open failures, per-ISO exceptions, invalid ISO, `validate` exceptions. |
| `2` | Validation failure: `validate` command when the conversion does not pass, or `-r --validate-strict` on mismatch. |

> [!NOTE]
> `err` is a single accumulator across a batch: a later ISO can overwrite an earlier
> exit code. Errors on one ISO do **not** stop processing of the remaining ISOs. One
> exception: an existing `<name>.iso.old` during rewrite is logged as an error but does
> **not** change the exit code — the ISO is skipped and processing continues.

## Batch / multi-ISO processing

- `-l`, `-t`, `-x`, `-r`, and `-V` accept **multiple** ISO files.
- `-i`, `--md5`, `--sha256`, `--copy-out`, and `validate` operate on a single ISO.
- Per-ISO counters reset for each image; cumulative counters (`TotalFilesAllIsos`,
  `TotalBytesAllIsos`) span the whole run.
- Per-ISO summary (on success):
  - tree: `N files, M bytes`
  - others: `N files in <path> total M bytes`
- Batch summary when more than one ISO was processed: `N files in K xiso's total M bytes`.
- If any warning was issued: `WARNING: Warning(s) were issued during execution--review stderr!`

## Optimized-tag detection

Before processing each ISO the tool reads the **optimized tag** — the 24-byte string
`in!xiso!2.7.1 (01.11.14)` at byte offset 31337 (minimum 7-byte prefix `in!xiso` is
sufficient). Consequences:

- **Rewrite mode**: an optimized image is skipped with "already optimized, skipping...".
  Otherwise the source is renamed to `<name>.iso.old` (aborting if that file already
  exists) and a new optimized ISO is written; `-D` deletes the `.old` afterwards.
- **Extract/list/tree**: the tag selects the directory right-offset calculation
  (`llCompat = !optimized`). Images without the tag use the legacy linked-list-compatible
  layout; images with the tag use the optimized layout.

## Examples

```bash
# Extract one ISO to a specific directory
extract-xiso -d ./out game.iso

# Extract several ISOs (default mode)
extract-xiso game1.iso game2.iso game3.iso

# Create with a custom name, skipping $SystemUpdate and temp files
extract-xiso -s -X "**/*.tmp" -c ./game_files custom_name.iso

# Create a Redump-style image (game partition at the XGD2 offset)
extract-xiso -c ./game_files redump.iso --prepend-sectors 129824

# Extract a Redump image whose game partition starts at a nonstandard offset
extract-xiso --skip-sectors 129824 -d ./out redump.iso

# Optimize (rewrite) an ISO, then validate the result
extract-xiso -r --validate --validate-strict game.iso

# Validate two images against each other
extract-xiso validate source.iso rebuilt.iso --validate-checksums --validate-report report.json

# Deep-audit several images
extract-xiso -V game1.iso game2.iso

# Hash all files in an image with SHA-256
extract-xiso --sha256 game.iso

# Copy one directory out of an image
extract-xiso --copy-out game.iso /media ./media_out
```

## Notes

- The banner is `extract-xiso v2.7.1 (01.11.14) for <win|linux|macos|cross-platform> - written by in <in@fishtank.com>`.
- `-v` prints the banner to stdout even under `-Q`; usage (`-h`) goes to stderr and is
  never suppressed by quiet modes.
- Info/hash/copy-out/audit/validate dispatch happens **before** the batch loop, so those
  modes ignore additional positional arguments beyond what they document.
- Output progress uses carriage returns when writing to a terminal and newlines when
  stdout is redirected, so logs stay readable in CI.

See also: [Getting Started](getting-started.md) · [Validation](validation.md) ·
[Redump & Disc Layouts](redump-workflows.md) · [FAQ](faq.md) ·
[Troubleshooting](troubleshooting.md)
