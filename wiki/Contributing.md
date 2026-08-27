# Contributing

Thanks for considering a contribution! This project is a byte-faithful port of
`extract-xiso.c` v2.7.1, and that goal shapes every rule below.

- [Workflow](#workflow)
- [Code style](#code-style)
- [Pull request guidelines](#pull-request-guidelines)
- [Compatibility discipline](#compatibility-discipline)
- [Areas to contribute](#areas-to-contribute)

## Workflow

1. **Fork** the repository and clone your fork.
2. Create a feature branch: `git checkout -b feat/my-change`.
3. Open `CSharp_XISOSharp.sln` in Visual Studio, or work from the CLI.
4. Build and test before starting:
   ```bash
   dotnet build
   dotnet test XISOSharp.Tests
   ```
5. Make your change **with tests** (see below).
6. Push and open a pull request against `main`.

## Code style

- Follow the analyzers and formatting rules in [`.editorconfig`](../.editorconfig)
  (Meziantou.Analyzer + Roslynator are wired into every project; the build must stay
  at **0 warnings**).
- The library multi-targets `net8.0`/`net9.0`/`net10.0` — do not use APIs that are
  unavailable on net8.0.
- **XML documentation is mandatory on every new public API member** — the package
  generates the doc file and package validation runs in strict mode.
- Prefer the existing idioms: `FileStreamOptions`, `BinaryPrimitives` little-endian
  helpers, `Span<byte>` for header I/O, thread-static scratch buffers.

## Pull request guidelines

- Keep changes **focused and minimal**; one logical change per PR.
- Every functional change must come with **unit or integration tests**.
- Verify the full suite passes and the solution builds with 0 warnings:
  ```bash
  dotnet build CSharp_XISOSharp.sln -c Release
  dotnet test XISOSharp.Tests -c Release
  ```
- For behavior-affecting changes, run the reference comparison
  (`Verify-Output.ps1`, see [Testing](testing.md#reference-cross-checking)).
- Update documentation when user-facing behavior changes (CLI flags, public API,
  format handling) — this wiki lives in `docs/`.
- Contributions are licensed under the MIT license of the project.

## Compatibility discipline

The original conversion followed a strict rule (from `ConversionPlan.md`):
**never proceed to the next step without a 100% hash match** against the reference C
tool. Preserve that spirit:

- Output ISOs must stay **byte-identical** to `extract-xiso` v2.7.1 for the same input.
- Do not "improve" AVL rebalancing or directory layout — the existing behavior is the
  compatibility contract.
- When changing the reader, keep `llCompat` semantics intact (legacy vs. optimized
  layouts).
- New features must be **additive** (new flags, new optional parameters, new methods)
  so existing behavior is unchanged.

## Areas to contribute

Good starting points (see [`ProposedEnhancements.md`](../ProposedEnhancements.md) for
the tracked backlog):

- Remaining `NOT DONE` enhancements and parity fixes (e.g. empty-entry `0x0000`
  sentinel, reserved attribute-bit masking).
- `docs/` improvements — accuracy passes, examples, screenshots.
- Test coverage growth (target >85% line coverage on `XisoReader.cs`,
  `XisoWriter.cs`, `AvlTree.cs`).
- Batch-script edge cases for `-d` on Windows.

See also: [Building](building.md) · [Testing](testing.md) ·
[Troubleshooting](troubleshooting.md) · [Proposed Enhancements](../ProposedEnhancements.md)
