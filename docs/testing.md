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

The xUnit suite lives in `XISOSharp.Tests` (target: net10.0). Run it with:

```bash
dotnet test XISOSharp.Tests
```

Highlights:

| Area | Files |
|---|---|
| Core create/extract/list/rewrite round-trips | `IntegrationTests.cs` |
| Reader edge cases and XGD offset detection | `XisoReaderTests.cs`, `XisoReaderEdgeCaseTests.cs` |
| Writer edge cases (empty dirs, large files, custom names) | `XisoWriterEdgeCaseTests.cs` |
| AVL tree behavior | `AvlTreeTests.cs`, `AvlTreeEdgeCasesTests.cs`, `AvlNodeTests.cs` |
| Audit | `AuditXisoTests.cs` |
| Validation | `XisoValidatorTests.cs` |
| Boyer–Moore search | `BoyerMooreTests.cs`, `BoyerMooreEdgeCasesTests.cs` |
| Encoding (Latin-1 round-trips) | `Latin1EncodingTests.cs` |
| Glob matching | `GlobMatcherTests.cs` |
| Exclude patterns | `ExcludePatternsTests.cs` |
| Skip/prepend sectors | `SkipPrependSectorsTests.cs` |
| Logging, constants, types, exceptions | `LoggerTests.cs`, `ConstantsTests.cs`, `TypesTests.cs`, `XisoExceptionTests.cs`, … |

Conventions:

- Tests run **sequentially** (`[Collection("Sequential")]`) because create/extract
  operations temporarily change the current directory.
- Tests create their own temp directories and clean up afterwards.
- A snapshot-style round-trip (create → extract → compare SHA-256 of every file) is
  the standard correctness pattern.

## TestData fixtures

`TestData/` holds stable fixtures:

| Path | Purpose |
|---|---|
| `source/` | Reference source tree: `binary.bin`, `file1.txt`, `file2.txt`, `subdir/`, `empty_dir/`, `test.xbe` |
| `rewrite_c/` | Output of the reference C tool (known-good) |
| `rewrite_cs/` | Output of this implementation (compared against `rewrite_c/`) |
| `output/` | Scratch area used by tests |

The presence of `test.xbe` ensures the media-enable patch path is exercised on every
create round-trip.

## Reference cross-checking

Two PowerShell helpers compare this implementation against the original C tool:

### `Scripts/Build-CReference.ps1`

Builds the reference `extract-xiso` from the bundled sources under `References/`
(CMake-based). Requires a C compiler (e.g. Visual Studio Build Tools or gcc).

### `Verify-Output.ps1`

Runs both tools over the same inputs and diffs the results:

```powershell
.\Verify-Output.ps1
```

Parameters (all optional):

| Parameter | Default / values |
|---|---|
| `-CExtractXiso` | Path to the C tool's `extract-xiso.exe` |
| `-CsExtractXiso` | Path to this project's CLI executable |
| `-TestData` | Path to the `TestData` folder |
| `-Mode` | `all` (default) — runs every scenario; or one of `version`, `create`, `extract`, `list`, `rewrite` |

> [!NOTE]
> The script defaults point at the sibling repo layout
> (`C:\Sincronizar\source\repos\CSharp_ExtractXiso`). Pass explicit paths if your
> checkout differs.

## Media-patch integration verification

The `.xbe` media-enable patch (pattern `E8 CA FD FF FF 85 C0 7D`, byte 7 → `0xEB`) is
applied when **writing** an ISO (create/rewrite), never when extracting. It is covered
at three levels:

1. **Unit tests** — `BoyerMooreTests` (search semantics) and `XisoWriterEdgeCaseTests`
   (`CreateXiso_MediaEnable_*`): create→extract round-trips that assert the patched
   bytes, including a pattern straddling the 2 MB read-buffer boundary (exercises the
   Boyer-Moore overlap logic), the disabled mode (`-m` / `Logger.MediaEnable = false`),
   and that non-`.xbe` files are untouched.
2. **Reference cross-check script** — `Scripts/Verify-MediaPatch.ps1`:
   1. extracts a real game ISO once (extraction never patches → original `.xbe` bytes),
   2. creates an ISO from those files with the reference C tool and this
      implementation, patched (default) and unpatched (`-m`),
   3. reads the `.xbe` files back out of each created ISO and proves: patched and
      unpatched files are byte-identical between the two tools; at every pattern site
      the patched file differs from the original exactly at byte 7 (`0x7D` → `0xEB`)
      and nowhere else; `.xbe` files without the pattern are untouched; unpatched
      creates keep the original bytes.
3. **Real-ISO validation** (Redump dumps of original Xbox games):
   - *007 – Everything or Nothing*: `default.xbe` and `driving.xbe` each contain one
     pattern site (`0x5399C` / `0x2561A1`) — patched output byte-identical between
     tools and matching the exact expected transformation; 16/16 checks passed.
   - *007 – Agent Under Fire*: `bond.xbe` has no pattern site — untouched by both
     tools.

```powershell
.\Scripts\Verify-MediaPatch.ps1 -IsoPath "H:\XBOXTest\007 - Everything or Nothing [NTSC-U][Redump].iso"
```

Parameters: `-IsoPath`, `-CExtractXiso` (reference C tool), `-CsExtractXiso` (this
implementation), `-WorkDir`, `-SkipExtract` (reuse an existing extraction).

## Coverage

The CI collects coverage with XPlat code coverage:

```bash
dotnet test XISOSharp.Tests --collect:"XPlat Code Coverage"
```

The report (`coverage.cobertura.xml`) is uploaded as a CI artifact from the
`ubuntu-latest` job.

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

## The GUI regression tester

`XISOSharpTester` is a WPF application (net10.0-windows) for **batch regression
testing**: it runs the same scenario across many ISOs with the reference C tool and
this implementation, compares outputs (file sets and hashes via `HashUtil`), and
exports PDF reports (`PdfExporter`). Services:

| Service | Purpose |
|---|---|
| `ExtractXisoWrapper` | Invokes the reference `extract-xiso.exe` |
| `XisoTestRunner` | Orchestrates test scenarios and comparisons |
| `HashUtil` | SHA-256 comparison of extracted outputs |
| `PdfExporter` | Test-session report generation |
| `TestProgress` | UI progress reporting |

It targets Windows only and is not part of CI.

See also: [Building](building.md) · [Contributing](contributing.md) ·
[Conversion plan](../ConversionPlan.md)
