# Project History

This page records completed releases, the implemented baseline, the reference
oracles, the 2026-11-07 upstream issue/PR scan and the edit/progress log. It
preserves the completed-work sections from the retired missing-feature plan
(`PlanToEnhance.md`).

- [Release history](#release-history)
- [Implemented baseline](#implemented-baseline)
- [Distribution and cross-repo status](#distribution-and-cross-repo-status)
- [Reference oracles](#reference-oracles)
- [Upstream issue/PR scan (2026-11-07)](#upstream-issuepr-scan-2026-11-07)
- [Progress log](#progress-log)
- [Historical handoff (2026-11-07)](#historical-handoff-2026-11-07)

## Release history

| Version | Date | Highlights |
|---|---|---|
| `1.5.0` | 2026-11-01 | Last release before the current plan; the CLI `dotnet tool` was retired and distribution moved to RID bundles. |
| `1.6.0` | 2026-11-06 | XDBF/XSRC 360 metadata, XBE logo/title/save images, FATX split cap, `--delete-source`, GOD/STFS title naming, read-only GOD input, XBLA/XBLIG STFS listing/extraction/validation, parallel CISO compression and extraction, safe-extract policy and CWD-free reader/writer. |
| `StfsSharp 1.0.0` | 2026-11-07 | Standalone read-only GOD/SVOD and STFS package readers; XISOSharp consumes the NuGet package through temporary facades. |
| `1.7.0` | 2026-11-07 | `StfsSharp` extraction; unchanged XISOSharp public API; moved readers, tests, fixtures, fuzzer and benchmarks. |
| `CCISharp 1.1.0` | 2026-11-07 | LZ4-HC levels 10..12, parallel CCI compression, multi-digit split discovery, `CCISharp.Cli`, fuzz and benchmarks. |
| `1.8.0` | 2026-11-07 | LIB-01…08 (source filesystems, per-call logging, sparse outputs, property tests, benchmarks, support policy, validation baseline), DOC-01…03 (DocFX link gate, public corpus, docs refresh) and GUI-01…05 (Xbox 360 GUI). |
| `1.8.x` distribution | 2026-11-07 | DST-03/04/05: winget/Scoop/Homebrew manifests, shell completions + `xisosharp(1)`, GHCR container image. DST-01/02 signing pipelines implemented behind repository secrets. |
| `1.9.0` | 2026-11-08 | `FAC-01` facade removal plus the feature pass (`BRF-01`…`BRF-13`) and distribution track (`DST-01`…`DST-05`): disc/DAT/hash sidecars, skeleton overlay, parallel collection verbs, symlink/name/determinism policies, Xbox 360 metadata, package manifests, completions, man page, container image and docs refresh. Downstream consumers moved to direct `CCISharp`/`StfsSharp` references (`XRP-04`). |

Release notes and per-release detail live in [Release Notes](release-notes.md) and
[`WhatsNew.md`](https://github.com/purelogiccode/XISOSharp/blob/master/WhatsNew.md).

## 1.9.0 feature pass (2026-11-07)

Released as 1.9.0 together with the `FAC-01` cleanup and the distribution
track.

| Item | What landed | Tests |
|---|---|---|
| `BRF-01` | `XisoDiscInfo.Read` + `disc-info`/`--disc-info` and JSON: XGD type/wave, video/game partitions, PFI layer break, embedded/`sectors.txt` security ranges, Redump-size warnings | `XisoDiscInfoTests` (10) |
| `BRF-02` | `XisoSkeleton.Apply` + `apply-skeleton`/`--apply-skeleton`: skeleton extents + optional `.hash` verification; skeleton now also preserves the system preamble and 0xFF file-tail padding so Petrify→Apply is byte-identical | `XisoSkeletonApplyTests` (10) plus existing skeleton suite |
| `BRF-03` | `title-info` merges embedded `default.xex` XDBF/XSRC publisher/developer/genre/description/thumbnail into GOD and XBLA/XBLIG header metadata; `XisoReader.GetLooseXexTitleInfo`/`GetXexTitleInfo(byte[])` support | `XexContainerMetadataTests` |
| `BRF-04` | `xex-image --type thumbnail` exports the XDBF `0x8000` PNG payload, with a clean absent/unsupported error | `XexContainerMetadataTests` |
| `BRF-05` | `XisoReader.ComputeFileHashSidecar` + `file-hashes` for ISO/split/CISO/CCI/GOD/STFS/ZAR; `verify-hashes` detects MD5/SHA-1/SHA-256 line lengths and STFS/GOD/ZAR inputs | `XisoFileHashSidecarTests` (11) |
| `BRF-07` | `--zar-level 1..22` for `--zar`, `--compress` and `convert --format zar`; level 6 stays byte-identical | `ZarLevelTests` (9) |
| `BRF-08` | `--xex-info`, `--xbe-info` and `--xbe-image` accept a single loose `.xex`/`.xbe`; `XisoReader.GetLoose*` and `XbeAssets.GetLooseImage` | `XexContainerMetadataTests` |
| `BRF-10` | `XisoNameRules` + `--strict-names` for create/pack/copy-in/petrify/build-image/Apply; >42-character components warn by default, non-Latin-1 names fail with an actionable message, `+`/`!`/spaces/Latin-1 round-trip | `XisoNameRulesTests` (11) |
| `BRF-11` | `--deterministic` alias (`--file-time 0`), accepted for rewrite too; repeated pack/create/build-image/memory-source tests are byte-identical | `DeterministicPackTests` (7) |
| `BRF-06` | `--jobs 1..64` for multi-image `checksum`/`hash`, `-V`/audit and `--batch` extract/list/tree/rewrite; per-image log capture replays in input order, JSON arrays keep order, and scoped `Logger` counters keep per-image summaries exact | `ParallelCollectionTests` (6) |
| `BRF-09` | `XisoDat.Parse`/`Verify` + `verify-dat`: Redump-style XML DAT `game/rom` size/MD5/SHA-1/CRC32 matching, verified/ambiguous/not-in-DAT/missing reports, text and `--json` | `XisoDatTests` (5) |
| `BRF-12` | `--symlink-mode follow\|skip\|reject` for `-c`, `--pack`, `build-image` and `apply-skeleton`; cyclic directory links always terminate, and `reject` names the link before writing | `XisoSymlinkTests` (9) |
| `BRF-13` | `HostIoDiagnostics.Describe` maps permission-denied, long-path, sharing, existing-destination, illegal-name and full-disk failures to path/operation/fix messages used by extraction and CLI modes | `HostIoDiagnosticsTests` (6) |
| `FAC-01` | `XISOSharp.GodHeader`/`StfsHeader`/`StfsPackage`/`GodBlockDevice` and the `GodTitleInfo`/`GodVolumeInfo`/`StfsEntry`/`StfsPackageInfo`/`StfsPackageKind`/`StfsTitleInfo`/`StfsValidationIssue`/`StfsValidationReport` facades removed; library, CLI, GUI, fuzz and tests use `StfsSharp` directly; internal `GodVolumeBlockDevice` keeps `XisoImageSource` intact; package baseline moved to 1.8.0 with reviewed CP0001 suppressions | `StfsSharpIntegrationTests`, existing GOD/STFS suites, `PublicApiTests` |

Verification for the pass: suite **2312 passed / 1 opt-in skip** on `net10.0`
and **2239 passed / 1 opt-in skip** on `net8.0`/`net9.0`; public API baseline
refreshed; build, pack and InspectCode clean.

## Implemented baseline

Do not re-implement these; they are covered by the shipped library and tests.

| Area | Already implemented |
|---|---|
| **Core XISO** | `XisoImage` facade, create, list, tree, extract, rewrite, validate, repair, salvage, checksum/hash, sector layout, explicit layout packing, pathological-TOC traversal, pre-sized extraction/writer I/O |
| **Containers** | one detection layer for ISO, split ISO, CISO v1/v2, split CISO, CCI/CCIM, split CCI, ZAR, read-only GOD/SVOD, read-only XBLA/XBLIG STFS |
| **Metadata** | XBE/XEX headers, XDBF/XSRC 360 metadata, GOD/STFS XContent title metadata, XBE logo/title/save images |
| **CLI** | extract/list/tree/checksum/hash/verify-hashes/validate/title-info/xbe-image, compress/decompress/convert/split, batch, JSON, `--layout`, `--jobs` (CISO/CCI/extraction), `--name-policy`, `--delete-source` |
| **1.6.0 scope** | XDBF, XBE assets, FATX cap, `--delete-source`, GOD/STFS title naming, read-only GOD input, STFS package listing/extraction/validation, parallel CISO compression, parallel extraction, safe-extract policy, CWD-free writer/reader |
| **1.7.0 scope** | `StfsSharp` extraction: standalone GOD/SVOD + STFS readers, NuGet 1.0.0, XISOSharp facades with an unchanged public API, moved tests/fixtures/fuzzer/benchmarks |
| **CCISharp 1.1 scope** | LZ4-HC levels 10..12, parallel sector compression (`Jobs` 1..64, byte-identical), multi-digit split discovery, `CCISharp.Cli`, fuzz + benchmarks, `CCISharp 1.1.0` on NuGet |
| **Quality** | 3-OS CI, format gate, InspectCode, AOT smoke, mutation fuzz, battle-interop, coverage floors, SBOM and build provenance |
| **Distribution (1.8.x)** | winget/Scoop/Homebrew manifests + generator, PowerShell/bash/zsh/fish completions, `xisosharp(1)`, CLI container image (GHCR), optional NuGet/Authenticode/macOS signing pipelines |
| **Cross-repo (XRP)** | SimpleXisoDrive GOD mounts via `XisoImage` + real-corpus verification; XISO Studio GOD explorer + title panel + XBE preview; ZArchiveSharp mutation fuzzer + reader benchmarks + NUL-name hardening; SimpleXisoDrive/XISO Studio direct `CCISharp`/`StfsSharp` consumers |

## Distribution and cross-repo status

### Package installation

- `winget`, Scoop and Homebrew manifests are generated from the release assets by
  `scripts/update-packaging.ps1 -Version <version>`.
- Completions live in `docs/completions/`; the man page is `docs/man/xisosharp.1`.
- The CLI image is published to `ghcr.io/purelogiccode/xisosharp`.
- `DST-03`, `DST-04` and `DST-05` are complete and covered by CI smoke jobs.

### Signing (DST-01/DST-02)

The NuGet author-signing and Windows Authenticode / macOS codesign+notarization
pipelines are implemented but stay unverified until a release is produced with
the real repository secrets. Configuration is documented in
[Building](building.md#distribution-artifacts); the close-out checks are:

- `dotnet nuget verify --all` on the published `.nupkg` and `.snupkg`.
- `signtool verify /pa` on both Windows bundles.
- `spctl --assess --type execute` on the macOS binaries (notarization ticket).

| Item | Required repository configuration |
|---|---|
| NuGet author signing | secrets `NUGET_SIGNING_CERT` (base64 PFX), `NUGET_SIGNING_PASSWORD`; optional variable `NUGET_SIGNING_TIMESTAMPER` |
| Windows Authenticode | variable `ENABLE_WINDOWS_SIGNING=true`; secrets `WINDOWS_SIGNING_CERT`, `WINDOWS_SIGNING_PASSWORD` |
| macOS codesign | variable `ENABLE_MACOS_SIGNING=true`; secrets `MACOS_SIGNING_CERT`, `MACOS_SIGNING_PASSWORD`, `MACOS_SIGNING_IDENTITY` |
| macOS notarization | additionally secrets `MACOS_NOTARY_APPLE_ID`, `MACOS_NOTARY_TEAM_ID`, `MACOS_NOTARY_PASSWORD` |

### Cross-repo consumers

| Item | Status |
|---|---|
| `XRP-01` SimpleXisoDrive | Done: XISOSharp 1.8.0 + StfsSharp, `XisoImage` adoption, GOD header/`.data`/parent resolution, real Bobble Head GOD mount verified; 436 tests; CI run `42405304528` green. |
| `XRP-02` XISO Studio | Done: XISOSharp 1.8.0, GOD input, title metadata panel, XBE logo/title/save preview; 1,561 tests; CI run `42405778091` green. |
| `XRP-03` ZArchiveSharp | Done: mutation fuzzer (16,385 iterations / 0 failures), reader benchmarks, NUL-name hardening; 4,293 tests × 3 TFMs + 43 CLI; CI run `42405322199` green. |
| `XRP-04` Other consumers | Done 2026-11-07: SimpleXisoDrive (441 tests: 407 core + 34 Unix) uses `XISOSharp` for mounting/XDVDFS reads and `StfsSharp` directly for GOD header/`.data`/parent resolution; XISO Studio (1,582 tests) uses `XISOSharp` for reads, `StfsSharp` directly for GOD metadata and `CCISharp` directly for CCI validation. Both build 0 warnings, pass their suites, and the changed files pass the whitespace check. |

## Reference oracles

All paths are under `References/`. These are **behaviour/format oracles** unless
noted otherwise.

| Reference | License | Used for | Status |
|---|---|---|---|
| `extract-xiso-build-202609111233` | BSD-style | Core XISO/CLI parity and oracle | Complete |
| `libCCI-0.1.0-1b0087e` | GPL-2.0-or-later | Normative CCI `FORMAT.md` and `cci_*` oracle | Complete — CCISharp 1.1 HC/parallel/multi-digit work verified |
| `Xbox360Convert-1.0.1` | GPL-3.0 (no own license file; links XboxToolkit) | GOD input, FATX split point, delete-source, GOD title naming | Complete — 1.6.0 work; reference only |
| `XboxKit-0.7` | Permissive (upstream) | Redump/ZAR parity baseline | Complete except the deferred XRD metadata |
| `XboxToolkit-main` | GPL-3.0-only | GOD/STFS read, XEX XDBF metadata, XBE image assets | Used as a behaviour oracle; `StfsSharp` extraction done in 1.7.0 |
| `xdvdfs-0.8.3` | MIT | CISO/split/checksum/CLI parity baseline | Complete |
| `XDVDFS-TR-0.8.4-TR` | MIT | `xdvdfs-cci` (covered by CCISharp); `xdvdfs-fsd`/`xdvdfs-web` reviewed | NFS/WASM are deliberately out of scope |
| `XeCrypt-main` | MIT | XBE certificate/STFS hash-tree reference | Signature verification excluded; the STFS hash tree is implemented independently |

**Licensing guardrail:** `libCCI` (GPL-2.0-or-later), `XboxToolkit` (GPL-3.0-only)
and `Xbox360Convert` (no license file; links GPL code) are **behaviour oracles
only**. Do not copy, port or transliterate their code. Implement from
`FORMAT.md` and observed on-disk layouts. `xdvdfs` is MIT; retain attribution if
code is ported.

**Reference baselines**

- CCI: `References/libCCI-0.1.0-1b0087e/FORMAT.md` wins over any summary in this repo.
- CISO/checksum: `xdvdfs 0.8.3` is the byte-parity baseline.
- Core XISO/CLI: `extract-xiso` build `202609111233` is the parity/oracle baseline.
- GOD/STFS: `XboxToolkit`/`Xbox360Convert` are behaviour references only.

## Upstream issue/PR scan (2026-11-07)

The open and closed issue/PR lists of `XboxDev/extract-xiso` and
`antangelo/xdvdfs` were reviewed on 2026-11-07. Conclusion: there is no large
missing feature. Nearly every feature request is already implemented in
XISOSharp; the remainder are platform/build questions, web/daemon/mounting work
that belongs to companion projects, or explicit non-goals. The few actionable
items became `BRF-11`…`BRF-13` in the (now retired) missing-feature plan.

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
| xdvdfs #121 show XGD offset in info | `-i` prints `Disc Offset` and `Disc Format`; deeper PFI/SS layout in `BRF-01` |
| xdvdfs #101 predictable pack output | sorted entries + `--file-time 0`; planned determinism guarantee |
| xdvdfs #17 / extract-xiso #68 media patching | implemented in create/rewrite and covered by [Media-patch integration verification](testing.md#media-patch-integration-verification) |
| extract-xiso #74/#81 long or special names | planned filename-limit validation |
| extract-xiso PR #83 symlinks | current follow-files/skip-directories policy; planned explicit modes |
| extract-xiso #69 permission/long-path errors | planned actionable diagnostics |

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

## Progress log

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
| 2026-11-07 | Work paused | 1.8.x | Distribution track implemented and locally validated; CI/install-smoke and real signing certs remain. |
| 2026-11-07 | First distribution CI run | 1.8.x | Docker + packaging-windows green; man-page `grep -q` SIGPIPE, winget uninstall cleanup and Homebrew local-cask restriction fixed; follow-up run pending |
| 2026-11-07 | Distribution CI green | 1.8.x | Scoop `bucket add` local-path failure fixed by installing the checked-in manifest; CI run `42396783434` fully green (build/test 3 OS, package installs, Docker, packaging, docs, AOT, fuzz) |
| 2026-11-07 | `XRP-01` SimpleXisoDrive | — | XISOSharp 1.8.0 + StfsSharp; `XisoImage` adoption; GOD header/`.data`/parent resolution; real Bobble Head GOD mount verified; 436 tests; format + InspectCode clean; CI run `42405304528` green |
| 2026-11-07 | `XRP-02` XISOStudio | — | XISOSharp 1.8.0; GOD input (file/folder pickers); title metadata panel; XBE logo/title/save preview; 1,561 tests; real GOD metadata verified. First CI flagged four solution-wide InspectCode suggestions; fixed in `0c43f02`; follow-up CI run `42405778091` green including InspectCode and all six publishes |
| 2026-11-07 | `XRP-03` ZArchiveSharp | — | `ZArchiveSharp.Fuzz` + CI fuzz-smoke (16,385 iterations/0 failures); `ZarReaderBenchmarks` + documented baseline; fuzzer found and fixed NUL-name extraction exception; 4,293 tests × 3 TFMs + 43 CLI; CI run `42405322199` green including fuzz-smoke and pack |
| 2026-11-07 | Plan retired | — | All tracked items complete; `PlanToEnhance.md` deleted and README/docs/sidebar references removed. |
| 2026-11-07 | `XRP-04` downstream packages | — | Hybrid consumer split: SimpleXisoDrive uses `XISOSharp` for mounting/XDVDFS and `StfsSharp` directly for GOD resolution; XISO Studio uses `XISOSharp` for reads, `StfsSharp` directly for GOD metadata and `CCISharp` directly for CCI validation; CCI added to SimpleXisoDrive path resolution and XISO Studio Explorer/integrity tests; build 0 warnings, suites green, changed files whitespace-clean; package usage documented in `docs/library.md` |
| 2026-11-07 | Documentation refresh | 1.8.x | XISOSharp README/library now list SimpleXisoDrive and XISO Studio as downstream `XisoImage` consumers; SimpleXisoDrive Architecture, Virtual File System and Contributing now describe `XisoImage` instead of the old `XisoExplorer`; plan/handoff finalized; XISOSharp code CI green (`f3c8a98`) and the docs-only follow-up is committed |
| 2026-11-07 | Plan extended with the 1.9.0 tracks | — | Owner decision: keep `net8.0`/`net9.0`/`net10.0`; added `FAC-01`/`FAC-02` (facade removal only) and `BRF-01`…`BRF-10` (beyond-reference features, later extended to `BRF-13` by the upstream scan after the already-covered media-patch and compare items were dropped). Planned, not started. |
| 2026-11-07 | Upstream issue/PR scan | — | Reviewed the `extract-xiso` and `xdvdfs` issue/PR lists; most requests are already covered; added the remaining scan items (now `BRF-11`…`BRF-13`) and the scan notes above. No product code changed. |
| 2026-11-07 | `DOC-04` companion README links | 1.8.x | Root, library (NuGet readme), CLI and `docs/library.md` now link SimpleXisoDrive, XISO Studio, StfsSharp, CCISharp and ZArchiveSharp. |
| 2026-11-07 | Plan reduced to missing features | — | Completed releases, the implemented baseline, the reference map, the upstream scan and the edit history moved to this page; `PlanToEnhance.md` now lists only `FAC-01`/`FAC-02`, `BRF-01`…`BRF-13` and the optional `XRP-04`. |
| 2026-11-07 | `BRF-01` disc-layout inspector | 1.9.0-dev | `XisoDiscInfo.Read` + `disc-info` human/JSON; synthetic XGD1/XGD2/XGD3/hybrid, truncation and container tests |
| 2026-11-07 | `BRF-02` skeleton overlay | 1.9.0-dev | `XisoSkeleton.Apply` + CLI; Petrify→Apply byte-identical; missing/extra/size/hash errors; skeleton preserves preamble and pad tails |
| 2026-11-07 | `BRF-03`/`BRF-04` embedded XEX metadata + thumbnail | 1.9.0-dev | GOD/STFS title-info merge; loose/byte[] XEX parsers; `xex-image --type thumbnail` PNG export |
| 2026-11-07 | `BRF-05` per-file hash sidecars | 1.9.0-dev | `ComputeFileHashSidecar` + `file-hashes`; ISO/CISO/CCI/GOD/STFS/ZAR parity; mixed SHA-1/SHA-256/MD5 verify |
| 2026-11-07 | `BRF-07` ZAR level + `BRF-08` loose executables | 1.9.0-dev | `--zar-level` 1..22; loose `.xex`/`.xbe` info/image forms share the image parsers |
| 2026-11-07 | `BRF-10` name policy + `BRF-11` determinism | 1.9.0-dev | `XisoNameRules`/`--strict-names`; `--deterministic` alias accepted for create/pack/rewrite/build-image |
| 2026-11-07 | 1.9.0 feature-pass verification | 1.9.0-dev | Suite 2312/1 (net10) + 2239/1 (net8/net9); public API baseline refreshed; build/pack/InspectCode 0 warnings |
| 2026-11-07 | `BRF-06` parallel collection verbs | 1.9.0-dev | `--jobs 1..64` for checksum/hash/audit/batch extract/list/tree/rewrite; ordered captured output and JSON; scoped logger counters |
| 2026-11-07 | `BRF-09` Redump DAT verification | 1.9.0-dev | `XisoDat.Parse`/`Verify` + `verify-dat`; size/MD5/SHA-1/CRC32 matching; verified/ambiguous/not-in-DAT/missing text and JSON |
| 2026-11-07 | `BRF-12` symlink policy + `BRF-13` host-I/O diagnostics | 1.9.0-dev | `--symlink-mode follow\|skip\|reject` for create/pack/build-image/Apply; `HostIoDiagnostics` actionable messages wired into extraction and CLI modes |
| 2026-11-07 | `FAC-01` facade removal | 1.9.0-dev | Removed the StfsSharp compatibility facades and models; library/CLI/GUI/fuzz/tests use StfsSharp directly; internal GOD adapter; baseline 1.8.0 + reviewed CP0001 suppressions |
| 2026-11-07 | `FAC-02` TFM matrix documented | 1.9.0-prep | Support policy, known limitations, README and release notes now describe `net8.0`/`net9.0` as supported compatibility targets; all three TFMs remain in the build/test matrix. |
| 2026-11-08 | 1.9.0 review fixes | 1.9.0 | Deterministic rewrite FILETIME overloads, size-only DAT entry handling, Unix `EWOULDBLOCK` mapping and default petrify name warnings; regression tests added. |
| 2026-11-08 | 1.9.0 released | 1.9.0 | Tag `bca71d0`; CI green with NuGet publish; six RID zips on GitHub; winget/Scoop/Homebrew manifests regenerated; DocFX/site/docs links green. |

## Historical handoff (2026-11-07)

This section is a snapshot from 2026-11-07, before the plan was reduced to the
missing-feature list. That plan has since been retired; all tracked items are
complete.

**State at pause:** the distribution track is green in CI, and the cross-repo
`XRP-01`/`XRP-02`/`XRP-03` upgrades are implemented, committed and pushed. Their
CI is green (SimpleXisoDrive `42405304528`, XISO Studio `42405778091`,
ZArchiveSharp `42405322199`). The XISOSharp repository itself has no
product-source changes from that session beyond the plan/CI fix.

### Completed locally

- XISOSharp: CI run `42396783434` fully green (3-OS build/test, package installs,
  packaging, Docker, docs, AOT, fuzz, InspectCode).
- SimpleXisoDrive: XISOSharp 1.8.0 + StfsSharp 1.0.0, `XisoImage` adoption, GOD
  mounts; build 0/0, format clean, InspectCode clean, **436** tests (402 core +
  34 Unix).
- XISO Studio: XISOSharp 1.8.0, GOD input, title metadata panel, XBE preview;
  build 0/0, InspectCode 0 warnings, **1,561** tests.
- ZArchiveSharp: `ZArchiveSharp.Fuzz` (16,385 seeded iterations / 0 failures),
  `ZarReaderBenchmarks` baseline, NUL-name extraction hardening; build 0/0,
  **4,293** tests on each of `net8.0`/`net9.0`/`net10.0` + 43 CLI tests.
- Real-corpus verification: Bobble Head GOD set mounts in SimpleXisoDrive (13
  root entries, XEX2 reads, `image.iso`) and opens in XISO Studio with
  `Title=Bobble Head`, `0x58480813`, `default.xex`.
- Documentation refresh: XISOSharp README/library list the companion projects;
  SimpleXisoDrive Architecture/Virtual File System/Contributing describe the
  `XisoImage` path; all companion docs/WhatsNew entries are current.

### First actions after the pause

1. The three companion repositories are committed and pushed; their CI is green
   (including ZArchiveSharp's 120-second `fuzz-smoke` and XISO Studio's
   solution-wide InspectCode).
2. The next product work was **1.9.0** (`FAC-01`/`FAC-02` plus
   `BRF-01`…`BRF-13` and `DST-01`…`DST-05`): the `StfsSharp` facades were
   removed while **`net8.0`/`net9.0`/`net10.0`** stay supported. **XRP-04** is
   complete and **DST-01/DST-02** await real signing certificates.
3. Do not delete anything under `bin/Release`, `publish*` or the release zips.

### Signing checklist

See [Distribution and cross-repo status](#distribution-and-cross-repo-status)
for the current configuration table and close-out commands.
