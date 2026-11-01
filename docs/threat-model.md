# Threat Model

XISOSharp parses **untrusted images and archives**. This page records the trust
boundaries, the main threats and the controls that mitigate them.

## Assets

- The host filesystem (extraction destination, in-place patch/repair targets).
- Process memory and CPU (hostile TOC/index/name trees must not OOM or hang).
- The user's original image (in-place operations must be reversible and guarded).
- Release integrity (CI-built artifacts, SBOM/provenance).

## Trust boundaries

1. **Container headers/indexes** — CISO, CCI and ZAR headers and per-sector indexes
   are attacker-controlled.
2. **XDVDFS directory tables** — child offsets, entry counts, names and sizes.
3. **Archive name trees** — paths, nesting depth and entry sizes.
4. **Destination path construction** — image names become host paths.
5. **XBE/XEX headers** — certificate/header offsets and lengths.
6. **Compression framing** — LZ4/DEFLATE/zstd block lengths and padding.

## Threats and controls

| Threat | Control |
|---|---|
| TOC cycles / absurd entry counts / deep nesting | explicit in-order stack, visited-offset set, `MaxTocEntriesPerTable`, `MaxTocDepth`, `MaxTocStackFrames`; named `XisoFormatException`. |
| Out-of-table offsets / truncated image | bounds checks before every read; `ReadExact`; `TruncatedCopyException`; per-file `ExtractFileException` context. |
| Path traversal / absolute paths / drive-relative names | `XisoPaths`/`XisoEntryNames`; separators, `..`, colons, trailing dots/spaces and DOS device names rejected or sanitized. |
| Case collisions on case-insensitive hosts | duplicate-name rejection at table parse/write; first-wins remap rules documented. |
| Decompression bombs | CISO/CCI sector-sized blocks; ZAR `maxEntries`/`maxTotalBytes` caps; CISO compressed-span cap; bounded allocations. |
| Malformed CISO/CCI indexes | alignment/version/block-size validation, monotonic positions, file-bounds checks, sane span caps (the mutation fuzzer found an `OverflowException` here in 1.5.0). |
| Hostile XBE/XEX headers | magic/length gates and bounded header reads; parsers return `null` instead of throwing for non-executables. |
| In-place write corruption | input==output guards, `<file>.old` backups, temp-file + move, partial-output cleanup. |
| Concurrent writers | process-wide create lock and per-operation state; extraction resumes with `--skip-existing`. |
| Supply-chain tampering | CI-built artifacts, pinned Actions, Dependabot, CodeQL/InspectCode, SBOM, build-provenance attestations. |

## Residual risks

- The parser is not formally verified; fuzzing and the corruption suite are
  evidence, not proof.
- Extraction intentionally writes files with image-controlled names; run it in a
  directory you control, not over system paths.
- In-place `--repair`/`--copy-in` modify the source; keep backups (the CLI writes
  one by default).
- Code signing/notarization is not enabled by default; verify the published
  hashes/SBOM/provenance before running release bundles.

Report suspected vulnerabilities through
[private vulnerability reporting](https://github.com/purelogiccode/XISOSharp/security/advisories/new),
not a public issue.
