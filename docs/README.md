# XISOSharp — Documentation

![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)
[![CI](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml)

**XISOSharp** is a pure C# implementation of [extract-xiso](https://github.com/XboxDev/extract-xiso)
v2.7.1 — the tool and library for creating, extracting, listing, auditing, and rewriting
Xbox ISO (XISO / XDVDFS) disc images. It is a direct, byte-identical port of the original
C codebase into idiomatic managed C# — no native dependencies, no P/Invoke.

This documentation set is the repository wiki **and** the GitHub Pages site. It covers the CLI, the .NET library API,
the XISO on-disk format, Xbox disc formats (XGD1/XGD2/XGD3/Hybrid, Redump images), archival workflows, and `xdvdfs` parity.
The site is rendered with **Docsify** — a fixed **left sidebar** is provided by [`_sidebar.md`](_sidebar.md) and [`index.html`](index.html) (Pages) and mirrored as [`wiki/_Sidebar.md`](../wiki/_Sidebar.md) (Wiki).

---

## Table of contents

> **Left menu:** the sidebar (`_sidebar.md`) is the canonical navigation. The table below mirrors it for plain GitHub rendering.

### User guide

| Page | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Installation, first extract/create/list, requirements |
| [CLI Reference](cli.md) | Every command, flag, mode, exit code, and batch behavior (incl. archival & xdvdfs verbs) |
| [Validation](validation.md) | `validate` command and `--validate*` flags |
| [Redump & Disc Layouts](redump-workflows.md) | XGD offsets (incl. hybrid `0x89D80000`), video partitions, `--skip-sectors` / `--prepend-sectors` |
| [Archival Workflows](archival.md) | Redump lossless pipeline: `--video` / `--random` / `--seed` / `--wipe` / `--trim` / `--petrify` / `--update` / `--zar` / `--all` / `--best` / `--compress` / `rebuild` / `--security-sectors` |
| [Build-Image & Image-Spec](xdvdfs-compat.md#build-image) | Ordered `wax` remapping (`host/**:image/{1}`, `!negation`, `{n}` captures), `--dry-run`, `xdvdfs.toml` |
| [Compression (CISO)](compression.md) | `compress`/`decompress` (CISO DEFLATE v1 + LZ4 v2, `align` 0/1/2), `BlockDevice` stack |
| [Checksums](xdvdfs-compat.md#checksum) | SHA3-256 image checksum (`checksum`) vs per-file MD5/SHA-256 (`--md5`/`--sha256`) |
| [XISO Format](xiso-format.md) | On-disk format: header, directory entries, AVL tree, ECMA-119, empty-dir `0x0000` sentinel |
| [FAQ](faq.md) | Frequently asked questions |

### Library API reference

| Page | What it covers |
|---|---|
| [Library Overview](library.md) | Architecture, packages, error handling, quick samples |
| [XisoReader](api-xisoreader.md) | Read/extract/list/audit/hash/checksum APIs (+ `IBlockDevice` overloads) |
| [XisoWriter](api-xisowriter.md) | Create/rewrite + remap (`build-image`) APIs |
| [Utilities & Types](api-utilities.md) | `Logger`, `GlobMatcher`/`WaxGlob`, `AvlTree`, `Constants`, `XisoRanges`, `XboxPrng`, `SecuritySectors`, `BlockDevice`, records, enums, exceptions |
| [xdvdfs Compatibility](xdvdfs-compat.md) | Block-device abstraction, CISO parity, remap semantics |

### Development

| Page | What it covers |
|---|---|
| [Building](building.md) | SDK requirements, build/publish, CI pipeline |
| [Testing](testing.md) | Test suite, TestData, reference-tool comparison, benchmarks, BattleTests |
| [Contributing](contributing.md) | Contribution workflow, code style, PR guidelines |
| [Troubleshooting](troubleshooting.md) | Common errors and how to resolve them |

---

## Feature highlights

- **Create** XISO images from a directory, with glob-based exclusion (`-X` via `GlobMatcher`/`WaxGlob`)
- **Extract** full images, or single files/directories with `--copy-out` / `--unpack` (auto `llCompat`)
- **List** (`-l`), recursive **tree** (`-t`), volume **info** (`-i`), **hash** (`--md5` / `--sha256`), per-image **SHA3-256** `checksum`
- **Rewrite** images into an optimized AVL layout (`-r`) + **validate** (`validate` / `--validate*`)
- **Audit** (`-V`) deep integrity: header (5 offsets), tag `31337`, tree cycles, sector bounds, reserved `0x48`, empty `0x0000`
- **Disc coverage** — RAW `0x0`, **GLOBAL/XGD2** `0x0FD90000`, **XGD3** `0x02080000`, **Hybrid** `0x89D80000`, **XGD1** `0x18300000` (native) + arbitrary `--skip-sectors`/`--prepend-sectors`
- **Redump archival (XboxKit parity):** `--video`, `--random` (filler), `--seed` (XGD1 PRNG brute-force), `--wipe`, `--trim`, `--petrify` (skeleton + SHA-1), `--update` (XGD3 `su…`), `--zar` (ZArchive/zstd), `--security-sectors <sectors.txt>`, aliases `--all`/`--best`/`--compress`, verb `rebuild` for lossless Redump ↔ XISO
- **xdvdfs parity:** `build-image` ordered `host/**:image/{0|1}` (`!` + `{n}` captures, `xdvdfs.toml`, `--dry-run`), `image-spec from`, **CISO** `compress`/`decompress` (DEFLATE v1 `0x80000000` + LZ4 v2, `align` 0/1/2) with `CisoBlockDevice` random-access, `IBlockDevice` (`File`/`Memory`/`Offset`/`Ciso`)
- Automatic `.xbe` **media-enable patching** (Boyer–Moore `E8 CA FD FF FF 85 C0 7D → EB`)
- Async APIs, `IProgress<ProgressInfo>` (`FileCount`/`DirCount`/`DirAdded`/`FileAdded`/`FinishedPacking`), `CancellationToken` throughout
- Multi-targets **.NET 8, .NET 9, and .NET 10**; strong-named; trim/AOT compatible; **left sidebar** on Pages & Wiki

## Quick start

```bash
# Extract an ISO to a directory
XISOSharp.Cli -d output_dir game.iso

# Create an ISO from a directory
XISOSharp.Cli -c source_dir

# List contents
XISOSharp.Cli -l game.iso
```

```csharp
// Library usage
using XISOSharp;

int result = XisoReader.Extract("game.iso", "output_directory", llCompat: false);
int result = XisoWriter.CreateXiso("source_directory", "output_directory",
    inRoot: null, sourceStream: null, out _, inName: "game.iso", progressCallback: null);
```

See [Getting Started](getting-started.md) for details.

## Project layout

| Project | Description |
|---|---|
| `XISOSharp.Core` | Class library (NuGet package `XISOSharp`) — complete read/write engine |
| `XISOSharp.Cli` | Command-line tool `XISOSharp` (extract-xiso-compatible flags) |
| `XISOSharp.Tests` | xUnit test suite |
| `XISOSharp.Benchmarks` | BenchmarkDotNet benchmarks (AVL tree, Boyer–Moore, sector math) |
| `XISOSharpTester` | WPF GUI for batch regression testing against the reference C tool |
| `References/` | Reference sources: `extract-xiso.c` v2.7.1, xdvdfs 0.8.3, XboxKit 0.7 |
| `TestData/` | Fixtures used by tests and the output-comparison scripts |

## License

MIT — see [LICENSE](../LICENSE).
