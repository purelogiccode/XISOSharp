# xdvdfs Compatibility (Packing / Compression Layer)

Modern xdvdfs is a `no_std`-capable filesystem library (`xdvdfs-core` traits + `xdvdfs-cli` subcommands: `wax`, `ciso`, `toml`). XISOSharp implements its packing & compression surface in pure managed C#.

- [Build-Image](#build-image)
- [Image-Spec](#image-spec)
- [Checksum](#checksum)
- [Block Device](#block-device)
- [Glob parity](#glob-parity)
- [API surface](#api-surface)

---

## Build-Image

**CLI:**

```
XISOSharp.Cli build-image [sourceDir] [output.iso] -f <xdvdfs.toml> -m "hostGlob:imagePath" [-O output] [-D|--dry-run]
XISOSharp.Cli build-image --dry-run -m "bin:/" -m "assets/**:/assets/{1}" ./src
```

**xdvdfs ref:** `xdvdfs-cli/src/cmd_build_image.rs` + `xdvdfs-core/src/write/fs.rs:RemapOverlayFilesystem` — ordered `wax` globs `host/** : image/{0|1}` with `!negation` + `{n}` captures + `--dry-run` + `xdvdfs.toml` `[map_rules]` (`README.md:72`).

**What it does:** ordered first-wins remapping of host filesystem → image filesystem. Rules evaluated in order; first match wins, `!` prefix negates (exclusion), unmapped → skip, duplicate image path → deterministic first-wins. Supports `*`/`**`/`?`/`[]`/`{a,b}` + whole-match `{0}` and per-group `{1..n}` captures + suffix re-add.

**Files:** `XISOSharp.Core/WaxGlob.cs` (capture engine), `XISOSharp.Core/RemapFilesystem.cs` (`RemapRule` + `DryRunRemap`/`BuildImage` → `XisoWriter.CreateFromRemapTree` with `IsRemap` flag skipping CWD), `XISOSharp.Cli/Program.cs:RunBuildImage`.

**`xdvdfs.toml`:**

```toml
[metadata]
output = "dist/image.iso"

[map_rules]
"bin" = "/"
"assets/**" = "/assets/{1}"
"!secret/**" = ""
```

Manual TOML subset parser (no `Tomlyn` dep) reads `[map_rules]` preserve-order, same semantics as xdvdfs `preserve_order` feature (`Cargo.toml:25`). CLI: `-f <toml>` loads rules, `-m "host:image"` appends, `-O` overrides `output`, `-D`/`--dry-run` calls `DryRunRemap` and prints host→image pairs without writing.

**Test vectors:** verified against `xdvdfs-0.8.3/tests/img.py::BuildImage` (capture semantics).

---

## Image-Spec

**CLI:** `XISOSharp.Cli image-spec from -O <out> -m "host:image" ... [specPath]` (stdout when `specPath` omitted, file when given).

**xdvdfs ref:** `image-spec from -O dist/image.iso -m "bin:/" -m "assets:/{0}" xdvdfs.toml` (`README.md:154`).

**API:** `RemapFilesystem.GenerateSpecText(IEnumerable<RemapRule> rules, string? outputPath)` + `WriteSpec` + `ParseSpecFile` (preserve-order). Serializes `[metadata] output` + `[map_rules]` in `xdvdfs.toml` preserve-order, matching xdvdfs `preserve_order`.

Round-trip: `GenerateSpecText` → `ParseSpecFile` → `BuildImage` yields identical `Remap` table (verified).

---

## Checksum

**CLI:**

```
XISOSharp.Cli checksum <image> [images...] [--silent]
XISOSharp.Cli --checksum <image> [--silent]            # flag form, multiple ISOs
```

Output: `hex tab path` (silent → hex only). Exit `0` on all, deterministic hex for `a.iso`/`b.iso` identical trees → `31e10d…`.

**xdvdfs ref:** `checksum [images...]` — `xdvdfs::checksum::checksum` **SHA3-256** over `BTreeMap<String,Node>` sorted `dir/file` paths + `hasher.update(path.bytes); hasher.update(data)` (`README.md:180`, `xdvdfs-core/Cargo.toml:27` `sha3`).

**Gap it closes:** XISOSharp previously only had per-file `MD5`/`SHA-256` (`ComputeFileHash` + `--md5`/`--sha256`) and `XisoValidator.ValidateConversion` (`--validate-checksums` `SHA-256`). No deterministic image-level checksum.

**Implementation:** `XISOSharp.Core/XisoChecksum.cs:13` `ComputeImageChecksum` / `ComputeImageChecksumHex` via `IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256)` (.NET 8+, streaming), `SortedDictionary<string,Node> StringComparer.Ordinal` (`/`-prefixed paths e.g. `/DIR/FILE` UTF-8 Latin1/`WINDOWS_1252` path bytes + streamed file data via `ReadData`). No `SHA3.Net`/`NSec` dep — BCL `SHA3_256` (FIPS) on .NET 10. Documented `NOT SHA256 of full image` parity warning.

**Also:** `--checksum` as CLI flag `Program.cs:533` + `checksumFlagMode` supports `checksum a.iso b.iso` and `--silent`.

---

## Block Device

**xdvdfs ref:** `xdvdfs-core/src/blockdev.rs` traits `read(offset,&mut buf)` / `write(offset,buf)` / `len()` + `OffsetWrapper` for CISO.

**XISOSharp:** `XISOSharp.Core/BlockDevice/`:

| Type | Role |
|---|---|
| `IBlockDevice : IDisposable` | `long Length {get;} int Read(long offset, Span<byte> buf); void Write(long offset, ReadOnlySpan<byte> buf);` |
| `FileBlockDevice` | wraps `FileStream` (BCL `FileStream` chunk `64*SectorSize`) |
| `MemoryBlockDevice` | in-memory `byte[]` — golden `.iso` blobs without temp files (mirrors `no_std` usage) |
| `OffsetBlockDevice` | `OffsetWrapper` parity — probes `[0, Global, Xgd3, Hybrid, Xgd1]` skip-sectors + CISO wrapper |
| `CisoBlockDevice` | CISO random-access (single-sector cache, `index[totalBlocks+1]` LE u32 offsets, DEFLATE/LZ4 on-demand decompress, single + split `*.N.cso` input) |

**Overloads:**

```csharp
public static (uint rootDirSector, uint rootDirSize, long discLseek) VerifyXiso(IBlockDevice dev, string isoName, int? skipSectors = null);
public static AuditResult AuditXiso(IBlockDevice dev);
```

`FileStream` overloads remain as thin wrappers delegating to `IBlockDevice`. Use `MemoryBlockDevice` for `406 → 675` unit tests (golden `.iso` without `TestData/output/source.iso` temp file). Mirrors `no_std` goal without targeting `no_std`.

---

## Glob parity

`GlobMatcher` already supports `* ? ** [] [!] \` + anchored vs `**/` + trailing `/`→`/**` (used for `-X`). xdvdfs `wax` adds `{0}`/`{n}` captures — **do not fork**: `GlobMatcher` now exposes `MatchWithGroups` / `TryMatch` → `GlobMatchResult { bool IsMatch; string[] Groups }` via `WaxGlob` delegation, keeping single matcher entry point. `WaxGlob` is the `wax` capture engine; `GlobMatcher` is façade for `-X` (non-capturing) and remap (capturing).

---

## API surface

| Type | Members |
|---|---|
| `WaxGlob` | `new(string pattern)`, `Regex Pattern`, `IsMatch(string path)`, `GetCapture(string path)`, `{0}`/`{n}` groups, `*`/`**`/`?`/`[]`/`{a,b}` |
| `RemapFilesystem` | `RemapRule { string HostGlob; string ImagePath; bool IsExclusion; }`, `DryRunRemap(sourceDir, rules)`, `BuildImage(sourceDir, outputIsoPath, rules, progress, ct)`, `GenerateSpecText`, `ParseSpecFile`, `WriteSpec` |
| `CisoWriter` / `CisoReader` | `CompressToCso`/`DecompressToIso` — see [Compression](compression.md) |
| `XisoChecksum` | `ComputeImageChecksum(string isoPath)`, `ComputeImageChecksumHex` (SHA3-256) |
| `BlockDevice/*` | `IBlockDevice`, `FileBlockDevice`, `MemoryBlockDevice`, `OffsetBlockDevice`, `CisoBlockDevice` |

See also: [CLI](cli.md) · [Compression](compression.md) · [XISO Format](xiso-format.md) · [Library](library.md)
