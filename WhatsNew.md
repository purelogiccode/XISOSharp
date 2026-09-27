# What's New in 1.3.0 (since 1.2.0)

Release tag [`1.3.0`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1419 tests: 1418 passed, 1 opt-in skip, 0 failed).

Correctness and maintenance release: the rewrite `-o` output name is honored
(including rooted paths), failed rewrites now exit non-zero instead of
reporting success, wrapped exceptions carry their cause, and the logging
surface is under test.

## Fixes

### Rewrite output name (`-o`) honored, rooted paths kept

- `XisoWriter.CreateXiso` used the source name in rewrite mode and ignored
  `inName`, so `XisoReader.Rewrite`'s `outputName` (CLI `-o`) had no effect on
  the output filename. It is now used verbatim, and the Windows drive-letter
  strip no longer rewrites an absolute `-o C:\out\game.iso` into the invalid
  `:\out\game.iso` (relative names still resolve against `-d`).
- A failed rewrite is no longer silent: `XisoReader.DecodeXiso`/`Rewrite`
  return the writer's non-zero result and the CLI checks it — a rewrite that
  produced no file exits 1 without printing `successfully rewritten`.
- Regressions locked by `Rewrite_WithAbsoluteOutputName_UsesProvidedName` and
  `Rewrite_UnwritableOutput_ReturnsError`.

### Exception causality (MA0054)

- Wrapped rethrows embed the caught exception as `InnerException`:
  `ProcessRunner` timeout → `TimeoutException`, `XisoReader` truncated file
  copy → `IOException`, GUI `MainViewModel` output-verification failures →
  `InvalidOperationException`. Message text is unchanged.

### Reference-oracle test paths

- `TestConditions` resolves the compiled `extract-xiso.exe` from the current
  `References/` drop (canonical `References/extract-xiso.exe`, dated drop root,
  or in-tree CMake build) instead of the removed `202505152050` artifact path —
  the legacy `llCompat` interop tests run and pass again.
- `RegenerateFixtureIso_WhenRequested` is now
  `ValidateFixtureIso_WhenRequested`: it compares against a temp copy and never
  overwrites the fixture; regeneration stays a manual out-of-band step
  (`docs/testing.md` updated).

## Logging

- `UpdateChecker` debug-logs every previously silent catch (offline/API/cache
  failures stay below `Warning`, so they never file bug reports); GUI
  `CliLocator.ProductVersion` debug-logs unreadable CLI metadata.
- `BugReporter` extracts `ComposeReport` and exposes `BuildExceptionBlock` to
  tests. New `BugReportFormatTests` lock the Environment / Error / Exception
  sections required by the bug-report service.

## Tests

- 1419 tests across `net8.0` / `net9.0` / `net10.0`: 1418 passed, 1 opt-in skip
  (`XISO_UPDATE_FIXTURE=1` fixture validator), 0 failed. New since 1.2.0:
  `BugReportFormatTests` (5) and the two rewrite regressions above; the three
  legacy extract-xiso interop tests now execute instead of skipping.

## Dependencies

- `Meziantou.Analyzer` 3.0.257 → 3.0.290 (all projects), `Avalonia` 12.1.2 →
  12.1.3 (GUI), `QuestPDF` 2026.8.0 → 2026.9.1 (Tester),
  `Microsoft.NET.Test.Sdk` 18.10.0 → 18.10.1 (Tests). No library dependency
  changes.

## Docs

- Badge rows (CI, NuGet, release, .NET, platform/RIDs, license, docs site,
  per-project) added to the root, library, CLI, Tests, Tester, and docs
  READMEs; suite counts and the `-o` rooted-path behavior refreshed.
