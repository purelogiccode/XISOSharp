# Todo — bugs and inconsistencies

Deep review of the repository at commit `cb82105` (2026-10-01). Produced from four parallel
code-review passes (library read path, library write/IO path, CLI/logging/tests,
GUI/apps/build/docs) plus targeted manual re-verification. Items marked **(verified)** were
re-checked against the code while compiling this list; the rest carry the reviewer's
`file:line` evidence and should be re-confirmed before fixing. Severity is impact-based,
not effort-based.

Items 1–20 were fixed in the first pass (commits `679f04c`, `6e992cc`, `ee2d5a8`) and
items 21–80 in the second pass (working tree; see the `**FIXED**` markers). Item 74's
hardcoded key is now a double-Base64 literal in `XISOSharp.Cli/Logging/ApiKeyStore.cs`,
decoded once at startup (`WarmUp` from `ApplicationStats.RecordLaunch`). Regression tests
for the fixed items live in `AuditXisoTests`, `XisoCoverageTests`, `XisoSplitTests`,
`XisoCorruptionResilienceTests`, `CliFlagContractExtraTests`, `Latin1EncodingExtraTests`,
`SectorAllocatorTests`, `XisoPathsTests`, `XisoValidatorTests`, `XisoWriterEdgeCaseTests`
and `XisoRedumpAndSkeletonTests`.

## High

1. **FIXED** **Path escape from crafted image names** — `XISOSharp/XisoSalvager.cs:384` (verified):
   `stagedName` only replaces `/` and `\`, so a Windows name like `C:evil` survives and
   `Path.Combine(stagingDir, "C:evil")` resolves drive-relative, writing outside the staging
   directory. The legacy extract walkers (`XisoReader.cs:659`, `:763`, `:1058`) reject only
   `.`/`..`/separators too, so a malicious image can escape the destination on Windows.
2. **FIXED** **In-place patch can overwrite the optimized tag/PVD** — `XISOSharp/XisoReader.cs:2859`:
   `GetSectorLayout` marks only the descriptor sector used; the tag at sector 15 and ECMA-119
   descriptors at sectors 16–17 are missing from `UsedRanges`, while `XisoRanges.cs:217`
   preserves the second descriptor sector. `SectorAllocator.FromLayout` first-fit from sector
   0, so `XisoPatcher.CopyIn` of ≥32 KiB overwrites the tag and later reads auto-select
   `llCompat`, invalidating the image.
3. **FIXED** **`-c .` packs from the parent directory** — `XISOSharp/XisoWriter.cs:416` (verified):
   when `rootDirectory` is `.` (or ends in `/.`), `isoDir` is `.`; the write phase does
   `SetCurrentDirectory("..")` and the synthetic root's `SetCurrentDirectory(".")` is a
   no-op, so file data is read relative to the source's parent (fails or packs same-named
   files from the wrong directory).
4. **FIXED** **Cancellation/too-large leaves the process CWD changed** — `XISOSharp/XisoWriter.cs:467`
   (verified): `catch (OperationCanceledException) { throw; }` and the
   `XisoFileTooLargeException` rethrow exit before the `cleanup:` label, skipping
   `Directory.SetCurrentDirectory(cwd)`; `GenerateAvlTreeLocal` and the pre-try validation
   throws (`:301`, `:332`) have the same problem. The XML remark promises the CWD is always
   restored.
5. **FIXED** **Split CISO with a dotted base cannot be reopened** — `XISOSharp/CisoSplitFile.cs:32`:
   `OpenParts` strips `.1.cso` then rebuilds part 1 with `Path.ChangeExtension(baseName,
   "1.cso")`; for `My.Game.1.cso` that yields `My.1.cso`, so a file the writer itself named
   `My.Game.1.cso` fails with `FileNotFoundException`.
6. **FIXED** **`-D` deletes the `.old` backup after a failed rewrite** — `XISOSharp.Cli/Program.cs:2349`
   (verified): `if (deleteOld) File.Delete(oldPath)` runs inside the rewrite `try` even when
   `DecodeXiso` returned non-zero or validation failed (`err = 2`); upstream only unlinks on
   success, so a failed rewrite can destroy the only remaining copy.
7. **FIXED** **GUI wipe/trim output is passed as a positional input** —
   `XISOSharp.Gui/Services/CliCommands.cs:236` and `:254` (verified against
   `Program.cs:3866`): the CLI expands every positional as an input and honors only `-o`, so
   the GUI's chosen output is processed as an extra image — exit 1 (`Cannot stat`) or the
   file is silently wiped to the derived name while the requested output is untouched.
8. **FIXED** **Optimized probe ignores prepended sectors** — `XISOSharp.Cli/Program.cs:2243`: the
   pre-loop `XisoReader.IsOptimizedImage(xisoPath)` omits `skipSectors`, so a self-produced
   optimized prepended image is treated as non-optimized and extract/list/tree run with
   `llCompat: true`, contradicting the API contract.
9. **FIXED** **Tester list comparison can never fail** — `XISOSharpTester/Services/XisoTestRunner.cs:746`:
   `ParseListOutput` matches a `Path/Size/StartSector` layout neither tool emits (both print
   `\name (N bytes)`), so both lists are always empty and `CompareListEntries` reports
   `AllMatch=true`; the "List Files" regression sub-test is a false pass.
10. **FIXED** **Drop routing sends non-`.iso` folders to Batch** —
    `XISOSharp.Gui/Views/MainWindow.axaml.cs:388` (verified) routes any folder containing an
    `IsImage` extension to Batch, but CLI `--batch` enumerates only `*.iso`
    (`XISOSharp.Cli/Program.cs:4521`, verified); folders with only `.xiso/.cso/.img/.zar`
    fail with "no .iso files found" instead of being routed to Create.
11. **FIXED** **`CliStatus` set off the UI thread** — `XISOSharp.Gui/ViewModels/MainViewModel.cs:349`
    (verified): the `catch` assigns the bound `ObservableProperty` directly after an
    `await ... ConfigureAwait(false)`; an exception after the probe resumes on a pool thread,
    breaking the file's own `SetOnUi` contract.

## Medium

12. **FIXED** **Audit inconsistency on tail corruption** — `XISOSharp/XisoReader.cs:2424`: `AuditXiso(string)`
    uses `GetVolumeInfo` + `AuditStream`, neither checking trailing header magic, while the
    block-device overload goes through `VerifyXiso` (`:323`) which does; the same image
    audits valid via path and invalid via device, and extraction rejects it.
13. **FIXED** **Available-bytes check overstates space for offset images** — `XISOSharp/XisoReader.cs:238`
    (and `:353`): `availableBytes = (totalSectors - rootDirSector) * SectorSize` uses the whole
    file while `rootDirSector` is partition-relative, so a `rootDirSize` past EOF passes for
    Redump/XGD images.
14. **FIXED** **Sector-count overflow for >4 GiB extents** — `XISOSharp/XisoReader.cs:2921` (verified):
    `(size + SectorSize - 1) / SectorSize` is `uint` arithmetic and wraps to 0 above
    `0xFFFFF800`, so a ~4 GiB file is omitted from `UsedRanges` and can be overwritten by
    `XisoPatcher`; same pattern at `:2898` and `XisoPatcher.cs:157`.
15. **FIXED** **All-zero empty-table sentinel unhandled** — `XISOSharp/XisoRanges.cs:363`:
    `CollectFileEntries` only handles `0xFFFF`; unlike the other walkers it treats the
    xdvdfs all-zero empty table as a phantom file entry, which `XisoSkeleton.Petrify` then
    hashes.
16. **FIXED** **`.`/`..` entry handling diverges per walker** — `XISOSharp/XisoReader.cs:659`:
    `TraverseXiso` throws for them while `ReadDirectoryEntries`/`ReadRawEntries` skip,
    `AuditWalk` doesn't flag, and `GetValidSectors`/`CollectFileEntries` treat them as real
    entries; an image can audit/list fine yet abort extraction/rewrite.
17. **FIXED** **Only one walker enforces the directory table size** — `XISOSharp/XisoReader.cs:2507`:
    `AuditWalk`, `ReadDirectoryEntries` (`:4024`), `ReadRawEntries` (`:2947`), `SalvageWalk`
    and `Repairer.CollectWalk` bound child offsets by image length only, so corrupt offsets
    into another file's data can audit as valid while extraction rejects them.
18. **FIXED** **Default output naming misses `/`** — `XISOSharp/XisoReader.cs:1759`: splitting
    `imageName` with `LastIndexOf(Constants.PathChar)` only means a `C:/games/game.iso` input
    creates/chdirs into the source directory instead of a `game` subdirectory;
    `XisoWriter.cs:207` correctly uses both separators.
19. **FIXED** **Repair collision check is case-sensitive** — `XISOSharp/XisoRepairer.cs:343` uses
    `StringComparer.Ordinal` while XISO names are case-insensitive elsewhere; `A/B` → `A_B`
    is allowed when `a_b` exists, producing a duplicate the re-audit doesn't report,
    contradicting the class doc (`:19`).
20. **FIXED** **Optimized-tag probe ignores the disc offset** — `XISOSharp/XisoReader.cs:2457`:
    `ProbeOptimizedTag` seeks the absolute tag offset and ignores `discLseek`, so offset
    (Redump/XGD) images report the tag missing in audit/`IsOptimized`.
21. **FIXED** **CWD not restored on pre-try throws** — `XISOSharp/XisoWriter.cs:301`: tree generation,
    cancellation checks and name validation run before the `try`; the per-directory
    `SetCurrentDirectory(prevDir)` at `:831` is also not in a `finally`.
22. **FIXED** **Relative output path resolves against the source** — `XISOSharp/XisoWriter.cs:241`
    (verified logic): `xisoPath` is combined from a possibly-relative `outputDirectory`
    before the chdir, but the stream is opened at `:347` after
    `SetCurrentDirectory(rootDirectory)`, so `-c src out\game.iso` writes to
    `src\out\game.iso`; the collision check at `:248` resolves against the original CWD, so
    validation and the write disagree.
23. **FIXED** **`IsWithinDirectory` fails under filesystem roots** — `XISOSharp/XisoPaths.cs:58`
    (verified): `TrimTrailingSeparators` keeps the trailing separator on roots (`C:\`,
    `\\server\share\`), so `full[dir.Length]` is the first name character and every path
    under a drive/share root reports "not within"; `CopyOut` to `D:\` throws and
    `LocalFilesystem("C:\\")` throws `UnauthorizedAccessException`.
24. **FIXED** **ZAR silently succeeds on a corrupt root table** — `XISOSharp/XisoZarchive.cs:224`:
    `ParseNode` returns when `childOffset >= dirSize`, so a root table size of 0 yields an
    empty `.zar` and `CreateZar` returns true, while `XisoReader` rejects the same image.
25. **FIXED** **Child offsets truncate to 16 bits** — `XISOSharp/DirectoryEntryTableWriter.cs:176`:
    `(ushort)(Offset / DwordSize)` with no bound; a table over 65535 DWORDs (~16k entries)
    silently writes a corrupt table instead of failing.
26. **FIXED** **Final CISO index entry can overflow 31 bits** — `XISOSharp/CisoWriter.cs:348`:
    the final entry casts `position >> align` to `uint` and masks the top bit without the
    `> 0x7FFFFFFF` guard used per-sector (`:331`), so an oversized payload is written with a
    bogus end offset instead of a clear error.
27. **FIXED** **CISO core reader validation gaps** — `XISOSharp/CisoReader.cs:306`:
    `ReadFromCsoCore` checks magic/header/block size but not `version` (v3+ silently treated
    as LZ4) or `align`, and allocates `new uint[indexLen]` from an unbounded
    `uncompressedSize` — a crafted header can drive a huge allocation.
28. **FIXED** **Temp ISO leak on compress failure** — `XISOSharp/CisoWriter.cs:96`: the temp ISO is
    created before the `try`/`finally` at `:106`, so a packing throw/non-zero return leaves
    the `%TEMP%` file behind.
29. **FIXED** **`CreateFromRemapTree` bypasses `CreateLock`** — `XISOSharp/XisoWriter.cs:992`:
    it mutates shared `Logger.TotalBytes/TotalFiles` and captures/restores the process CWD
    without the lock, so it can interleave with a concurrent `CreateXiso` and restore the
    wrong directory.
30. **FIXED** **Combined `--wipe --trim` drops later modes** — `XISOSharp.Cli/Program.cs:4175`: the
    `continue` meant to skip the separate trim also skips the following `--petrify`/`--zar`
    for that image when the wiped file is absent or the wipe was declined.
31. **FIXED** **TestDataWriter releases a mutex it may not own** — `XISOSharp.TestDataGenerator/TestDataWriter.cs:64`:
    on cross-process `WaitOne` timeout the `finally` calls `ReleaseMutex()` without
    ownership, replacing the clear `TimeoutException` with `ApplicationException`; this runs
    from a `[ModuleInitializer]`, so the whole test host fails with a misleading error.
32. **FIXED** **`--silent` is order-dependent** — `XISOSharp.Cli/Program.cs:890`: it consults
    `checksumFlagMode` while parsing, so `--silent --checksum file` errors while
    `--checksum --silent file` works, contradicting the CLI-002 order-independence contract.
33. **FIXED** **`--file-time` silently ignored outside create** — `XISOSharp.Cli/Program.cs:631`:
    consumed only in the create-list branch (`:1523`); extract/list/tree/rewrite (including
    `--pack`) accept and ignore it, unlike the CLI-017 pattern of rejecting ignored
    mode-specific flags.
34. **FIXED** **Extract ignores `--preserve-attrs`** — `XISOSharp.Cli/Program.cs:2210`: the pure-extract
    branch never calls `RejectIgnoredOutputFlags`, and the redump batch (`:1468`) does the
    same, while list/tree/create reject it.
35. **FIXED** **`--validate-strict` is a no-op** — `XISOSharp.Cli/Program.cs:498`: `validateStrict` only
    gates flag placement (`:1151`), never reaches `XisoValidator`; since CLI-020 makes
    non-strict rewrites exit 2 as well, the flag contradicts the help text (`:4863`).
36. **FIXED** **CLI compress guard misses the split first part** — `XISOSharp.Cli/Program.cs:3187`:
    only the derived base output is checked, while the GUI mirror also checks the `.1.cso`
    part (`MainViewModel.cs:743`); a source named like its own first part passes the guard
    and only fails later inside `CisoWriter`.
37. **FIXED** **`--jobs`/`--policy` silently ignored outside `--zar`** — `XISOSharp.Cli/Program.cs:767`:
    parsed unconditionally but read only by the parallel ZAR path (`:3855`), unlike other
    mode-specific flags that are rejected.
38. **FIXED** **Vacuous tests** — `XISOSharp.Tests/ApplicationStatsExtraTests.cs:31`
    (`RecordLaunch_WhenDisabled_DoesNotThrow` passes because the test host is already
    disabled, never exercising `XISO_DISABLE_STATS`) and
    `XISOSharp.Tests/AuditXisoTests.cs:244` (the only assertion sits inside
    `if (result.IsValid)`, so a regression to invalid still passes).
39. **FIXED** **Doc drift: battle harness default** — `docs/testing.md:253` says 3 ISOs; the code
    default is 1 (`XISOSharp.BattleTests/BattleOptions.cs:10`, README.md:24).
40. **FIXED** **Doc drift: coverage artifact OS** — `docs/testing.md:187` says `ubuntu-latest`;
    `.github/workflows/ci.yml:73,93-99` uploads coverage from `windows-latest` only.
41. **FIXED** **Doc drift: Tester CI claim** — `docs/testing.md:243` says the WPF Tester is not part of
    CI; the full solution (including `XISOSharpTester`, with `EnableWindowsTargeting`) is
    built on all three OSes.
42. **FIXED** **Doc drift: missing scripts** — `docs/testing.md:97,102,155` (plus
    `docs/contributing.md:50`, `docs/faq.md:21`, `docs/library.md:59`) reference
    `Scripts/Build-CReference.ps1`, `Verify-Output.ps1` and `Verify-MediaPatch.ps1`, none of
    which exist in the repo.
43. **FIXED** **Doc drift: `--help` claim** — `README.md:125` (and `docs/cli.md:39`, `README.md:571`)
    say `--help` is treated as a filename; the CLI handles `--help` and exits 0
    (`XISOSharp.Cli/Program.cs:303-306`).
44. **FIXED** **Doc drift: Release build packs** — `README.md:591,595` say a Release solution build
    packs NuGet; `GeneratePackageOnBuild` is false (`XISOSharp/XISOSharp.csproj:55`).
45. **FIXED** **Publish scripts wipe protected artifact stores** — `publish-cli.ps1:60` deletes
    `publish/<rid>` and `publish-gui.ps1:35` deletes `publish-gui/<rid>`, contradicting
    AGENTS.md's hard rule that those trees are never deleted.
46. **FIXED** **GUI accepts `.zar` where the reader cannot** — `XISOSharp.Gui/Views/MainWindow.axaml.cs:36`
    (and the Extract filter at `MainWindow.axaml:21`) includes `.zar`, but
    `XisoReader.OpenImageStream` supports only ISO/CISO (`XisoReader.cs:61-71`); a dropped or
    picked `.zar` is routed to Extract and fails while the header hint advertises `.zar`.
47. **FIXED** **File-time helpers bypass CISO support** — `XISOSharp/XisoReader.cs:2165`:
    `GetFileTimeRaw`/`SetFileTime` open a plain `FileStream` instead of `OpenImageStream`
    (unlike `GetVolumeInfo`/`GetSectorLayout`/`ListDirectory`), so a `.cso` path fails here
    while sibling APIs succeed.

## Low

48. **FIXED** `XISOSharp/XisoReader.cs:1468` — `IsOptimizedImage(Stream)` uses a single `Read` and
    requires 24 bytes, so a legitimate short read reports "not optimized"; sibling probes
    use `ReadExact`.
49. **FIXED** `XISOSharp/XisoReader.cs:93` — `StripRewriteSuffix` does `filename[..^4]` for non-`.old`
    names without a length check; a name shorter than 4 chars throws instead of returning
    the documented error code.
50. **FIXED** `XISOSharp/XisoRepairer.cs:38` — doc says the `.old` backup is "replacing any previous
    backup", but the code keeps the first backup (`XisoPatcher.cs:28`, BUG-LIB-027).
51. **FIXED** `XISOSharp/XisoExplorer.cs:337` — `Dispose` sets `_disposed` and disposes `_heldStream`
    without taking `_sync`, racing keep-open operations the class doc says are safe.
52. **FIXED** `XISOSharp/XisoValidator.cs:231` — `LogResult` prints `Checksums: MATCH` whenever
    `checksumsVerified` is true, even when files are missing/extra and never compared.
53. **FIXED** `XISOSharp/XisoWriter.cs:493` — no error path unlinks the partially written output ISO,
    so a failed/cancelled create leaves a truncated `.iso` that looks like an artifact.
54. **FIXED** `XISOSharp/CisoWriter.cs:119` — a failed compress leaves the partial `.cso` (and created
    split parts); only `tempIso` is cleaned, unlike `XisoSplitter`.
55. **FIXED** `XISOSharp/RemapFilesystem.cs:639` — the duplicated typed walk swallows unreadable-dir
    errors with `catch { continue; }` while `BuildMappings` logs them; the divergence hides
    why entries are missing.
56. **FIXED** `XISOSharp/XisoWriter.cs:659` — sizes/byte counts are interpolated with the current
    culture (no `InvariantCulture`), making logs locale-dependent.
57. **FIXED** `XISOSharp/XisoZarchive.cs:150` — `ParseXdvdfs` runs outside the try, so
    `XisoFormatException`/`EndOfStreamException` escape `CreateZar`'s true/false contract.
58. **FIXED** `XISOSharp/XisoZarchive.cs:262` — a short read of an entry name (`n == 0`) silently
    returns, dropping the entry and its right subtree; a truncated image can yield an
    incomplete archive reported as success.
59. **FIXED** `XISOSharp/CisoReader.cs:327` — `ReadFromCsoCore` rejects only `sector >= totalBlocks`; a
    read starting at/after `uncompressedSize` but inside the zero-padded last block returns
    padding bytes, while the block-device paths return 0 at EOF.
60. **FIXED** `XISOSharp/SectorAllocator.cs:353` — coalescing casts `(uint)(end - lastStart)`; adjacent
    ranges spanning the full 32-bit sector space wrap the merged count to 0.
61. **FIXED** `XISOSharp/CisoWriter.cs:398` — `DeriveDefaultCsoPath(".")` uses `Path.GetFileName(".")`
    = `.` and `GetDirectoryName(".")` = `""`, producing `..cso` in the CWD.
62. **FIXED** `XISOSharp/Latin1Encoding.cs:55` — the `GetBytes(char[])`/`GetChars(byte[])` overrides
    never validate index/count, surfacing `IndexOutOfRangeException` instead of the
    `Encoding` contract's `ArgumentOutOfRangeException`.
63. **FIXED** `XISOSharp/LocalFilesystem.cs:84` — without a `Root`, `FileExists("")` calls
    `Path.GetFullPath("")` which throws `ArgumentException`; only `IOException`/
    `UnauthorizedAccessException` are caught, violating `IFilesystem`'s "unresolvable paths
    return false".
64. **FIXED** `XISOSharp.Gui/Services/CliRunner.cs:62` — cancellation, timeout and start failures all
    return `-1` without invoking the `onLine` sink, so the UI shows only "finished with exit
    code -1" while the real reason stays in the file log.
65. **FIXED** `XISOSharp.BattleTests/Program.cs:14` — the banner hardcodes "extract-xiso (v2.7.1)"
    while AGENTS.md mandates the dated reference build `202609111233`.
66. **FIXED** `XISOSharp.BattleTests/BattleReport.cs:17` — reports go under
    `Directory.GetCurrentDirectory()/BattleReports`, not "next to the sources" as
    `docs/testing.md:300` claims.
67. **FIXED** `docs/building.md:147` — claims a green CI implies reference byte-compatibility, but the
    reference-binary interop tests early-return when the gitignored `References/` binaries
    are absent (`docs/testing.md:75-77`).
68. **FIXED** `docs/getting-started.md:59` — sample banner omits the URL that `Constants.Banner`
    always includes (`XISOSharp/Constants.cs:229`) and shows an outdated version.
69. **FIXED** `docs/testing.md:85` — the TestData table lists `source/empty_dir/`, `rewrite_c/`,
    `rewrite_cs/`; `TestDataWriter` creates none of them.
70. **FIXED** `docs/testing.md:316` (and `docs/contributing.md:57`) — links `../ConversionPlan.md`,
    which does not exist.
71. **FIXED** `XISOSharpTester` dead code — `Services/HashUtil.cs:31` (`IsAllZero`) and `:71`
    (`ComputeMd5`), `Models/XisoFileEntry.cs:32` (`IsSmall`), and the unused sync wrappers
    `Run`/`RunQuiet`/`ListFiles`/`ExtractFiles`/`Rewrite`/`GetVersion` in
    `Services/ExtractXisoWrapper.cs` have no callers.
72. **FIXED** `XISOSharp.Tests/CliLocatorTests.cs:42` — version-cleanliness assertions run only when
    `ProductVersion` is non-null, so a regression to null is not caught.
73. **FIXED** `XISOSharp.Cli/Logging/BugReporter.cs:47` — `LastByKey` entries are never pruned; an
    unbounded dictionary in long-running GUI/Tester hosts.
74. **FIXED** `XISOSharp.Cli/Logging/ApplicationStats.cs:27` and `BugReporter.cs:31` — the telemetry
    bearer key is hardcoded and shipped in every bundle (trivially extractable; by design
    for an anonymous client, but a known risk).
75. **FIXED** `XISOSharp.Tests/TestConditions.cs:221` — the Windows symlink probe ignores a false
    `WaitForExit(30000)` and never kills a hung `mklink`, leaving an orphan child.
76. **FIXED** `XISOSharp.Tests/XisoSnapshotTests.cs:17` — resolves `Fixtures/test_fixture.iso` with a
    fixed 3-level traversal instead of the `TestDataLocator` helper BUG-TEST-006 says
    centralizes path resolution.
77. **FIXED** `XISOSharp.Cli/Program.cs:1450` — duplicate `assumeYes && assumeNo` check, unreachable
    because `:1143` already returned.
78. **FIXED** `XISOSharp.Cli/Program.cs:1005` — the error message claims `--skip-sectors` is supported
    in create mode, but `:973` rejects exactly that combination.
79. **FIXED** `XISOSharp.Cli/Program.cs:3823` — `RunRedumpBatch` discards `securitySectorsPath`
    (`_ = securitySectorsPath;`) while the main parser always rejects the flag (`:988`); the
    parameter is unreachable dead code.
80. **FIXED** **Update-check logic duplicated** — `XISOSharp.Gui/Services/UpdateService.cs` mirrors the
    CLI's `UpdateChecker` version parsing/comparison and probes GitHub on every GUI start,
    while the CLI uses a 24-hour disk cache; the two should share one implementation (and a
    cache) to avoid drift.

## Test-suite health

- During the full-suite verification run, `net9.0` reported one transient failure that did
  not reproduce on rerun (1713/1714 green). The failing test name was not captured; treat
  shared-state/timing flakes as a standing risk and consider capturing the name on the next
  occurrence.
- `net10.0` is green at 1796 passed / 1 skipped (1797 total); `net8.0`/`net9.0` are green at
  1736 passed / 1 skipped (1737 total) after the items 1–80 fixes added 23 regression tests
  (7 for items 1–20, 16 for items 21–80).

## Markers without an in-repo tracker index

`TODO #n` markers are cross-referenced in docs, but these `BUG-` markers carry IDs with no
in-repo tracker entry or test/doc cross-reference found: `BUG-LIB-020`
(`XISOSharp/LocalFilesystem.cs:34`), `BUG-LIB-025` (`XISOSharp/XisoPaths.cs:15`),
`BUG-LIB-039` (`XISOSharp/Latin1Encoding.cs:21`), `BUG-GUI-012`
(`XISOSharp.Gui/Views/MainWindow.axaml.cs:34`), `BUG-X-001/004/005`
(GUI/Tester linked logging, process runner, tool locator), `BUG-TEST-005/006/007`
(TestDataGenerator), `BUG-BEN-001`–`004` (Benchmarks), and `TODO #11`
(`XISOSharpTester/ViewModels/MainViewModel.Explore.cs:18`).

## Suggested fix order

1. High items 1–7 (data loss / path escape / broken verbs), then 8–11.
2. Medium CLI/GUI contract mismatches (30–37) together with their tests.
3. Medium library correctness (12–29), starting with the overflow and allocation-guard
   items.
4. Docs drift (39–45, 67–70) as a single documentation pass.
5. Low items opportunistically; 73–76 as small hardening cleanups.
