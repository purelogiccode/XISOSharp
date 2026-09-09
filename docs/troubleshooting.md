# Troubleshooting

Common errors, their causes, and fixes.

- [Exit codes](#exit-codes)
- [Error messages](#error-messages)
- [Permission and file-system issues](#permission-and-file-system-issues)
- [Redump / offset issues](#redump--offset-issues)
- [Build and test issues](#build-and-test-issues)

## Exit codes

| Code | Meaning | See |
|---|---|---|
| `0` | Success | — |
| `1` | Error (usage, I/O, invalid ISO, …) | [CLI Reference](cli.md#exit-codes) |
| `2` | Validation failure | [Validation](validation.md#exit-codes) |

## Error messages

### `does not appear to be a valid xbox iso image`

The header magic (`MICROSOFT*XBOX*MEDIA`) was not found at any probed offset.

- The file is not an XISO (or is truncated/corrupt).
- The file is a **Redump dump** with a video partition at a nonstandard offset — use
  `--skip-sectors` (see below).
- You passed the video partition instead of the game partition.

### `appears to be corrupt`

The header was found, but the trailing magic (second `MICROSOFT*XBOX*MEDIA` at
`0x107EC` within the partition) does not match. The file is damaged or has been
modified. See [Recovering a corrupt image](#recovering-a-corrupt-image--v---repair---salvage)
below.

### `root directory sector ... exceeds total sectors`

The root directory table pointer is beyond the end of the file — truncated image or
corrupt header. See [Recovering a corrupt image](#recovering-a-corrupt-image--v---repair---salvage)
below.

### Recovering a corrupt image (`-V` → `--repair` → `--salvage`)

No reference tool repairs images — XISOSharp does. Diagnose first, then pick the
matching verb:

```bash
XISOSharp -V game.iso          # deep audit: header, tag, tree, bounds, cycles
XISOSharp --repair game.iso    # fixable in place (keeps game.iso.old backup)
XISOSharp --salvage game.iso   # rebuild game.salvaged.iso from the rest
```

- `-V` lists every issue found. Fixable-in-place issues (reserved attribute
  bits, a missing optimized tag, path separators in filenames) are exactly what
  `--repair` patches, byte for byte, with a `<file>.old` backup first
  (`--no-backup` skips it; `--dry-run` previews without changing anything).
- Truncation and structural damage (cut-off data, broken offset chains, cycles,
  depth overflows) cannot be patched — `--salvage` carries every still-reachable
  entry into a fresh `game.salvaged.iso` (or `--repair-out <path>`), reports
  `Carried:` vs `Dropped:`, and re-audits the result. The source is never
  modified, and CISO input is accepted (it rebuilds a plain `.iso`).
- An image with no readable header at all (`does not appear to be a valid xbox
  iso image`) has nothing to repair with — re-dump or re-download it.
  See [Repair](api-xisoreader.md#repair) and [Salvage](api-xisoreader.md#salvage).

### `filename '...' contains invalid character(s), aborting`

The image contains an entry named `.`, `..`, or with `/`/`\` — malformed or malicious
image. This is a safety check inherited from the reference tool.

### `open error: <file> No such file or directory`

The file does not exist, is locked, or the path was mistyped. Remember that flags must
come **before** filenames; an *unknown* flag is treated as a filename and produces this
error, while a *known* flag in a filename slot fails fast with
`Error: <flag> must come before ISO filenames` (see below).

### `Error: <flag> must come before ISO filenames`

You put a flag after an ISO filename (e.g. `game.iso -d ./new/` — upstream #61):
option parsing stops at the first filename, so the flag would otherwise be opened
as an image. Move the flag before the filename (`-x -d ./new/ game.iso`). The check
is skipped when the token exists on disk, so a file literally named like a flag
still works. See [CLI Reference](cli.md#misplaced-flags-upstream-61).

### `Output path must not be empty`

An empty `-d` value (typically `-d "%UNSET_VAR%"` in a batch script). Quote-check
the variable or omit `-d` to extract next to the ISO. See
[CLI Reference](cli.md#destination-directory-edge-cases--d).

### `Failed to extract "<entry>" (sector N, M bytes) -> "<dest>": ...`

A per-file extraction failure with full context (xdvdfs #187): the entry, its
data sector, expected size, and the underlying OS cause. Common reasons: the
destination name is illegal on the filesystem, permission was denied, the drive
vanished, or the image data ends early (truncated download — the detail then
reads `image data ends before the reported file size` with read-vs-expected
counts). Add `--continue-on-error` to log-and-skip failed files while the rest
still extracts. See [CLI Reference](cli.md#extraction-robustness).

### `Failed to unpack image "<name>": N file(s) failed: ...`

The end-of-run summary of a `--continue-on-error` run: at least one file
failed (each listed with the detail above), so the exit code is non-zero even
though the healthy files extracted. Fix the causes (names, permissions, image
damage), then re-run — with `--skip-existing` to skip what's already done. See
[CLI Reference](cli.md#extraction-robustness).

### `... is already optimized, skipping...`

The image carries the optimized tag; rewrite mode has nothing to do. Extract/list
still work normally.

### `<file>.iso.old already exists, cannot rewrite ...`

Rewrite mode renames the source to `<name>.iso.old` first. Delete the stale `.old`
file (or use `-D` next time to remove it automatically after rewriting).

### `Error: ... is the same file as the input ...` / `... would overwrite the ... backup ...`

The input==output safety guard refused an `-o` that points back at its own input
(rewrite `-o` onto the input or its `.old` backup, wipe/trim/compress/decompress
output onto the input, `rebuild -o` onto a component, split parts onto the source).
Pick a different output name — or omit `-o` to use the mode's explicit in-place
behavior (rewrite via `.old`, `TrimXiso(input, input)`). See
[CLI Reference](cli.md#inputoutput-safety-guard).

### `skip: <path>` lines during extract/unpack/copy-out

Not an error: `--skip-existing` left a file already on disk with a matching size
untouched. Re-running an interrupted unpack prints one `skip:` line per completed
file and only writes the missing ones. See
[CLI Reference](cli.md#resume-interrupted-unpacks).

### `Error: cannot write to <path>: ...`

The output path is not writable or its directory does not exist. Check permissions and
create the target directory (`-d` creates it for you in extract mode).

### `Failed to extract ... image data ends before the reported file size ...`

The image's directory entry claims more bytes than the image actually contains —
a truncated download or corrupt/torn image. The file fails with
`ErrFileTruncated` (expected vs actual byte counts are in the message) instead
of being left short on disk; add `--continue-on-error` to salvage the rest.
(Retired warning text from older builds:
`WARNING: File <name> is truncated. Reported size: X bytes, read size: Y bytes!`.)
See [CLI Reference](cli.md#extraction-robustness). For a systematically damaged
image (not just one short file), audit and rebuild it instead — see
[Recovering a corrupt image](#recovering-a-corrupt-image--v---repair---salvage).

### `petrify` battle Skipped: oracle emits unreadable skeleton

`petrify ... CLI skeleton is structurally correct (...) but differs from xboxkit -p
(oracle zeroes filesystem tables inside mixed bone/file extents — oracle-side defect,
not comparable)`.

xboxkit 0.7's skeleton walk zeroes to merged-extent ends, paving over filesystem
("bone") sectors that share an extent with file data — on real mastered images
only a handful of tables survive, so its skeleton is unlistable (native
extract-xiso reports a single root entry). It can additionally copy bone sectors
from wrong file offsets: it seeks the input absolutely per file while hashing
inline, then resumes relatively, desyncing later reads. XISOSharp walks
bone-keep segments instead (every table sector verbatim, everything else zero),
so its skeletons stay listable and rebuildable. Byte-parity with the oracle
cannot pass on such images by design; the battle Skips with this reason after
verifying our skeleton structurally (same length as the game partition, bones
verbatim, rest zero). If upstream xboxkit fixes its walk, the byte comparison
goes green again on its own.

## Permission and file-system issues

**Extraction fails with permission denied.**

- Check write access to the output directory.
- On Windows, avoid extracting into protected locations (`C:\Program Files`, system
  roots) without elevation.
- Make sure no other process has the output file open.

**Creation skips files with "warning: permission denied: <name>, skipping."**

The entry could not be read. The tool skips it with a warning and reports the skipped
count at the end. Fix the ACLs and re-run if the file matters.

**"Path too long" warnings on Windows.**

Windows path-length limits can bite with deep trees. Enable long paths
(`LongPathsEnabled`) or move the working directory closer to the drive root.

## Redump / offset issues

**`--skip-sectors` value does not work.**

- The value is the **game partition** offset in 2048-byte sectors: `offset / 2048`.
  For `0x0FD90000` → `129,824`; `0x02080000` → `16,640`; `0x18300000` → `198,144`.
- Verify the header is actually at `N × 2048 + 0x10000` in your dump (e.g. with a hex
  editor, look for `MICROSOFT*XBOX*MEDIA`).

**`--prepend-sectors` output is not recognized by other tools.**

Ensure the filesystem lands at an offset other tools probe: use one of the canonical
values above so auto-detection works without flags.

**`--skip-sectors` with `-V`, `-i`, hashes, `--copy-out`, or others is rejected.**

Those modes do not support offsets; the CLI rejects the combination with a
clear error instead of producing wrong results. `--skip-sectors` is valid only
in extract, list, tree, rewrite (`-r`), `--unpack`, `--filetime`, and
`--set-filetime` — rejected with `-c` and with `-i`, `--ls`, `--xex-info`,
`--xbe-info`, `--md5`/`--sha256`, `--copy-out`, `--copy-in`, `-V`,
`validate`/`--validate*`, redump verbs, and `checksum`. `--prepend-sectors`
is valid only in create (`-c`) and rewrite (`-r`).

## Build and test issues

**`dotnet build` fails with a missing SDK.**

The SDK version is pinned in `global.json` (10.0.301, `rollForward: latestMinor`).
Install .NET SDK 10.0.301+ or adjust `global.json` for your environment.

**Tests fail with `The process cannot access the file because it is being used by
another process`.**

The test suite is sequential, but a previous test run may still hold file handles or
the `TestData` outputs. Close other `extract-xiso` processes, clean
`TestData/output`, and re-run.

**`Verify-Output.ps1` cannot find the C tool.**

The script's default parameters point at the sibling repo layout
(`C:\Sincronizar\source\repos\CSharp_ExtractXiso`). Pass explicit paths:

```powershell
.\Verify-Output.ps1 -CExtractXiso "C:\path\to\extract-xiso.exe" -CsExtractXiso "C:\path\to\extract-xiso.exe" -TestData "C:\path\to\TestData"
```

**The WPF tester does not build on Linux/macOS.**

`XISOSharpTester` is Windows-only (`net10.0-windows`). It is not part of CI; build the
solution on non-Windows with `dotnet build CSharp_XISOSharp.sln` after excluding that
project, or just build `XISOSharp.Core`/`XISOSharp.Cli`/`XISOSharp.Tests`.

## Still stuck?

Open an issue with:

- the exact command line used,
- the full output (including stderr),
- the file size and a hex dump of the first 0x11000 bytes if the image fails
  verification.

See also: [FAQ](faq.md) · [CLI Reference](cli.md) · [Redump & Disc Layouts](redump-workflows.md)
