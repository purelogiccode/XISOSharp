# What's New in 1.4.2 (since 1.4.1)

Deep-review hardening and GUI experience release. A four-pass code review of the
whole repository produced 80 tracked findings (path escapes from crafted image
names, in-place patch collisions with the tag/descriptor sectors, CWD leaks,
partial-output artifacts, CLI contract mismatches, vacuous tests, doc drift);
this release resolves them, adds the follow-up regressions found while
re-reviewing the fixes, and closes a final pass over those commits (API
compatibility, `-c .` default output, host-safe rewrite/list, audit/extraction
parity, remap cleanup). The desktop app gains About/Donate/Exit actions, F8
screenshots, and a shared startup update check.

**Namespace note**: data models, records, and enums now live in
`XISOSharp.Models` — including the previously root-namespace `UnpackOptions`,
`ProcessRunResult`, `ExplorerNode`, and `XisoExplorerOptions`; add
`using XISOSharp.Models;` when upgrading from 1.4.1. Targets remain `net8.0` /
`net9.0` / `net10.0`. Full suite green on all three: **1817 passed / 1 opt-in
skip (1818) on net10.0** and **1756 passed / 1 opt-in skip (1757) on
net8.0/net9.0**. The suite now disables xUnit parallelization assembly-wide
because create/extract mutate the process-wide current directory.

## Highlights

- **GUI**: Donate / About / Exit header actions and a proper About dialog, F8
  window screenshots, a startup "update available" prompt, tooltips throughout,
  corrected drag-and-drop routing (including `.zar` in multi-item drops), and
  Wipe/Trim output paths that actually work.
- **One update checker**: the CLI and GUI now share `UpdateCore` (version
  parsing/comparison, RID mapping, asset naming, 24-hour disk cache), so the GUI
  no longer probes GitHub on every start and the two front-ends cannot drift.
- **Safety-first patching and writing**: crafted image names cannot escape the
  destination, in-place patches never allocate over the optimized tag or the
  ECMA-119 descriptors (even for header-only volumes), the working directory is
  always restored, and failed or cancelled runs no longer leave truncated
  `.iso` / `.cso` / `.zar` artifacts — including the `build-image` remap path.
- **Backup discipline**: `.old` backups survive failed rewrites, an existing
  backup is never overwritten, `-D` only unlinks after a fully successful (and
  validated) rewrite, and a pre-existing `.zar`/split part is never deleted by a
  failed call.
- **Compatibility parity**: listing and rewriting accept every structurally
  valid name on every host (a colon name is only refused at extract on Windows),
  and audit now rejects an oversized root table exactly like extraction does.
- **Telemetry hardening**: the shared API key is no longer a plaintext literal
  (double-Base64 `ApiKeyStore`), expected operational warnings stay in the local
  log (`NoBugReport`), and the bug-report dedupe map is bounded.
- **Tester**: the list comparison now parses what the tools actually print and
  decodes extract-xiso's Latin-1 output, so non-ASCII names compare correctly.

## Library

### Path and walker safety

- Crafted image names that could escape the destination are rejected or
  sanitized everywhere: separators, the Windows drive-relative colon
  (`C:evil`), and any trailing dot/space that Win32 strips during path
  normalization (`foo.` no longer collides with `foo`).
- Host safety applies where host paths are built: extraction refuses such names
  on Windows, while listing, tree, rewrite, and ZAR packing follow the reference
  tool and accept them on every host (a colon stays a valid name on Unix).
- Every directory walk bounds child offsets by the recorded table size (not
  just image length), so a corrupt offset into another file's data can no
  longer audit as valid while extraction rejects it.
- The xdvdfs all-zero empty-table sentinel is handled by `XisoRanges` and the
  auditor like the `0xFFFF` sentinel; `.`/`..` structural records are skipped
  consistently (children still walked) across `TraverseXiso`, `AuditWalk`,
  `SalvageWalk`, `CollectWalk`, `GetValidSectors`, and `CollectFileEntries`.
- A truncated directory-entry name now fails the walk instead of silently
  dropping the entry and its right-sibling chain from the sector map.
- Zero-size directory entries (a known real-world xdvdfs quirk) list as empty
  instead of failing the new table bounds.

### In-place patching (`--copy-in`)

- The sector layout reserves the optimized tag (partition sector 15) and the
  ISO9660 descriptor pair (sectors 16-17) plus the layout-tool signature sector
  when present — including header-only (no files) volumes — so an allocation
  can never overwrite them.
- Table and extent sector counts use 64-bit math: a ~4 GiB file is no longer
  dropped from the allocation map by a 32-bit wrap.
- The root directory size is recorded even when the table stays in place, so
  appended entries stay reachable.
- Directory tables fail instead of truncating when a child offset would encode
  the `0xFFFF` empty-child sentinel.

### Writing and containers

- `CreateXiso` restores the working directory on every path (success, error,
  cancellation); a relative `-d` resolves against the caller's CWD, not the
  source directory the write phase changes into.
- A current-directory source (`-c .` / `-c ..`) packs the source and defaults
  the output to the source's parent (mirroring `-c <leaf>` from that parent);
  the same-leaf collision guard no longer aborts the default case.
- Failed/cancelled creates delete the partial output — including the
  `build-image` remap writer — and a failed compress deletes the partial `.cso`
  and closes split-part handles before deleting (`FileShare.None` blocked the
  delete on Windows).
- `CreateZar` only deletes an output it created: a pre-existing `.zar` survives
  a parse failure or a cancellation before packing.
- CISO reading validates version/alignment/size and bounds the index table
  against the file; reads at/after the logical end return no data instead of
  the last block's zero padding; the final index entry rejects 31-bit overflow.
- ZAR packing rejects a zero-size root table (nonzero sector), treats a
  zero-size nested directory as empty, and fails on a truncated name.
- Split CSO/ISO part discovery appends `.{n}` to the base name, so dotted
  names (`My.Game.1.cso`) reopen correctly.
- `Latin1Encoding` range validation is overflow-safe and follows the
  `Encoding` contract (`ArgumentOutOfRangeException` for indexes).
- `XisoExplorer.Dispose` serializes with keep-open reads; `XisoPaths` resolves
  both sides of an input==output check against one CWD snapshot (and fixes
  containment under filesystem roots).

### Repair and audit

- The repairer writes a missing optimized tag at the disc offset for
  prepended/Redump images instead of corrupting the video partition.
- Path and block-device audits agree on the trailing header magic and probe the
  tag at the detected disc offset, including the empty-image path.
- Audit and extraction agree on the root table: a root size past the end of the
  image is reported invalid instead of auditing clean.

## CLI

- `--silent` is order-independent (`--silent --checksum` works like
  `--checksum --silent`, and with `-v`/`--help` in any position) and is still
  rejected without `--checksum`.
- Mode-specific flags are rejected loudly instead of silently ignored:
  `--file-time` outside create, `--preserve-attrs` outside rewrite,
  `--jobs`/`--policy` outside `--zar`, and `--jobs` with `--compress` or a
  multi-mode redump run (where the parallel path can never take effect).
- `--wipe`/`--trim` honor `-o` (the output is no longer processed as a second
  input); `--ciso-split` refuses a source named like its own first part;
  `--is-optimized` honors `--skip-sectors`.
- Rewrite: `-D` only deletes the `.old` backup after a fully successful
  (validated) rewrite, and one file's failure no longer suppresses a later
  file's success output or keeps its backup.
- Help text corrected: `--help` is real help, `--validate-strict` is parity-only
  (mismatches exit 2 either way), the `--skip-sectors` wording matches what
  create mode accepts, and the redump modes show `-o <output>` instead of a
  positional second file (which is parsed as another input).
- stdout/stderr are always UTF-8 rather than the host console code page, so
  Latin-1 filenames (`café.txt`) render and pipe identically on every platform
  and match xdvdfs's UTF-8 output.

## GUI

- Header actions: **Donate** (PureLogicCode page), **About** (version,
  description, credits, links), **Exit**; new About and message-box windows.
- **F8** saves a PNG screenshot of the active window to a `Screenshot` folder
  beside the app (falling back to `%LocalAppData%/XISOSharp/Screenshot`).
- Startup update prompt (shared 24-hour cache with the CLI; disable with
  `XISO_NO_UPDATE_CHECK=1`), subscribing to window events before probing so a
  fast show can never leave the dialog pending.
- Drag-and-drop routing: only folders containing `*.iso` go to Batch;
  `.cso`/`.zar`/`.img`-only folders go to Create; a dropped `.zar` routes to the
  Rebuild tab in single- and multi-item drops. Wipe/Trim pass the chosen output
  through `-o` correctly.
- `CliStatus` is always updated on the UI thread; the CLI-run cancel/timeout/
  start failures reach the log panel; GUI settings (CLI path, overwrite
  default) persist to `%AppData%/XISOSharp/gui-settings.json` again.
- Screenshot diagnostics are treated as expected conditions (no bug report) and
  the fallback folder is tried for security exceptions too.
- Tooltips across the main window; `--probe-cli` and `--self-test` headless
  helpers kept green.

## Logging, bug reports, and telemetry

- CLI, GUI, and Tester share one Serilog pipeline; expected operational
  warnings (usage/validation refusals, missing-file probes, non-zero CLI exits,
  unreadable dropped paths, screenshot environment issues) are tagged
  `NoBugReport` and stay local, while genuine Warning+ events still report.
- The shared ApplicationStats / bug-report API key is stored double-Base64
  encoded (`ApiKeyStore`) and decoded once at startup — obfuscation, not
  encryption, but no plaintext literal in sources or bundles.
- The bug-report dedupe dictionary prunes stale keys in long-running hosts;
  `TestDataWriter` releases its mutex only when it owns it; `ProcessRunner`
  falls back to a stable working directory when the inherited CWD was deleted.

## Tester

- `ParseListOutput` parses the real `\name (N bytes)` format emitted by both
  tools; extract-xiso output is decoded as Latin-1 so `café.txt` and friends
  compare equal. Two empty parses are a failure, not a vacuous pass.
- Removed dead helpers/wrappers (`HashUtil.ComputeMd5`/`IsAllZero`,
  `XisoFileEntry.IsSmall`, unused sync wrappers including `RunQuietAsync`).

## Docs and build

- Fixed drift: test counts, `--help` behavior, Release-build packing, battle
  harness default, coverage artifact OS, Tester CI claim, coverage figures,
  missing PowerShell scripts, stale `ConversionPlan.md` links, and the
  `docs/troubleshooting.md` verification-script section.
- `publish-cli.ps1` / `publish-gui.ps1` no longer delete the protected
  `publish*/<rid>` trees: they stage into `%TEMP%` and merge the same-named
  files over the artifact store (AGENTS.md hard rule). `publish-cli.ps1 -Zip`
  requires an exact release tag or `-Version <x.y.z>` so a pre-release run can
  never overwrite a shipped `release_*.zip`.
- Namespace docs match the shipped surface again (`UnpackOptions`,
  `ProcessRunResult`, `ExplorerNode`, `XisoExplorerOptions` in `XISOSharp`;
  typed models/enums in `XISOSharp.Models`; interfaces in
  `XISOSharp.Interfaces`), and the library samples include the needed usings.
- `docs/` is served both as a Docsify site on GitHub Pages (`_sidebar.md`
  left menu) and mirrored to the GitHub Wiki by `wiki.yml` +
  `.github/sync-wiki.ps1`, which generates `Home.md` and `_Sidebar.md` from the
  same sources.

## Tests

- New coverage includes: review regressions (path escapes, table bounds,
  sentinel handling, split naming, partial-output cleanup, pre-existing-output
  preservation, offset-image repair, CLI flag contracts), the follow-up fixes
  (`-c .` default output, colon names through list/tree/rewrite, audit root-size
  parity, `build-image` cancellation cleanup, `--jobs` rejection with
  `--compress`), GUI command builders and services, shared process
  runner/update checker, internals (SHA3, Latin-1, bounded sub-streams),
  zero-size directories, and Latin-1 process output decoding.
- The suite runs fully sequentially (assembly-wide
  `[CollectionBehavior(DisableTestParallelization = true)]`) so the shared
  process CWD is never mutated concurrently.
- Suite: **net8.0/net9.0 — 1757 tests (1756 passed, 1 opt-in skip)**;
  **net10.0 — 1818 tests (1817 passed, 1 opt-in skip, adds the GUI tests)**.
