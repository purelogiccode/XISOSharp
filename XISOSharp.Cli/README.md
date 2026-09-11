# XISOSharp CLI (`XISOSharp.Cli`)

Command-line tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images. Ships as `XISOSharp(.exe)` (see [Install](#install) below).

This project is a direct conversion of the [extract-xiso](https://github.com/XboxDev/extract-xiso) CLI tool (reference build [202609111233](https://github.com/XboxDev/extract-xiso/releases/tag/build-202609111233)) from C to C#. It provides the same interface and produces byte-identical output for all operations, plus 35+ extra modes (Redump archival, xdvdfs parity, audit/repair/salvage, CISO, checksums).

## Install

```bash
# Framework-dependent build (requires the .NET 10 SDK, pinned in global.json)
dotnet build XISOSharp.Cli -c Release
# bin: XISOSharp.Cli/bin/Release/net10.0/XISOSharp.Cli(.exe)

# Self-contained trimmed single-file (no runtime needed), renamed to XISOSharp(.exe)
./publish-cli.ps1
# binaries land in publish/<rid>/XISOSharp(.exe), ~14-16 MB each
# RIDs: win-x86, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64

# ... or one RID manually (single-file + trimmed come from the csproj defaults)
dotnet publish XISOSharp.Cli -c Release -f net10.0 -r win-x64 --self-contained
```

> [!TIP]
> Double-clicking `XISOSharp.exe` in Explorer prints the usage text and **keeps the
> window open** (`Press any key to exit...`) instead of flashing away. The pause only
> happens on an interactive no-argument launch — scripts, pipes, test hosts, and
> `XISO_NO_PAUSE=1` are never blocked.

## Automatic update checks

On every launch the CLI compares its version against the latest GitHub release
(at most one network request per 24 hours — the result is cached under
`%LocalAppData%/XISOSharp/update-check.json`). When a newer release exists you get
a stderr notice with the release URL and the matching platform asset
(`release_<version>_<rid>.zip`, e.g. `release_1.0.0_win-x64.zip`), and on an
interactive console you are offered to open the release page in your browser.

```text
[UPDATE] XISOSharp 1.0.3 is available (you have 1.0.2).
[UPDATE] Download: https://github.com/purelogiccode/XISOSharp/releases/download/1.0.3/release_1.0.3_win-x64.zip
[UPDATE] Release notes: https://github.com/purelogiccode/XISOSharp/releases/tag/1.0.3
Open the release page in your browser now? [y/N]:
```

Skipped (no network, no output) for `-q`/`-Q`/`-v` runs and under test hosts.
Set `XISO_NO_UPDATE_CHECK=1` to disable the check entirely. The check never
fails the run and never files a bug report.

## Usage

```
XISOSharp [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
XISOSharp [options] -c <dir> [name] [-c <dir> [name]] ...
XISOSharp validate <source.iso> <output.iso> [options]
XISOSharp rebuild <xiso|game.zar> [video.iso] [filler|seed] [su...] -o <redump.iso>
XISOSharp build-image [sourceDir] [output.iso] -m "host:image" [-f <toml>] [-O output] [-D|--dry-run]
XISOSharp image-spec from -O <out> -m "host:image" ... [specPath]
XISOSharp compress|cso <sourceDir|image.iso> [output.cso] [--ciso-level 0..9] [--ciso-version 1|2|auto] [--ciso-split bytes]
XISOSharp decompress|uncso|decso <cso|.1.cso> [output.iso]
XISOSharp checksum [--silent] <image> [images...]
XISOSharp split [--size <bytes|half>] [--output <base>] <image> [images...]
XISOSharp join [--output <file>] <first.1.iso> [...]
```

Rules: flags must precede positional arguments (the first non-flag token ends option
parsing, except the first-token verbs above). Flags match exactly — combined shorts
like `-lr` are NOT supported. An unknown flag is treated as a filename; a *known*
flag in a filename slot fails fast (`must come before ISO filenames`). Help is `-h`
ONLY — `--help` is treated as a filename. `-h`/`-v` exit 0; with no arguments usage
is printed and the tool exits 1.

## Modes (mutually exclusive; extract is the default)

| Flag | Description |
|---|---|
| `-c <dir> [name]` | Create an ISO from `<dir>` (optional `name` overrides output, may include a path; repeatable) |
| `--pack <input> [name]` | Pack a directory into an ISO, or repack an existing ISO in place (rewrite) |
| `-x` | Extract xiso(s) (explicit; the default mode) |
| `--unpack <file> [dest]` | Unpack the whole image (auto-named `./<game>/` when dest omitted) |
| `-l` | List top-level entries (non-recursive) |
| `-t` | Tree — recursive listing with full paths, sizes, totals |
| `-i <file> [path]` | Volume info + directory entry metadata (sector, size, attributes) |
| `--ls <file> [path]` | Flat directory listing, names only (mirrors `ls`) |
| `--md5 <file> [path]` / `--sha256 <file> [path]` | Hash files inside the image (whole image / directory / single file) |
| `--xex-info <file> <path>` | Xbox 360 XEX2 header (module flags, entry point, title ID, region, ...) |
| `--xbe-info <file> <path>` | Original-Xbox XBEH header + certificate (title ID/name, media, region) |
| `--copy-out <iso> <path> <dest>` | Copy a file or directory out of an image |
| `--copy-in <iso> <host> <path>` | Patch a host file into an image (replace or add; writes `<iso>.old` unless `--no-backup`) |
| `-V <file1.xiso> ...` | Deep audit: header, tree, sector bounds, cycles, attributes, optimized tag |
| `--repair <file>` | Fix audit issues in place (`.old` backup; `--dry-run` previews; `--no-backup` skips) |
| `--salvage <file>` | Rebuild reachable entries from a corrupt image (`--repair-out` overrides output) |
| `-r` | Rewrite as optimized ISO (byte-identical to `extract-xiso -r`) |
| `validate <src> <out>` | Validate conversion between two images (+ `--validate*` flags; mismatch exits 2) |
| `checksum [images...]` / `--checksum` | SHA3-256 deterministic image checksum (`--silent` → hex only) |
| `split` / `join` (`joinsplit`) | Split into `<base>.1.iso`… parts / reassemble (`--size`, `--output`) |
| `--filetime <image>` / `--set-filetime <image> <value>` | Show / set the FILETIME volume field |
| `--sector-layout <image>` | Full sector map: volume summary, per-file extents, used/free ranges |
| `--ranges <image>` | System (bone) vs file sector ranges (inclusive spans) |
| `--is-optimized <image>` | Print whether the image carries the optimized tag (supports `--skip-sectors`) |
| `--batch <dir>` (+ `--batch-recursive`) | Process all `.iso` in `<dir>` (extract/list/tree/rewrite/audit only) |
| `compress` (`cso`) / `decompress` (`uncso`, `decso`) | CISO v2 LZ4 (default) / v1 DEFLATE round-trip (`--ciso-level`, `--ciso-version`, `--ciso-split`) |
| `build-image` / `image-spec` | xdvdfs-parity ordered packing from globs / TOML spec generation |
| `--video` / `--random` / `--seed` / `--wipe` / `--trim` / `--petrify` / `--update` / `--zar` | Redump archival verbs (`--jobs`/`--policy` for `--zar`; `--all`/`--best`/`--compress` aliases; `--security-sectors` for rebuild) |
| `rebuild` | Rebuild a lossless Redump image from components (`-o <redump.iso>`) |

## Options

| Flag | Description |
|---|---|
| `-d <directory>` | Extract: output directory. Rewrite: directory for the rewritten ISO |
| `-D` | Rewrite: delete old xiso after processing. `build-image`: dry-run alias |
| `-h` | Print help and exit (the ONLY help flag) |
| `-m` | Disable automatic `.xbe` media-enable patching (create/rewrite; not recommended) |
| `-n`, `--no` | Never overwrite: refuse when an output exists (cannot combine with `-y`) |
| `-y`, `--yes` | Always overwrite without prompting |
| `-o <filename>` | Custom output filename (rewrite/rebuild/compress) |
| `-q` | Quiet (suppress non-error output) |
| `-Q` | Silent (suppress all output) |
| `-s` | Skip `$SystemUpdate` folder (create: implies `-X "**/$SystemUpdate/**"`) |
| `-v` | Print version and exit |
| `-X <glob>` | Create mode only: exclude files/dirs by glob (repeatable; `/` separator, case-insensitive) |
| `--skip-sectors N` | Filesystem starts N sectors into the file (Redump video partition); extract/list/tree/rewrite/unpack/filetime |
| `--prepend-sectors N` | Write N empty sectors before the filesystem (create/rewrite) |
| `--preserve-attrs` | Rewrite: keep source RO/HID/SYS/NOR bits instead of DIR/ARC defaults |
| `--file-time <value>` | Fixed volume FILETIME on create/pack/build-image (ISO-8601, decimal, `0x` hex, `now`, `0` = deterministic) |
| `--skip-existing` | Extract/unpack/copy-out: skip files already on disk with matching sizes (resume) |
| `--continue-on-error` | Extract/unpack/copy-out: log per-file failures and continue (still exits non-zero) |
| `--no-backup` | Skip the `<iso>.old` backup (copy-in/repair only) |
| `--dry-run` | Preview `--repair` fixes without changing anything (repair only) |
| `--repair-out <path>` | Salvage output override (salvage only) |
| `--validate` / `--validate-checksums` / `--validate-strict` / `--validate-report <file>` | Post-rewrite/conversion validation (require `-r` or `validate`) |
| `--ciso-level 0..9` / `--ciso-version 1\|2\|auto` / `--ciso-split <bytes>` | CISO codec options (compress only) |
| `--jobs <n>` / `--policy <skip\|overwrite\|auto-rename>` | Parallel + overwrite handling for `--zar` |
| `--security-sectors <file>` (`--sectors`) | Rebuild-only security-sector range override |
| `-f <toml>` / `-m "host:image"` (`--map`) / `-O <out>` (`--output`) | `build-image`/`image-spec` inputs |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success; `-h`; `-v`; `validate` passed; audit passed; empty image (`ErrIsoNoFiles` is success) |
| `1` | Any error: usage, flag values, mode conflicts, open failures, invalid ISO |
| `2` | Validation mismatch (`validate` command or `-r --validate`) |

## Examples

```bash
# Extract / list / audit
XISOSharp -d ./out game.iso
XISOSharp --unpack game.iso ./out
XISOSharp -t game.iso
XISOSharp -V game1.iso game2.iso

# Resume an interrupted unpack
XISOSharp --skip-existing --unpack game.iso ./out

# Create (skip updates + temp files, custom name)
XISOSharp -s -X "**/*.tmp" -c ./game_files custom.iso

# Pack / repack / rewrite
XISOSharp --pack ./game_files
XISOSharp --pack game.iso
XISOSharp -r --validate --validate-strict game.iso

# Redump-style round-trip (game partition at the XGD2 offset)
XISOSharp -c --prepend-sectors 129824 ./game_files redump.iso
XISOSharp --skip-sectors 129824 -d ./out redump.iso

# Copy out / patch in / hashes / headers
XISOSharp --copy-out game.iso /media ./media_out
XISOSharp --copy-in game.iso ./my-config.ini /config.ini
XISOSharp --sha256 game.iso
XISOSharp --xbe-info game.iso /default.xbe

# Sector map, sector ranges, optimized-tag probe
XISOSharp --sector-layout game.iso
XISOSharp --ranges game.iso
XISOSharp --is-optimized game.iso

# Batch a folder of ISOs
XISOSharp --batch ./isos -d ./extracted
XISOSharp -r --batch ./isos --batch-recursive

# Archival (Redump)
XISOSharp --video game.redump.iso
XISOSharp --best game.redump.iso
XISOSharp --zar -o game.zar game.iso
XISOSharp rebuild x.iso video.iso filler.bin su20076000_00000000 -o rebuilt.redump.iso

# xdvdfs parity
XISOSharp build-image ./src -m "bin:/" -m "assets/**:/assets/{1}" -O out.iso
XISOSharp compress game.iso game.cso --ciso-level 9
XISOSharp decompress game.cso game.iso
XISOSharp checksum game.iso
XISOSharp split --size half --output base game.iso
XISOSharp join --output rejoined.iso base.1.iso
```

## Full reference

Every mode, option matrix, restriction table, and deep-dive lives in the
[CLI Reference](../docs/cli.md). New additions beyond `extract-xiso` (audit,
repair/salvage, unpack/batch/pack, copy-in/out, hashes, XEX/XBE parsing,
Redump archival, xdvdfs parity) are listed there and in the
[main README](../README.md#using-the-cli).

## License

MIT
