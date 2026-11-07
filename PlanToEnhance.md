# XISOSharp — Missing Features

- **Updated:** 2026-11-07
- **Latest release:** XISOSharp **1.8.0** (2026-11-07)
- **Scope:** only features that are **not implemented yet**. Completed releases,
  the implemented baseline, the reference-oracle map, the upstream issue/PR scan
  and the edit/progress history live in
  [Project History](docs/project-history.md). Release-signing verification is
  also tracked there and in [Building](docs/building.md#distribution-artifacts).
- **Next release:** **2.0.0** — remove the `StfsSharp` compatibility facades,
  keeping `net8.0`/`net9.0`/`net10.0`.
- **Next feature release:** **2.1.0** — beyond-reference features.

> **How to use this file:** change `- [ ]` to `- [x]` when an item meets its
> **Done when** line, then move its summary to
> [Project History](docs/project-history.md) and record the date/version there.

**Priority key**

| Priority | Meaning |
|---|---|
| **P1** | Next release / main feature track |
| **P2** | Quality and user-facing polish |
| **P3** | Optional or cross-repo follow-up |

## At a glance

| Priority | Workstream | Target | Items |
|---|---|---|---:|
| **P1** | 2.0.0 facade removal | 2.0.0 | 2 (`FAC-01`/`FAC-02`) |
| **P1** | Beyond-reference features | 2.1.0 | 13 (`BRF-01`…`BRF-13`) |
| **P3** | Optional cross-repo consumer | later | 1 (`XRP-04`) |
| | **Total** | | **16** |

**Suggested order**

1. `FAC-01`, `FAC-02` — clear the 2.0.0 public-API debt first.
2. `BRF-01`, `BRF-02`, `BRF-03` — the three capability gaps.
3. `BRF-04`…`BRF-08` and `BRF-10`…`BRF-13` — the rest of 2.1.0.
4. `BRF-09` — optional 2.2.0 collection feature.
5. `XRP-04` — optional external consumer.

---

## 1. P1 — 2.0.0: remove the `StfsSharp` facades (keep all TFMs)

> Owner decision 2026-11-07: **do not drop `net8.0`/`net9.0`**. The 2.0.0
> release is a public-API cleanup only; the build/test matrix stays
> `net8.0`/`net9.0`/`net10.0`.

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

## 2. P1 — 2.1.0: beyond-reference features

> These are not parity gaps: XISOSharp already covers the working feature set of
> `extract-xiso` build `202609111233`, `xdvdfs 0.8.3` and `XboxKit 0.7`
> (see [Project History](docs/project-history.md)). This track adds
> capabilities none of those tools expose, plus the small number of real
> improvements found in the upstream issue/PR scan.

### Track summary

| Item | Priority | Release | Value |
|---|---|---|---|
| `BRF-01` Redump/XGD disc-layout inspector | P1 | 2.1.0 | Archival layout validation |
| `BRF-02` Skeleton overlay (inverse of petrify) | P1 | 2.1.0 | Completes loose-files → XISO → Redump |
| `BRF-03` Embedded `default.xex` title metadata for GOD/STFS | P1 | 2.1.0 | 360 title-panel completeness |
| `BRF-04` XEX/XDBF thumbnail export | P2 | 2.1.0 | Completes the image-preview family |
| `BRF-05` Per-file hash sidecar for any container | P2 | 2.1.0 | `verify-hashes` without petrify |
| `BRF-06` Parallel collection verbs | P2 | 2.1.0 | Large-library throughput |
| `BRF-07` ZAR compression-level flag | P2 | 2.1.0 | Archive-size control |
| `BRF-08` Loose `.xbe`/`.xex` inputs | P2 | 2.1.0 | Developer workflow |
| `BRF-09` Redump DAT verification | P3 | 2.2.0 (optional) | Collection validation |
| `BRF-10` Xbox 42-character filename validation | P2 | 2.1.0 | Hardware-compatible names |
| `BRF-11` Deterministic-pack alias + regression test | P3 | 2.1.0 | Reproducible builds |
| `BRF-12` Create-time symlink policy | P3 | 2.1.0 | Explicit link handling |
| `BRF-13` Actionable host-I/O diagnostics | P3 | 2.1.0 | Better errors |

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
  - Add `--xex-image <image> <internal-path> --type thumbnail -o <out.png>` (and the loose-file form once `BRF-08` lands); the XDBF `0x8000` entry is already captured in `XexTitleInfo.Thumbnail` (PNG payload in the synthetic fixtures).
  - Validate/pass through the PNG payload, add a clean error for an absent or unsupported thumbnail, and render the preview in the GUI title panel.
  - **Done when:** a synthetic XEX with a thumbnail exports a readable PNG, a missing thumbnail is a clean message, and the GUI preview matches the CLI export.

- [ ] **BRF-05 · Generate per-file hash sidecars for any container**
  - Add `XisoReader.ComputeFileHashSidecar(...)` and CLI `--file-hashes <image> [-o <file>] [--algorithm sha1|sha256|md5] [--json]`; the default output is the `<image stem>.hash` SHA-1 format consumed by `verify-hashes`.
  - Support ISO, split ISO, CISO, CCI, GOD, STFS and ZAR through the existing container view. Keep `--petrify` behavior unchanged. Extend `verify-hashes` to detect SHA-1/SHA-256/MD5 line lengths so non-default sidecars verify too.
  - **Done when:** the generated sidecar round-trips through `verify-hashes`, matches petrify's `.hash` for the same image, and container inputs produce identical hashes.

- [ ] **BRF-06 · Parallelize collection verbs**
  - Extend `--jobs 1..64` to multi-image `checksum`, `hash`, `-V`/audit and `--batch` extract/list/tree/rewrite; keep output ordering deterministic (including JSON arrays) and print the existing summaries at the end. Single-image semantics stay unchanged.
  - **Done when:** `--jobs N` output is byte-identical to `--jobs 1`, cancellation stops new work, and multi-image batch tests cover success, failure and mixed results.

- [ ] **BRF-07 · Expose the ZAR compression level**
  - Add `--zar-level <1..22>` to `--zar` and `convert --format zar`, wired to `ZstdCompressionOptions.FromLevel`/`IZarBlockCompressor`; keep level `6` and the fixed 64 KiB ZAR block size as defaults (the block size is format-defined).
  - **Done when:** levels `1`, `6` and `22` round-trip through ZArchiveSharp, the reference ZArchive reader opens the output, and default output stays byte-identical to today.

- [ ] **BRF-08 · Accept loose `.xbe`/`.xex` inputs**
  - Let `--xex-info`, `--xbe-info` and `--xbe-image` detect a standalone `.xex`/`.xbe` file and parse it directly, in addition to the current `<image> <internal-path>` form; the same parsers and JSON shape are used.
  - **Done when:** a loose executable reports exactly the same fields as the same file read from an ISO, and the existing image-plus-path form is unchanged.

- [ ] **BRF-09 · (P3, optional) Redump DAT verification**
  - Add `verify-dat <dat> <image>... [--json]`: parse a Redump XML DAT (`game/rom` size + MD5/SHA-1), match each image by hashes, and report verified/missing/ambiguous entries. Reuse the full-image digest path (`hash`).
  - This is a collection-management feature rather than an XISO operation; keep it opt-in and document that `docs/known-limitations.md` currently records DAT matching as not built in.
  - **Done when:** a synthetic DAT verifies matching images, reports mismatches, and JSON output is covered; no dependency on a specific DAT source is added.

- [ ] **BRF-10 · Enforce/report the Xbox 42-character filename limit**
  - XDVDFS stores a one-byte name length, but Xbox and Xbox 360 tooling expects each path component to stay within the classic 42-character limit; over-long names currently surface as upstream `Invalid argument` errors (extract-xiso #74). Validate components during create, `--copy-in`, `build-image`, `--petrify` output and `Apply`; warn by default with the offending path and add `--strict-names` to fail before writing.
  - Keep the Latin-1/Windows-1252 encoding contract, and turn non-encodable names, `+`/`!`/special characters and over-long components into actionable errors (extract-xiso #74/#81).
  - **Done when:** a >42-character component warns and `--strict-names` fails before writing; names with `+`, `!`, spaces and Windows-1252 characters round-trip; the error names the file and the limit.

- [ ] **BRF-11 · Lock deterministic packing**
  - XISOSharp already sorts source entries (`XisoWriter` uses `StringComparer.Ordinal`) and accepts `--file-time 0`; add a `--deterministic` alias that implies `--file-time 0`, document exactly which inputs affect the bytes, and add repeated-pack regression tests for directory, `build-image`, `--layout` and memory-source packing (xdvdfs #101).
  - **Done when:** packing the same source twice produces byte-identical output with `--deterministic`, repacking an image is stable, and the docs describe the guarantee.

- [ ] **BRF-12 · Add a create-time symlink policy**
  - Add `--symlink-mode follow|skip|reject` (default: follow file symlinks and skip directory reparse points as today) for `-c`, `--pack`, `build-image` and `Apply`; `reject` fails with the link path and `skip` logs it.
  - **Done when:** cyclic directory links terminate, each mode is tested, and the default output is unchanged (extract-xiso PR #83).

- [ ] **BRF-13 · Improve host-I/O diagnostics**
  - Map permission-denied, long-path, read-only destination, illegal-byte and name-collision failures to actionable messages naming the path, operation and likely fix; include the same detail in `--json` errors where applicable.
  - **Done when:** tests cover permission, long-path and >42-character names on Windows/Linux, and no raw `Invalid argument`/`Permission denied` reaches the user without context (extract-xiso #69/#74/#81).

### Suggested release split

| Release | Items |
|---|---|
| **2.1.0** | `BRF-01`…`BRF-08`, `BRF-10`…`BRF-13` |
| **2.2.0** (optional) | `BRF-09` |

---

## 3. P3 — Optional cross-repo consumer

- [ ] **XRP-04 · Other consumers**
  - Let `VideoGameFileSystemParser` and other apps consume `CCISharp`/`StfsSharp`; document the package usage.
  - **Done when:** at least one external consumer builds against the new packages.
