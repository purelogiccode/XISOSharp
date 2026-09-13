# What's New in 1.1.0 (since 1.0.2)

Release tag [`1.1.0`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1400 tests: 1396 passed, 4 pre-existing skips, 0 failed).

Additive release for VFS-consumer parity (SimpleXisoDrive/Dokan): the public API
gains bounded in-place file reads, a keep-open explorer mode, descriptor
creation-time/sector surfacing, `FileShare` control, and Windows attribute
mapping — no behavior change to existing APIs.

## Library

### In-place file reads — no extraction

- `XisoExplorer.OpenReadStream(string)` / `OpenReadStream(ExplorerNode)` return
  read-only, seekable streams over a file's data extent inside the image. Reads
  are clamped to the entry's `FileSize` (not the image length), so a corrupt TOC
  cannot leak the next file's sectors; seeking at/past the end reads 0 bytes per
  `Stream` conventions; CISO/split-CISO images work transparently.
- `XisoReader.ReadFileBytes(isoPath, internalPath, Span<byte>, fileOffset)` and
  the stream overload are one-shot conveniences over the same bounds; the stream
  overload leaves the caller's stream open, and missing paths/directories throw
  `InvalidDataException`.
- A stateless explorer's read stream owns the image handle (dispose it); a
  keep-open explorer's read streams stay valid until the explorer is disposed.

### Keep-open explorer mode

- `XisoExplorerOptions { KeepOpen, Share }` and
  `XisoExplorer(string, XisoExplorerOptions)`: `KeepOpen` holds one image stream
  for the explorer's lifetime (metadata calls use the held stream), serializes
  operations with an internal lock, and `Dispose()` closes the stream — the
  mount/VFS shape that avoids re-opening the image for every lookup.
  `KeepOpen: false` stays byte-for-byte identical to 1.0.2.

### FileShare control on image opens

- `XisoReader.OpenImageStream(path, FileShare)` and `XisoExplorerOptions.Share`:
  plain-ISO streams pass the share mode through (default `FileShare.Read`,
  unchanged), and CISO inputs thread it to the container file.
  `FileShare.ReadWrite` lets a mounted image coexist with AV scanners or sync
  clients that open the `.iso` for write.

### Volume descriptor timestamps and sector

- `VolumeInfo` gains `CreationTime` (`DateTimeOffset?`, `null` when invalid;
  raw 0 maps to 1601-01-01), `FileTimeRaw`, and `DescriptorSector`
  (partition-relative sector `32` for every supported layout — the partition
  shift is `DiscLseek`; −1 when invalid) — all populated by the same probe
  `GetVolumeInfo` already performs.
- `XisoAttributes.ToWindowsFileAttributes(byte)` maps the raw XDVDFS attribute
  byte to `System.IO.FileAttributes` (`ReadOnly` always set;
  `Directory`/`Hidden`/`System`/`Archive` OR'd in; `Normal` when nothing else
  applies; reserved bits masked), matching SimpleXisoDrive's expectations.

### Fixes

- `XisoExplorer.OpenReadStream(string)` and the keep-open constructor no longer
  leak the image handle when a lookup or probe throws.
- `BoundedSubStream` rejects seeks before the window start with `IOException`
  (per `Stream` conventions) instead of reading preceding image bytes.
- `ReadFileBytes` validates the path even when the buffer is empty.
- `GetVolumeInfo` skips probe candidates past EOF instead of aborting, so a
  trimmed XGD3 image (smaller than the global candidate offset) is detected.

## GUI (Avalonia)

- Modern dark theme: left navigation rail with an accent pill, card layout,
  green accent palette, rounded inputs and log console, and a dark native title
  bar on Windows.
- New **ZAR** tab: packs an ISO/XISO/Redump image into a `.zar` (ZArchive/zstd,
  loadable in Xenia canary) with overwrite/skip/auto-rename collision policies;
  drop routing and tab-order shortcuts account for the new tab.

## Dependencies

- `ZArchiveSharp` NuGet dependency updated 1.0.2 → 1.3.0 (mount-friendly reader
  API, specific open-failure reasons; additive, no wire-format changes).
