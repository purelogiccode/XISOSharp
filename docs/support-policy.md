# Support Policy

## Targets

| Target | Status | Notes |
|---|---|---|
| `net10.0` | **LTS** | Primary target; ships in the release bundles. |
| `net9.0` | EOL (12 May 2026) | Still built/tested for source compatibility; no new features depend on it. |
| `net8.0` | Supported until 10 Nov 2026 | Kept through the 1.5.x line; will be dropped in the next major after EOL. |

`netstandard2.0`/`net6.0` are **not** targets: the library uses
`Span<T>`/`FileStreamOptions`/`SHA3_256` and modern BCL APIs. Consumers on older
frameworks can use the 1.4.x package line.

## Versioning

- **Major**: breaking public-API changes (announced in the release notes).
- **Minor**: additive APIs, new CLI verbs/flags, behavior fixes. Optional
  parameters may be added; package validation against the previous release
  catches accidental removals.
- **Patch**: fixes only, no new public surface.

The package validates against `PackageValidationBaselineVersion` (currently
1.4.4) with a reviewed `CompatibilitySuppressions.xml` for the 1.5.0
optional-parameter additions. Do not add suppressions without a written reason.

## Platform support

Windows 10 1607+, Linux (glibc) and macOS are supported. Release bundles cover
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `MacOsX-x64` and
`MacOsX-arm64`; the CLI also builds `win-x86`. The WPF tester is Windows-only.

## Security

See [SECURITY.md](../SECURITY.md) for reporting and [Known
Limitations](known-limitations.md) for the current boundaries. Release artifacts
ship with an SPDX SBOM and build-provenance attestation; code signing is
conditional on repository signing secrets.
