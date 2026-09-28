# What's New in 1.4.0 (since 1.3.0)

Release tag [`1.4.0`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1427 tests: 1426 passed, 1 opt-in skip, 0 failed).

Integrity-audit release: `-V` no longer fails a structurally sound image just
because the optimized tag is missing — raw (unconverted) dumps pass with
`Optimized: no` — and `.cso` images are audited through their decompressed
view. The library exposes the same control via a `requireOptimizedTag` overload
and reports tag presence through the new `AuditResult.IsOptimized`.

## Features

### Integrity-first audit (`-V`)

- `-V` prints `Optimized: yes/no` and keeps `Result: PASS` for raw/unconverted
  images whose tag is absent; broken trees, out-of-bounds sectors, cycles,
  invalid attributes, and invalid names still fail the audit. `--is-optimized`
  remains the strict tag-only probe, and `--repair` (re)writes a missing tag.
- `XisoReader.AuditXiso(string isoPath, bool requireOptimizedTag)` and the
  `IBlockDevice` overload expose the requirement to library callers. The 1-arg
  overloads keep the original strict behavior (tag required).
- `AuditResult.IsOptimized` reports whether the tag was found for every audit
  result. Empty (zero-root) images are probed too, so a present tag is never
  reported as `no`.

### CISO audit fix

- `AuditXiso(string)` opened a plain `FileStream`, so a `.cso` image was
  audited as compressed bytes (bogus tree/sector issues). It now opens through
  `OpenImageStream`, matching `GetVolumeInfo`, and audits the decompressed
  view; `AuditXiso_Cso_MatchesPlainIso` locks plain-vs-CISO parity.

## Tests

- 1427 tests across `net8.0` / `net9.0` / `net10.0`: 1426 passed, 1 opt-in skip
  (`XISO_UPDATE_FIXTURE=1` fixture validator), 0 failed. New since 1.3.0:
  `CliAuditTests` (3, end-to-end `-V` runs) and `AuditXisoTests` integrity/tag/
  CISO cases (4), plus device empty-root tag coverage.

## Dependencies

- `coverlet.collector` 10.0.1 → 10.1.0 (Tests). No library dependency changes.

## Docs

- Root, library, CLI, Tests, and docs READMEs plus `docs/cli.md`,
  `docs/api-xisoreader.md`, and `docs/troubleshooting.md` describe the
  integrity audit and tag reporting; suite counts refreshed. The release
  bundles now also include `WhatsNew.md` alongside `README.md` and `LICENSE`.
