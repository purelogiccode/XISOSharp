# What's New in 1.0.2 (since 1.0.1)

Release tag [`1.0.2`](https://github.com/purelogiccode/XISOSharp/releases).
Targets remain `net8.0` / `net9.0` / `net10.0`; full suite green on all three
(1338 tests: 1334 passed, 4 pre-existing skips, 0 failed).

## CLI

### XISOSharp-branded version banner

`-v` (and the usage header) now prints
`XISOSharp v<version> for <win|linux|macos|cross-platform> - https://github.com/purelogiccode/XISOSharp`
instead of the `extract-xiso` compatibility line. The version comes from the
MinVer build stamp (build metadata trimmed). Image compatibility is unaffected:
the optimized tag (`in!xiso!2.7.1 (01.11.14)`) and the `Constants.ExisoVersion`
provenance constant are unchanged, and the extract-xiso acknowledgement still
ships in `LICENSE`.

### Automatic update check

Interactive launches compare the running version with the latest GitHub release
(24-hour cache under `%LocalAppData%/XISOSharp/update-check.json`) and print an
`[UPDATE]` notice — with the matching `release_<version>_<rid>.zip` asset —
offering to open the release page. Skipped for `-q`/`-Q`/`-v` runs and test
hosts; disable with `XISO_NO_UPDATE_CHECK=1`.

### New inspection verbs

`--sector-layout` (volume summary, per-file extents, used/free ranges),
`--ranges` (system/bone vs file sector ranges), and `--is-optimized`
(optimized-tag probe, `--skip-sectors` aware).

### Double-click pause

An interactive no-argument launch prints usage and waits for a keypress
(`XISO_NO_PAUSE=1` opts out); scripts, pipes, and test hosts are never blocked.

### Warning+ bug reports

The CLI, GUI, and Tester forward Warning-and-above Serilog events to the
bug-report API (environment, error, and exception sections);
`XISO_DISABLE_BUGREPORT=1` opts out.

## GUI (Avalonia)

- Single-file bundle fix: `ToolLocator` now probes the directory of the real
  executable (`Environment.ProcessPath`) so the shipped `XISOSharp` CLI is
  found next to the app instead of reporting "CLI not found".
- Legacy `XISOSharp.Cli` file-name fallback removed; resolution is
  `XISOSharp(.exe)` only.
- Status bar and log show a branded product label (`XISOSharp <version>`) read
  from the CLI binary metadata.
- Embedded app icon; framework-dependent publish builds and stages the CLI
  beside the GUI automatically.

## Library

- `ToolLocator` is the shared resolver (override → app dir → process dir →
  `PATH`) with a bounded, tree-killed `-v` probe; new `ToolLocatorTests`.
- `Constants.Banner` reports the XISOSharp product version.

## Repository & docs

- GitHub Pages publishing + wiki-sync workflows; full third-party notices in
  `LICENSE`; distributed bundles include `LICENSE` and `README.md`.
- Docs and readmes updated for the branded banner, new inspection verbs,
  update checks, GUI bundle layout, and the shared tool locator.
