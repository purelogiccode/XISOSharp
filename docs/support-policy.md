# Support Policy

## Targets

| Target | Status | Notes |
|---|---|---|
| `net11.0` | **STS** (supported to 14 Nov 2028) | Only target; C# 15; ships in the release bundles. |

`net8.0`/`net9.0` reached end of support on 10 November 2026 and are no longer
targets. `net10.0` remains a supported LTS runtime but this package no longer
ships a `net10.0` asset; consumers pinned to .NET 10 can use the 1.9.x package
line. `netstandard2.0`/`net6.0` were never targets: the library uses
`Span<T>`/`FileStreamOptions`/`SHA3_256` and modern BCL APIs.

## Versioning

- **Major**: breaking public-API changes (announced in the release notes).
- **Minor**: additive APIs, new CLI verbs/flags, behavior fixes. Optional
  parameters may be added; package validation against the previous release
  catches accidental removals.
- **Patch**: fixes only, no new public surface.

The package validates against `PackageValidationBaselineVersion` (currently
1.8.0). The net11-only retarget intentionally drops the baseline `net10.0`
asset, recorded as a reviewed `PKV006` suppression; the EOL `net8.0`/`net9.0`
frameworks are excluded in `XISOSharp.csproj`. The 1.9.0 `FAC-01` facade
removals remain part of the baseline history. Do not add suppressions without a
written reason.

## Platform support

Windows 10 1607+, Linux (glibc) and macOS are supported. Release bundles cover
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `MacOsX-x64` and
`MacOsX-arm64`; the CLI also builds `win-x86`. The WPF tester is Windows-only.

## Security

See [SECURITY.md](../SECURITY.md) for reporting and [Known
Limitations](known-limitations.md) for the current boundaries. Release artifacts
ship with an SPDX SBOM and build-provenance attestation; code signing is
conditional on repository signing secrets.
