# Known Limitations

XISOSharp aims to be the most complete **XISO/XDVDFS** library, not every
Xbox container. This page lists deliberate boundaries and current workarounds.

- [Scope](#scope)
- [Writer concurrency](#writer-concurrency)
- [Windows paths and names](#windows-paths-and-names)
- [Archival metadata](#archival-metadata)
- [Compression](#compression)
- [Platforms and support](#platforms-and-support)
- [Distribution](#distribution)

## Scope

XISO/XDVDFS, Redump full-disc images, split ISO, CISO/CSO, CCI/CCIM, ZAR,
GOD and single-file XBLA/XBLIG header title metadata, Xbox 360 XDBF/XSRC title
metadata, XBE logo/title/save image extraction, and XBE/XEX **read-only**
metadata are in scope.

Deliberately out of scope:

- GOD/STFS/CON (Xbox 360 Games-on-Demand) sector input. `title-info` reads a
  GOD header or its `.data` folder, and single-file XBLA/XBLIG STFS package
  headers, for title name/ID/disc metadata; full read-only GOD input through
  `StfsSharp` is planned for 1.7.0, and full XBLA/XBLIG package listing remains
  future work.
- CHD containers
- XEX decryption or XBE editing
- FUSE/Dokan mounting (see SimpleXisoDrive)
- direct optical-drive reading (implement `IBlockDevice`)
- Python bindings / a native ABI

## Writer concurrency

`XisoWriter.CreateXiso`/`PackFromDirectory` still use the process working
directory internally and serialize concurrent creates with a process-wide lock
(TODO #22). Parallel creation in one process is therefore serialized; separate
processes are fine. `XisoReader` extraction has an `IFilesystem` path that does
not change the CWD.

## Windows paths and names

The CLI and GUI manifests opt into `longPathAware`, and extraction rejects or
sanitizes path separators, drive-relative colons, trailing dots/spaces and DOS
device names (`CON`, `NUL`, `COM1`…). The OS must still have long paths enabled
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

- CCI uses the fast LZ4 block encoder by default. Output is spec-valid and
  interoperable, but not byte-identical to libCCI/Repackinator's LZ4-HC level 12
  output. CCISharp 1.1 will add an HC encoder.
- CISO v2 is byte-identical to modern `xdvdfs compress`; CISO v1 (DEFLATE) is
  supported for reading and writing but is legacy.
- In-place `--repair`, `--copy-in` and `--set-filetime` refuse compressed and
  split containers; decompress/join first.

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
