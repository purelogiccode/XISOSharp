# What's New in 1.0.1 (since 1.0.0)

Release tag [`1.0.1`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1290 tests: 1286 passed, 4 pre-existing skips, 0 failed).

## Fixes

### Rewrite keeps zero-size directories as directories

Optimizing (`-r` / `Rewrite`) an image containing a zero-size directory entry —
seen in the wild in e.g. Marvel vs Capcom 2 — used to write that entry back as a
**file** (ARC attribute, file data) instead of a **directory**, breaking the game.

Cause: `XisoReader.TraverseXiso` in `GenerateAvl` mode only recursed into
subdirectories with `fileSize > 0` and left `AvlNode.Subdirectory` as `null` for
zero-size ones; the writer treats `null` as "file". The fix assigns
`AvlNode.EmptySubdirectory`, matching upstream `extract-xiso` build
`202609111233`, which fixed the same bug in `traverse_xiso()` (subdirectory left
`NULL` instead of `EMPTY_SUBDIRECTORY`).

### SHA3-256 checksum works on OSes without native SHA3 support

`XisoChecksum.ComputeImageChecksum` used only the BCL
`IncrementalHash` SHA3-256, which throws `PlatformNotSupportedException` on OSes
without a SHA3 provider (Windows 10 CNG, OpenSSL 1.x) — all checksum tests
failed there. New pure-managed FIPS 202 SHA3-256 (`XISOSharp/Sha3.cs`, no new
dependencies, trim/AOT-safe) is now used as a fallback whenever
`SHA3_256.IsSupported` is false; digests are identical either way (verified
against OpenSSL 3.0, including rate-boundary sizes).

## Dependencies

- `ZArchiveSharp` **1.0.1 → 1.0.2**, now consumed purely as a NuGet package —
  the sibling-checkout `ProjectReference` fallback was removed, so no
  side-by-side `CSharp_ZArchiveSharp` clone is needed to build.
- `Meziantou.Analyzer` 3.0.235 → 3.0.236 (all projects), `Avalonia.Diagnostics`
  11.3.21 → 11.3.22 (GUI, Debug-only). Dev-only, no runtime impact.

## Documentation

- Checksum docs (`xdvdfs-compat.md`, `api-xisoreader.md`, `api-utilities.md`)
  describe the BCL/managed-fallback behavior.
- Rewrite docs (`api-xisowriter.md`) and the format reference (`xiso-format.md`)
  document the zero-size-directory handling and upstream parity.
- Reference/build docs updated for the NuGet-only ZArchiveSharp consumption and
  the `extract-xiso-build-202609111233` reference drop.
- Library README intro no longer pins a single extract-xiso version number.

## Internal (no behavior change)

Solution-wide Roslynator formatting cleanup (line wrapping, `Fill(0)` →
`Clear()`, internal `Sha3_256` → `Sha3256` rename).
