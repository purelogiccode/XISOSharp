# Changelog

All notable changes to the XISOSharp library will be documented in this file.

## [Unreleased]

- `VerifyXiso`/`DecodeXiso` accept `skipSectors` to read Redump-style images whose game
  partition does not start at file offset 0 (extract-xiso issue #33)
- `CreateXiso` accepts `prependSectors` to write images with room for a video partition;
  symmetric with `skipSectors` for round-trip reconstruction
- CLI: new `--skip-sectors N` and `--prepend-sectors N` flags
- `CreateXiso` accepts `excludePatterns` (glob patterns); new `GlobMatcher` utility;
  CLI: repeatable `-X <glob>` flag, `-s` implicitly excludes `$SystemUpdate` on create
- Structured write progress: `IProgress<ProgressInfo>` channel with
  `ProgressInfoType` events (`FileCount`, `DirCount`, `DirAdded`, `FileAdded`,
  `FinishedPacking`) on `CreateXiso`/`CreateXisoAsync` and rewrite APIs
- `--ls <file> [path]` CLI flag and `XisoReader.ListDirectoryFlat` — non-recursive
  directory listing (default root, optional subdirectory path)
- Media-enable patch verification: byte-asserting unit tests (incl. read-buffer
  boundary crossing) and `Scripts/Verify-MediaPatch.ps1` cross-check against the
  reference C tool on real game ISOs
- Xbox 360 XEX support (issue #28): `XisoReader.GetXexInfo`/`XexInfo` XEX2 header
  parsing and the `--xex-info` CLI flag; `.xex` files verified never media-patched;
  overflow-safe bounds checks for malformed XEX headers
- Batch processing (issue #114): `--batch <dir>` processes all `.iso` files in a
  directory (with `--batch-recursive` for subdirectories) in extract/list/tree/
  rewrite/audit modes
- `unpack` command: `XisoReader.UnpackImage` (auto-detects the optimized layout,
  ISO-named default output directory) and the `--unpack <file> [dest]` CLI flag
- `pack` command: `XisoWriter.PackFromDirectory`/`PackFromDirectoryAsync` and the
  `--pack <input> [name]` CLI flag (directory → ISO, ISO → in-place repack)

## [2.7.1] - 2025-07-21

- Initial NuGet release
- C# port of extract-xiso v2.7.1
- Support for GLOBAL, XGD3, and XGD1 disc formats
- Multi-targeting: net8.0, net9.0, net10.0
- Async APIs with CancellationToken support
- Pluggable logging via Logger class
- Strong-name signed assembly
- Source Link and snupkg debugging support
