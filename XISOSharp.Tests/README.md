# XISOSharp.Tests

[![CI](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml)
![Tests](https://img.shields.io/badge/tests-2162-brightgreen)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![xUnit](https://img.shields.io/badge/xUnit-2.9.3-blueviolet)](https://xunit.net/)
[![Test SDK](https://img.shields.io/badge/test--sdk-18.10.1-blueviolet)](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)
[![Coverlet](https://img.shields.io/badge/coverage-coverlet-blueviolet)](https://github.com/coverlet-coverage/coverlet)
![Test matrix](https://img.shields.io/badge/tests-windows--latest-lightgrey)
[![GitHub stars](https://img.shields.io/github/stars/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/issues)
[![GitHub contributors](https://img.shields.io/github/contributors/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/graphs/contributors)
[![Last commit](https://img.shields.io/github/last-commit/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/commits/master)
[![License](https://img.shields.io/badge/License-MIT-green)](https://github.com/purelogiccode/XISOSharp/blob/master/LICENSE)

Unit tests for the XISOSharp.Core library. Uses xUnit to verify the correctness of the C# implementation against the original extract-xiso reference.

## Test Coverage

- **AVL Tree** — insertion, balancing, left/right rotations, fetching, traversal (prefix/infix/postfix)
- **AVL Tree edge cases** — empty trees, duplicate keys, single node, degenerate inserts
- **Boyer-Moore** — pattern initialization, search, media-enable pattern matching
- **Types** — `ExtractError`, `ExtractErrorException`, `ExtractMode`, `CreateList`
- **Constants** — magic values, header data, offsets, padding constants
- **DirEntry** — directory entry structure and serialization
- **FileTimeHelper** — Unix epoch to Windows FILETIME conversion
- **Logger** — output suppression flags
- **XisoReader** — header verification and traversal
- **XisoInfo** — volume metadata, directory listing, entry info lookup
- **XisoReader.Tree** — recursive tree listing with sizes
- **XisoReader.CopyOut** — single file and directory extraction
- **XisoReader.CopyIn / XisoPatcher** — in-place patching: replace/add, table moves, errors, backup, `.xbe`, `--copy-in` CLI
- **DirectoryEntryTableWriter** — table offsets, record encoding, byte-identity with writer output
- **XisoReader.ComputeFileHash** — MD5 and SHA-256 per-file hashing
- **XisoReader.ComputeDirectoryHashes** — batch hashing of all files in a directory
- **XisoReader.AuditXiso** — deep integrity audit (header, tree, sectors, cycles)
- **Snapshot** — `Fixtures/test_fixture.iso`: deterministic create (`fileTime: 0`) is byte-identical across runs and to the reference; extract/rewrite round-trips match SHA-256 per file
- **Corruption resilience** — truncated tables, manipulated header pointers, out-of-image extents, `uint.MaxValue` sizes, invalid filenames, >4 GB inputs fail fast with named errors
- **Reader gap-closers** — multi-sector tables, disc-layout probes, block-device errors, sentinel shapes (line coverage: `XisoReader` 94.2%, `XisoWriter` 89.6%, `AvlTree` 100%)
- **Legacy interop** — images created by the reference extract-xiso (legacy layout) round-trip through `llCompat` extract/list/rewrite
- **File copier** — `XisoFileCopier.CopyExact` size matrix, truncation counts, short-read stitching, pooled buffers, mid-copy cancel, per-chunk `FileProgress` on copy-out/unpack
- **Filesystem destinations** — `IFilesystem`/`LocalFilesystem`/`MemoryFilesystem` semantics, generic `UnpackImage` byte-parity with the legacy disk unpack, resume/continue-on-error/cancel/truncation through custom destinations
- **Image explorer** — `XisoExplorer` load/navigate/copy-out/hash/XEX/path helpers over `.iso` and `.cso` (engine behind the Tester's Explore tab)
- **Image splitting** - `XisoSplitter` split/halves/join round-trips, sector alignment, guards, progress/cancel, `split`/`join` CLI verbs
- **Container input** - `XisoImageSource` detection table (ISO/XISO/IMG/CISO/split CISO/CCI/split CCI/split ISO/ZAR), magic sniffing, `CciBlockDevice`, split-ISO seeks, and CCI/split-ISO/ZAR reader-verb parity with plain ISO
- **CCI** - `XisoCci` single/split round-trips, default-path helpers, CLI `--format cci`, `--cci-level`/`--cci-split`, `cci`/`uncci` aliases, and in-place refusal guards; `CisoTests` covers recompressing a CISO through the direct decompressed view
- **ZAR input** - `XisoZarchive.ListZar`/`ExtractZar`/`ConvertZarToIso`, unsafe-name rejection, depth-bound rejection, pre-sized exact extraction, `ComputeZarChecksum` parity with `ComputeImageChecksum`, per-entry `ComputeFileHash`, ZAR↔ISO/CCI validation (tree + checksums), and CLI `-l`/`-x`/`checksum`/`validate`/`convert`
- **STFS packages** - synthetic XBLA/XBLIG packages: file table and directory paths, contiguous and hash-chain block lists, writable primary/secondary tables, level-1 hash boundary, `StfsPackage`/`StfsEntry`/`StfsPackageInfo`, `ComputeStfsChecksum` parity with the equivalent ZAR tree, extraction/skip-existing, unsafe-name and corrupt-header rejection, SHA-1 hash-tree validation (`StfsPackage.Validate`, corrupt data/table/top/header coverage), CLI `-l`/`-t`/`-x`/`--unpack`/`-V`/`checksum`/`--json`, and container/redump/rewrite refusals
- **Pathological TOC traversal** - 4,096-entry left-skewed/right-skewed/alternating/balanced tables: list/tree/extract/rewrite parity, rewrite-checksum equality with the balanced form, audit/sector-range/repair/salvage/ZAR parity on the left spine, left self-cycle refusal, and continue-on-error subtree skipping
- **Batch conversion** - `compress`/`decompress`/`convert`/`split` `--batch`/`--batch-recursive`, naming templates (`{name}`/`{ext}`/`{format}`), `--skip-existing` resume, `--continue-on-error` partial-output cleanup, duplicate-output refusal, and `--jobs` parallelism
- **Redump input guard** - every redump mode (`--video`/`--random`/`--seed`/`--wipe`/`--trim`/`--petrify`/`--update`/`--zar`) refuses `.cci`/`.cso`, split-ISO and `.zar` inputs with a decompress/convert/join message while a plain ISO still runs; `MainViewModelTests` covers the bidirectional ZAR tab, `CliCommandsTests.Convert`/the GUI self-test cover the convert argv, and `NoBugReport`-tagged overwrite refusals are asserted too
- **JSON output** - `--json` schema for `info`/`list`/`tree`/`checksum`/`sector-layout`/`ranges`/`filetime`/`xex-info`/`xbe-info`/`validate`/`version`, stdout/stderr separation, multi-image list documents, CCI input, and rejection for mutating modes
- **Preallocation / sequential I/O** - `IFilesystem.CreateFile(path, expectedLength)` pre-sizing, `LocalFilesystem`/`MemoryFilesystem` overrides, default-member fallback for custom stores, and entry sizes passed through extraction
- **Parallel CISO compression** - `CisoWriter.CompressToCso`/`CompressStream` `jobs` overloads produce byte-identical v1/v2 single and split output, keep per-sector progress ordered, and reject `jobs` outside 1..64; CLI `compress --jobs 4` matches the sequential output byte-for-byte and refuses CCI single-file parallelism
- **Writer concurrency** - `XisoWriter.CreateXiso`/`PackFromDirectory` run independently without the process-wide lock: concurrent packs overlap, a second create completes while the first is blocked in its progress callback, relative source paths resolve against the caller CWD, caller-supplied trees without `HostPath` are resolved against the caller CWD without a chdir, and the CWD never changes
- **Reader CWD removal** - `XisoReader.Extract`/`UnpackImage` route entry paths through a `LocalFilesystem` rooted at the output directory, so extraction never changes the process CWD; `UnpackImageTests` covers both output-path forms, two concurrent extracts into separate destinations, and a mid-run CWD change that must not redirect files
- **Parallel extraction** - `UnpackOptions.MaxDegreeOfParallelism` queues independent files during the tree walk and copies them on a bounded pool with ordered `FileAdded` progress; byte-identical ISO/CISO/split output, `MemoryFilesystem` destinations, `--skip-existing` resume, `--continue-on-error` summaries, fail-fast named errors, cancellation cleanup, and CLI `-x`/`--unpack --jobs` contracts
- **Safe-extract name policy** - `ExtractNamePolicy.Reject`/`Sanitize`/`Skip` through `UnpackOptions.NamePolicy`, collision-safe `~N` renames, unsafe directory subtrees, parallel/filesystem destinations, STFS and ZAR overloads, and CLI `--name-policy` contracts
- **Explicit sector-layout packing** - `XisoLayoutSpec` JSON/text parsing, `FromSectorLayout`, fixed-extent placement, first-fit allocation around reserved gaps, byte-identical JSON round-trips, total-sector preservation, prepended partitions, `build-image`/`-c`/`--pack --layout`, and overlap/out-of-range/system-area/too-small/unknown-path failures
- **In-place repair** - `XisoRepairer` class-C fixes (reserved bits, tag, separator renames + collision refusal), convergence, backup/dry-run semantics, CISO/CCI/split refusals, `--repair` CLI
- **Salvage rebuild** — `XisoSalvager` carry/drop exactness (forged sizes/sectors, real truncation, cycles, depth gate), CISO→plain ISO, re-salvage stability, `--salvage`/`--repair-out` CLI
- **Executable info** — `GetXexInfo`/`GetXbeInfo` path + stream overloads, all-fields parsing, cert bounds, explorer surface, CLI end-to-end
- **Disc identity** — `VolumeInfo.DiscFormat` across RAW/GLOBAL/XGD3/Hybrid/XGD1/unknown layouts
- **Rebuilt sector-0 images** — descriptor at absolute offset 0: probe/verify/volume-info, explorer listing + read stream, filetime, block-device probe, sector-layout used ranges, in-place `CopyIn` add/replace/root-table-move, and `GetXisoRanges`/`CreateZar` round-trips
- **Tool locator** — `ToolLocator` resolution chain (override → app directory → process directory → `PATH`), OS-aware file names, blank/unknown handling, bounded `-v` probe
- **CLI surface** — `-h`/`-v` flags and the branded banner, `--sector-layout`/`--ranges`/`--is-optimized` verbs, update-check asset naming
- **Process runner** — `ProcessRunner` exit codes, stdout/stderr capture, per-line callbacks, missing executables, argument validation, cancellation, timeouts
- **Update checker extras** — version parsing, update comparison, RID mapping, cache round-trips/escaping, asset URL lookup
- **Output guards / prompt** — misplaced-flag detection, input==output refusals (rewrite/rebuild/image), overwrite-prompt flag combinations and responses
- **Bug reports / telemetry** — report entry points under test hosts, exception-block budgets, environment block lines, stats launch-ping opt-outs
- **Internal helpers** (`InternalsVisibleTo`) — SHA3-256 NIST vectors/chunking/disposal, Latin-1 codec full byte range + overflow-safe range validation, bounded sub-stream window/ownership semantics
- **Hardening regressions** — path-escape names, table-size bounds, all-zero sentinel, zero-size directory listing, split part naming (dotted bases), partial-output cleanup and pre-existing-output preservation (`CreateZar`/`CompressToCso`), offset-image tag repair, same-CWD input==output guard
- **CLI flag contracts** — `--silent` order independence (incl. `-v`/`--help`), `--file-time`/`--preserve-attrs`/`--jobs`/`--policy` mode rejection (incl. `--jobs` with `--compress`/multi-mode), `--ciso-split` first-part guard, Wipe/Trim flags-before-positionals
- **Follow-up regressions** — `-c .` default output next to the source, colon names through list/tree/rewrite (extract refuses on Windows), audit root-size/extraction parity, header-only sector-layout reservations, `build-image` cancellation cleanup, child-offset `0xFFFF` sentinel guard
- **Sequential execution** — assembly-wide `[CollectionBehavior(DisableTestParallelization = true)]` because many tests set the process CWD themselves (creation and extraction no longer do)
- **GUI command builders** (net10.0) — `CliCommands` argv for every verb, flags-before-positionals invariant, `CliLocator` version metadata fallbacks
- **GUI services** (net10.0) — screenshot file naming/folder resolution/collision suffixes and update-check version comparison/release-URL fallback

## Running Tests

```
dotnet test
```

## License

MIT
