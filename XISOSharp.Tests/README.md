# XISOSharp.Tests

[![CI](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml)
![Tests](https://img.shields.io/badge/tests-1774-brightgreen)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![xUnit](https://img.shields.io/badge/xUnit-2.9.3-blueviolet)](https://xunit.net/)
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
- **Reader gap-closers** — multi-sector tables, disc-layout probes, block-device errors, sentinel shapes (line coverage: `XisoReader` 95.9%, `XisoWriter` 86.6%, `AvlTree` 100%)
- **Legacy interop** — images created by the reference extract-xiso (legacy layout) round-trip through `llCompat` extract/list/rewrite
- **File copier** — `XisoFileCopier.CopyExact` size matrix, truncation counts, short-read stitching, pooled buffers, mid-copy cancel, per-chunk `FileProgress` on copy-out/unpack
- **Filesystem destinations** — `IFilesystem`/`LocalFilesystem`/`MemoryFilesystem` semantics, generic `UnpackImage` byte-parity with the legacy disk unpack, resume/continue-on-error/cancel/truncation through custom destinations
- **Image explorer** — `XisoExplorer` load/navigate/copy-out/hash/XEX/path helpers over `.iso` and `.cso` (engine behind the Tester's Explore tab)
- **Image splitting** — `XisoSplitter` split/halves/join round-trips, sector alignment, guards, progress/cancel, `split`/`join` CLI verbs
- **In-place repair** — `XisoRepairer` class-C fixes (reserved bits, tag, separator renames + collision refusal), convergence, backup/dry-run semantics, CISO/split refusals, `--repair` CLI
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
- **Internal helpers** (`InternalsVisibleTo`) — SHA3-256 NIST vectors/chunking/disposal, Latin-1 codec full byte range, bounded sub-stream window/ownership semantics
- **GUI command builders** (net10.0) — `CliCommands` argv for every verb, flags-before-positionals invariant, `CliLocator` version metadata fallbacks
- **GUI services** (net10.0) — screenshot file naming/folder resolution/collision suffixes and update-check version comparison/release-URL fallback

## Running Tests

```
dotnet test
```

## License

MIT
