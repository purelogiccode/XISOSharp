# What's New in 1.4.1 (since 1.4.0)

Maintenance release: the `ZArchiveSharp` dependency moves 1.3.0 → 1.4.0 and the
ZArchiveSharp license notice is refreshed — the library, including the
`ZArchiveSharp.Pipeline` pack/extract layer XISOSharp uses, is now fully MIT.
No XISOSharp API, CLI, or ZAR wire-format changes; targets remain `net8.0` /
`net9.0` / `net10.0`; full suite green on all three (1427 tests: 1426 passed,
1 opt-in skip, 0 failed).

## Dependencies

- `ZArchiveSharp` 1.3.0 → 1.4.0. Upstream highlights: the pipeline layer
  (`ZarPipeline`, `ZarPackEngine`, `ProcessRunner`) is now an original
  MIT-licensed implementation, per-item batch isolation is stricter, and the
  process runner no longer stalls on an inherited stderr pipe.
- The public surface is unchanged, so XISOSharp compiles and behaves
  identically: ZAR outputs, CLI semantics, and exit codes are untouched, and
  the ZArchive reader/pack/extract tests stay green against 1.4.0.

## License

- `LICENSE` item 4 records ZArchiveSharp as fully MIT as of v1.4.0
  (Copyright (c) 2026 PureLogicCode.com; lead developer Peterson Fernandes),
  including `ZArchiveSharp.Pipeline`.
- The packaged library license (`XISOSharp/LICENSE`) is synced with the root
  `LICENSE`, so the NuGet package now carries the full third-party notices
  (extract-xiso BSD-4-clause, xdvdfs, XboxKit, ZArchiveSharp).

## Docs

- Root `README.md` and `docs/building.md` dependency references updated
  1.3.0 → 1.4.0.
