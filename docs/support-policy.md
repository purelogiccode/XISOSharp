# Support Policy

## Targets

| Target | Status | Notes |
|---|---|---|
| `net11.0` | **STS** (supported to 14 Nov 2028) | Additional target; C# 15; ships in the release bundles. |
| `net10.0` | **LTS** (supported to 14 Nov 2028) | Supported compatibility target. |
| `net9.0` | EOL (10 Nov 2026) | Supported compatibility target; no security fixes after EOL. |
| `net8.0` | EOL (10 Nov 2026) | Supported compatibility target; no security fixes after EOL. |

2.0.1 restores the `net8.0`/`net9.0`/`net10.0` assets that 2.0.0 accidentally
dropped. `netstandard2.0`/`net6.0` are **not** targets: the library uses
`Span<T>`/`FileStreamOptions`/`SHA3_256` and modern BCL APIs. Consumers on
older frameworks can use the 1.4.x package line.

## Versioning

- **Major**: breaking public-API changes (announced in the release notes).
- **Minor**: additive APIs, new CLI verbs/flags, behavior fixes. Optional
  parameters may be added; package validation against the previous release
  catches accidental removals.
- **Patch**: fixes only, no new public surface.

The package validates against `PackageValidationBaselineVersion` (currently
2.0.0). The `FAC-01` facade removals remain part of the baseline history. Do
not add suppressions without a written reason.

## Platform support

Windows 10 1607+, Linux (glibc) and macOS are supported. Release bundles cover
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `MacOsX-x64` and
`MacOsX-arm64`; the CLI also builds `win-x86`. The WPF tester is Windows-only.

## Security

See [SECURITY.md](../SECURITY.md) for reporting and [Known
Limitations](known-limitations.md) for the current boundaries. Release artifacts
ship with an SPDX SBOM and build-provenance attestation; code signing is
conditional on repository signing secrets.
