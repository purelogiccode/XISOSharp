# Testing

XISOSharp treats byte-compatibility with the reference C tool as its core guarantee.
This page describes the automated test suite, the test fixtures, the
reference-comparison scripts, and the benchmarks.

- [Test suite](#test-suite)
- [TestData fixtures](#testdata-fixtures)
- [Reference cross-checking](#reference-cross-checking)
- [Coverage](#coverage)
- [Benchmarks](#benchmarks)
- [The GUI regression tester](#the-gui-regression-tester)

## Test suite

The xUnit suite lives in `XISOSharp.Tests` (engine + CLI, targets: net8.0/net9.0/net10.0).
`ZArchiveSharp.Tests` (pure-C# ZArchive/zstd port) moved to the sibling
`../CSharp_ZArchiveSharp` repo with its own solution and CI. Run the local suite with:

```bash
dotnet test XISOSharp.Tests
```

(Plain `dotnet test` on the solution runs it.)

Highlights:

| Area | Files |
|---|---|
| Core create/extract/list/rewrite round-trips | `IntegrationTests.cs` |
| Reader edge cases and XGD offset detection | `XisoReaderTests.cs`, `XisoReaderEdgeCaseTests.cs` |
| Writer edge cases (empty dirs, large files, custom names) | `XisoWriterEdgeCaseTests.cs` |
| AVL tree behavior | `AvlTreeTests.cs`, `AvlTreeEdgeCasesTests.cs`, `AvlNodeTests.cs` |
| Audit | `AuditXisoTests.cs` |
| Repair (class-C in-place, backup/dry-run, CISO/split refusals) | `XisoRepairTests.cs` |
| Salvage rebuild (carry/drop, CISO, `--repair-out`, `-y`/`-n`) | `XisoSalvageTests.cs` |
| Executable info (`GetXexInfo`/`GetXbeInfo`, explorer, CLI) | `XbeInfoTests.cs`, `XisoCsoExplorerTests.cs` |
| Disc identity (`VolumeInfo.DiscFormat`, `-i`) | `XisoDiscFormatTests.cs` |
| Validation | `XisoValidatorTests.cs` |
| Boyer–Moore search | `BoyerMooreTests.cs`, `BoyerMooreEdgeCasesTests.cs` |
| Encoding (Latin-1 round-trips) | `Latin1EncodingTests.cs` |
| Glob matching | `GlobMatcherTests.cs` |
| Exclude patterns | `ExcludePatternsTests.cs` |
| Skip/prepend sectors | `SkipPrependSectorsTests.cs` |
| Unpack resume (`UnpackOptions.SkipExisting`, cancel+resume, copy-out) | `UnpackResumeTests.cs` |
| Table writer (offsets, encoding, byte-identity with writer output) | `DirectoryEntryTableWriterTests.cs` |
| Snapshot (`Fixtures/test_fixture.iso` byte-identity; extract/rewrite SHA-256 per file) | `XisoSnapshotTests.cs` |
| Corruption resilience (truncated tables, bad pointers, huge sizes, bad names, >4 GB, zero-size directories) | `XisoCorruptionResilienceTests.cs` |
| Review hardening (path escapes, table bounds, sentinels, split naming, partial/pre-existing output cleanup, offset-image tag repair) | `XisoCorruptionResilienceTests.cs`, `XisoRedumpAndSkeletonTests.cs`, `CisoTests.cs`, `XisoRepairTests.cs` |
| Reader gap-closers (multi-sector tables, disc probes, device errors, sentinels) | `XisoCoverageTests.cs` |
| Legacy interop (reference extract-xiso legacy-layout images via `llCompat` extract/list/rewrite) | `XisoLegacyInteropTests.cs` |
| In-place patching (replace/add, table moves, errors, backup, `.xbe`, `--copy-in` CLI) | `XisoPatcherTests.cs` |
| Extraction robustness (truncation errors, file context, `--continue-on-error`, CLI) | `ExtractRobustnessTests.cs` |
| XISO → ZAR conversion (extract round-trip, zstd ratio gate, reader hashes, `removeUpdate`, offsets, `zarchive.exe` interop) | `XisoZarConvertTests.cs` |
| Input==output safety guards (library + CLI) and misplaced-flag errors | `XisoOutputGuardTests.cs`, `CliOutputGuardTests.cs` |
| Extract destination edge cases: trailing separators, UNC, spaces, empty, CLI end-to-end | `CliDestinationDirTests.cs` |
| Public stream API (`OpenImageStream`, `Stream` overloads, seekability guards) | `XisoStreamApiTests.cs` |
| Destination filesystems (`IFilesystem`, `LocalFilesystem`, `MemoryFilesystem`, generic `UnpackImage` parity) | `XisoFilesystemTests.cs` |
| Image explorer (`XisoExplorer` load/navigate/copy-out/hash/XEX/path helpers) | `XisoExplorerTests.cs` |
| Image splitting (`XisoSplitter` split/halves/join, alignment, guards, CLI verbs) | `XisoSplitTests.cs` |
| CISO compress/decompress, split parts, `.cso` auto-detect | `CisoTests.cs`, `CisoAutoDetectTests.cs` |
| Golden interop vs reference `xdvdfs-cli 0.8.3` (both directions, split layout) | `CisoSplitInteropTests.cs` |
| Logging, constants, types, exceptions | `LoggerTests.cs`, `ConstantsTests.cs`, `TypesTests.cs`, `XisoExceptionTests.cs`, … |

Conventions:

- Tests run **sequentially** (assembly-wide
  `[CollectionBehavior(DisableTestParallelization = true)]`, on top of
  `[Collection("Sequential")]` on the CWD-sensitive classes) because create/extract
  operations temporarily change the process-wide current directory.
- Tests create their own temp directories and clean up afterwards.
- A snapshot-style round-trip (create → extract → compare SHA-256 of every file) is
  the standard correctness pattern, locked by the checked-in reference
  `XISOSharp.Tests/Fixtures/test_fixture.iso` (built deterministically with
  `fileTime: 0`; validate with `XISO_UPDATE_FIXTURE=1 dotnet test --filter
  FullyQualifiedName~ValidateFixtureIso_WhenRequested`, then regenerate the
  binary out of band after a legitimate writer change and commit it).
- Reference-binary interop tests (`CisoSplitInteropTests.cs`,
  `XisoLegacyInteropTests.cs`) silently pass when the binary under `References/`
  is absent, so CI and clean checkouts stay green.

## TestData fixtures

`TestData/` holds stable fixtures:

| Path | Purpose |
|---|---|
| `source/` | Reference source tree: `binary.bin`, `file1.txt`, `file2.txt`, `subdir/subfile.txt`, `subdir/nested/deep.txt`, `test.xbe` |
| `output/` | Scratch area; also holds the derived `source.iso` fixture (always rebuilt) |

The presence of `test.xbe` ensures the media-enable patch path is exercised on every
create round-trip.

## Reference cross-checking

There are no standalone PowerShell helper scripts in the repository; comparison
against the original C tool is built into the test suite and the battle harness:

- `XISOSharp.Tests/XisoLegacyInteropTests.cs` creates legacy-layout images with
  the reference `extract-xiso.exe` (gitignored `References/` drop, dated build
  `202609111233`) and round-trips them through the `llCompat` reader paths. The
  tests early-return when the oracle is absent, so clean checkouts and non-Windows
  CI stay green.
- `XISOSharp.Tests/CisoSplitInteropTests.cs` checks split-CISO golden vectors
  against the reference `xdvdfs-cli` 0.8.3.
- `XISOSharp.BattleTests` ([below](#the-cli-battle-harness)) compares
  list/extract/rewrite against a real `extract-xiso.exe` and the xdvdfs/xboxkit
  oracles over game dumps, failing the run on any mismatch.

Build the reference C tool from the bundled sources under `References/`
(CMake-based; requires a C compiler) or drop a prebuilt `extract-xiso.exe` into
the oracle discovery path (`SkipConditions.OraclePath`).

## Reference-binary interop tests

`XISOSharp.Tests/CisoSplitInteropTests.cs` (split-CSO golden vectors vs the
reference `xdvdfs-cli 0.8.3`) and (in the sibling `../CSharp_ZArchiveSharp` repo)
`ZArchiveSharp.Tests/ZArchiveSharpTests.cs` (`zarchive.exe` both-directions interop) shell out to reference binaries that live
in the gitignored `References/` folder (`References/xdvdfs-0.8.3/xdvdfs.exe`;
the ZArchiveSharp-side `zarchive.exe` now lives in the sibling `../CSharp_ZArchiveSharp`
repo at `References/ZArchive-0.1.2/zarchive.exe`). The convention, mirroring the
`zarchive.exe` pattern:

- Tests silently pass (early `return`) when the binary is absent, so CI and clean
  checkouts stay green without the binaries.
- `xdvdfs` parts land relative to the child working directory (the reference
  `SplitOutput` derives part names from the file name only), so tests set
  `ProcessStartInfo.WorkingDirectory` to a temp dir.
- The content oracle is `xdvdfs md5` (`open_image`-aware): `unpack`/`copy-out`
  take raw ISOs only, and stock 0.8.3 itself cannot read sparse multi-part files,
  so multi-part assertions check writer-layout parity plus our-reader round-trips.
  See [Compression](compression.md#round-trip--interop).

## Media-patch integration verification

The `.xbe` media-enable patch (pattern `E8 CA FD FF FF 85 C0 7D`, byte 7 → `0xEB`) is
applied when **writing** an ISO (create/rewrite), never when extracting. It is covered
at three levels:

1. **Unit tests** — `BoyerMooreTests` (search semantics) and `XisoWriterEdgeCaseTests`
   (`CreateXiso_MediaEnable_*`): create→extract round-trips that assert the patched
   bytes, including a pattern straddling the 2 MB read-buffer boundary (exercises the
   Boyer-Moore overlap logic), the disabled mode (`-m` / `Logger.MediaEnable = false`),
   and that non-`.xbe` files are untouched.
2. **Battle-harness cross-check** — the `rewrite` op compares this
   implementation against `extract-xiso.exe` byte-for-byte (SHA-256) and the
   `pack` op compares content checksums against `xdvdfs.exe`, both over real
   dumps and with the media-enable patch active; any divergence in the patched
   `.xbe` bytes fails the battle (see
   [The CLI battle harness](#the-cli-battle-harness)).
3. **Real-ISO validation** (Redump dumps of original Xbox games):
   - *007 – Everything or Nothing*: `default.xbe` and `driving.xbe` each contain one
     pattern site (`0x5399C` / `0x2561A1`) — patched output byte-identical between
     tools and matching the exact expected transformation; 16/16 checks passed.
   - *007 – Agent Under Fire*: `bond.xbe` has no pattern site — untouched by both
     tools.

## Coverage

The CI collects coverage with XPlat code coverage:

```bash
dotnet test XISOSharp.Tests --collect:"XPlat Code Coverage"
```

The report (`coverage.cobertura.xml`) is uploaded as a CI artifact from the
`windows-latest` job (the only job that runs the test suite).

Measured line coverage (coverlet, full net10.0 suite, 1.4.2): `XisoReader.cs`
94.2%, `XisoWriter.cs` 89.6%, `AvlTree.cs` 100% — all above the 85% target. The
remaining reader gaps are unreachable-by-construction defenses: the
per-table entry-count caps (offsets are 16-bit, so >65536 distinct positions
cannot occur), mid-copy I/O races, ACL-only permission paths, volume
re-checks after a successful entry lookup, and legacy degenerate table
shapes neither writer emits.

## Benchmarks

`XISOSharp.Benchmarks` uses BenchmarkDotNet (`[MemoryDiagnoser]`) for:

| Benchmark | Measures |
|---|---|
| `AvlTreeBenchmarks` | AVL insert performance |
| `BoyerMooreBenchmarks` | Pattern search performance |
| `NumSectorsBenchmarks` | Sector math |

```bash
dotnet run --project XISOSharp.Benchmarks -c Release
```

`ZArchiveSharp.Benchmarks` (zstd L1/L6/L19 compress + decode, `.zar` pack/extract
over memory streams) moved with the library to the sibling `../CSharp_ZArchiveSharp`
repo — run it there:

```bash
dotnet run --project ZArchiveSharp.Benchmarks -c Release -- --filter *
```

## The GUI regression tester

`XISOSharpTester` is a WPF application (net10.0-windows) for **batch regression
testing**: it runs the same scenario across many ISOs with the reference C tool and
this implementation, compares outputs (file sets and hashes via `HashUtil`), and
exports PDF reports (`PdfExporter`). Services:

| Service | Purpose |
|---|---|
| `ExtractXisoWrapper` | Invokes the reference `extract-xiso.exe` (Latin-1 stdout decoding) |
| `XisoTestRunner` | Orchestrates test scenarios and comparisons (list parser understands `\name (N bytes)`) |
| `HashUtil` | SHA-256 comparison of extracted outputs |
| `PdfExporter` | Test-session report generation |
| `TestProgress` | UI progress reporting |

Its main page also has an **Explore** section: an in-process image browser
(`TreeView` with lazy directory loading over `XisoExplorer` — no extraction)
with per-node copy-out, SHA-256 display, and an XEX2 info panel.

All GUI runners share one core implementation: the single shared
`XISOSharp.ProcessRunner` (async drains, timeout, tree-kill) + `XISOSharp.ToolLocator`
(override → sibling of the app → `PATH`, plus a `-v` probe) — replacing the old
per-app runners.

It targets Windows only at runtime; the project is still built as part of the full
solution on every CI OS (`EnableWindowsTargeting`), it just never runs there.

## The CLI battle harness

`XISOSharp.BattleTests` is a console harness that pits the **XISOSharp CLI** against
reference tools over real game dumps: the native **`extract-xiso.exe`** (reference build `202609111233`)
(beside the harness), **`xdvdfs.exe` 0.8.3** (xdvdfs-parity features), and
**`xboxkit.exe` 0.7** (XboxKit-parity archival features). It shells out to the
executables — no in-process library calls — so it tests exactly what end users
run. Each tool's stdout is decoded with its real encoding (extract-xiso Latin-1;
xdvdfs, xboxkit, and the XISOSharp CLI UTF-8), so non-ASCII names compare equal
instead of collapsing to `U+FFFD`:

- **Sampling:** picks a random sample of `*.iso` files (default **1**) from
  `H:\XBOXTest` (override with `--dir`, `--count`, explicit `*.iso` paths, or a
  `--seed` for reproducibility — the seed is reported for re-runs).
- **Battles per ISO** (select with `--ops <a,b,c>`; default = all; ops whose
  oracle exe is missing are skipped):
  - extract-xiso oracle:
    - `list` — `-l` entry lines must match exactly;
    - `extract` — `-x -d` trees must match: same file set (ordinal), same per-file
      SHA-256, same directory set;
    - `rewrite` — `-r -d` outputs must match byte-for-byte (SHA-256). Inputs are
      staged to copies first (the oracle renames its input to `.old`; the sources on
      `H:` are never touched).
  - xdvdfs oracle (xdvdfs-parity features):
    - `checksum` — deterministic SHA3-256 image checksums must match exactly;
    - `md5` — per-file MD5 lists must agree (xdvdfs-only dir rows are noted, not fatal);
    - `unpack` — `--unpack` vs `xdvdfs unpack`: extracted trees must match;
    - `pack` — `-c -m` vs `xdvdfs pack` over the same unpacked dir: the content
      checksums of both images must match (layout-agnostic parity);
    - `cso` — round-trip: XISOSharp compresses, **xdvdfs reads the CSO back**
      (md5 per file), XISOSharp decompresses, content checksum must equal the source.
  - xboxkit oracle (XboxKit-parity archival features; each side gets a staged copy):
    - `petrify`/`video`/`random`/`seed`/`zar` — outputs must match byte-for-byte
      (SHA-256). `zar` parity holds because our name table follows btree
      discovery order like xboxkit's (not pack order — see
      [Archival](archival.md#zar)). Exception: `petrify` falls back to a structural tiebreaker on
      mismatch — our skeleton must verify (bones verbatim, rest zeroed); if it
      does while xboxkit's does not, the op Skips as an oracle-side defect
      (xboxkit 0.7 zeroes filesystem tables inside mixed bone/file extents, see
      [Troubleshooting](troubleshooting.md#petrify-battle-skipped-oracle-emits-unreadable-skeleton));
    - `trim`/`wipe` — the game partition is staged to a sector-0 file first
      (xboxkit 0.7 cannot trim/wipe a full Redump: `-t`/`-w` alone write nothing
      there and `-y` silences the warning), then both tools run on it and the
      outputs are compared byte-for-byte;
    - `rebuild` — components extracted once, then the game partition is staged
      to a sector-0 file as the `<xiso>` input for both tools (neither
      rebuilder accepts a full Redump there — both validate the XISO header at
      `0x10000`); both tools rebuild the full redump and each rebuilt image
      must match the original byte-for-byte. xboxkit's output is read at its
      deterministic `<stem>.iso` path beside its staged input, never by
      newest-file guess (the staged component copies share that directory);
  - Redump-only ops (`video`/`random`/`seed`/`trim`/`wipe`/`petrify`/`rebuild`)
    auto-**skip** on trimmed XISOs — the reference tools refuse them there
    (xboxkit always exits 0, so success is detected via output files).
- **Timing:** every op reports `cli` vs `native`/`xdvdfs`/`xboxkit` seconds and a
  ratio; the summary totals both sides (JIT caveat: XISOSharp pays warm-up on its
  first op).
- **Exit codes:** `0` all passed, `1` config error, `2` any check failed.
- **Reports:** `BattleReports/battle_<stamp>.txt|.json` under the current working
  directory (per-op `cliSeconds`/`oracleSeconds` and per-tool totals in the JSON).

```bash
dotnet run --project XISOSharp.BattleTests -c Release -- --seed 2026
XISOSharp.BattleTests --ops list,extract,rewrite --count 3 --dir "H:\XBOXTest"
XISOSharp.BattleTests --ops checksum,md5,unpack,pack,cso      # xdvdfs-parity features
XISOSharp.BattleTests --ops petrify,video,random,seed,trim,wipe,zar,rebuild  # XboxKit archival
```

This is the regression gate for the extract-xiso byte-parity contract
([xdvdfs Compat](xdvdfs-compat.md) covers the packing layer; the format details —
attribute normalization and empty-file frontier sectors — are in
[XISO Format](xiso-format.md#attributes)).

See also: [Building](building.md) · [Contributing](contributing.md) ·
[Troubleshooting](troubleshooting.md)
