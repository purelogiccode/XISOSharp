# Building

This page covers SDK requirements, build and publish commands, and the CI pipeline.

- [Requirements](#requirements)
- [Build](#build)
- [Test](#test)
- [Publish](#publish)
- [NuGet packaging](#nuget-packaging)
- [CI pipeline](#ci-pipeline)

## Requirements

- **.NET SDK 10.0.301 or newer 10.0.3xx patch** — pinned in [`global.json`](../global.json)
  (`rollForward: latestPatch`): 10.0.3xx patches are accepted; newer 10.0.4xx
  feature bands and 11+ are not, and anything older than 10.0.301 (e.g. a
  VS-shipped 10.0.1xx) fails before build.
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
| `ZArchiveSharp` (`ZArchiveSharp.Tests` / `ZArchiveSharp.Benchmarks` / `ZArchiveSharp.Cli`) | — | Lives in the sibling `../CSharp_ZArchiveSharp` repo (own solution); consumed here as NuGet package `ZArchiveSharp` 1.4.0 (`ZArchiveSharp.Cli` is a `dotnet tool`, not a library reference) |
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

The suite is xUnit-based and runs sequentially (many historical tests set the
process working directory, so the assembly disables parallelization). Create and
extract themselves are CWD-free and covered by concurrent-operation tests. With
code coverage:

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
| Version | Derived from git tags via **MinVer** (`v1.0.2` / `1.0.2`) |
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

1. **build-and-test** - matrix over `ubuntu-latest`, `windows-latest`,
   `macos-latest`: restore → format verification (Windows) → build (Release) →
   test the `XISOSharp.Tests` project with XPlat code coverage. The merged
   line/branch coverage lands in the job summary (fails below **70% line / 60%
   branch**), and the trx + coverage reports are uploaded as artifacts.
2. **gui-smoke** - Windows: `XISOSharp -v`, `XISOSharp.Gui --probe-cli` and
   `XISOSharp.Gui --self-test`, the headless contract the GUI ships with.
3. **docs-links** - restores DocFX, builds the API metadata/reference into the
   staged Docsify site and runs `scripts/check_docs_links.py`; broken local
   Markdown/HTML links fail the job.
4. **inspectcode** - JetBrains InspectCode (pinned `2026.2.3.1`) over the whole
   solution; any warning or error fails the job and the SARIF report is uploaded
   to **Security → Code scanning**.
5. **dependency-audit** - `dotnet list package --vulnerable --include-transitive`
   fails on any known-vulnerable direct or transitive package.
6. **aot-smoke** - publishes `XISOSharp.AotSmoke` with NativeAOT on Linux and
   runs it, proving the library (and transitive ZAR/CCI packages) stay
   trim/AOT-compatible.
7. **fuzz-smoke** - two minutes of deterministic mutation fuzzing over ISO,
   CISO, CCI, ZAR, STFS and XBE/XEX inputs; any exception outside the documented
   format/I/O contract fails the job.
8. **battle-interop** - Windows: the CLI is compared against the checked-in
   extract-xiso, xdvdfs and xboxkit oracles over the shared synthetic,
   copyright-free corpus (`--synthetic`, generated by
   `PublicCorpusWriter`) for list/extract/rewrite, checksum/md5, unpack/pack
   and CISO round-trip; reports are uploaded as artifacts.
9. **pack** - after every gate: `dotnet pack` the library (the only published
   package), then upload the nupkgs.
10. **publish** (only on version tags, environment `nuget`) - push the nupkgs to
    nuget.org using the `NUGET_API_KEY` secret.

Companion workflows:

| Workflow | Trigger | Purpose |
|---|---|---|
| `codeql.yml` | push/PR to `master`, weekly | CodeQL C# analysis → code scanning |
| `dependency-review.yml` | pull requests | Blocks high/critical vulnerable dependencies; PR summary |
| `scorecard.yml` | weekly, push to `master` | OpenSSF Scorecard supply-chain score → code scanning |
| `release.yml` | version tags | Builds the six RID bundles, smoke-tests them, creates the GitHub release |
| `pages.yml` / `wiki.yml` | `docs/**` on `master` | Publish the Docsify site and mirror it to the wiki |

Dependabot (`.github/dependabot.yml`) opens grouped weekly PRs for NuGet
packages and GitHub Actions, and security updates are enabled repository-wide.

## Releases

Pushing a version tag (`1.6.0` or `v1.6.0`) does two independent things:

- `ci.yml` packs and publishes `XISOSharp.<version>.nupkg` + `.snupkg` (the
  library is the only NuGet package) to NuGet.
- `release.yml` builds the six framework-dependent bundles
  (`release_<version>_<rid>.zip` for `win-x64`, `win-arm64`, `linux-x64`,
  `linux-arm64`, `MacOsX-x64`, `MacOsX-arm64`) from the same tag, smoke-tests
  the native ones, generates an SPDX 2.2 SBOM and a build-provenance
  attestation, and attaches everything to the GitHub release. Its body is the
  version's section of [`release-notes.md`](release-notes.md).

`release.yml` also has a `workflow_dispatch` entry with a `dry_run` switch to
rehearse the bundle build + smoke tests without creating a release. The manual
`publish-cli.ps1` / `publish-gui.ps1` recipe in
[`AGENTS.md`](../AGENTS.md) remains the offline fallback.


The reference-binary cross-checks (extract-xiso / xdvdfs / xboxkit oracles) early-return
when the gitignored `References/` binaries are absent, so a green pipeline means the suite
passed — not necessarily that reference byte-compatibility was exercised. Run the battle
harness locally with the oracles installed for that guarantee (see
[Testing](testing.md#the-cli-battle-harness)).

See also: [Testing](testing.md) · [Contributing](contributing.md) ·
[Troubleshooting](troubleshooting.md)
