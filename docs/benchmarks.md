# Benchmarks

This page records the reproducible performance baselines for XISOSharp and a
methodology comparison against the reference tools. Results are informational:
the byte-level tests, not wall-clock numbers, are the compatibility guarantee.

- [Hardware and runtime](#hardware-and-runtime)
- [In-process library benchmarks](#in-process-library-benchmarks)
- [CLI comparison](#cli-comparison)
- [Methodology](#methodology)
- [Reproduce](#reproduce)
- [Interpreting the numbers](#interpreting-the-numbers)

## Hardware and runtime

| Component | Value |
|---|---|
| CPU | AMD Ryzen 9 7900 (12C/24T, 3.70 GHz base) |
| RAM | 32 GiB |
| System disk | ADATA LEGEND 840 NVMe SSD |
| OS | Windows 11, build `10.0.26300.9818` |
| Runtime | .NET 10.0.14, x64 RyuJIT (AVX-512) |
| BenchmarkDotNet | 0.15.8 |

The corpus is deterministic and generated on the fly; no game data is used.

## In-process library benchmarks

Command:

```powershell
dotnet run --project XISOSharp.Benchmarks -c Release -- --filter "*XisoEndToEnd*" --exporters markdown
```

| Method | Mean | Allocated |
|---|---:|---:|
| `Pack` (1 MiB tree -> ISO) | 4.518 ms | 12,439 KB |
| `OpenAndList` (open + full tree walk) | 330.0 us | 70.3 KB |
| `Hash` (CRC32/MD5/SHA-1/SHA-256/SHA3-256) | 14.556 ms | 1,028 KB |
| `CisoCompress` (level 6) | 8.949 ms | 12,969 KB |
| `CisoDecompress` | 2.895 ms | 1,314 KB |
| `Extract` (tree -> disk) | 4.783 ms | 225 KB |
| `CciCompress` (level 9, single part) | 2.753 ms | 2,080 KB |
| `CciDecompress` | 2.761 ms | 1,096 KB |
| `LargeImageRandomRead` (512 x 4 KiB reads from a 64 MiB file) | 12.664 ms | 1,038 KB |

All values are the BenchmarkDotNet mean after at least three measured
iterations. `Pack`/`CisoCompress`/`CciCompress` include writing the output file;
`Extract` includes creating the destination tree on disk.

## CLI comparison

Command (script in the repository):

```powershell
pwsh -File scripts/bench-compare.ps1 -Iterations 2 -SizeMiB 1024
```

Corpus: one 1 GiB tree (8 files, deterministic pseudo-random content), measured
once after a warmup. Wall clock includes process startup and cache effects.

### Create (source tree -> ISO)

| Tool | Median | Min | Max | MiB/s |
|---|---:|---:|---:|---:|
| XISOSharp `-c` | 742.2 ms | 653.3 ms | 831.0 ms | 1379.7 |
| extract-xiso `-c` | 1042.6 ms | 482.3 ms | 1603.0 ms | 982.1 |
| xdvdfs `pack` | 732.4 ms | 609.1 ms | 855.7 ms | 1398.1 |

### Extract (ISO -> source tree)

| Tool | Median | Min | Max | MiB/s |
|---|---:|---:|---:|---:|
| XISOSharp `-x` | 813.3 ms | 704.2 ms | 922.3 ms | 1259.1 |
| extract-xiso `-x` | 637.3 ms | 600.3 ms | 674.4 ms | 1606.7 |
| xdvdfs `unpack` | 1272.6 ms | 1227.5 ms | 1317.6 ms | 804.7 |

**XboxKit** is not in the plain-XISO create/extract tables: its `-x` extractor
only accepts Redump-layout images with a video partition, not a standalone
XISO. The BattleTests harness still compares XboxKit's Redump verbs
(`petrify`/`video`/`random`/`seed`/`trim`/`wipe`/`zar`/`rebuild`) on synthetic
and real Redump images.

## Methodology

- **In-process** BenchmarkDotNet runs isolate the library: no process startup,
  JIT and file-system cache controlled by BenchmarkDotNet's pilot/warmup loop.
- **CLI comparison** starts a fresh process per iteration, like a user would.
  A framework-dependent XISOSharp CLI adds roughly 0.4 s of .NET startup on
  this host; `extract-xiso` and `xdvdfs` start in tens of milliseconds. At the
  1 GiB corpus size the data path dominates, but the startup overhead remains
  visible. Use the in-process numbers for library throughput and the CLI
  numbers for end-to-end user-wait estimates.
- Output files are deleted before each measured iteration. The corpus and all
  scratch outputs live under the temp corpus root.
- Results depend on disk, CPU, OS cache state and power plan. Re-run the
  commands on the target hardware; do not compare numbers across machines as a
  pass/fail gate.

## Reproduce

```powershell
# In-process library benchmarks (all end-to-end cases)
dotnet run --project XISOSharp.Benchmarks -c Release -- --filter "*XisoEndToEnd*"

# CLI comparison (1 GiB corpus, 2 measured iterations)
pwsh -File scripts/bench-compare.ps1 -Iterations 2 -SizeMiB 1024

# Generate the public synthetic corpus used by tests/BattleTests
dotnet run --project XISOSharp.TestDataGenerator -c Release -- --corpus TestData/corpus --force
```

The comparison script only deletes its own `%TEMP%\xiso_bench_compare` workspace
or the explicit `-CorpusRoot` passed to it.

## Interpreting the numbers

- Create is dominated by file reads and sector writes; the three tools are
  within roughly 1.4x on this corpus.
- XISOSharp extraction was slower than native `extract-xiso` here but faster
  than xdvdfs `unpack`. Extraction includes creating the destination tree and
  per-file pre-sizing; `--jobs 2..64` can overlap independent file copies on
  SSDs.
- CCI is cheaper than CISO on this synthetic tree because its per-sector
  payloads are smaller; real game data with mixed entropy produces different
  ratios.
- The 64 MiB random-read case is a smoke test for the image facade and block
  device path, not a storage benchmark.
