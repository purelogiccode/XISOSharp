# XISOSharp — Remaining Work Plan

- **Updated:** 2026-11-07
- **Latest release:** XISOSharp **1.8.0** (2026-11-07)
- **Working tree:** 1.8.0 + DST-03/04/05 distribution track + XRP-01/02/03 cross-repo upgrades; DST-01/02 signing is implemented behind repository secrets; the 2.0.0 and 2.1.0 tracks are planned but **not started**
- **Next release:** **2.0.0** — remove the `StfsSharp` compatibility facades, **keeping `net8.0`/`net9.0`/`net10.0`** (owner decision 2026-11-07)
- **Next feature release:** **2.1.0** — beyond-reference features (`BRF-01`…`BRF-15`)
- **Packages:** XISOSharp `1.8.0`, StfsSharp `1.0.0`, CCISharp `1.1.0`, ZArchiveSharp `1.4.0`
- **Scope of this file:** only work that is still open. Implemented features are listed once in [§9](#9-implemented-baseline-do-not-re-implement) so they are not re-implemented.

> **How to use this file:** change `- [ ]` to `- [x]` when an item meets its **Done when** line. Add the version/date to the progress log in [§14](#14-progress-log).

> **TL;DR**
> 1. **Ship 1.6.0** — ✅ done (tag `350f629`, NuGet and GitHub release verified).
> 2. **Ship 1.7.0** — ✅ done: `StfsSharp 1.0.0` extracted and published; XISOSharp 1.7.0 tagged/released with six bundles.
> 3. **Ship CCISharp 1.1** — ✅ done: LZ4-HC, parallel CCI compression, multi-digit parts; `CCISharp 1.1.0` published and integrated.
> 4. **Harden and ship 1.8.0** — ✅ done: LIB-01…08, DOC-01…03 and GUI-01…05 implemented. P3 distribution/cross-repo items remain deliberately deferred.
> 5. **P3 distribution track** — DST-03/04/05 implemented (winget/Scoop/Homebrew manifests, shell completions + man page, container image). DST-01/02 are implemented behind repository signing secrets and stay open until a signed package/release is verified. XRP-01…03 are done; XRP-04 is the remaining optional cross-repo item.
> 6. **2.0.0 facade removal** — planned, not started. Removes the `StfsSharp` compatibility facades; the `net8.0`/`net9.0`/`net10.0` matrix stays (owner decision 2026-11-07).
> 7. **2.1.0 beyond-reference track** — planned, not started. Adds disc-layout inspection, skeleton overlay, embedded 360 title metadata, XEX thumbnails, per-file hash sidecars, parallel collection verbs, image compare, ZAR tuning, loose-file metadata, optional Redump DAT verification, plus the improvements found in the extract-xiso/xdvdfs issue scan (`BRF-01`…`BRF-15`, see [§8C](#8c-p2--upstream-issuepr-scan-2026-11-07)).

---

## 0. Snapshot

| Field | Value |
|---|---|
| Published library | **XISOSharp 1.8.0** (2026-11-07) |
| Published readers | **StfsSharp 1.0.0** (2026-11-07), **CCISharp 1.1.0** (2026-11-07, NuGet) |
| Current tree | **1.8.0 + DST-03/04/05 + XRP-01/02/03**; signing scaffolding for DST-01/02; 2.0.0/2.1.0 planned (not started) |
| Next release gate | **2.0.0** — `StfsSharp` facade removal; the `net8.0`/`net9.0`/`net10.0` matrix is retained |
| Next feature release | **2.1.0** — beyond-reference features (`BRF-01`…`BRF-15`) |
| Compression track | **CCISharp 1.1** — ✅ HC encoder, parallel writer, multi-digit parts |
| Tests (last recorded) | XISOSharp 2144 passed / 1 skipped on `net8.0`/`net9.0`; 2217 passed / 1 skipped on `net10.0`; CCISharp 78/78 on all TFMs; StfsSharp 37/37 on all TFMs; SimpleXisoDrive 436 (402+34); XISOStudio 1561; ZArchiveSharp 4293 × 3 TFMs + 43 CLI (after the fuzzer-found NUL-name hardening) |
| CI gates | 3-OS build/test, `dotnet format`, InspectCode, AOT smoke, fuzz, battle-interop, packaging (completions/man/winget/Scoop/Homebrew), Docker smoke, coverage floors |
| Reference set | 8 local copies under `References/` — see [§10](#10-reference-map) |

**Checkbox key**

| Mark | Meaning |
|---|---|
| `- [ ]` | Open — not implemented or not verified |
| `- [x]` | Done — record the version/date in the progress log |

**Priority key**

| Priority | Meaning |
|---|---|
| **P0** | Release blocker |
| **P1** | Next release / main feature track |
| **P2** | Quality, docs and user-facing polish |
| **P3** | Optional or cross-repo follow-up |

---

## 1. Open work at a glance

| Priority | Workstream | Target | Open items |
|---|---|---|---:|
| **P0** | Ship 1.6.0 | ✅ released 2026-11-06 | 0 |
| **P1** | Extract `StfsSharp` | ✅ released 2026-11-07 | 0 |
| **P1** | CCISharp 1.1 | ✅ released 2026-11-07 | 0 |
| **P2** | Library hardening | 1.8.0 | 0 |
| **P2** | Docs, fixtures and GUI | 1.8.0 | 0 |
| **P1** | 2.0.0 facade removal | 2.0.0 | 2 (`FAC-01`/`FAC-02`) |
| **P1** | Beyond-reference features | 2.1.0 | 15 (`BRF-01`…`BRF-15`) |
| **P3** | Distribution and supply chain | later | 2 (DST-01/02 await signing certs) |
| **P3** | Cross-repo consumers | after 1.8.0 | 1 (XRP-04 optional; XRP-01/02/03 done) |
| | **Total** | | **20 open** (17 planned; DST-01/02 await certificates; XRP-04 optional) + 42 done |

**Recommended order**

| Order | Work | Why |
|---:|---|---|
| 1 | `STF-01` … `STF-08` | ✅ done; `StfsSharp 1.0.0` and XISOSharp 1.7.0 are published. |
| 2 | `CCI-01` … `CCI-06` | ✅ done; `CCISharp 1.1.0` is published and integrated. |
| 3 | `LIB-01` … `LIB-08` | ✅ done in 1.8.0. |
| 4 | `DOC-*`, `GUI-*` | ✅ done in 1.8.0. |
| 5 | `FAC-01`, `FAC-02` | 2.0.0 breaking cleanup: remove the `StfsSharp` facades and keep all three TFMs. |
| 6 | `BRF-01` … `BRF-15` | 2.1.0 beyond-reference track (disc info, skeleton overlay, embedded 360 metadata, XEX images, file hashes, parallel collection verbs, compare, ZAR tuning, loose files, optional DAT, plus upstream issue-scan items). |
| 7 | `DST-03` … `DST-05` | ✅ implemented: manifests, completions/man page and container image. |
| 8 | `DST-01`, `DST-02` | Implemented behind repository signing secrets; close when a signed package/release is verified. |
| 9 | `XRP-01` … `XRP-03` | ✅ done (SimpleXisoDrive, XISOStudio, ZArchiveSharp). |
| 10 | `XRP-04` | Optional; another consumer repository. |

---

## 2. P0 — Ship 1.6.0

> The 1.6.0 library/CLI scope is implemented in the working tree. This section is release mechanics and documentation only; no new format work.

- [x] **REL-01 · Finalize 1.6.0 documentation**
  - Move `## Unreleased` to `## 1.6.0` in `docs/release-notes.md`, add the release date, refresh `WhatsNew.md`, root/library/CLI/Tests/Tester READMEs, the feature matrix and `docs/known-limitations.md`.
  - **Done when:** no `Unreleased` entry remains for 1.6.0 and every doc matches the tag.
  - **Done 2026-11-06:** release notes + `WhatsNew.md` dated 1.6.0, test counts refreshed, library README container matrix updated, known-limitations/StfsSharp note added, support-policy/building examples refreshed.

- [x] **REL-02 · Run the 1.6.0 release verification**
  - Full Release build; the test suite on `net8.0`/`net9.0`/`net10.0`; `dotnet format whitespace --verify-no-changes`; InspectCode 0 warnings; `dotnet pack`; AOT smoke; fuzz smoke; battle-interop.
  - **Done when:** every gate passes on the exact release commit.
  - **Done 2026-11-06:** Release build 0 warnings/0 errors; tests 2098/1 on net8/net9 and 2162/1 on net10; format clean; InspectCode 0 warnings; pack succeeded (pre-release version until the tag); AOT smoke passed; fuzz 4,113 iterations/0 failures; synthetic battle 8 passed/0 failed/8 skipped.

- [x] **REL-03 · Tag and publish 1.6.0**
  - Tag `1.6.0`, push, approve the `nuget` environment, verify `XISOSharp.1.6.0.nupkg` + `.snupkg` and the GitHub release with six RID bundles.
  - **Done when:** `api.nuget.org/v3-flatcontainer/xisosharp/index.json` lists 1.6.0 and `gh release view 1.6.0 --json assets` lists the six `release_1.6.0_<rid>.zip` assets (SBOM/provenance assets may accompany them).
  - **Done 2026-11-06:** tag `1.6.0` at `350f629`; NuGet `1.6.0` indexed; GitHub release has six RID zips + SPDX asset.

- [x] **REL-04 · Post-release smoke test**
  - Extract `win-x64`, `linux-x64` and `MacOsX-arm64` bundles; run `XISOSharp -v`, `XISOSharp.Gui --probe-cli` and `XISOSharp.Gui --self-test <cli>`.
  - **Done when:** all three report 1.6.0 and `SELF-TEST: all passed`.
  - **Done 2026-11-06:** release workflow smoke-tested `win-x64`, `linux-x64` and `MacOsX-arm64`; downloaded `win-x64` reports `XISOSharp v1.6.0`, `--probe-cli` reports 1.6.0, and `--self-test` prints `SELF-TEST: all passed`.

---

## 3. P1 — 1.7.0: extract `StfsSharp`

> Read-only GOD/SVOD and XBLA/XBLIG STFS support already works in-repo. This milestone moves those readers into a standalone MIT package so XISOSharp, companion apps and future consumers share one implementation. It is a packaging/API task, not new format support.

- [x] **STF-01 · Create the `StfsSharp` repository and package skeleton**
  - Model on CCISharp/ZArchiveSharp: `StfsSharp/`, `StfsSharp.Tests/`, `StfsSharp.Benchmarks/`, optional `StfsSharp.Cli/`, `docs/`, `Directory.Build.props`, `global.json`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, 3-OS CI. Target the supported XISOSharp matrix (currently `net8.0`/`net9.0`/`net10.0`; see `LIB-07`), MinVer, `IsTrimmable`/`IsAotCompatible`, zero runtime dependencies, nupkg + snupkg.
  - **Done when:** the package builds, packs and CI is green on Windows/Linux/macOS.
  - **Done 2026-11-07:** repo created at `C:\...\CSharp_STFSSharp`, 4 projects (library/tests/benchmarks/fuzz), `net8.0`/`net9.0`/`net10.0`, MinVer `v1.0.0`, SourceLink, trimmable/AOT, pack + snupkg; GitHub CI green on Windows/Ubuntu/macOS (fuzz-smoke included) and `StfsSharp 1.0.0` published to NuGet.

- [x] **STF-02 · Move XContent/title metadata into `StfsSharp`**
  - Move `XContentHeaderReader`, the `GodHeader` title fields, `StfsHeader`, `GodTitleInfo`, `StfsTitleInfo` and the package-kind enum into the `StfsSharp` public API.
  - **Done when:** XISOSharp `title-info` text/JSON output is unchanged and the metadata tests run against `StfsSharp`.
  - **Done 2026-11-07:** `GodHeader`/`StfsHeader`/`GodTitleInfo`/`StfsTitleInfo`/`StfsPackageKind` and the internal `XContentHeaderReader` moved; `GodHeaderTests` and `StfsHeaderTests` now live in `StfsSharp.Tests`; XISOSharp keeps facade metadata tests and CLI output is unchanged.

- [x] **STF-03 · Move the read-only GOD/SVOD sector reader**
  - Move `GodHeader.TryReadVolume`, `GodVolumeInfo`, the SVOD fragment roll-over and hash-table interleave behind `StfsFile`/`StfsStream` in `StfsSharp`.
  - **Done when:** a synthetic multi-`DataNNNN` GOD set reads byte-identically through `StfsSharp` and matches the current `GodBlockDevice` output.
  - **Done 2026-11-07:** `GodVolume` + seekable `GodVolumeStream` replace the in-repo mapping; `StfsFile` is the container factory. Synthetic two-fragment, enhanced-GDF, security-sector, zero-prefix and unaligned-read tests pass; real GOD mapping is covered by the optional corpus test.

- [x] **STF-04 · Move the STFS package reader**
  - Move `StfsPackage`, `StfsEntry`, `StfsPackageInfo`, contiguous/hash-chain block lists, `ReadAllBytes`, `CopyTo` and `ExtractTo`.
  - Keep the current read-only scope: XBLA/XBLIG/Community Games packages; no saves, profiles or DLC packages.
  - **Done when:** listing/extraction matches the in-repo implementation on the synthetic fixture matrix.
  - **Done 2026-11-07:** full `StfsPackage` port with `ReadFileChunks`, `StfsProgress`, warning/name-policy channels; read-only, writable-format, XBLIG/Community, non-contiguous and hash-boundary tests pass; real XBLA/XBLIG packages open, list and extract.

- [x] **STF-05 · Move STFS hash-tree validation**
  - Move `StfsPackage.Validate`, `StfsValidationReport` and `StfsValidationIssue`: per-block SHA-1, level-0/1/2 tables, top hash and header hash.
  - **Done when:** valid/corrupt fixture results are identical to today's `StfsPackage.Validate`.
  - **Done 2026-11-07:** validation ported unchanged; corrupt data block, hash table, top hash, header hash, broken/cyclic chain and implausible block-count tests pass; real XBLA package reports PASS (394 files, 11,707 blocks).

- [x] **STF-06 · Integrate `StfsSharp` without breaking XISOSharp's public API**
  - Replace the in-repo readers with `StfsSharp`; keep `XISOSharp.GodHeader`, `XISOSharp.BlockDevice.GodBlockDevice`, `XISOSharp.StfsPackage` and `XisoChecksum.ComputeStfsChecksum` as thin facades/adapters. Use type forwarding or wrappers for public types; remove them only in 2.0.0. Refresh `PublicApi.approved.txt`.
  - **Done when:** XISOSharp consumes the `StfsSharp` NuGet package, every read verb still passes, and `dotnet pack` package validation stays green.
  - **Done 2026-11-07:** `XISOSharp.csproj` references `StfsSharp 1.0.0`; `GodHeader`/`StfsHeader`/`StfsPackage`/`GodBlockDevice` are mapping facades; `XContentHeaderReader` removed; `PublicApi.approved.txt` unchanged (no public-surface break); pack passes with the 1.4.4 baseline; full test suite and CLI/real-package smoke pass.

- [x] **STF-07 · Move tests, fixtures and fuzz coverage**
  - Move `GodHeaderTests`, `GodBlockDeviceTests`, `StfsHeaderTests` and `StfsPackageTests` into `StfsSharp`; keep the XISOSharp integration tests. Move/extend `SyntheticStfsPackage` and add a `StfsSharp` fuzz target for `Open`, `Validate`, `ReadAllBytes` and `ExtractTo`.
  - **Done when:** both repositories have green suites and the fuzzer covers the moved readers.
  - **Done 2026-11-07:** fixture and reader tests moved (37 tests on each TFM); a deterministic mutation fuzzer covers STFS and GOD `Open`/`Validate`/`ReadAllBytes`/`ExtractTo`; XISOSharp keeps integration/facade parity tests and its own fuzz target; 60-second CI fuzz-smoke green.

- [x] **STF-08 · Publish `StfsSharp` and cut 1.7.0**
  - Write `docs/format.md`, `docs/api.md`, `docs/interop.md` and README; add `THIRD-PARTY-NOTICES.md`; tag/publish `StfsSharp`; bump the XISOSharp reference; tag and publish **XISOSharp 1.7.0** with release notes and six bundles.
  - **Done when:** `StfsSharp` is on NuGet, XISOSharp 1.7.0 is published, and the docs state that GOD/STFS readers are `StfsSharp`-backed.
  - **Done 2026-11-07:** `StfsSharp 1.0.0` is on NuGet and GitHub (`v1.0.0` tag + release); docs written. XISOSharp tag `1.7.0` at `84d12d9`; CI green (including NuGet publish); GitHub release has six RID zips + SPDX; win-x64 bundle reports `XISOSharp v1.7.0`, `--probe-cli` 1.7.0 and `SELF-TEST: all passed`.

---

## 4. P1 — CCISharp 1.1

> ✅ Done. Separate package/repository (`purelogiccode/CCISharp`); `CCISharp 1.1.0` is on NuGet and XISOSharp consumes it.

- [x] **CCI-01 · Add the LZ4-HC level-12 encoder**
  - Add a managed LZ4-HC encoder (or an optional opt-in adapter) compatible with libCCI's `LZ4HC_CLEVEL_MAX`; keep the fast encoder as the default and keep the default package dependency-free. Expose it as `--cci-level 12` or `--hc`.
  - Do **not** reuse the global `--best` name: in XISOSharp it is already the Redump `--trim --wipe` alias.
  - **Done when:** HC output decodes in libCCI, is equal-or-smaller than the fast encoder on the synthetic corpus, and byte-level differences are documented.
  - **Done 2026-11-07:** `Lz4Hc.Compress` and `CciWriteOptions.Level` 10..12 (12 = max effort); fast encoder stays the default. libCCI `cci_extract` decodes every HC test file; HC is equal-or-smaller than level 9 on the synthetic corpora (strictly smaller on the repetitive fixture). Byte-level notes are in CCISharp `docs/interop.md`.

- [x] **CCI-02 · Add parallel CCI sector compression**
  - Add a bounded worker-pool compressor in CCISharp that writes sector payloads in order, so output is byte-identical to the sequential writer. Then wire single-file `compress --format cci --jobs 1..64` in XISOSharp and remove the "CCI is single-threaded" rejection.
  - **Done when:** `--jobs N` produces the same bytes as `--jobs 1`, and CLI contract tests cover the new path.
  - **Done 2026-11-07:** `CciWriteOptions.Jobs` 1..64 drives an ordered batch pipeline; tests cover levels 0/1/9/12 and split/single output. XISOSharp passes `--jobs` to the new `XisoCci.CompressToCci(... jobs ...)` overload; `CliCciTests` asserts byte parity and round-trip, and the single-threaded rejection is gone.

- [x] **CCI-03 · Add optional multi-digit CCI part discovery**
  - Accept `game.10.cci` when no single-digit interpretation applies; keep writer output at `game.1.cci` … `game.9.cci` for reference compatibility.
  - **Done when:** the reader discovers contiguous parts `1..N`, the writer still emits single-digit parts, and the compatibility rule is documented.
  - **Done 2026-11-07:** `CciFile` parses any numeric suffix, discovers contiguous `1..N`, and falls back to a standalone image when the requested number is outside the set. XISOSharp's `CciNaming`/`StripImageSuffix` follow the same rule. Writer output stays `.1.cci` … `.9.cci`; the rule is in CCISharp `docs/format.md` and `docs/api.md`.

- [x] **CCI-04 · Add CCISharp fuzz and benchmarks**
  - Fuzz header/index/blob/split parsing; benchmark compress/decompress, random sector reads, split overhead and allocations.
  - **Done when:** the fuzzer finds no unhandled exception or OOM and benchmark results are documented.
  - **Done 2026-11-07:** `CCISharp.Fuzz` covers header/index/blob mutations and split-set mutations (multi-digit rename, gaps, truncation, case changes); 60-second CI fuzz smoke has zero failures. `CCISharp.Benchmarks` covers fast/HC/parallel/split compression, extraction and random reads with `MemoryDiagnoser`; results are in `docs/benchmarks.md`.

- [x] **CCI-05 · Publish CCISharp 1.1 and bump XISOSharp**
  - Add `docs/format.md`, `docs/api.md`, `docs/interop.md`, README and release notes; tag/publish CCISharp 1.1.0; update `XISOSharp.csproj` `PackageReference` and package validation.
  - **Done when:** CCISharp 1.1.0 is on NuGet and XISOSharp builds/tests against it.
  - **Done 2026-11-07:** docs + README + release notes written; tag `v1.1.0` pushed, CI green (including `publish to NuGet`), flat-container index lists `1.1.0`. `XISOSharp.csproj` references `CCISharp 1.1.0`; `dotnet pack` package validation passes.

- [x] **CCI-06 · Verify interop against libCCI**
  - Use `cci_encode`/`cci_extract`/`cci_info` from `References/libCCI-0.1.0-1b0087e` as an oracle on synthetic images; optionally cross-check a Repackinator-produced fixture if one is available. Document any byte-level differences.
  - **Done when:** both directions round-trip and the interop report is in `docs/interop.md`.
  - **Done 2026-11-07:** fast, HC and parallel CCISharp output all extract byte-identically through `cci_extract`; level-12 libCCI output opens, validates and decodes through CCISharp. Report and synthetic-corpus results are in CCISharp `docs/interop.md`; the Windows CI interop tests run on every push.

- [x] **CCI-07 · (P2, optional) Ship `CCISharp.Cli`**
  - Add `pack`, `unpack`, `info` and `validate` for interop and CI smoke tests.
  - **Done when:** the CLI packs/unpacks a synthetic image in CI.
  - **Done 2026-11-07:** `CCISharp.Cli` implements all four verbs; the `cli-smoke` CI job packs with level 12/`--jobs 4`, runs `info` + deep `validate`, unpacks and compares the ISO byte-for-byte.

---

## 5. P2 — Library hardening

> ✅ Completed 2026-11-07 in 1.8.0; see the progress log in [§14](#14-progress-log).

- [x] **LIB-01 · Walk local create sources through `IFilesystem`**
  - Add a source-filesystem abstraction to `XisoWriter.CreateXiso`/`PackFromDirectory` so a custom store can be packed without host paths; keep the current overloads additive.
  - **Done when:** a `MemoryFilesystem` source packs a byte-identical image and concurrent creates remain CWD-free.

- [x] **LIB-02 · Make logging per-call**
  - Add an `XisoOptions`/`ILogger`-style sink to the read/write entry points; keep the static `Logger` as a compatibility facade.
  - **Done when:** two concurrent operations can log to separate sinks and existing static logging still works.

- [x] **LIB-03 · Add a sparse/large-file output strategy**
  - Use OS sparse-file support for pre-sized multi-GB images, with a safe fallback. Cover the writer, CISO/CCI decompression and extraction.
  - **Done when:** a multi-GB sparse image writes without zero-filling on Windows/Linux and extracts byte-identically.

- [x] **LIB-04 · Add property-based round-trip tests**
  - Add generators for pack/extract, split/join, CISO/CCI compress/decompress and ISO↔ZAR; use deterministic seeds.
  - **Done when:** the property suite runs in CI within budget and covers 0/1 sector, padding and compression-threshold edge sizes.

- [x] **LIB-05 · Complete the end-to-end benchmark suite**
  - Add extract, CCI compress/decompress and large-image I/O to `XISOSharp.Benchmarks`; keep the current pack/list/hash/CISO cases.
  - **Done when:** `--filter "*EndToEnd*"` covers every major path and results are documented.

- [x] **LIB-06 · Publish a benchmark comparison**
  - Compare against extract-xiso, xdvdfs and XboxKit with methodology, hardware and a reproducible script.
  - **Done when:** `docs/benchmarks.md` contains the numbers and commands.

- [x] **LIB-07 · Resolve the post-EOL target-framework plan**
  - `net9.0` is EOL (2026-05-12) and `net8.0` reaches EOL on 2026-11-10. Decide before 1.7.0 whether 1.7.x keeps them compatibility-only and drops them in 2.0.0, or drops them sooner; update the csproj files, CI, `docs/support-policy.md` and READMEs.
  - **Done when:** the support policy and build matrix agree and CI remains green.
  - **Owner update 2026-11-07:** the 2.0.0 drop is cancelled. `FAC-02` keeps `net8.0`/`net9.0`/`net10.0` and updates the docs/support policy accordingly.

- [x] **LIB-08 · Refresh the package-validation baseline**
  - Move `PackageValidationBaselineVersion` from `1.4.4` to the latest published stable (`1.5.0`/`1.6.0` as appropriate), review `CompatibilitySuppressions.xml`, and refresh `PublicApi.approved.txt`.
  - **Done when:** `dotnet pack` passes with the new baseline and no unreviewed suppressions remain.

---

## 6. P2 — Docs, fixtures and GUI

> ✅ Completed 2026-11-07 in 1.8.0; see the progress log in [§14](#14-progress-log).

- [x] **DOC-01 · Generate and publish the DocFX API reference**
  - Build the API site from the XML docs, publish it to GitHub Pages and add a link check.
  - **Done when:** the API pages are live and CI fails on broken links.

- [x] **DOC-02 · Add a public synthetic fixture corpus**
  - Add a deterministic generator/regeneration script with no copyrighted game data. Cover XISO, split ISO, CISO, CCI, ZAR, GOD, STFS, XBE/XEX and corrupt cases.
  - **Done when:** the corpus is checked in or generated in CI, and BattleTests consume it.

- [x] **DOC-03 · Refresh user docs after 1.7.0**
  - Update the feature matrix, `docs/known-limitations.md`, `docs/compression.md`, `docs/cli.md`, `docs/library.md`, READMEs and release notes for `StfsSharp` and CCISharp 1.1; fold in any 1.6.0 deltas missed by `REL-01`.
  - **Done when:** the docs match the shipped packages and no stale "future" claims remain.

- [x] **GUI-01 · Add GOD/SVOD input to the GUI**
  - Open-dialog filters, drop routing, list/tree/extract, `decompress`/`convert`, and title metadata.
  - **Done when:** the GUI opens a synthetic GOD set and extracts it byte-identically to the CLI.

- [x] **GUI-02 · Add STFS package input to the GUI**
  - Open/list/extract/validate XBLA/XBLIG packages and route `-V` validation.
  - **Done when:** the GUI lists and extracts a synthetic package and reports validation status.

- [x] **GUI-03 · Add the Xbox 360 title panel**
  - Show `title-info` fields for XBE/XEX/GOD/STFS, including XDBF/XSRC metadata.
  - **Done when:** the panel matches CLI `title-info --json` on synthetic fixtures.

- [x] **GUI-04 · Add the XBE image preview**
  - Preview the logo, title image and save image (PNG/DDS) from `XbeAssets`.
  - **Done when:** all three asset types render and a missing asset is a clean message.

- [x] **GUI-05 · (P2, optional) Expose parallel jobs controls**
  - Add `--jobs` controls for extraction and compression where supported; keep CCI disabled until CCISharp 1.1 lands.
  - **Done when:** the GUI command builders and self-test cover the new flags.

- [x] **DOC-04 · Cross-link the companion projects in the READMEs**
  - Add a "Companion projects" section to the root, library and CLI READMEs and `docs/library.md`, listing `SimpleXisoDrive`, `XISO Studio`, `StfsSharp`, `CCISharp` and `ZArchiveSharp`.
  - **Done when:** every README links the two desktop companions and the three library packages, and the library README (packed into the nupkg) carries the links.
  - **Done 2026-11-07:** root `README.md`, `XISOSharp/README.md` (NuGet readme), `XISOSharp.Cli/README.md` and `docs/library.md` link SimpleXisoDrive and XISO Studio plus the three sibling NuGet packages.

---

## 7. P3 — Distribution and supply chain

- [ ] **DST-01 · NuGet author signing**
  - Sign nupkg/snupkg when a repository certificate secret is configured.
  - **Done when:** a signed package verifies and the unsigned fallback still works.
  - **Implemented 2026-11-07 (awaiting secrets):** `ci.yml` `pack` decodes `NUGET_SIGNING_CERT`/`NUGET_SIGNING_PASSWORD`, runs `dotnet nuget sign` + `dotnet nuget verify --all` with an optional `NUGET_SIGNING_TIMESTAMPER`, and skips the step when unset. Local self-signed sign/verify and the unsigned fallback were exercised; close when a release with the real certificate verifies.

- [ ] **DST-02 · Windows Authenticode signing + macOS notarization**
  - Gate on repository secrets; sign the CLI/GUI bundles and notarize the macOS builds.
  - **Done when:** `signtool verify` and `spctl --assess` succeed on the release assets.
  - **Implemented 2026-11-07 (awaiting secrets):** `release.yml` gains `sign-windows` (Authenticode sign + `signtool verify /pa` before zipping) and `sign-macos` (codesign every Mach-O, notarize with `notarytool` when the `MACOS_NOTARY_*` secrets exist), gated by `ENABLE_WINDOWS_SIGNING` / `ENABLE_MACOS_SIGNING`. Smoke and release prefer the signed artifacts; unsigned bundles remain the default. Local self-signed `signtool sign`/`verify` passed.

- [x] **DST-03 · Package-manager manifests**
  - Generate winget, scoop and Homebrew manifests from the release assets.
  - **Done when:** each manifest installs the latest release in CI or a manual smoke test.
  - **Done 2026-11-07:** `scripts/update-packaging.ps1 -Version 1.8.0` generated the winget manifests, Scoop manifest (also at the repository-root `bucket/`), and Homebrew cask from the 1.8.0 release digests. `winget validate` passes; the win-x64 asset hash and nested `XISOSharp.exe`/`XISOSharp.Gui.exe` layout were verified against the published zip; CI adds winget, Scoop and Homebrew install smokes.

- [x] **DST-04 · Shell completions and man page**
  - Add PowerShell, bash, zsh and fish completions plus `xisosharp.1`; publish them as separate release assets or docs pages (the six bundle zips keep their exact five-file layout).
  - **Done when:** completions load and the man page renders.
  - **Done 2026-11-07:** `docs/completions/{xisosharp.ps1,xisosharp.bash,_xisosharp,xisosharp.fish}` and `docs/man/xisosharp.1` are served by the docs site. PowerShell `TabExpansion2` and bash `_xisosharp` were exercised locally; CI parses all four scripts, checks every CLI flag is completable (`scripts/check_cli_completions.py`), and renders/lints the man page with `groff`/`mandoc`. The six RID zips are unchanged.

- [x] **DST-05 · Optional Docker image**
  - Publish `ghcr.io/purelogiccode/xisosharp` with the CLI and document usage.
  - **Done when:** the image runs `XISOSharp -v` and a smoke conversion.
  - **Done 2026-11-07:** root `Dockerfile` publishes the framework-dependent CLI on the .NET 10 runtime; `.github/workflows/docker.yml` builds, runs `-v`, create/list/extract byte-compares a synthetic folder, and pushes `:<version>` + `:latest` to GHCR on tag pushes. The exact publish arguments and smoke commands were exercised locally (version `0.0.0-dockertest`).

---

## 8. P3 — Cross-repo consumers

> These items live in the companion repositories (`CSharp_SimpleXisoDrive`,
> `CSharp_XISOStudio`, `CSharp_ZArchiveSharp`, `CSharp_VideoGameFileSystemParser`),
> not in this tree. They stay open here as a cross-repo reminder.

- [x] **XRP-01 · Upgrade `SimpleXisoDrive`**
  - Move to XISOSharp 1.7/`StfsSharp`, adopt `XisoImage`, and verify GOD mount through `XisoImageSource.OpenRead`.
  - **Done when:** a GOD set mounts and lists through the new facade.
  - **Done 2026-11-07:** XISOSharp 1.8.0 + StfsSharp 1.0.0; `XisoVfsVolume` uses `XisoImage`; `ImagePathResolver` resolves `.data`/parent GOD paths; `--image-iso` uses `XisoImageSource.OpenRead`; renamed sector images keep a content fallback. Synthetic GOD tests (402+34) plus a real Bobble Head GOD set mounted with 13 root entries and `default.xex` XEX2 reads. Docs/WhatsNew updated.

- [x] **XRP-02 · Upgrade `XISOStudio`**
  - Move to XISOSharp 1.7, adopt `XisoImage`, add GOD to the open dialog, the 360 title panel and the XBE image preview; keep CHD/ZIP support in the app.
  - **Done when:** the new inputs open and preview without app-specific format-parsing code.
  - **Done 2026-11-07:** XISOSharp 1.8.0; `XisoImageExplorer` uses `XisoImage`; factory resolves GOD headers/`.data`/parent folders; explorer has Browse/Folder pickers; new details panel shows title metadata and renders XBE logo/title/save PNGs via `XbeAssets`; CHD/ZAR explorers implement metadata/unavailable states. 1,561 tests green; real GOD metadata (`Bobble Head`, 0x58480813) verified. Docs/WhatsNew updated.

- [x] **XRP-03 · Add ZArchiveSharp fuzz and benchmarks**
  - Add a ZAR decode fuzz harness and benchmark coverage.
  - **Done when:** the fuzzer runs in CI and the benchmark baseline is documented.
  - **Done 2026-11-07:** `ZArchiveSharp.Fuzz` (mixed/long-name/dictionary archives, byte+structural mutations, open-contract checks, tree walk, reads/streams/extraction) with a CI `fuzz-smoke` gate; 16,385 seeded iterations/0 failures. Added `ZarReaderBenchmarks` (open/enumerate/random/cross-block/cold-cache/stream/extract) and recorded the baseline in `docs/benchmarks.md`. The fuzzer found a NUL-name extraction `ArgumentException`; entry-name validation and `EnsureWithinRoot` now reject it as a documented `InvalidOperationException` (4,293 tests × 3 TFMs + 43 CLI).

- [ ] **XRP-04 · (P3, optional) Other consumers**
  - Let `VideoGameFileSystemParser` and other apps consume `CCISharp`/`StfsSharp`; document the package usage.
  - **Done when:** at least one external consumer builds against the new packages.

---

## 8A. P1 — 2.0.0: remove the `StfsSharp` facades (keep all TFMs)

> Owner decision 2026-11-07: **do not drop `net8.0`/`net9.0`**. The 2.0.0
> release is a public-API cleanup only; the build/test matrix stays
> `net8.0`/`net9.0`/`net10.0`. The facades were introduced in 1.7.0/1.8.0 and
> were always documented as temporary ("removed in 2.0.0").

- [ ] **FAC-01 · Remove the compatibility facades**
  - Delete `XISOSharp.GodHeader`, `XISOSharp.StfsHeader`, `XISOSharp.StfsPackage`, `XISOSharp.BlockDevice.GodBlockDevice` and the matching `XISOSharp.Models` types: `GodTitleInfo`, `GodVolumeInfo`, `StfsEntry`, `StfsPackageInfo`, `StfsPackageKind`, `StfsTitleInfo`, `StfsValidationIssue`, `StfsValidationReport`.
  - Migrate library internals, CLI, GUI, fuzz and fixtures to `StfsSharp` types directly. Keep an internal `GodVolumeBlockDevice` adapter for `XisoImageSource.OpenRead`/`OpenBlockDevice`; it is not public surface.
  - Keep `XisoChecksum.ComputeStfsChecksum`/`ComputeStfsChecksumHex` as direct `StfsSharp`-backed helpers (they expose no facade types) so the `checksum` verb and existing consumers keep working.
  - Refresh `XISOSharp.Tests/PublicApi.approved.txt`, add reviewed API-compat suppressions for the intentional removals, and move `PackageValidationBaselineVersion` to `1.8.0`.
  - **Done when:** every read verb still works for GOD/SVOD and XBLA/XBLIG inputs, the public-API snapshot contains no facade type, `dotnet pack` passes with the reviewed suppressions, and the suite is green on all three TFMs.

- [ ] **FAC-02 · Keep and document the TFM matrix**
  - Keep `net8.0`/`net9.0`/`net10.0` in `XISOSharp`, `XISOSharp.Cli`, `XISOSharp.TestDataGenerator` and `XISOSharp.Tests`; do not add TFM conditionals beyond the existing `NET9_0_OR_GREATER` guards.
  - Update `docs/support-policy.md`, `docs/known-limitations.md`, the READMEs and release notes so 2.0.0 no longer says the older TFMs are removed; describe all three as supported compatibility targets.
  - **Done when:** the support policy and build matrix agree, all three TFMs build/test, and no document claims a 2.0.0 TFM drop.

---

## 8B. P1 — 2.1.0: beyond-reference features

> These are **not parity gaps**: XISOSharp already covers the working feature
> set of `extract-xiso` build `202609111233`, `xdvdfs 0.8.3` and `XboxKit 0.7`
> (see [§9](#9-implemented-baseline-do-not-re-implement); XRD remains
> deliberately deferred). This track adds capabilities none of those tools
> expose, plus the small number of real improvements found in the upstream
> issue/PR scan ([§8C](#8c-p2--upstream-issuepr-scan-2026-11-07)).
> Status: **planned, not started** (owner request 2026-11-07).

### Track summary

| Item | Priority | Release | Value |
|---|---|---|---|
| `BRF-01` Redump/XGD disc-layout inspector | P1 | 2.1.0 | Archival layout validation |
| `BRF-02` Skeleton overlay (inverse of petrify) | P1 | 2.1.0 | Completes loose-files → XISO → Redump |
| `BRF-03` Embedded `default.xex` title metadata for GOD/STFS | P1 | 2.1.0 | 360 title-panel completeness |
| `BRF-04` XEX/XDBF thumbnail export | P2 | 2.1.0 | Completes the image-preview family |
| `BRF-05` Per-file hash sidecar for any container | P2 | 2.1.0 | `verify-hashes` without petrify |
| `BRF-06` Parallel collection verbs | P2 | 2.1.0 | Large-library throughput |
| `BRF-07` `compare` two images | P2 | 2.1.0 | Conversion QA across containers |
| `BRF-08` ZAR compression-level flag | P2 | 2.1.0 | Archive-size control |
| `BRF-09` Loose `.xbe`/`.xex` inputs | P2 | 2.1.0 | Developer workflow |
| `BRF-10` Redump DAT verification | P3 | 2.2.0 (optional) | Collection validation |
| `BRF-11` Xbox 42-character filename validation | P2 | 2.1.0 | Hardware-compatible names |
| `BRF-12` Deterministic-pack alias + regression test | P3 | 2.1.0 | Reproducible builds |
| `BRF-13` Media-enable patch audit | P2 | 2.1.0 | `-m` parity verified |
| `BRF-14` Create-time symlink policy | P3 | 2.1.0 | Explicit link handling |
| `BRF-15` Actionable host-I/O diagnostics | P3 | 2.1.0 | Better errors |

- [ ] **BRF-01 · Add a Redump/XGD disc-layout inspector**
  - Add `XisoDiscInfo.Read(path)` and CLI `--disc-info <image> [--json]`: report XGD type/subtype and wave, video/game partition byte+sector ranges, PFI presence/layerbreak, security-sector ranges (embedded security sector when present, else `sectors.txt`), known-Redump-size match, and consistency warnings.
  - Parse only observed on-disk fields. `XgdTables` is the existing format baseline; `References/XboxToolkit-main` remains a **behaviour oracle only** (GPL guardrail) and `References/XboxKit-0.7/LibXGD/XGD.cs` is the permissively licensed table reference.
  - Work for plain ISO and `--skip-sectors`/auto-detected Redump offsets; containers report the game-partition view and clearly mark video fields unavailable.
  - **Done when:** synthetic XGD1/XGD2/XGD3/hybrid fixtures report the expected layout, a truncated/wrong-size image reports a clear issue, and `--json` is covered by snapshot tests.

- [ ] **BRF-02 · Add skeleton overlay (`--apply-skeleton`)**
  - Add `XisoSkeleton.Apply(skeletonPath, sourceDirectory, outputPath, hashPath = null)` and CLI `--apply-skeleton <skeleton.xiso> <source-dir> -o <out.iso> [--hash <file>]`: read the skeleton's entry table/extents, place source file data at the recorded positions, preserve the skeleton's directory/system sectors, and verify the optional SHA-1 `.hash` while writing.
  - This is the one-step inverse of `--petrify`; it reuses the existing `--sector-layout`/`XisoWriter` extent machinery rather than duplicating the allocator. Missing/extra files and hash mismatches are named errors by default.
  - **Done when:** `Petrify` → `Apply` on the same files reproduces a byte-identical XISO on synthetic fixtures, the `.hash` verifies, and missing/extra/hash-mismatch cases fail with clear messages.

- [ ] **BRF-03 · Read embedded `default.xex` metadata for GOD/STFS**
  - Extend `title-info`/`XisoReader.GetTitleInfo` so a GOD container or XBLA/XBLIG package with `default.xex` also reports the XEX2/XDBF/XSRC publisher, developer, genre, description and `hasThumbnail`, merged into the existing XContent result; add the fields to the JSON model and the GUI title panel.
  - Use the GOD logical stream for GOD and `StfsSharp.StfsPackage.ReadAllBytes` for STFS; reuse `XexDbdf`/`GetXexTitleInfo` parsing. Fall back cleanly to header-only metadata when `default.xex` is absent or unreadable.
  - **Done when:** a synthetic GOD set and STFS package report the same XEX fields as reading `default.xex` from an equivalent extracted ISO, and the GUI panel matches `title-info --json`.

- [ ] **BRF-04 · Export the XEX/XDBF thumbnail**
  - Add `--xex-image <image> <internal-path> --type thumbnail -o <out.png>` (and the loose-file form once `BRF-09` lands); the XDBF `0x8000` entry is already captured in `XexTitleInfo.Thumbnail` (PNG payload in the synthetic fixtures).
  - Validate/pass through the PNG payload, add a clean error for an absent or unsupported thumbnail, and render the preview in the GUI title panel.
  - **Done when:** a synthetic XEX with a thumbnail exports a readable PNG, a missing thumbnail is a clean message, and the GUI preview matches the CLI export.

- [ ] **BRF-05 · Generate per-file hash sidecars for any container**
  - Add `XisoReader.ComputeFileHashSidecar(...)` and CLI `--file-hashes <image> [-o <file>] [--algorithm sha1|sha256|md5] [--json]`; the default output is the `<image stem>.hash` SHA-1 format consumed by `verify-hashes`.
  - Support ISO, split ISO, CISO, CCI, GOD, STFS and ZAR through the existing container view. Keep `--petrify` behavior unchanged. Extend `verify-hashes` to detect SHA-1/SHA-256/MD5 line lengths so non-default sidecars verify too.
  - **Done when:** the generated sidecar round-trips through `verify-hashes`, matches petrify's `.hash` for the same image, and container inputs produce identical hashes.

- [ ] **BRF-06 · Parallelize collection verbs**
  - Extend `--jobs 1..64` to multi-image `checksum`, `hash`, `-V`/audit and `--batch` extract/list/tree/rewrite; keep output ordering deterministic (including JSON arrays) and print the existing summaries at the end. Single-image semantics stay unchanged.
  - **Done when:** `--jobs N` output is byte-identical to `--jobs 1`, cancellation stops new work, and multi-image batch tests cover success, failure and mixed results.

- [ ] **BRF-07 · Add `compare` for two images**
  - Add `XisoComparator.Compare(a, b)` and CLI `compare <a> <b> [--json] [--algorithm sha256]`: compare logical trees across ISO/split/CISO/CCI/GOD/ZAR, reporting only-in-A, only-in-B, size mismatches and per-file hash mismatches. Exit `0` identical, `2` different, `1` error (matching the `validate` convention).
  - **Done when:** identical images compare equal across container formats, a single changed byte/path reports the exact file, and JSON output is snapshot-tested.

- [ ] **BRF-08 · Expose the ZAR compression level**
  - Add `--zar-level <1..22>` to `--zar` and `convert --format zar`, wired to `ZstdCompressionOptions.FromLevel`/`IZarBlockCompressor`; keep level `6` and the fixed 64 KiB ZAR block size as defaults (the block size is format-defined).
  - **Done when:** levels `1`, `6` and `22` round-trip through ZArchiveSharp, the reference ZArchive reader opens the output, and default output stays byte-identical to today.

- [ ] **BRF-09 · Accept loose `.xbe`/`.xex` inputs**
  - Let `--xex-info`, `--xbe-info` and `--xbe-image` detect a standalone `.xex`/`.xbe` file and parse it directly, in addition to the current `<image> <internal-path>` form; the same parsers and JSON shape are used.
  - **Done when:** a loose executable reports exactly the same fields as the same file read from an ISO, and the existing image-plus-path form is unchanged.

- [ ] **BRF-10 · (P3, optional) Redump DAT verification**
  - Add `verify-dat <dat> <image>... [--json]`: parse a Redump XML DAT (`game/rom` size + MD5/SHA-1), match each image by hashes, and report verified/missing/ambiguous entries. Reuse the full-image digest path (`hash`).
  - This is a collection-management feature rather than an XISO operation; keep it opt-in and document that `docs/known-limitations.md` currently records DAT matching as not built in.
  - **Done when:** a synthetic DAT verifies matching images, reports mismatches, and JSON output is covered; no dependency on a specific DAT source is added.

- [ ] **BRF-11 · Enforce/report the Xbox 42-character filename limit**
  - XDVDFS stores a one-byte name length, but Xbox and Xbox 360 tooling expects each path component to stay within the classic 42-character limit; over-long names currently surface as upstream `Invalid argument` errors (extract-xiso #74). Validate components during create, `--copy-in`, `build-image`, `--petrify` output and `Apply`; warn by default with the offending path and add `--strict-names` to fail before writing.
  - Keep the Latin-1/Windows-1252 encoding contract, and turn non-encodable names, `+`/special characters and over-long components into actionable errors (extract-xiso #74/#81).
  - **Done when:** a >42-character component warns and `--strict-names` fails before writing; names with `+`, `!`, spaces and Windows-1252 characters round-trip; the error names the file and the limit.

- [ ] **BRF-12 · Lock deterministic packing**
  - XISOSharp already sorts source entries (`XisoWriter` uses `StringComparer.Ordinal`) and accepts `--file-time 0`; add a `--deterministic` alias that implies `--file-time 0`, document exactly which inputs affect the bytes, and add repeated-pack regression tests for directory, `build-image`, `--layout` and memory-source packing (xdvdfs #101).
  - **Done when:** packing the same source twice produces byte-identical output with `--deterministic`, repacking an image is stable, and the docs describe the guarantee.

- [ ] **BRF-13 · Audit media-enable patching (`-m`)**
  - Add a synthetic `.xbe` fixture containing the `E8 CA FD FF FF 85 C0 7D` signature; assert create and rewrite change the patch byte to `EB`, that `-m` leaves it untouched, and that the log/JSON reports the patched-file count.
  - Keep the Boyer-Moore overlap-7 behavior; this verifies the upstream report that the reference patch can silently no-op (extract-xiso #68).
  - **Done when:** create and rewrite tests cover patched/unpatched signatures, `-m` is honored in both modes, and the count is surfaced.

- [ ] **BRF-14 · Add a create-time symlink policy**
  - Add `--symlink-mode follow|skip|reject` (default: follow file symlinks and skip directory reparse points as today) for `-c`, `--pack`, `build-image` and `Apply`; `reject` fails with the link path and `skip` logs it.
  - **Done when:** cyclic directory links terminate, each mode is tested, and the default output is unchanged (extract-xiso PR #83).

- [ ] **BRF-15 · Improve host-I/O diagnostics**
  - Map permission-denied, long-path, read-only destination, illegal-byte and name-collision failures to actionable messages naming the path, operation and likely fix; include the same detail in `--json` errors where applicable.
  - **Done when:** tests cover permission, long-path and >42-character names on Windows/Linux, and no raw `Invalid argument`/`Permission denied` reaches the user without context (extract-xiso #69/#74/#81).

### Suggested release split

| Release | Items |
|---|---|
| **2.1.0** | `BRF-01`…`BRF-09`, `BRF-11`…`BRF-15` |
| **2.2.0** (optional) | `BRF-10` |

---

## 8C. P2 — Upstream issue/PR scan (2026-11-07)

> Reviewed the open and closed issue/PR lists of `XboxDev/extract-xiso` and
> `antangelo/xdvdfs` on 2026-11-07. Conclusion: **there is no large missing
> feature**. Nearly every feature request is already implemented in XISOSharp;
> the remainder are platform/build questions, web/daemon/mounting work that
> belongs to companion projects, or explicit non-goals. The actionable items
> became `BRF-11`…`BRF-15`; the rest are recorded here so the scan is not
> repeated.

### Already implemented (no new work)

| Upstream request | XISOSharp equivalent |
|---|---|
| extract-xiso #66 audit an XISO | `-V` deep audit |
| extract-xiso #67 custom rewrite filename | `-r -o <name>` |
| extract-xiso #33 `-o[ffset]` skip video partition | `--skip-sectors` |
| extract-xiso #92 empty directories become files | empty-directory `0xFF` sentinel |
| extract-xiso #26 preallocate output files | pre-sized/sparse extraction and writer |
| extract-xiso #85 multiple ISO arguments | multi-image + `--batch` |
| extract-xiso #95 large file / AVL failure | `XisoFileTooLargeException` + typed errors |
| extract-xiso #25 OOM on invalid TOC | pathological-TOC guards |
| xdvdfs #49 explicit sector layout | `--sector-layout` + `--layout` |
| xdvdfs #54 unpack a single file | `--copy-out` |
| xdvdfs #165 copy files into an image | `--copy-in` |
| xdvdfs #62/#185 CCI support | CCISharp 1.1 integration |
| xdvdfs #97 split XISO | `split`/`join` |
| xdvdfs #93 batch convert + naming | `--batch` + `--output-name` |
| xdvdfs #37/#155 image timestamps | `--file-time` / `--set-filetime` |
| xdvdfs #121 show XGD offset in info | `-i` reports the disc format; full offset/layout in `BRF-01` |
| xdvdfs #101 predictable pack output | sorted entries + `--file-time 0`; regression/alias in `BRF-12` |
| xdvdfs #17 / extract-xiso #68 media patching | implemented in create/rewrite; audit in `BRF-13` |
| extract-xiso #74/#81 long or special names | new `BRF-11` |
| extract-xiso PR #83 symlinks | current follow-files/skip-directories policy; explicit modes in `BRF-14` |
| extract-xiso #69 permission/long-path errors | new `BRF-15` |

### Out of scope or not applicable

| Upstream item | Why no XISOSharp action |
|---|---|
| extract-xiso #63 direct DVD-drive reading | permanent non-goal; implement `IBlockDevice` |
| extract-xiso build/CI/readme/C++ modernization items | C-project maintenance, not library behavior |
| xdvdfs #88 Python bindings | non-goal (managed .NET only) |
| xdvdfs #100/#106/#116/#117/#119/#129/#131 desktop/FUSE/NFS/fsd | SimpleXisoDrive owns mounting; NFS/daemon work is out of scope |
| xdvdfs #167/#120/#102/#114/#46/#36/#190 web UI items | `xdvdfs-web` is a separate Rust/browser app |
| xdvdfs #187 web FileSystem permission errors | browser File System Access API limitation |
| xdvdfs #192 `std` via `bincode` | Rust dependency detail |
| xdvdfs #191 Steel Battalion Recomp | filed in the wrong repository (closed) |

### New items created from the scan

| Item | Upstream source | Summary |
|---|---|---|
| `BRF-11` | extract-xiso #74/#81 | Validate the 42-character Xbox name limit and improve name encoding errors |
| `BRF-12` | xdvdfs #101 | Make determinism explicit (`--deterministic`) and regression-test repeated packing |
| `BRF-13` | extract-xiso #68 / xdvdfs #17 | Prove `-m` and the media-enable patch actually change the expected bytes |
| `BRF-14` | extract-xiso PR #83 | Add an explicit create-time symlink policy |
| `BRF-15` | extract-xiso #69/#74/#81 | Turn host permission/long-path/encoding failures into actionable diagnostics |

---

## 9. Implemented baseline (do not re-implement)

| Area | Already implemented |
|---|---|
| **Core XISO** | `XisoImage` facade, create, list, tree, extract, rewrite, validate, repair, salvage, checksum/hash, sector layout, explicit layout packing, pathological-TOC traversal, pre-sized extraction/writer I/O |
| **Containers** | one detection layer for ISO, split ISO, CISO v1/v2, split CISO, CCI/CCIM, split CCI, ZAR, read-only GOD/SVOD, read-only XBLA/XBLIG STFS |
| **Metadata** | XBE/XEX headers, XDBF/XSRC 360 metadata, GOD/STFS XContent title metadata, XBE logo/title/save images |
| **CLI** | extract/list/tree/checksum/hash/verify-hashes/validate/title-info/xbe-image, compress/decompress/convert/split, batch, JSON, `--layout`, `--jobs` (CISO/CCI/extraction), `--name-policy`, `--delete-source` |
| **1.6.0 scope** | A1 XDBF, A2 XBE assets, A3 FATX cap, A4 `--delete-source`, A5 GOD/STFS title naming, read-only GOD input, STFS package listing/extraction/validation, parallel CISO compression, parallel extraction, safe-extract policy, CWD-free writer/reader |
| **1.7.0 scope** | `StfsSharp` extraction: standalone GOD/SVOD + STFS readers, NuGet 1.0.0, XISOSharp facades with an unchanged public API, moved tests/fixtures/fuzzer/benchmarks |
| **CCISharp 1.1 scope** | LZ4-HC levels 10..12, parallel sector compression (`Jobs` 1..64, byte-identical), multi-digit split discovery, `CCISharp.Cli`, fuzz + benchmarks, `CCISharp 1.1.0` on NuGet |
| **Quality** | 3-OS CI, format gate, InspectCode, AOT smoke, mutation fuzz, battle-interop, coverage floors, SBOM and build provenance |
| **Distribution (1.8.x)** | winget/Scoop/Homebrew manifests + generator, PowerShell/bash/zsh/fish completions, `xisosharp(1)`, CLI container image (GHCR), optional NuGet/Authenticode/macOS signing pipelines |
| **Cross-repo (XRP)** | SimpleXisoDrive GOD mounts via `XisoImage` + real-corpus verification; XISOStudio GOD explorer + title panel + XBE preview; ZArchiveSharp mutation fuzzer + reader benchmarks + NUL-name hardening |

---

## 10. Reference map

All paths are under `References/`. These are **behaviour/format oracles** unless noted otherwise.

| Reference | License | Used for | Remaining work |
|---|---|---|---|
| `extract-xiso-build-202609111233` | BSD-style | Core XISO/CLI parity and oracle | None |
| `libCCI-0.1.0-1b0087e` | GPL-2.0-or-later | Normative CCI `FORMAT.md` and `cci_*` oracle | None — CCISharp 1.1 HC/parallel/multi-digit work verified |
| `Xbox360Convert-1.0.1` | GPL-3.0 (no own license file; links XboxToolkit) | GOD input, FATX split point, delete-source, GOD title naming | None — A3/A4/A5 implemented in 1.6.0; reference only |
| `XboxKit-0.7` | Permissive (upstream) | Redump/ZAR parity baseline | None — XRD is deferred |
| `XboxToolkit-main` | GPL-3.0-only | GOD/STFS read, XEX XDBF metadata, XBE image assets | GUI surfaces (`GUI-*`); planned `BRF-03`/`BRF-04` use it as a behaviour oracle; `StfsSharp` extraction done in 1.7.0 |
| `xdvdfs-0.8.3` | MIT | CISO/split/checksum/CLI parity baseline | None |
| `XDVDFS-TR-0.8.4-TR` | MIT | `xdvdfs-cci` (covered by CCISharp); `xdvdfs-fsd`/`xdvdfs-web` reviewed | None in XISOSharp — NFS/WASM are out of scope |
| `XeCrypt-main` | MIT | XBE certificate/STFS hash-tree reference | None — signature verification is excluded; STFS hash tree is already implemented independently |

**Licensing guardrail:** `libCCI` (GPL-2.0-or-later), `XboxToolkit` (GPL-3.0-only) and `Xbox360Convert` (no license file; links GPL code) are **behaviour oracles only**. Do not copy, port or transliterate their code. Implement from `FORMAT.md` and observed on-disk layouts. `xdvdfs` is MIT; retain attribution if code is ported.

**Reference baselines**

- CCI: `References/libCCI-0.1.0-1b0087e/FORMAT.md` wins over any summary in this repo.
- CISO/checksum: `xdvdfs 0.8.3` is the byte-parity baseline.
- Core XISO/CLI: `extract-xiso` build `202609111233` is the parity/oracle baseline.
- GOD/STFS: `XboxToolkit`/`Xbox360Convert` are behaviour references only.
- Upstream issue/PR scan: `extract-xiso` and `xdvdfs` were reviewed on 2026-11-07; no missing feature beyond `BRF-11`…`BRF-15` (see [§8C](#8c-p2--upstream-issuepr-scan-2026-11-07)).

---

## 11. Non-goals and deferred work

These are deliberate boundaries. Do not add checkboxes for them; revisit only if the owner changes the decision.

| Item | Why not |
|---|---|
| CHD containers | Belongs to SimpleXisoDrive/XISOStudio, not the Xbox-focused core. |
| STFS/CON/LIVE/PIRS writing, rebuild and re-signing | Read-only by design; needs private keys and signing. |
| XEX decryption / XBE editing | Out of scope; `GetXexInfo`/`GetXbeInfo` stay read-only. |
| Executable signature verification | Owner decision 2026-11-01; `--validate` stays structural. |
| Direct optical-drive reading | Permanent non-goal; users implement `IBlockDevice` over their own reader. |
| FUSE/Dokan/NFS mounting | SimpleXisoDrive owns mounting. |
| Web/WASM/Tauri UI, Python bindings, native ABI | Owner decision 2026-11-01; XISOSharp stays a managed .NET library. |
| XRD Redump metadata | Deferred: XboxKit 0.7's implementation is a stub; no verified oracle. Revisit only with a complete spec and oracle. |
| Bit-exact CCI output vs Repackinator | Interoperability is the goal; HC is best-effort byte parity, not a guarantee. |
| ZIP input | XISOStudio already ships SharpCompress. |
| XGD-shaped synthesis from a folder / XGD header writer | Cannot produce a bootable disc; `--prepend-sectors` + `rebuild` cover real workflows. |
| `dotnet tool` package | Removed after 1.5.0; the CLI ships only in the six RID bundles. |

---

## 12. Definition of done

For every item that changes code:

- Library API implemented; CLI and GUI wired where applicable.
- Unit + integration + parity/interop + CLI contract tests added.
- Synthetic fixtures only; no copyrighted game data and no GPL binaries.
- Docs, READMEs and release notes updated in the same change.
- Full suite green on the supported TFM matrix (currently `net8.0`/`net9.0`/`net10.0`; retained by `FAC-02`); `dotnet format` clean; InspectCode 0 warnings; trim/AOT analyzers clean.
- `dotnet pack` succeeds with package validation.
- No deletion of files under `bin/Release`, `publish*` or release zips (see `AGENTS.md`).

---

## 13. Verification commands

```powershell
# Full build + tests
dotnet build CSharp_XISOSharp.sln -c Release -m:1
dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj -c Release

# Formatting and packaging
dotnet format whitespace CSharp_XISOSharp.sln --verify-no-changes --no-restore
dotnet pack XISOSharp/XISOSharp.csproj -c Release -o artifacts

# AOT, fuzz and benchmarks
dotnet publish XISOSharp.AotSmoke/XISOSharp.AotSmoke.csproj -c Release -r linux-x64
dotnet run --project XISOSharp.Fuzz -c Release -- --seconds 60
dotnet run --project XISOSharp.Benchmarks -c Release -- --filter "*EndToEnd*"

# Distribution artifacts
python scripts/check_cli_completions.py
winget validate --manifest packaging/winget/manifests/p/purelogiccode/XISOSharp/1.8.0
docker build -t xisosharp --build-arg VERSION=1.8.0 .
docker run --rm xisosharp -v
```

---

## 14. Progress log

| Date | Item | Version | Notes |
|---|---|---|---|
| 2026-11-06 | Plan rewritten as a checkbox backlog | — | Baseline 1.5.0; 1.6.0 code-complete but untagged |
| 2026-11-06 | `REL-01` 1.6.0 documentation finalized | 1.6.0 | Release notes/WhatsNew dated; README and support docs refreshed |
| 2026-11-06 | `REL-02` verification complete | 1.6.0 | Build 0/0; tests 2098/1 + 2162/1; format clean; InspectCode 0; pack OK; AOT, fuzz and synthetic battle passed |
| 2026-11-06 | InspectCode/NU1903 cleanup | — | 44 InspectCode warnings fixed; ILCompiler pack pinned to 10.0.14 |
| 2026-11-06 | macOS CWD test fix | 1.6.0 | Compare actual CWD before/after; avoids `/var` vs `/private/var` symlink mismatch |
| 2026-11-06 | `REL-03` tagged and published | 1.6.0 | Tag `1.6.0` at `350f629`; NuGet indexed; GitHub release 6 zips + SPDX |
| 2026-11-06 | `REL-04` post-release smoke | 1.6.0 | win/linux/macOS bundles passed; downloaded win-x64 CLI/GUI smoke passed |
| 2026-11-07 | `STF-01` repo/package skeleton | StfsSharp 1.0.0 | 4 projects, 3-OS CI, NuGet package published; GitHub repo `purelogiccode/STFSSharp` |
| 2026-11-07 | `STF-08` StfsSharp release | StfsSharp 1.0.0 | `v1.0.0` tag pushed, CI green, GitHub release published |
| 2026-11-07 | `STF-02`–`STF-05` readers moved | StfsSharp 1.0.0 | GOD/SVOD + STFS readers, metadata and validation ported; 37 tests per TFM |
| 2026-11-07 | `STF-06` XISOSharp integration | 1.7.0-prep | NuGet dependency + facades; public API unchanged; pack validation green |
| 2026-11-07 | `STF-07` tests/fixtures/fuzz | StfsSharp 1.0.0 | Tests and fixture moved, fuzzer added; XISOSharp suite 2101/2165 |
| 2026-11-07 | Real-corpus verification | StfsSharp 1.0.0 | Aegis Wing XBLA (validate PASS), 1on1 Soccer XBLIG, GOD mapping; CLI smoke passed |
| 2026-11-07 | `STF-08` XISOSharp 1.7.0 released | 1.7.0 | Tag `1.7.0` at `84d12d9`; CI green + NuGet publish; GitHub release 6 zips + SPDX; win-x64 bundle/`--self-test` passed |
| 2026-11-07 | `CCI-01`/`CCI-06` LZ4-HC + interop | CCISharp 1.1.0 | Managed HC levels 10..12; libCCI decodes fast/HC/parallel output; HC equal-or-smaller on synthetic corpora; interop report written |
| 2026-11-07 | `CCI-02`/`CCI-03` parallel + multi-digit | CCISharp 1.1.0 | `Jobs` 1..64 byte-identical; XISOSharp `--jobs` wired; multi-digit reader discovery with single-digit writer output |
| 2026-11-07 | `CCI-04`/`CCI-07` fuzz, benchmarks, CLI | CCISharp 1.1.0 | Fuzz + benchmark projects; `CCISharp.Cli` pack/unpack/info/validate; CI `fuzz-smoke` + `cli-smoke` green |
| 2026-11-07 | `CCI-05` CCISharp 1.1.0 published | CCISharp 1.1.0 | Tag `v1.1.0`; CI green; NuGet 1.1.0 indexed; XISOSharp ref bumped; pack validation green |
| 2026-11-07 | CCISharp 1.1 integration verification | 1.8.0-dev | XISOSharp suite 2106/1 (net8/net9) + 2170/1 (net10); build 0 warnings; format clean; full restore from nuget.org |
| 2026-11-07 | `LIB-01` source filesystem packing | 1.8.0 | `ISourceFilesystem` + `MemoryFilesystem` source; `CreateXiso`/`PackFromDirectory` overloads; host-parity and concurrent CWD tests |
| 2026-11-07 | `LIB-02` per-call logging | 1.8.0 | `IXisoLogger`/`XisoLogOptions`/`XisoLogScope`; `UnpackOptions.Log`; isolated concurrent sinks and static fallback tests |
| 2026-11-07 | `LIB-03` sparse output | 1.8.0 | NTFS sparse marking + zero-skip CISO/CCI/ZAR; NTFS allocation and round-trip tests |
| 2026-11-07 | `LIB-04` property round-trips | 1.8.0 | Seeded boundary-size pack/extract, split/join, CISO, CCI and ZAR cases |
| 2026-11-07 | `LIB-05`/`LIB-06` benchmarks | 1.8.0 | End-to-end extract/CCI/large-read cases; `docs/benchmarks.md` + `scripts/bench-compare.ps1`; BDN and 1 GiB CLI comparison captured |
| 2026-11-07 | `LIB-07`/`LIB-08` support + validation baseline | 1.8.0 | `net8`/`net9` compatibility-only through 1.x; validation baseline moved to 1.7.0 and old suppressions retired; pack green |
| 2026-11-07 | `DOC-01` DocFX link gate | 1.8.0 | Live API site retained; `docs-links` CI job + `scripts/check_docs_links.py` (2428 local links checked) |
| 2026-11-07 | `DOC-02` public corpus | 1.8.0 | `TestDataGenerator --corpus` covers XISO/split/CISO/CCI/ZAR/GOD/STFS/XBE/XEX/corrupt + manifest; `PublicCorpusTests` and BattleTests consume it |
| 2026-11-07 | `DOC-03` docs refresh | 1.8.0 | READMEs, feature/limitations/compression/library/CLI/testing docs, release notes and support policy updated |
| 2026-11-07 | `GUI-01`–`GUI-05` Xbox 360 GUI | 1.8.0 | GOD/STFS open/list/tree/extract/unpack/audit, title panel, XBE preview, drop routing, `--jobs` controls; self-test and GUI parity tests |
| 2026-11-07 | 1.8.0 verification | 1.8.0 | Suite 2144/1 (net8/net9) + 2217/1 (net10); format clean; InspectCode 0; pack 1.7.0 baseline; AOT, fuzz 6,605 iterations, battle 40/40, GUI self-test and docs links green |
| 2026-11-07 | 1.8.0 released | 1.8.0 | Tag `bce1393`; CI green; NuGet 1.8.0 indexed; GitHub release has six RID zips + SPDX; downloaded win-x64 bundle reports v1.8.0, `--probe-cli` 1.8.0 and `SELF-TEST: all passed` |
| 2026-11-07 | `DST-04` completions + man page | 1.8.x | PowerShell/bash/zsh/fish completions and `xisosharp(1)` in docs; CLI-flag coverage checker; `packaging-smoke` CI |
| 2026-11-07 | `DST-03` package manifests | 1.8.x | winget/Scoop/Homebrew manifests generated from 1.8.0 digests; `winget validate` green; asset hash + nested layout verified; install-smoke CI |
| 2026-11-07 | `DST-05` container image | 1.8.x | `Dockerfile` + `docker.yml`; local publish/smoke arguments validated; GHCR publish on tags |
| 2026-11-07 | `DST-01`/`DST-02` signing pipelines | 1.8.x | NuGet sign/verify and Windows/macOS sign jobs gated on repository secrets; local self-signed sign/verify passed; open until real certs verify |
| 2026-11-07 | Distribution verification | 1.8.x | Full suite 2144/1 + 2217/1; build 0 warnings; format clean; pack green; docs links 263 OK; real `XBOXReady` ISOs audit/checksum/title-info PASS; winget asset hash + layout verified |
| 2026-11-07 | Work paused | 1.8.x | Distribution track implemented and locally validated; CI/install-smoke and real signing certs remain. Resume notes in [§15](#15-resume-notes--handoff) |
| 2026-11-07 | First distribution CI run | 1.8.x | Docker + packaging-windows green; man-page `grep -q` SIGPIPE, winget uninstall cleanup and Homebrew local-cask restriction fixed; follow-up run pending |
| 2026-11-07 | Distribution CI green | 1.8.x | Scoop `bucket add` local-path failure fixed by installing the checked-in manifest; CI run `42396783434` fully green (build/test 3 OS, package installs, Docker, packaging, docs, AOT, fuzz) |
| 2026-11-07 | `XRP-01` SimpleXisoDrive | — | XISOSharp 1.8.0 + StfsSharp; `XisoImage` adoption; GOD header/`.data`/parent resolution; real Bobble Head GOD mount verified; 436 tests; format + InspectCode clean; CI run `42405304528` green |
| 2026-11-07 | `XRP-02` XISOStudio | — | XISOSharp 1.8.0; GOD input (file/folder pickers); title metadata panel; XBE logo/title/save preview; 1,561 tests; real GOD metadata verified. First CI flagged four solution-wide InspectCode suggestions; fixed in `0c43f02`; follow-up CI run `42405778091` green including InspectCode and all six publishes |
| 2026-11-07 | `XRP-03` ZArchiveSharp | — | `ZArchiveSharp.Fuzz` + CI fuzz-smoke (16,385 iterations/0 failures); `ZarReaderBenchmarks` + documented baseline; fuzzer found and fixed NUL-name extraction exception; 4,293 tests × 3 TFMs + 43 CLI; CI run `42405322199` green including fuzz-smoke and pack |
| 2026-11-07 | Documentation refresh | 1.8.x | XISOSharp README/library now list SimpleXisoDrive and XISO Studio as downstream `XisoImage` consumers; SimpleXisoDrive Architecture, Virtual File System and Contributing now describe `XisoImage` instead of the old `XisoExplorer`; plan/handoff finalized; XISOSharp code CI green (`f3c8a98`) and the docs-only follow-up is committed |
| 2026-11-07 | Plan extended with 2.0.0 + 2.1.0 tracks | — | Owner decision: keep `net8.0`/`net9.0`/`net10.0`; added `FAC-01`/`FAC-02` (facade removal only) and `BRF-01`…`BRF-10` (beyond-reference features, later extended to `BRF-15` by the upstream scan). Planned, not started. |
| 2026-11-07 | Upstream issue/PR scan | — | Reviewed the `extract-xiso` and `xdvdfs` issue/PR lists; most requests are already covered; added `BRF-11`…`BRF-15` and [§8C](#8c-p2--upstream-issuepr-scan-2026-11-07). No product code changed. |
| 2026-11-07 | `DOC-04` companion README links | 1.8.x | Root, library (NuGet readme), CLI and `docs/library.md` now link SimpleXisoDrive, XISO Studio, StfsSharp, CCISharp and ZArchiveSharp. |

---

## 15. Resume notes / handoff

**State at pause (2026-11-07):** the distribution track is green in CI, and the
cross-repo XRP-01/02/03 upgrades are implemented, committed and pushed. Their
CI is green (SimpleXisoDrive `42405304528`, XISOStudio `42405778091`,
ZArchiveSharp `42405322199`). The XISOSharp repository itself has no
product-source changes from this session beyond the plan/CI fix. The plan was
extended the same day with the 2.0.0 facade-removal track and the 2.1.0
beyond-reference track; neither is started.

### Completed locally

- XISOSharp: CI run `42396783434` fully green (3-OS build/test, package installs,
  packaging, Docker, docs, AOT, fuzz, InspectCode).
- SimpleXisoDrive: XISOSharp 1.8.0 + StfsSharp 1.0.0, `XisoImage` adoption, GOD
  mounts; build 0/0, format clean, InspectCode clean, **436** tests (402 core +
  34 Unix).
- XISOStudio: XISOSharp 1.8.0, GOD input, title metadata panel, XBE preview;
  build 0/0, InspectCode 0 warnings, **1,561** tests.
- ZArchiveSharp: `ZArchiveSharp.Fuzz` (16,385 seeded iterations / 0 failures),
  `ZarReaderBenchmarks` baseline, NUL-name extraction hardening; build 0/0,
  **4,293** tests on each of `net8.0`/`net9.0`/`net10.0` + 43 CLI tests.
- Real-corpus verification: Bobble Head GOD set mounts in SimpleXisoDrive (13
  root entries, XEX2 reads, `image.iso`) and opens in XISOStudio with
  `Title=Bobble Head`, `0x58480813`, `default.xex`.
- Documentation refresh: XISOSharp README/library list the downstream consumers;
  SimpleXisoDrive Architecture/Virtual File System/Contributing describe the
  `XisoImage` path; all companion docs/WhatsNew entries are current.

### First actions on resume

1. The three companion repositories are committed and pushed; their CI is green
   (including ZArchiveSharp's 120-second `fuzz-smoke` and XISOStudio's
   solution-wide InspectCode).
2. The next product work is **2.0.0** (`FAC-01`/`FAC-02`): remove the
   `StfsSharp` facades but **keep `net8.0`/`net9.0`/`net10.0`**. After that,
   start **2.1.0** (`BRF-01`…`BRF-15`). **XRP-04** (another consumer repository)
   is optional and **DST-01/DST-02** await real signing certificates.
3. Do not delete anything under `bin/Release`, `publish*` or the release zips.

### DST-01/DST-02 signing checklist

| Item | Required repository configuration |
|---|---|
| NuGet author signing | secrets `NUGET_SIGNING_CERT` (base64 PFX), `NUGET_SIGNING_PASSWORD`; optional variable `NUGET_SIGNING_TIMESTAMPER` |
| Windows Authenticode | variable `ENABLE_WINDOWS_SIGNING=true`; secrets `WINDOWS_SIGNING_CERT`, `WINDOWS_SIGNING_PASSWORD` |
| macOS codesign | variable `ENABLE_MACOS_SIGNING=true`; secrets `MACOS_SIGNING_CERT`, `MACOS_SIGNING_PASSWORD`, `MACOS_SIGNING_IDENTITY` |
| macOS notarization | additionally secrets `MACOS_NOTARY_APPLE_ID`, `MACOS_NOTARY_TEAM_ID`, `MACOS_NOTARY_PASSWORD` |

Verification to record before closing DST-01/02: `dotnet nuget verify --all` on
the published nupkg/snupkg, `signtool verify /pa` on both Windows bundles, and
`spctl --assess --type execute` on the macOS binaries (notarization ticket).

### Pending after this track

- `XRP-01`/`XRP-02`/`XRP-03` are done; `XRP-04` (other consumers, optional) is
  the only open cross-repo item.
- The next product release is **2.0.0** (`FAC-01`/`FAC-02`: remove the
  `StfsSharp` facades, keep `net8.0`/`net9.0`/`net10.0`), followed by **2.1.0**
  (`BRF-01`…`BRF-09`, `BRF-11`…`BRF-15`) and optional **2.2.0** (`BRF-10`).
- After every release run
  `./scripts/update-packaging.ps1 -Version <version>` and commit the refreshed
  winget/Scoop/Homebrew files.
