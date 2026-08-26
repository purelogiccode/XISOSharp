# XISOSharp — Documentation

![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)
[![CI](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml)

**XISOSharp** is a pure C# implementation of [extract-xiso](https://github.com/XboxDev/extract-xiso)
v2.7.1 — the tool and library for creating, extracting, listing, auditing, and rewriting
Xbox ISO (XISO / XDVDFS) disc images. It is a direct, byte-identical port of the original
C codebase into idiomatic managed C# — no native dependencies, no P/Invoke.

This documentation set is the repository wiki. It covers the CLI, the .NET library API,
the XISO on-disk format, Xbox disc formats (XGD1/XGD2/XGD3, Redump images), and the
development workflow.

---

## Table of contents

### User guide

| Page | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Installation, first extract/create/list, requirements |
| [CLI Reference](cli.md) | Every command, flag, mode, exit code, and batch behavior |
| [Validation](validation.md) | `validate` command and `--validate*` flags |
| [Redump & Disc Layouts](redump-workflows.md) | XGD offsets, video partitions, `--skip-sectors` / `--prepend-sectors` |
| [XISO Format](xiso-format.md) | On-disk format: header, directory entries, AVL tree, ECMA-119 |
| [FAQ](faq.md) | Frequently asked questions |

### Library API reference

| Page | What it covers |
|---|---|
| [Library Overview](library.md) | Architecture, packages, error handling, quick samples |
| [XisoReader](api-xisoreader.md) | Read/extract/list/audit/hash APIs |
| [XisoWriter](api-xisowriter.md) | Create/rewrite APIs |
| [Utilities & Types](api-utilities.md) | `Logger`, `GlobMatcher`, `AvlTree`, `Constants`, records, enums, exceptions |

### Development

| Page | What it covers |
|---|---|
| [Building](building.md) | SDK requirements, build/publish, CI pipeline |
| [Testing](testing.md) | Test suite, TestData, reference-tool comparison, benchmarks |
| [Contributing](contributing.md) | Contribution workflow, code style, PR guidelines |
| [Troubleshooting](troubleshooting.md) | Common errors and how to resolve them |

---

## Feature highlights

- **Create** XISO images from a directory, with glob-based file/folder exclusion (`-X`)
- **Extract** full images, or single files/directories with `--copy-out`
- **List** (`-l`), recursive **tree** (`-t`), volume **info** (`-i`), **hash** (`--md5` / `--sha256`)
- **Rewrite** images into an optimized AVL layout (`-r`)
- **Audit** (`-V`) deep integrity checks: header, tree, sector bounds, cycle detection
- **Validate** post-conversion correctness (`validate` command, `--validate*` flags)
- Automatic detection of **GLOBAL, XGD2, XGD3, and XGD1** disc formats
- **Redump support**: `--skip-sectors` / `--prepend-sectors` for images with a video partition
- Automatic `.xbe` **media-enable patching** (Boyer–Moore pattern scan)
- Async APIs, progress callbacks, cancellation tokens for UI embedding
- Multi-targets **.NET 8, .NET 9, and .NET 10**; strong-named; trim/AOT compatible

## Quick start

```bash
# Extract an ISO to a directory
extract-xiso -d output_dir game.iso

# Create an ISO from a directory
extract-xiso -c source_dir

# List contents
extract-xiso -l game.iso
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
| `XISOSharp.Cli` | Command-line tool `extract-xiso` (byte-compatible with the original) |
| `XISOSharp.Tests` | xUnit test suite |
| `XISOSharp.Benchmarks` | BenchmarkDotNet benchmarks (AVL tree, Boyer–Moore, sector math) |
| `XISOSharpTester` | WPF GUI for batch regression testing against the reference C tool |
| `References/` | Reference sources: `extract-xiso.c` v2.7.1, xdvdfs 0.8.3, XboxKit 0.6 |
| `TestData/` | Fixtures used by tests and the output-comparison scripts |

## License

MIT — see [LICENSE](../LICENSE).
