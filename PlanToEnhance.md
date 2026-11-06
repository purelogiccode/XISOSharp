# XISOSharp — Remaining Work Plan

- **Updated:** 2026-11-06
- **Baseline:** XISOSharp **1.5.0** (published 2026-11-01)
- **Working tree:** 1.6.0 scope implemented, **not yet tagged** (`1.5.0` + 17 commits)
- **Next releases:** 1.6.0 (release gate) → 1.7.0 (`StfsSharp`) → CCISharp 1.1 (parallel track)
- **Packages:** CCISharp `1.0.1`, ZArchiveSharp `1.4.0`
- **Scope of this file:** only work that is still open. Implemented features are listed once in [§9](#9-implemented-baseline-do-not-re-implement) so they are not re-implemented.

> **How to use this file:** change `- [ ]` to `- [x]` when an item meets its **Done when** line. Add the version/date to the progress log in [§14](#14-progress-log).

> **TL;DR**
> 1. **Ship 1.6.0** — code is in the tree; docs, tag and release verification remain.
> 2. **Ship 1.7.0** — extract the existing GOD/STFS readers into the `StfsSharp` package. No new format support is required.
> 3. **Ship CCISharp 1.1** — LZ4-HC, parallel CCI compression and multi-digit parts; then remove the CCI `--jobs` restriction.
> 4. **Harden** — source `IFilesystem`, per-call logging, sparse output, property tests and benchmarks.

---

## 0. Snapshot

| Field | Value |
|---|---|
| Published library | **XISOSharp 1.5.0** (2026-11-01) |
| Current tree | **1.6.0 code-complete**, untagged (`1.5.0-17-gd791d0f`) |
| Next release gate | 1.6.0 tag → NuGet nupkg/snupkg → GitHub release (six RID bundles) |
| Next feature release | **1.7.0** — `StfsSharp` extraction |
| Compression track | **CCISharp 1.1** — HC encoder, parallel writer, multi-digit parts |
| Tests (last recorded) | 2098 passed / 1 skipped on `net8.0`/`net9.0`; 2162 passed / 1 skipped on `net10.0` |
| CI gates | 3-OS build/test, `dotnet format`, InspectCode, AOT smoke, fuzz, battle-interop, coverage floors |
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
| **P0** | Ship 1.6.0 | 1.6.0 | 2 |
| **P1** | Extract `StfsSharp` | 1.7.0 | 8 |
| **P1** | CCISharp 1.1 | CCISharp 1.1 | 7 |
| **P2** | Library hardening | 1.7.x / 2.0.0 | 8 |
| **P2** | Docs, fixtures and GUI | 1.7.x | 8 |
| **P3** | Distribution and supply chain | later | 5 |
| **P3** | Cross-repo consumers | after 1.7.0 | 4 |
| | **Total** | | **42 open** (3 optional) + 2 done |

**Recommended order**

| Order | Work | Why |
|---:|---|---|
| 1 | `REL-03` … `REL-04` | Tag/publish 1.6.0; `REL-01`/`REL-02` are done. |
| 2 | `STF-01` … `STF-08` | Biggest structural change; makes GOD/STFS readers reusable. |
| 3 | `CCI-01` … `CCI-06` | Independent track; unblocks CCI HC and `--jobs`. |
| 4 | `LIB-01` … `LIB-08` | Harden the API after the release branches settle. |
| 5 | `DOC-*`, `GUI-*` | User-facing polish; depends on the library work. |
| 6 | `DST-*`, `XRP-*` | Distribution and companion repositories. |

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

- [ ] **REL-03 · Tag and publish 1.6.0**
  - Tag `1.6.0`, push, approve the `nuget` environment, verify `XISOSharp.1.6.0.nupkg` + `.snupkg` and the GitHub release with six RID bundles.
  - **Done when:** `api.nuget.org/v3-flatcontainer/xisosharp/index.json` lists 1.6.0 and `gh release view 1.6.0 --json assets` lists the six `release_1.6.0_<rid>.zip` assets (SBOM/provenance assets may accompany them).

- [ ] **REL-04 · Post-release smoke test**
  - Extract `win-x64`, `linux-x64` and `MacOsX-arm64` bundles; run `XISOSharp -v`, `XISOSharp.Gui --probe-cli` and `XISOSharp.Gui --self-test <cli>`.
  - **Done when:** all three report 1.6.0 and `SELF-TEST: all passed`.

---

## 3. P1 — 1.7.0: extract `StfsSharp`

> Read-only GOD/SVOD and XBLA/XBLIG STFS support already works in-repo. This milestone moves those readers into a standalone MIT package so XISOSharp, companion apps and future consumers share one implementation. It is a packaging/API task, not new format support.

- [ ] **STF-01 · Create the `StfsSharp` repository and package skeleton**
  - Model on CCISharp/ZArchiveSharp: `StfsSharp/`, `StfsSharp.Tests/`, `StfsSharp.Benchmarks/`, optional `StfsSharp.Cli/`, `docs/`, `Directory.Build.props`, `global.json`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, 3-OS CI. Target the supported XISOSharp matrix (currently `net8.0`/`net9.0`/`net10.0`; see `LIB-07`), MinVer, `IsTrimmable`/`IsAotCompatible`, zero runtime dependencies, nupkg + snupkg.
  - **Done when:** the package builds, packs and CI is green on Windows/Linux/macOS.

- [ ] **STF-02 · Move XContent/title metadata into `StfsSharp`**
  - Move `XContentHeaderReader`, the `GodHeader` title fields, `StfsHeader`, `GodTitleInfo`, `StfsTitleInfo` and the package-kind enum into the `StfsSharp` public API.
  - **Done when:** XISOSharp `title-info` text/JSON output is unchanged and the metadata tests run against `StfsSharp`.

- [ ] **STF-03 · Move the read-only GOD/SVOD sector reader**
  - Move `GodHeader.TryReadVolume`, `GodVolumeInfo`, the SVOD fragment roll-over and hash-table interleave behind `StfsFile`/`StfsStream` in `StfsSharp`.
  - **Done when:** a synthetic multi-`DataNNNN` GOD set reads byte-identically through `StfsSharp` and matches the current `GodBlockDevice` output.

- [ ] **STF-04 · Move the STFS package reader**
  - Move `StfsPackage`, `StfsEntry`, `StfsPackageInfo`, contiguous/hash-chain block lists, `ReadAllBytes`, `CopyTo` and `ExtractTo`.
  - Keep the current read-only scope: XBLA/XBLIG/Community Games packages; no saves, profiles or DLC packages.
  - **Done when:** listing/extraction matches the in-repo implementation on the synthetic fixture matrix.

- [ ] **STF-05 · Move STFS hash-tree validation**
  - Move `StfsPackage.Validate`, `StfsValidationReport` and `StfsValidationIssue`: per-block SHA-1, level-0/1/2 tables, top hash and header hash.
  - **Done when:** valid/corrupt fixture results are identical to today's `StfsPackage.Validate`.

- [ ] **STF-06 · Integrate `StfsSharp` without breaking XISOSharp's public API**
  - Replace the in-repo readers with `StfsSharp`; keep `XISOSharp.GodHeader`, `XISOSharp.BlockDevice.GodBlockDevice`, `XISOSharp.StfsPackage` and `XisoChecksum.ComputeStfsChecksum` as thin facades/adapters. Use type forwarding or wrappers for public types; remove them only in 2.0.0. Refresh `PublicApi.approved.txt`.
  - **Done when:** XISOSharp consumes the `StfsSharp` NuGet package, every read verb still passes, and `dotnet pack` package validation stays green.

- [ ] **STF-07 · Move tests, fixtures and fuzz coverage**
  - Move `GodHeaderTests`, `GodBlockDeviceTests`, `StfsHeaderTests` and `StfsPackageTests` into `StfsSharp`; keep the XISOSharp integration tests. Move/extend `SyntheticStfsPackage` and add a `StfsSharp` fuzz target for `Open`, `Validate`, `ReadAllBytes` and `ExtractTo`.
  - **Done when:** both repositories have green suites and the fuzzer covers the moved readers.

- [ ] **STF-08 · Publish `StfsSharp` and cut 1.7.0**
  - Write `docs/format.md`, `docs/api.md`, `docs/interop.md` and README; add `THIRD-PARTY-NOTICES.md`; tag/publish `StfsSharp`; bump the XISOSharp reference; tag and publish **XISOSharp 1.7.0** with release notes and six bundles.
  - **Done when:** `StfsSharp` is on NuGet, XISOSharp 1.7.0 is published, and the docs state that GOD/STFS readers are `StfsSharp`-backed.

---

## 4. P1 — CCISharp 1.1

> Separate package/repository. XISOSharp already consumes CCISharp 1.0.1; these items close the compression-quality, parallelism and interop gaps.

- [ ] **CCI-01 · Add the LZ4-HC level-12 encoder**
  - Add a managed LZ4-HC encoder (or an optional opt-in adapter) compatible with libCCI's `LZ4HC_CLEVEL_MAX`; keep the fast encoder as the default and keep the default package dependency-free. Expose it as `--cci-level 12` or `--hc`.
  - Do **not** reuse the global `--best` name: in XISOSharp it is already the Redump `--trim --wipe` alias.
  - **Done when:** HC output decodes in libCCI, is equal-or-smaller than the fast encoder on the synthetic corpus, and byte-level differences are documented.

- [ ] **CCI-02 · Add parallel CCI sector compression**
  - Add a bounded worker-pool compressor in CCISharp that writes sector payloads in order, so output is byte-identical to the sequential writer. Then wire single-file `compress --format cci --jobs 1..64` in XISOSharp and remove the "CCI is single-threaded" rejection.
  - **Done when:** `--jobs N` produces the same bytes as `--jobs 1`, and CLI contract tests cover the new path.

- [ ] **CCI-03 · Add optional multi-digit CCI part discovery**
  - Accept `game.10.cci` when no single-digit interpretation applies; keep writer output at `game.1.cci` … `game.9.cci` for reference compatibility.
  - **Done when:** the reader discovers contiguous parts `1..N`, the writer still emits single-digit parts, and the compatibility rule is documented.

- [ ] **CCI-04 · Add CCISharp fuzz and benchmarks**
  - Fuzz header/index/blob/split parsing; benchmark compress/decompress, random sector reads, split overhead and allocations.
  - **Done when:** the fuzzer finds no unhandled exception or OOM and benchmark results are documented.

- [ ] **CCI-05 · Publish CCISharp 1.1 and bump XISOSharp**
  - Add `docs/format.md`, `docs/api.md`, `docs/interop.md`, README and release notes; tag/publish CCISharp 1.1.0; update `XISOSharp.csproj` `PackageReference` and package validation.
  - **Done when:** CCISharp 1.1.0 is on NuGet and XISOSharp builds/tests against it.

- [ ] **CCI-06 · Verify interop against libCCI**
  - Use `cci_encode`/`cci_extract`/`cci_info` from `References/libCCI-0.1.0-1b0087e` as an oracle on synthetic images; optionally cross-check a Repackinator-produced fixture if one is available. Document any byte-level differences.
  - **Done when:** both directions round-trip and the interop report is in `docs/interop.md`.

- [ ] **CCI-07 · (P2, optional) Ship `CCISharp.Cli`**
  - Add `pack`, `unpack`, `info` and `validate` for interop and CI smoke tests.
  - **Done when:** the CLI packs/unpacks a synthetic image in CI.

---

## 5. P2 — Library hardening

- [ ] **LIB-01 · Walk local create sources through `IFilesystem`**
  - Add a source-filesystem abstraction to `XisoWriter.CreateXiso`/`PackFromDirectory` so a custom store can be packed without host paths; keep the current overloads additive.
  - **Done when:** a `MemoryFilesystem` source packs a byte-identical image and concurrent creates remain CWD-free.

- [ ] **LIB-02 · Make logging per-call**
  - Add an `XisoOptions`/`ILogger`-style sink to the read/write entry points; keep the static `Logger` as a compatibility facade.
  - **Done when:** two concurrent operations can log to separate sinks and existing static logging still works.

- [ ] **LIB-03 · Add a sparse/large-file output strategy**
  - Use OS sparse-file support for pre-sized multi-GB images, with a safe fallback. Cover the writer, CISO/CCI decompression and extraction.
  - **Done when:** a multi-GB sparse image writes without zero-filling on Windows/Linux and extracts byte-identically.

- [ ] **LIB-04 · Add property-based round-trip tests**
  - Add generators for pack/extract, split/join, CISO/CCI compress/decompress and ISO↔ZAR; use deterministic seeds.
  - **Done when:** the property suite runs in CI within budget and covers 0/1 sector, padding and compression-threshold edge sizes.

- [ ] **LIB-05 · Complete the end-to-end benchmark suite**
  - Add extract, CCI compress/decompress and large-image I/O to `XISOSharp.Benchmarks`; keep the current pack/list/hash/CISO cases.
  - **Done when:** `--filter "*EndToEnd*"` covers every major path and results are documented.

- [ ] **LIB-06 · Publish a benchmark comparison**
  - Compare against extract-xiso, xdvdfs and XboxKit with methodology, hardware and a reproducible script.
  - **Done when:** `docs/benchmarks.md` contains the numbers and commands.

- [ ] **LIB-07 · Resolve the post-EOL target-framework plan**
  - `net9.0` is EOL (2026-05-12) and `net8.0` reaches EOL on 2026-11-10. Decide before 1.7.0 whether 1.7.x keeps them compatibility-only and drops them in 2.0.0, or drops them sooner; update the csproj files, CI, `docs/support-policy.md` and READMEs.
  - **Done when:** the support policy and build matrix agree and CI remains green.

- [ ] **LIB-08 · Refresh the package-validation baseline**
  - Move `PackageValidationBaselineVersion` from `1.4.4` to the latest published stable (`1.5.0`/`1.6.0` as appropriate), review `CompatibilitySuppressions.xml`, and refresh `PublicApi.approved.txt`.
  - **Done when:** `dotnet pack` passes with the new baseline and no unreviewed suppressions remain.

---

## 6. P2 — Docs, fixtures and GUI

- [ ] **DOC-01 · Generate and publish the DocFX API reference**
  - Build the API site from the XML docs, publish it to GitHub Pages and add a link check.
  - **Done when:** the API pages are live and CI fails on broken links.

- [ ] **DOC-02 · Add a public synthetic fixture corpus**
  - Add a deterministic generator/regeneration script with no copyrighted game data. Cover XISO, split ISO, CISO, CCI, ZAR, GOD, STFS, XBE/XEX and corrupt cases.
  - **Done when:** the corpus is checked in or generated in CI, and BattleTests consume it.

- [ ] **DOC-03 · Refresh user docs after 1.7.0**
  - Update the feature matrix, `docs/known-limitations.md`, `docs/compression.md`, `docs/cli.md`, `docs/library.md`, READMEs and release notes for `StfsSharp` and CCISharp 1.1; fold in any 1.6.0 deltas missed by `REL-01`.
  - **Done when:** the docs match the shipped packages and no stale "future" claims remain.

- [ ] **GUI-01 · Add GOD/SVOD input to the GUI**
  - Open-dialog filters, drop routing, list/tree/extract, `decompress`/`convert`, and title metadata.
  - **Done when:** the GUI opens a synthetic GOD set and extracts it byte-identically to the CLI.

- [ ] **GUI-02 · Add STFS package input to the GUI**
  - Open/list/extract/validate XBLA/XBLIG packages and route `-V` validation.
  - **Done when:** the GUI lists and extracts a synthetic package and reports validation status.

- [ ] **GUI-03 · Add the Xbox 360 title panel**
  - Show `title-info` fields for XBE/XEX/GOD/STFS, including XDBF/XSRC metadata.
  - **Done when:** the panel matches CLI `title-info --json` on synthetic fixtures.

- [ ] **GUI-04 · Add the XBE image preview**
  - Preview the logo, title image and save image (PNG/DDS) from `XbeAssets`.
  - **Done when:** all three asset types render and a missing asset is a clean message.

- [ ] **GUI-05 · (P2, optional) Expose parallel jobs controls**
  - Add `--jobs` controls for extraction and compression where supported; keep CCI disabled until CCISharp 1.1 lands.
  - **Done when:** the GUI command builders and self-test cover the new flags.

---

## 7. P3 — Distribution and supply chain

- [ ] **DST-01 · NuGet author signing**
  - Sign nupkg/snupkg when a repository certificate secret is configured.
  - **Done when:** a signed package verifies and the unsigned fallback still works.

- [ ] **DST-02 · Windows Authenticode signing + macOS notarization**
  - Gate on repository secrets; sign the CLI/GUI bundles and notarize the macOS builds.
  - **Done when:** `signtool verify` and `spctl --assess` succeed on the release assets.

- [ ] **DST-03 · Package-manager manifests**
  - Generate winget, scoop and Homebrew manifests from the release assets.
  - **Done when:** each manifest installs the latest release in CI or a manual smoke test.

- [ ] **DST-04 · Shell completions and man page**
  - Add PowerShell, bash, zsh and fish completions plus `xisosharp.1`; publish them as separate release assets or docs pages (the six bundle zips keep their exact five-file layout).
  - **Done when:** completions load and the man page renders.

- [ ] **DST-05 · Optional Docker image**
  - Publish `ghcr.io/purelogiccode/xisosharp` with the CLI and document usage.
  - **Done when:** the image runs `XISOSharp -v` and a smoke conversion.

---

## 8. P3 — Cross-repo consumers

- [ ] **XRP-01 · Upgrade `SimpleXisoDrive`**
  - Move to XISOSharp 1.7/`StfsSharp`, adopt `XisoImage`, and verify GOD mount through `XisoImageSource.OpenRead`.
  - **Done when:** a GOD set mounts and lists through the new facade.

- [ ] **XRP-02 · Upgrade `XISOStudio`**
  - Move to XISOSharp 1.7, adopt `XisoImage`, add GOD to the open dialog, the 360 title panel and the XBE image preview; keep CHD/ZIP support in the app.
  - **Done when:** the new inputs open and preview without app-specific format-parsing code.

- [ ] **XRP-03 · Add ZArchiveSharp fuzz and benchmarks**
  - Add a ZAR decode fuzz harness and benchmark coverage.
  - **Done when:** the fuzzer runs in CI and the benchmark baseline is documented.

- [ ] **XRP-04 · (P3, optional) Other consumers**
  - Let `VideoGameFileSystemParser` and other apps consume `CCISharp`/`StfsSharp`; document the package usage.
  - **Done when:** at least one external consumer builds against the new packages.

---

## 9. Implemented baseline (do not re-implement)

| Area | Already implemented |
|---|---|
| **Core XISO** | `XisoImage` facade, create, list, tree, extract, rewrite, validate, repair, salvage, checksum/hash, sector layout, explicit layout packing, pathological-TOC traversal, pre-sized extraction/writer I/O |
| **Containers** | one detection layer for ISO, split ISO, CISO v1/v2, split CISO, CCI/CCIM, split CCI, ZAR, read-only GOD/SVOD, read-only XBLA/XBLIG STFS |
| **Metadata** | XBE/XEX headers, XDBF/XSRC 360 metadata, GOD/STFS XContent title metadata, XBE logo/title/save images |
| **CLI** | extract/list/tree/checksum/hash/verify-hashes/validate/title-info/xbe-image, compress/decompress/convert/split, batch, JSON, `--layout`, `--jobs` (CISO/extraction; CCI pending), `--name-policy`, `--delete-source` |
| **1.6.0 scope** | A1 XDBF, A2 XBE assets, A3 FATX cap, A4 `--delete-source`, A5 GOD/STFS title naming, read-only GOD input, STFS package listing/extraction/validation, parallel CISO compression, parallel extraction, safe-extract policy, CWD-free writer/reader |
| **Quality** | 3-OS CI, format gate, InspectCode, AOT smoke, mutation fuzz, battle-interop, coverage floors, SBOM and build provenance |

---

## 10. Reference map

All paths are under `References/`. These are **behaviour/format oracles** unless noted otherwise.

| Reference | License | Used for | Remaining work |
|---|---|---|---|
| `extract-xiso-build-202609111233` | BSD-style | Core XISO/CLI parity and oracle | None |
| `libCCI-0.1.0-1b0087e` | GPL-2.0-or-later | Normative CCI `FORMAT.md` and `cci_*` oracle | CCISharp 1.1 HC/multi-digit work (`CCI-*`) |
| `Xbox360Convert-1.0.1` | GPL-3.0 (no own license file; links XboxToolkit) | GOD input, FATX split point, delete-source, GOD title naming | None — A3/A4/A5 implemented in 1.6.0; reference only |
| `XboxKit-0.7` | Permissive (upstream) | Redump/ZAR parity baseline | None — XRD is deferred |
| `XboxToolkit-main` | GPL-3.0-only | GOD/STFS read, XEX XDBF metadata, XBE image assets | `StfsSharp` extraction (`STF-*`) and GUI surfaces (`GUI-*`) |
| `xdvdfs-0.8.3` | MIT | CISO/split/checksum/CLI parity baseline | None |
| `XDVDFS-TR-0.8.4-TR` | MIT | `xdvdfs-cci` (covered by CCISharp); `xdvdfs-fsd`/`xdvdfs-web` reviewed | None in XISOSharp — NFS/WASM are out of scope |
| `XeCrypt-main` | MIT | XBE certificate/STFS hash-tree reference | None — signature verification is excluded; STFS hash tree is already implemented independently |

**Licensing guardrail:** `libCCI` (GPL-2.0-or-later), `XboxToolkit` (GPL-3.0-only) and `Xbox360Convert` (no license file; links GPL code) are **behaviour oracles only**. Do not copy, port or transliterate their code. Implement from `FORMAT.md` and observed on-disk layouts. `xdvdfs` is MIT; retain attribution if code is ported.

**Reference baselines**

- CCI: `References/libCCI-0.1.0-1b0087e/FORMAT.md` wins over any summary in this repo.
- CISO/checksum: `xdvdfs 0.8.3` is the byte-parity baseline.
- Core XISO/CLI: `extract-xiso` build `202609111233` is the parity/oracle baseline.
- GOD/STFS: `XboxToolkit`/`Xbox360Convert` are behaviour references only.

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
- Full suite green on the supported TFM matrix (currently `net8.0`/`net9.0`/`net10.0`; see `LIB-07`); `dotnet format` clean; InspectCode 0 warnings; trim/AOT analyzers clean.
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
```

---

## 14. Progress log

| Date | Item | Version | Notes |
|---|---|---|---|
| 2026-11-06 | Plan rewritten as a checkbox backlog | — | Baseline 1.5.0; 1.6.0 code-complete but untagged |
| 2026-11-06 | `REL-01` 1.6.0 documentation finalized | 1.6.0 | Release notes/WhatsNew dated; README and support docs refreshed |
| 2026-11-06 | `REL-02` verification complete | 1.6.0 | Build 0/0; tests 2098/1 + 2162/1; format clean; InspectCode 0; pack OK; AOT, fuzz and synthetic battle passed |
| 2026-11-06 | InspectCode/NU1903 cleanup | — | 44 InspectCode warnings fixed; ILCompiler pack pinned to 10.0.14 |
| | | | |
