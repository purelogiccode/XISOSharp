# Building

This page covers SDK requirements, build and publish commands, and the CI pipeline.

- [Requirements](#requirements)
- [Build](#build)
- [Test](#test)
- [Publish](#publish)
- [NuGet packaging](#nuget-packaging)
- [CI pipeline](#ci-pipeline)

## Requirements

- **.NET SDK 10.0.301 or newer within major 10** — pinned in [`global.json`](../global.json)
  (`rollForward: latestMinor`): newer 10.x feature bands are accepted, but 11+
  is not, and anything older than 10.0.301 (e.g. a VS-shipped 10.0.1xx) fails
  before build.
- Any OS supported by .NET (the CI builds on Windows, Linux, and macOS).
- No native toolchain is required — this is a pure managed codebase.

## Build

```bash
git clone https://github.com/purelogiccode/XISOSharp.git
cd XISOSharp

# Restore + build everything (Debug)
dotnet build CSharp_XISOSharp.sln

# Release build
dotnet build CSharp_XISOSharp.sln -c Release

# Build just the CLI or the library
dotnet build XISOSharp.Cli -c Release
dotnet build XISOSharp -c Release
```

The solution contains:

| Project | Target(s) | Output |
|---|---|---|
| `XISOSharp` | net8.0, net9.0, net10.0 | `XISOSharp.dll` + NuGet package |
| `XISOSharp.Cli` | net8.0, net9.0, net10.0 (ships: net10.0) | `XISOSharp` executable |
| `XISOSharp.Gui` | net10.0 | Avalonia GUI (shippable, shells out to the CLI) |
| `XISOSharp.Tests` | net8.0, net9.0, net10.0 | xUnit test assembly (engine + CLI) |
| `ZArchiveSharp` (`ZArchiveSharp.Tests` / `ZArchiveSharp.Benchmarks` / `ZArchiveSharp.Cli`) | — | Lives in the sibling `../CSharp_ZArchiveSharp` repo (own solution); consumed here as NuGet package `ZArchiveSharp` 1.0.2 (`ZArchiveSharp.Cli` is a `dotnet tool`, not a library reference) |
| `XISOSharp.Benchmarks` | net10.0 | BenchmarkDotNet harness |
| `XISOSharpTester` | net10.0-windows | WPF regression-test rig (Windows-only, runs from build output) |

> [!NOTE]
> Two GUI stacks, different jobs: `XISOSharp.Gui` (Avalonia, cross-platform) is
> the shippable GUI — it shells out to the CLI and publishes per-RID via
> `publish-gui.ps1`. `XISOSharpTester` (WPF, Windows-only) is the in-process
> regression rig for engine + reference-tool interop (see [Testing](testing.md#the-gui-regression-tester));
> it runs from its build output (`dotnet run --project XISOSharpTester`) and is
> intentionally not published single-file. Both (plus the battle harness) share
> the single `XISOSharp.ProcessRunner` + `XISOSharp.ToolLocator`
> (override → sibling → `PATH` + `-v` probe) in the core library — no per-app runners.

> [!NOTE]
> The Core project packs a NuGet package only via an explicit
> `dotnet pack XISOSharp -c Release -o ./artifacts` (CI `pack` job, releases) —
> plain `dotnet build` stays clean (`GeneratePackageOnBuild` is off).

## Test

```bash
dotnet test XISOSharp.Tests
dotnet test XISOSharp.Tests -c Release
```

The suite is xUnit-based and runs sequentially (the engine changes the current
directory during create/extract, so tests are in a `Sequential` collection). With code
coverage:

```bash
dotnet test XISOSharp.Tests --collect:"XPlat Code Coverage"
```

See [Testing](testing.md) for the full picture, including cross-checking against the
reference C tool.

## Publish

The CLI can be published as a **self-contained single-file** executable — no .NET
runtime required on the target machine:

```bash
# Windows x64
dotnet publish XISOSharp.Cli -c Release -f net10.0 -r win-x64 --self-contained

# Linux x64
dotnet publish XISOSharp.Cli -c Release -f net10.0 -r linux-x64 --self-contained

# macOS (Intel / Apple Silicon)
dotnet publish XISOSharp.Cli -c Release -f net10.0 -r osx-x64 --self-contained
dotnet publish XISOSharp.Cli -c Release -f net10.0 -r osx-arm64 --self-contained
```

(`-f net10.0` is required: the CLI multi-targets net8/9/10, but only net10.0
ships. `PublishSingleFile` needs no `-p:` override — it defaults on in the
csproj. `publish-cli.ps1` / `publish-gui.ps1` wrap this loop for all RIDs.)

Supported runtime identifiers (single source of truth: `XISOSharp.Cli.csproj` /
`XISOSharp.Gui.csproj` `RuntimeIdentifiers`, matching the `publish-*.ps1` defaults):
`win-x86; win-x64; win-arm64; linux-x64; linux-arm64; osx-x64; osx-arm64` for the
CLI (XboxKit parity, incl. the 32-bit `win-x86`); the GUI ships the six 64-bit
RIDs only.

Output lands in `XISOSharp.Cli/bin/Release/net10.0/<rid>/publish/` as a single
`XISOSharp` (or `XISOSharp.exe`) binary (renamed from the `XISOSharp.Cli`
assembly name by the csproj `RenamePublishedExeToXisoSharp` target).

## NuGet packaging

The library package (`XISOSharp`) is configured in `XISOSharp.csproj`:

| Setting | Value |
|---|---|
| Version | Derived from git tags via **MinVer** (`v2.7.1` / `2.7.1`) |
| Symbols | `snupkg` with SourceLink (`EmbedAllSources`, `EmbedUntrackedSources`; library only — apps carry embedded/portable PDBs) |
| Reproducibility | `Deterministic` + `ContinuousIntegrationBuild` on CI (repo-wide, incl. CLI/GUI binaries) |
| Signing | Strong-named (`XISOSharp.snk`, committed — contributors sign with no setup) |
| Trimming/AOT | `IsTrimmable`, AOT-compatible library surface (no in-repo AOT publish) |
| API validation | `EnablePackageValidation` + strict mode (library only — apps ship as binaries, not NuGet) |
| Metadata | MIT license, README, icon, tags (`xiso`, `xbox`, `extract-xiso`, …) |

Pack manually:

```bash
dotnet pack XISOSharp -c Release -o ./artifacts
```

## CI pipeline

`.github/workflows/ci.yml` runs on every push/PR to `master`/`main` and on version
tags (`v*` or bare `[0-9]*`, both accepted by MinVer and the publish gate):

1. **build-and-test** — matrix over `ubuntu-latest`, `windows-latest`,
   `macos-latest`: restore → build (Release) → test the `XISOSharp.Tests`
   project with XPlat code coverage;
   the coverage report is uploaded as an artifact.
2. **pack** (after tests) — `dotnet pack` the core project and upload the nupkgs.
3. **publish** (only on version tags, environment `nuget`) — push the nupkgs to
   nuget.org using the `NUGET_API_KEY` secret.

The CI also runs the test suite's cross-checks against the bundled reference data, so a
green pipeline implies byte-compatibility with the reference C tool for all covered
scenarios.

See also: [Testing](testing.md) · [Contributing](contributing.md) ·
[Troubleshooting](troubleshooting.md)
