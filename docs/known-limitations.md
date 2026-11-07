# Known Limitations

XISOSharp aims to be the most complete **XISO/XDVDFS** library, not every
Xbox container. This page lists deliberate boundaries and current workarounds.

- [Scope](#scope)
- [Concurrency](#concurrency)
- [Windows paths and names](#windows-paths-and-names)
- [Archival metadata](#archival-metadata)
- [Compression](#compression)
- [Platforms and support](#platforms-and-support)
- [Distribution](#distribution)

## Scope

XISO/XDVDFS, Redump full-disc images, split ISO, CISO/CSO, CCI/CCIM, ZAR,
read-only GOD/SVOD sector input, GOD and single-file XBLA/XBLIG header title
metadata, XBLA/XBLIG STFS package listing/extraction and SHA-1 hash-tree
validation, Xbox 360 XDBF/XSRC title
metadata, XBE logo/title/save image extraction, and XBE/XEX **read-only**
metadata are in scope.

Deliberately out of scope:

- STFS/CON/LIVE/PIRS **writing**, package rebuild and re-signing. Read-only
  GOD/SVOD input is supported through `GodBlockDevice`, and single-file
  XBLA/XBLIG package listing/extraction is supported through `StfsPackage`;
  STFS writes stay out of scope. `StfsPackage.Validate` verifies the per-block
  SHA-1 hash tree (level-0/1/2 tables, top hash and header hash), but package
  signatures are not verified and the reader accepts XBLA/XBLIG/Community Game
  content types (not saves, profiles or DLC packages). The readers are
  StfsSharp-backed compatibility facades; the facade types are scheduled for
  removal in XISOSharp 2.0.0 so consumers can move to the `StfsSharp` package
  directly.
- CHD containers
- XEX decryption or XBE editing
- FUSE/Dokan mounting (see SimpleXisoDrive)
- direct optical-drive reading (implement `IBlockDevice`)
- Python bindings / a native ABI

## Concurrency

`XisoWriter.CreateXiso`/`PackFromDirectory` resolve a local source to absolute
host paths, keep progress counters per operation, and take no process-wide lock.
Concurrent creates run independently and never change the process working
directory. A caller-supplied `inRoot` without host paths is resolved once against
the caller's CWD, so it is CWD-free too.

`XisoReader.Extract`/`UnpackImage` also never change the process working
directory: extraction resolves entry paths through a `LocalFilesystem` rooted at
the output directory, and concurrent extracts into different destinations stay
independent. `XisoReader.UnpackImage(..., IFilesystem)` remains the way to target
a custom store.

## Windows paths and names

The CLI and GUI manifests opt into `longPathAware`, and extraction rejects or
sanitizes path separators, drive-relative colons, trailing dots/spaces and DOS
device names (`CON`, `NUL`, `COM1`…). `--name-policy reject|sanitize|skip` (and
`UnpackOptions.NamePolicy`) chooses fail-fast, collision-safe renaming, or
skipping for a recovery run. The OS must still have long paths enabled
(`LongPathsEnabled`) on Windows 10 1607+.

## Archival metadata

- **XRD** Redump metadata generation is not implemented. XboxKit 0.7's own XRD
  writer is a stub (zeroed hashes, TODO certificates), so there is no verified
  oracle to port.
- Redump **rebuild** needs the original video partition and filler/seed (or ZAR
  sidecar); it cannot invent missing disc bytes.
- `verify-hashes` validates the per-file SHA-1 `.hash` sidecar; Redump DAT
  matching is not built in (use the `hash` command for full-image digests).

## Compression

- CCI levels `0..9` use the fast LZ4 block encoder; levels `10..12` use
  CCISharp's managed LZ4-HC encoder (12 = highest effort). Output is spec-valid
  and interoperable; HC byte parity with libCCI/Repackinator is best-effort,
  not a guarantee.
- Single-file CCI `--jobs 1..64` compresses sectors in parallel with
  byte-identical output; batch `--jobs` still runs conversions in parallel.
- CISO v2 is byte-identical to modern `xdvdfs compress`; CISO v1 (DEFLATE) is
  supported for reading and writing but is legacy.
- In-place `--repair`, `--copy-in`, `--set-filetime`, `-r` (rewrite) and `split`
  refuse compressed, GOD, STFS and split containers; decompress/join/extract
  first.

## Platforms and support

- The library targets `net8.0`, `net9.0` and `net10.0`. .NET 9 is EOL (May
  2026) and .NET 8 reaches EOL in November 2026; `net10.0` is the LTS target
  going forward.
- The test suite runs on Windows, Linux and macOS. The WPF `XISOSharpTester`
  harness only runs on Windows (it builds everywhere).
- The per-file size field is 32-bit, so a single file cannot exceed ~4 GiB.
  Plain-ISO `split` defaults to `0xFF000000` (4 GiB − 16 MiB) so parts fit FATX;
  CISO/CCI keep `0xffbf6000` as their xdvdfs-compatible compression default.

## Distribution

- Release bundles and the NuGet library are built by CI with an SBOM and build
  provenance. Authenticode signing, macOS notarization and NuGet author signing
  require repository signing secrets and are not enabled by default.
- The CLI ships only through GitHub releases (no NuGet package). The release
  zips are framework-dependent (need the .NET 10 runtime); the self-contained
  single-file builds come from `publish-cli.ps1` / `publish-gui.ps1`.
