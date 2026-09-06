# XISOSharp.Tests

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
- **Legacy interop** — images created by reference extract-xiso 2.7.1 round-trip through `llCompat` extract/list/rewrite
- **File copier** — `XisoFileCopier.CopyExact` size matrix, truncation counts, short-read stitching, pooled buffers, mid-copy cancel, per-chunk `FileProgress` on copy-out/unpack
- **Filesystem destinations** — `IFilesystem`/`LocalFilesystem`/`MemoryFilesystem` semantics, generic `UnpackImage` byte-parity with the legacy disk unpack, resume/continue-on-error/cancel/truncation through custom destinations
- **Image explorer** — `XisoExplorer` load/navigate/copy-out/hash/XEX/path helpers over `.iso` and `.cso` (engine behind the Tester's Explore tab)
- **Image splitting** — `XisoSplitter` split/halves/join round-trips, sector alignment, guards, progress/cancel, `split`/`join` CLI verbs
- **In-place repair** — `XisoRepairer` class-C fixes (reserved bits, tag, separator renames + collision refusal), convergence, backup/dry-run semantics, CISO/split refusals, `--repair` CLI
- **Salvage rebuild** — `XisoSalvager` carry/drop exactness (forged sizes/sectors, real truncation, cycles, depth gate), CISO→plain ISO, re-salvage stability, `--salvage`/`--repair-out` CLI
- **Executable info** — `GetXexInfo`/`GetXbeInfo` path + stream overloads, all-fields parsing, cert bounds, explorer surface, CLI end-to-end
- **Disc identity** — `VolumeInfo.DiscFormat` across RAW/GLOBAL/XGD3/Hybrid/XGD1/unknown layouts

## Running Tests

```
dotnet test
```

## License

MIT
