# Release Notes

## 1.0.2

Release tag [`1.0.2`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1338 tests: 1334 passed, 4 pre-existing skips, 0 failed).

### CLI

#### `-v` banner is XISOSharp-branded

`-v` (and the usage header) now prints
`XISOSharp v<version> for <win|linux|macos|cross-platform> - https://github.com/purelogiccode/XISOSharp`
instead of the `extract-xiso` compatibility line. The version is the MinVer
build stamp with `+build` metadata trimmed, so it matches the package/assembly
version. `Constants.ExisoVersion` is kept for provenance and the on-disk
optimized tag (`in!xiso!2.7.1 (01.11.14)`) is unchanged, so image compatibility
is unaffected. The extract-xiso BSD-4-clause acknowledgement remains in the
shipped `LICENSE`.

#### Automatic update check

Every interactive launch compares the running version against the latest
GitHub release — at most one request per 24 hours, cached at
`%LocalAppData%/XISOSharp/update-check.json`. A newer release prints an
`[UPDATE]` notice with the release page and the matching
`release_<version>_<rid>.zip` asset, and on an interactive console you are
offered to open the release page in your browser. The check is skipped for
`-q`/`-Q`/`-v` runs and test hosts, never fails the run, and can be disabled
with `XISO_NO_UPDATE_CHECK=1`.

#### New inspection verbs

`--sector-layout` (volume summary, per-file extents, used/free ranges),
`--ranges` (system/bone vs file sector ranges), and `--is-optimized`
(optimized-tag probe, `--skip-sectors` aware).

#### Interactive launch pause

Double-clicking `XISOSharp.exe` with no arguments prints usage and waits for a
keypress instead of closing the console window. Scripts, pipes, test hosts, and
`XISO_NO_PAUSE=1` are never blocked.

#### Warning+ logs forwarded to the bug-report service

The shared Serilog pipeline in the CLI, GUI, and Tester forwards
Warning-and-above events to the bug-report API with environment, error, and
exception sections; opt out with `XISO_DISABLE_BUGREPORT=1`.

### GUI (Avalonia)

- **CLI discovery fix for single-file bundles**: `ToolLocator` now also probes
  the directory of the real executable (`Environment.ProcessPath`) after
  `AppContext.BaseDirectory`, since self-extracting bundles run from
  `%TEMP%\.net\...`. This resolves the "CLI not found" state when the GUI runs
  from a published bundle.
- The legacy `XISOSharp.Cli` file-name fallback was removed; resolution is
  `XISOSharp(.exe)` only.
- Status bar and log show a branded product label (`XISOSharp <version>`) read
  from the CLI binary's version metadata instead of echoing the `-v` banner.
- The app icon is embedded in both the executable and the window.
- Framework-dependent publish builds and stages the CLI beside the GUI
  automatically (previous attempts failed with `NETSDK1151`).

### Library

- `ToolLocator` is now the shared resolver/probe used by the GUI, Tester, and
  BattleTests: explicit override → app directory → process directory → `PATH`,
  with a bounded, tree-killed `-v` probe via `ProcessRunner`, plus a new
  `ToolLocatorTests` suite.
- `Constants.Banner` reports the XISOSharp product version; the extract-xiso
  baseline constant is retained for provenance.

### Repository & docs

- GitHub Pages publishes the Docsify documentation, and a workflow syncs the
  wiki (sidebar included).
- `LICENSE` now carries the full third-party notices (extract-xiso BSD-4-clause,
  xdvdfs, XboxKit, ZArchiveSharp); distributed bundles include `LICENSE` and
  `README.md`.
- Docs and readmes refreshed for the branded banner, the new inspection verbs,
  update checks, the GUI bundle layout, and the shared tool locator.

### Dependencies

No library dependency changes: `ZArchiveSharp` stays at 1.0.2, analyzer/Roslynator
versions are unchanged from 1.0.1.

## 1.0.1

Release tag [`1.0.1`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1290 tests: 1286 passed, 4 pre-existing skips, 0 failed).

### Fixes

#### Rewrite keeps zero-size directories as directories

Optimizing (`-r` / `Rewrite`) an image containing a zero-size directory entry —
seen in the wild in e.g. Marvel vs Capcom 2 — used to write that entry back as a
**file** (ARC attribute, file data) instead of a **directory**, breaking the game.

Cause: `XisoReader.TraverseXiso` in `GenerateAvl` mode only recursed into
subdirectories with `fileSize > 0` and left `AvlNode.Subdirectory` as `null` for
zero-size ones; the writer treats `null` as "file". The fix assigns
`AvlNode.EmptySubdirectory`, matching upstream `extract-xiso` build
`202609111233`, which fixed the same bug in `traverse_xiso()` (subdirectory left
`NULL` instead of `EMPTY_SUBDIRECTORY`). See [Rewrite mode](api-xisowriter.md#rewrite-mode)
and [XISO Format](xiso-format.md#directory-tables-and-the-avl-tree).

#### SHA3-256 checksum works on OSes without native SHA3 support

`XisoChecksum.ComputeImageChecksum` used only the BCL
`IncrementalHash` SHA3-256, which throws `PlatformNotSupportedException` on OSes
without a SHA3 provider (Windows 10 CNG, OpenSSL 1.x) — all checksum tests
failed there. New pure-managed FIPS 202 SHA3-256 (`XISOSharp/Sha3.cs`, no new
dependencies, trim/AOT-safe) is now used as a fallback whenever
`SHA3_256.IsSupported` is false; digests are identical either way (verified
against OpenSSL 3.0, including rate-boundary sizes). See
[Checksums](xdvdfs-compat.md#checksum).

### Dependencies

- `ZArchiveSharp` **1.0.1 → 1.0.2**, now consumed purely as a NuGet package —
  the sibling-checkout `ProjectReference` fallback was removed, so no
  side-by-side `CSharp_ZArchiveSharp` clone is needed to build. See
  [Building](building.md).
- `Meziantou.Analyzer` 3.0.235 → 3.0.236 (all projects), `Avalonia.Diagnostics`
  11.3.21 → 11.3.22 (GUI, Debug-only). Dev-only, no runtime impact.

### Documentation

- Checksum docs ([xdvdfs Compat](xdvdfs-compat.md#checksum),
  [XisoReader](api-xisoreader.md#checksum-sha3-256),
  [Utilities](api-utilities.md)) describe the BCL/managed-fallback behavior.
- Rewrite docs ([XisoWriter](api-xisowriter.md#rewrite-mode)) and the format
  reference ([XISO Format](xiso-format.md#directory-tables-and-the-avl-tree))
  document the zero-size-directory handling and upstream parity.
- Reference/build docs updated for the NuGet-only ZArchiveSharp consumption and
  the `extract-xiso-build-202609111233` reference drop.
- Library README intro no longer pins a single extract-xiso version number.

### Internal (no behavior change)

Solution-wide Roslynator formatting cleanup (line wrapping, `Fill(0)` →
`Clear()`, internal `Sha3_256` → `Sha3256` rename).

## 1.0.0

Initial NuGet release: pure-C# port of `extract-xiso` v2.7.1 (byte-identical
output) extended with XboxKit archival workflows and xdvdfs packing
(build-image remapping, CISO, SHA3-256 checksums). See the
[README](README.md#feature-highlights) for the full feature list.
