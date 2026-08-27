# FAQ

Frequently asked questions about XISOSharp, XISO images, and Xbox disc formats.

## General

**What is XISO / XDVDFS?**

XISO (XDVDFS — Xbox DVD Filesystem) is the filesystem used on Xbox game discs. It is a
simple, sector-based filesystem with an AVL-tree directory structure — see
[XISO Format](xiso-format.md).

**Is XISOSharp a wrapper around the C tool?**

No. It is a **pure C# port** of `extract-xiso.c` v2.7.1 — no native code, no P/Invoke.
The C sources under `References/` are used only for cross-checking during development.

**Is the output byte-identical to the original extract-xiso?**

Yes, by design. The port reproduces the C tool's algorithms exactly (including AVL
rebalancing and directory layout), and the test suite plus `Verify-Output.ps1`
continuously verify SHA-256 equality of outputs.

**Which disc formats are supported?**

RAW (bare XISO), GLOBAL, XGD2, XGD3, and XGD1 — detected automatically. Redump images
with a video partition are supported via `--skip-sectors` / `--prepend-sectors` (see
[Redump & Disc Layouts](redump-workflows.md)).

## Usage

**Why does `extract-xiso` say a Redump image is invalid?**

Redump dumps contain a video partition followed by the game partition. If the game
partition sits at a nonstandard offset, auto-detection can fail. Find the offset and
use `--skip-sectors`:

```bash
extract-xiso --skip-sectors 129824 -d ./out dump.iso    # XGD2-style offset
```

**Do I need `--skip-sectors` for every Redump image?**

Only when the game partition is not at one of the probed offsets (RAW, `0x0FD90000`,
`0x02080000`, `0x18300000`). Many dumps are detected automatically.

**What does `-s` do exactly?**

`-s` skips `$SystemUpdate` entries. During **create** it is implemented as the exclude
pattern `**/$SystemUpdate/**` (matches entries named exactly `$SystemUpdate`); during
**extract/rewrite** it filters `$SystemUpdate` paths while reading (substring match).

**How do I exclude files when creating an image?**

Use the repeatable `-X` flag with glob patterns:

```bash
extract-xiso -X "**/*.tmp" -X "**/node_modules/**" -c ./game_files
```

Patterns without a `**/` prefix match only at the root. See
[CLI Reference](cli.md#exclude-patterns).

**Why is my ISO "already optimized"?**

The image carries the optimized tag (`in!xiso!2.7.1 (01.11.14)` at offset 31337),
which means it was already rewritten by an extract-xiso-family tool. Rewrite mode skips
such images because there is nothing left to optimize.

**What is the 4 GB limit?**

The on-disk file-size field is a 32-bit unsigned integer, so individual files cannot
exceed 4,294,967,295 bytes (~4 GB). Larger files throw `XisoFileTooLargeException`.

## Library

**Which .NET versions does the library support?**

`net8.0`, `net9.0`, and `net10.0`. The CLI targets net10.0 but can be published
self-contained for any platform (see [Building](building.md#publish)).

**Is the library thread-safe?**

The engine uses thread-static scratch buffers, so independent operations on different
threads are safe. However, `CreateXiso` walks the file system using
`Directory.SetCurrentDirectory` internally — concurrent create operations in the same
process are not supported.

**How do I suppress console output from the library?**

```csharp
Logger.Quiet = true;      // suppress normal output
Logger.RealQuiet = true;  // suppress everything, including errors
```

You can also redirect `Logger.Out` / `Logger.Error` to any `TextWriter`.

**What does `llCompat` mean?**

It selects the directory right-offset calculation: `true` for legacy
linked-list-compatible images (not written by extract-xiso-family tools), `false` for
optimized images. The CLI chooses automatically via the optimized tag.

## Format

**Are filenames UTF-8?**

No. XDVDFS filenames are **byte strings** (Windows-1252 / Latin-1). XISOSharp reads and
writes them with a Latin-1 encoding so every byte round-trips; names containing `.`,
`..`, `/`, or `\` are rejected on read.

**What is the optimized tag for?**

It marks an image as having the optimized AVL directory layout so readers can skip the
legacy linked-list compatibility path. It is a 24-byte marker at byte offset 31337.

**Why are empty directories one sector of 0xFF?**

The format has no explicit empty-directory marker; an empty directory table is written
as a single 0xFF-filled sector with a table size of 2048 bytes, and readers detect the
leading `0xFFFF` as "end of table".

**Does XISOSharp support split ISOs (.iso.001, …)?**

No — split-file images are out of scope. XISOSharp works with single-file images only.

## Misc

**Where is the C# code different from the C original?**

Only additively: new CLI flags (`-t`, `-i`, `-V`, `-o`, `--copy-out`, `--md5`,
`--sha256`, `-X`, `--skip-sectors`, `--prepend-sectors`, `validate`/`--validate*`),
async APIs, progress callbacks, and a public library surface. Core algorithms are
unchanged.

**Where can I report bugs or request features?**

Open an issue on the repository. Feature ideas are tracked in
[`ProposedEnhancements.md`](../ProposedEnhancements.md).

See also: [Getting Started](getting-started.md) · [CLI Reference](cli.md) ·
[Troubleshooting](troubleshooting.md)
