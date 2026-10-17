# What's New in 1.4.3 (since 1.4.2)

Split CISO part sets now honor the caller-chosen `FileShare`. `CisoSplitFile.OpenParts`
was hardcoded to `FileShare.Read`, so `FileShare.ReadWrite` reached plain ISOs and
single `.cso` containers but not the individual `*.1.cso` part files: a split set held
open for writing by a sync client, an in-progress copy, or antivirus still failed with
a sharing violation on Windows even when the caller asked for writer coexistence. The
share mode now threads to every part, matching the single-container and plain-ISO
paths. Defaults are unchanged (`FileShare.Read`).

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1758 passed / 1 opt-in skip (1759) on net8.0/net9.0** and **1819 passed / 1 opt-in
skip (1820) on net10.0**.

## Highlights

- **Writer coexistence for split CISO sets**: reading or browsing a `*.1.cso` part set
  while another process keeps a part open for writing now works when the caller passes
  `FileShare.ReadWrite` - `XisoReader.OpenImageStream(path, FileShare.ReadWrite)` and
  `XisoExplorerOptions { Share = FileShare.ReadWrite }` cover plain ISOs, single `.cso`
  containers, and split part sets alike.
- **Defaults stay conservative**: with the default `FileShare.Read` - or when the
  writer shares no write access - the previous error behavior is unchanged; the
  relaxation only happens when the caller asks for it.

## Library

- `CisoSplitFile.OpenParts(firstPartPath, share = FileShare.Read)` threads the share
  mode to every part file; `CisoBlockDevice` passes the caller's `FileShare` through
  on both the single-container and the split-set paths.
- `XisoExplorerOptions.Share` documentation now names all three image shapes (plain
  ISO, single `.cso`, split `*.1.cso` part set).

## Tests

- `CisoOpenParts_HonorsCallerShareMode` (XisoSplitTests) pins the share plumbing:
  read/write sharing coexists with a writer holding the first part, and the default
  stays read-shared (the exclusion half is asserted on Windows, where share modes are
  enforced).
- `SplitCso_ExplorerWithReadWriteShare_CoexistsWithWriterHoldingPart` (CisoTests)
  covers the end-to-end `XisoExplorer` keep-open flow over a real split set with a
  writer holding the first part.
- Suite: **net8.0/net9.0 - 1759 tests (1758 passed, 1 opt-in skip)**;
  **net10.0 - 1820 tests (1819 passed, 1 opt-in skip, adds the GUI tests)**.
