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
- **XisoReader.ComputeFileHash** — MD5 and SHA-256 per-file hashing
- **XisoReader.ComputeDirectoryHashes** — batch hashing of all files in a directory
- **XisoReader.AuditXiso** — deep integrity audit (header, tree, sectors, cycles)

## Running Tests

```
dotnet test
```

## License

MIT
