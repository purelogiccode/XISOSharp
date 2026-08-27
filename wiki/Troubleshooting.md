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
modified.

### `root directory sector ... exceeds total sectors`

The root directory table pointer is beyond the end of the file — truncated image or
corrupt header.

### `filename '...' contains invalid character(s), aborting`

The image contains an entry named `.`, `..`, or with `/`/`\` — malformed or malicious
image. This is a safety check inherited from the reference tool.

### `open error: <file> No such file or directory`

The file does not exist, is locked, or the path was mistyped. Remember that flags must
come **before** filenames; an unknown flag is treated as a filename and produces this
error.

### `... is already optimized, skipping...`

The image carries the optimized tag; rewrite mode has nothing to do. Extract/list
still work normally.

### `<file>.iso.old already exists, cannot rewrite ...`

Rewrite mode renames the source to `<name>.iso.old` first. Delete the stale `.old`
file (or use `-D` next time to remove it automatically after rewriting).

### `Error: cannot write to <path>: ...`

The output path is not writable or its directory does not exist. Check permissions and
create the target directory (`-d` creates it for you in extract mode).

### `WARNING: File <name> is truncated. Reported size: X bytes, read size: Y bytes!`

The image's directory entry claims more bytes than the file actually contains — the
ISO is truncated or corrupt. Extraction continues but the file is incomplete.

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

**`--skip-sectors` with `-V`, `-i`, hashes, or `--copy-out` is rejected.**

Those modes currently do not support offsets; the CLI rejects the combination with a
clear error instead of producing wrong results.

## Build and test issues

**`dotnet build` fails with a missing SDK.**

The SDK version is pinned in `global.json` (10.0.301, `rollForward: latestFeature`).
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
