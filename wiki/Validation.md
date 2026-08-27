# Validation

XISOSharp can verify that a conversion (Redump → XISO, or rewrite) produced a correct
image by comparing the source and output **file trees**. This is exposed both as a
standalone command and as a post-rewrite step.

- [Standalone `validate` command](#standalone-validate-command)
- [Post-rewrite validation](#post-rewrite-validation)
- [What is compared](#what-is-compared)
- [JSON report](#json-report)
- [Exit codes](#exit-codes)
- [Examples](#examples)

## Standalone `validate` command

The `validate` command compares two existing ISO images and must be the **first**
token on the command line (it does not start with `-`):

```bash
extract-xiso validate <source.iso> <output.iso> [--validate-checksums] [--validate-report <file>]
```

| Flag | Effect |
|---|---|
| `--validate-checksums` | Additionally verify SHA-256 per file (reads all file data twice; slower). |
| `--validate-report <file>` | Write the result as a JSON report. |
| `-q` / `-Q` | Apply as usual. |

The source may be a Redump image (game partition auto-detected at its known offset) or
an ordinary XISO; the output is the image produced by a conversion.

## Post-rewrite validation

Combine with `-r` to validate every rewritten image:

```bash
extract-xiso -r --validate [--validate-checksums] [--validate-strict] [--validate-report <file>] game.iso
```

| Flag | Effect |
|---|---|
| `--validate` | Compare source (`.old`) and rewritten image after the rewrite. |
| `--validate-checksums` | Also verify SHA-256 checksums. |
| `--validate-strict` | Exit code 2 on any mismatch (otherwise mismatches are reported but the exit code stays 0). |
| `--validate-report <file>` | Write a JSON report per ISO. |

> [!NOTE]
> `--skip-sectors` / `--prepend-sectors` cannot be combined with validation flags —
> the validator currently reads images at their standard detected offsets. The CLI
> rejects the combination explicitly.

## What is compared

1. **File counts** — total files and directories.
2. **File paths** — case-insensitive matching per the XDVDFS spec.
3. **File sizes** — byte-accurate.
4. **Checksums** — SHA-256 per file, only when `--validate-checksums` is given.

Reported issue types: `MissingInOutput`, `ExtraInOutput`, `SizeMismatch`,
`ChecksumMismatch`.

Example output:

```text
[VALIDATE] Source: game.redump.iso (1,247 files, 4,312,453,120 bytes)
[VALIDATE] Output: game.xiso (1,247 files, 4,312,453,120 bytes)
[VALIDATE] File count: MATCH
[VALIDATE] File paths: MATCH
[VALIDATE] File sizes: MATCH
[VALIDATE] Checksums: MATCH (SHA-256 verified)
[VALIDATE] RESULT: PASS — All files validated successfully
```

Error reporting:

```text
[VALIDATE] MISSING: /default.xbe (expected 2,457,600 bytes)
[VALIDATE] SIZE MISMATCH: /media/video.bik — source: 15,728,640, output: 15,728,000
[VALIDATE] CHECKSUM FAIL: /audio/sound.wav — source: a1b2c3..., output: d4e5f6...
[VALIDATE] RESULT: FAIL — 3 issues found
```

## JSON report

`--validate-report <file>` writes a machine-readable report:

```json
{
  "source": { "path": "source.iso", "fileCount": 1247, "dirCount": 42, "totalBytes": 4312453120 },
  "output": { "path": "output.iso", "fileCount": 1247, "dirCount": 42, "totalBytes": 4312453120 },
  "passed": true,
  "issueCount": 0,
  "issues": []
}
```

Each issue entry carries `type`, `path`, `sourceSize`, `outputSize`, `sourceHash`, and
`outputHash` (hashes are lowercase hex, `null` when checksums were not verified).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Validation passed. |
| `2` | Validation failed (`validate` command, or `-r --validate-strict`). |
| `1` | Error while reading/validating (invalid ISO, I/O error). |

## Examples

```bash
# Compare a Redump source with a rebuilt XISO, including checksums
extract-xiso validate game.redump.iso rebuilt.xiso --validate-checksums

# Rewrite and validate with a JSON report; fail the build on mismatch
extract-xiso -r --validate --validate-strict --validate-report report.json game.iso
```

See also: [CLI Reference](cli.md) · [Redump & Disc Layouts](redump-workflows.md) ·
[Library API — XisoValidator](api-utilities.md#xisovalidator)
