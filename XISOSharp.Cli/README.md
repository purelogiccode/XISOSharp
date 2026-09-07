# XISOSharp CLI (`XISOSharp.Cli`)

Command-line tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images. Published as `XISOSharp(.exe)` (see `publish-cli.ps1`).

This project is a direct conversion of the [extract-xiso](https://github.com/XboxDev/extract-xiso) CLI tool (v2.7.1) from C to C#. It provides the same interface and produces byte-identical output for all operations.

## Usage

```
XISOSharp [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
XISOSharp [options] -c <dir> [name] [-c <dir> [name]] ...
```

### Modes (mutually exclusive)

| Flag | Description |
|---|---|
| `-c <dir> [name]` | Create xiso from file(s) starting in `<dir>` |
| `--pack <input> [name]` | Pack a directory into an ISO, or repack an existing ISO in place |
| `-x` | Extract xiso(s) (the default mode) |
| `--unpack <file> [dest]` | Unpack the whole image (auto-named `./<game>/` when dest omitted) |
| `--copy-out <iso> <path> <dest>` | Copy a file or directory out of an xiso |
| `--copy-in <iso> <host> <path>` | Copy a host file into an xiso (replace or add; writes `<iso>.old` backup unless `--no-backup`) |
| `-i <file> [path]` | Show volume info and directory entry metadata |
| `-l` | List files in xiso(s) |
| `-t` | List all files recursively with sizes (tree) |
| `--ls <file> [path]` | Flat directory listing (names only) |
| `--md5 <file> [path]` | Compute MD5 hash of file(s) in xiso |
| `--sha256 <file> [path]` | Compute SHA-256 hash of file(s) in xiso |
| `--xex-info <file> <path>` | Xbox 360 XEX2 header of an executable in the image |
| `--xbe-info <file> <path>` | Original-Xbox XBEH header + certificate of an executable in the image |
| `-V <file1.xiso> ...` | Deep-audit xiso(s): validate header, tree, sectors |
| `--repair <file>` | Fix audit-flagged issues in place (`.old` backup; `--dry-run` previews; `--no-backup` skips backup) |
| `--salvage <file>` | Rebuild reachable entries into a fresh audited `.iso` (`--repair-out` overrides output) |
| `-r` | Rewrite xiso(s) as optimized xiso(s) |
| `validate <src> <out>` | Validate conversion between two images (+ `--validate*` report/strict/checksums; flavors imply `--validate`, require `-r`/`validate`, mismatch exits 2) |
| `checksum [images...]` / `--checksum` | SHA3-256 deterministic image checksum (`--silent` → hex only) |
| `split` / `join`/`joinsplit` | Split a plain ISO into `<base>.1.iso`… parts / reassemble them |
| `--filetime <image>` / `--set-filetime <image> <value>` | Show / set the FILETIME volume field |
| `--batch <dir>` | Process all `.iso` files in `<dir>` (extract/list/tree/rewrite/audit only) + `--batch-recursive` |
| `compress` / `decompress` (`cso`/`uncso`/`decso`) | CISO v2 LZ4 (default) / v1 DEFLATE round-trip (`--ciso-level`/`--ciso-version`/`--ciso-split`), incl. split `.N.cso` |
| `build-image` / `image-spec` | xdvdfs-parity ordered packing from globs (`\:` colon escaping) / TOML spec |
| `--video` / `--random` / `--seed` / `--wipe` / `--trim` / `--petrify` / `--update` / `--zar` | Redump archival verbs (+ `--jobs`/`--policy` for `--zar`, `--all`/`--best`/`--compress` aliases; `--security-sectors` rebuild-only) |
| `rebuild` | Rebuild a lossless Redump image from components |

Help is `-h` ONLY (`--help` is treated as a filename). Full flag reference (every option, exit code, matrix): [CLI Reference](../docs/cli.md).

### Options

| Flag | Description |
|---|---|
| `-d <directory>` | In extract mode, expand xiso in `<directory>`. In rewrite mode, rewrite xiso in `<directory>` |
| `-D` | In rewrite mode, delete old xiso after processing |
| `-h` | Print help text and exit |
| `-m` | Disable automatic `.xbe` media enable patching |
| `-o <filename>` | In rewrite mode, set custom output filename (default: original name with `.iso` extension) |
| `-q` | Quiet (suppress all non-error output) |
| `-Q` | Silent (suppress all output) |
| `-s` | Skip `$SystemUpdate` folder |
| `-v` | Print version information and exit |

### New Features (beyond extract-xiso)

These commands are not present in the original C tool:

- **`-t`** — Tree listing with file sizes and totals
- **`-i`** — Volume metadata and directory entry inspection (incl. friendly disc-layout identity)
- **`-V`** — Deep integrity audit (header, tag, tree, sector bounds, cycle detection)
- **`--repair` / `--dry-run`** — In-place fix of audit-flagged issues (`.old` backup)
- **`--salvage` / `--repair-out`** — Rebuild reachable entries from a damaged image into a fresh audited `.iso`
- **`-o`** — Custom output filename for rewrite mode
- **`--unpack` / `--batch`** — Whole-image unpack; sorted bulk processing
- **`--pack`** — Directory → create, ISO → rewrite
- **`--copy-out`** — Selective file/directory extraction
- **`--copy-in`** — Patch a host file into an image in place (host directories rejected fast)
- **`--md5` / `--sha256`** — Per-file hash computation
- **`--xex-info` / `--xbe-info`** — Executable header parsing (no reference tool covers XBE)
- **`--skip-existing` / `--continue-on-error`** — Resume and per-file robustness
- **`checksum` / `validate`** — Deterministic SHA3-256 checksums; conversion validation + JSON reports
- **`split` / `join`** — FATX-friendly part splitting and reassembly
- **Redump archival** — `--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`/`rebuild`
- **xdvdfs parity** — `build-image`/`image-spec`, CISO compress/decompress, `BlockDevice` random-access

## License

MIT
