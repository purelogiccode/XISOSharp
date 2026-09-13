# What's New in 1.2.0 (since 1.1.0)

Release tag [`1.2.0`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1412 tests: 1408 passed, 4 pre-existing skips, 0 failed).

Additive release for VFS-consumer parity (SimpleXisoDrive/Dokan): the probe and
read stack accepts the rebuilt "sector-0" XISO layout (volume descriptor at the
very start of the image instead of partition sector 32), and the in-place
patcher, sector ranges, and ZAR packing understand it end-to-end.

## Library

### Rebuilt sector-0 XISO support

- `VerifyXiso` (stream and block-device overloads), `GetVolumeInfo`,
  `GetFileTimeRaw`/`SetFileTime`, and `OffsetBlockDevice.Probe` detect a
  descriptor at absolute offset 0. Such images report `DiscLseek = 0` and
  `DescriptorSector = 0`; sector numbers stay partition-relative, so
  `XisoExplorer`, directory listing, and bounded file reads work unchanged.
- `GetSectorLayout` marks the detected descriptor sector as used instead of
  hardcoding sector 32, and `PatchVolumeHeaderRoot` updates the active header
  base when a root table relocates — fixing a silent-corruption path where
  `CopyIn` on a sector-0 image could overwrite the descriptor or leave the
  volume header pointing at the old root table.
- `XisoRanges.GetXisoRanges`/`GetFileEntries` and `XisoZarchive.CreateZar`
  resolve the descriptor through the new `XisoReader.TryFindHeaderBase` helper
  (standard sector 32 first, then sector 0).
- `RebuildRedump` rejects sector-0 inputs with a clear error (repack to the
  standard layout first); `HasXisoMagic` recognizes them in the `.zar` sidecar
  path.

### Prompt cancellation in `XboxPrng.TryGetSeed`

- The brute-force seed search now checks the cancellation token inside each
  worker's candidate chunk, so a canceled search stops promptly instead of
  finishing millions of candidates first. This removes the coverage-instrumented
  stall that aborted the net8.0 CI test host under the blame-hang watchdog.

## Tests

- New `XisoRebuiltSector0Tests` (12 tests): probe/verify/volume-info parity,
  explorer listing + `OpenReadStream`, filetime read/write, block-device probe,
  sector layout used/free ranges, in-place add/replace/root-table-move, and
  `GetXisoRanges`/`CreateZar` round-trips on sector-0 images.

## Docs

- [`xiso-format.md`](docs/xiso-format.md) documents the rebuilt sector-0
  variant; [`api-xisoreader.md`](docs/api-xisoreader.md) covers the probe
  table, the `DescriptorSector` contract, and the Redump rebuild restriction;
  the root/library/CLI/Tests READMEs note the extra accepted layout.

## CI

- Test runs always publish a `.trx` artifact and skip hang-dump collection
  (`--blame-hang-dump-type none`); the seed-search stall that caused the
  recurring net8.0 failure is fixed.
