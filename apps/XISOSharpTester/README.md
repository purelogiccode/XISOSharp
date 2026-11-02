# XISOSharpTester

[![CI](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/XISOSharp/actions/workflows/ci.yml)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%2B%20WPF-512BD4)](https://dotnet.microsoft.com/)
[![WPF-UI](https://img.shields.io/badge/WPF--UI-4.3.0-blueviolet)](https://github.com/lepoco/wpfui)
[![QuestPDF](https://img.shields.io/badge/QuestPDF-2026.9.1-blueviolet)](https://www.questpdf.com/)
[![Serilog](https://img.shields.io/badge/Serilog-4.4.0-blueviolet)](https://serilog.net/)
[![GitHub release](https://img.shields.io/github/v/release/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/releases/latest)
[![GitHub stars](https://img.shields.io/github/stars/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/issues)
[![GitHub contributors](https://img.shields.io/github/contributors/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/graphs/contributors)
[![Last commit](https://img.shields.io/github/last-commit/purelogiccode/XISOSharp)](https://github.com/purelogiccode/XISOSharp/commits/master)
[![License](https://img.shields.io/badge/License-MIT-green)](https://github.com/purelogiccode/XISOSharp/blob/master/LICENSE)

A WPF desktop application for regression testing the XISOSharp C# implementation against the original C extract-xiso tool. Runs batch comparisons across multiple XISO images and reports pass/fail status with SHA-256 hash verification.

## Features

- **Batch test** multiple XISO files at once
- **Verify** — compares XISO header verification between C# and the C tool
- **List** — compares file listing output
- **Extract** — extracts files with both tools and compares SHA-256 hashes of every file
- **Rewrite** — rewrites with both tools and compares output ISO hashes
- **Audit** — deep integrity verification (header, tree, sector bounds, cycle detection)
- **Round-trip** — creates XISO from extracted files and verifies the output
- **PDF export** — exports detailed test results to PDF via QuestPDF
- **Explore** — browse an image's contents in-process (no extraction): `TreeView`
  with lazy directory loading, volume summary, per-node details, per-node
  **Copy out** (files and directories), **SHA-256** display, and an **XEX2 info
  panel** for executables (`.iso` or `.cso` images)
- **Side-by-side comparison** with the original `extract-xiso` tool (bundled `extract-xiso.exe` on Windows; extensionless `extract-xiso` also accepted)

## Building

Open `CSharp_XISOSharp.sln` in Visual Studio, or run:

```
dotnet build
```

The tester resolves `extract-xiso` via the shared `XISOSharp.ToolLocator` chain (explicit path, then a sibling of the app executable under either spelling — `AppContext.BaseDirectory`, then the launched process directory for single-file bundles — then `PATH`) with a `-v` probe over the single shared `XISOSharp.ProcessRunner` (async drains, timeout, tree-kill) — the same pair that backs the GUI (`XISOSharp`) and the battle harness, replacing per-app runners. A bundled Windows `extract-xiso.exe` is copied to the output when present, but absence falls back gracefully through the same chain. If no tool is found, comparison tests against the native tool are skipped and only standalone C# library tests run.

## License

MIT
