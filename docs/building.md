# Building

This page covers SDK requirements, build and publish commands, and the CI pipeline.

- [Requirements](#requirements)
- [Build](#build)
- [Test](#test)
- [Publish](#publish)
- [NuGet packaging](#nuget-packaging)
- [CI pipeline](#ci-pipeline)

## Requirements

- **.NET SDK 10.0.301 or newer** — pinned in [`global.json`](../global.json)
  (`rollForward: latestFeature`), so a slightly newer 10.0.x SDK also works.
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
dotnet build XISOSharp.Core -c Release
```

The solution contains:

| Project | Target(s) | Output |
|---|---|---|
| `XISOSharp.Core` | net8.0, net9.0, net10.0 | `XISOSharp.dll` + NuGet package |
| `XISOSharp.Cli` | net10.0 | `XISOSharp.Cli` executable |
| `XISOSharp.Tests` | net10.0 | xUnit test assembly (engine + CLI) |
| `ZARSharp.Tests` | net10.0 | xUnit test assembly (ZArchive/zstd port) |
| `XISOSharp.Benchmarks` | net10.0 | BenchmarkDotNet harness |
| `XISOSharpTester` | net10.0-windows | WPF regression-test GUI |

> [!NOTE]
> The Core project packs a NuGet package on every build (`GeneratePackageOnBuild`),
> so `dotnet build` also produces `.nupkg` / `.snupkg` files in
> `XISOSharp.Core/bin/<config>/`.

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
dotnet publish XISOSharp.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Linux x64
dotnet publish XISOSharp.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# macOS (Intel / Apple Silicon)
dotnet publish XISOSharp.Cli -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
dotnet publish XISOSharp.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

Supported runtime identifiers (`XISOSharp.Cli.csproj`):
`win-x64; linux-x64; osx-x64; osx-arm64`.

Output lands in `XISOSharp.Cli/bin/Release/net10.0/<rid>/publish/` as a single
`XISOSharp.Cli` (or `XISOSharp.Cli.exe`) binary.

## NuGet packaging

The library package (`XISOSharp`) is configured in `XISOSharp.Core.csproj`:

| Setting | Value |
|---|---|
| Version | Derived from git tags via **MinVer** (`v2.7.1` / `2.7.1`) |
| Symbols | `snupkg` with SourceLink (`EmbedAllSources`, `EmbedUntrackedSources`) |
| Reproducibility | `Deterministic` + `ContinuousIntegrationBuild` on CI |
| Signing | Strong-named (`XISOSharp.snk`) |
| Trimming/AOT | `IsTrimmable`, `IsAotCompatible` |
| API validation | `EnablePackageValidation` + strict mode |
| Metadata | MIT license, README, icon, tags (`xiso`, `xbox`, `extract-xiso`, …) |

Pack manually:

```bash
dotnet pack XISOSharp.Core -c Release -o ./artifacts
```

## CI pipeline

`.github/workflows/ci.yml` runs on every push/PR to `main` and on `v*` tags:

1. **build-and-test** — matrix over `ubuntu-latest`, `windows-latest`,
   `macos-latest`: restore → build (Release) → test with XPlat code coverage;
   the coverage report is uploaded as an artifact.
2. **pack** (after tests) — `dotnet pack` the core project and upload the nupkgs.
3. **publish** (only on `v*` tags, environment `nuget`) — push the nupkgs to
   nuget.org using the `NUGET_API_KEY` secret.

The CI also runs the test suite's cross-checks against the bundled reference data, so a
green pipeline implies byte-compatibility with the reference C tool for all covered
scenarios.

See also: [Testing](testing.md) · [Contributing](contributing.md) ·
[Troubleshooting](troubleshooting.md)
