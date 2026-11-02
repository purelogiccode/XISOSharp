# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.6.x | :white_check_mark: |
| < 1.6 | :x: |

Security fixes ship in the latest minor release. Pre-release builds
(`*-alpha.*`, `*-beta.*`) are not supported.

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/purelogiccode/XISOSharp/security/advisories/new)
(Security -> Advisories -> Report a vulnerability). The report is visible only
to the maintainers and to you.

Please include:

- the affected component (library `XISOSharp`, CLI `XISOSharp`, GUI
  `XISOSharp.Gui`) and version (`XISOSharp -v`);
- the platform/RID and .NET runtime;
- a minimal reproducer, ideally a small crafted image (`.iso`, `.xiso`,
  `.cso`, `.cci`, `.zar`) or the exact command line;
- the observed impact (crash, hang, out-of-bounds write, path traversal,
  resource exhaustion, ...);
- any suggested fix or patch.

### What to expect

| Stage | Target |
| --- | --- |
| Acknowledgement | 3 business days |
| Triage + severity assessment | 7 business days |
| Fix or mitigation plan | 30 days (critical: as soon as practical) |

We coordinate disclosure with you. Once a fix is available we publish a
[GitHub Security Advisory](https://github.com/purelogiccode/XISOSharp/security/advisories),
credit the reporter unless you prefer to stay anonymous, and release a patched
version. Please give us a reasonable window before publishing details.

## Scope

In scope:

- the library, CLI and GUI in this repository;
- the image parsers and writers: XISO/XISO reader and writer, CISO/CCI
  compression, split-image handling, ZArchive (`.zar`), and the audit/repair/
  salvage paths;
- path handling during extraction (`--unpack`, `--copy-out`, ZAR extraction),
  including archive entries that attempt to escape the destination directory;
- decompression and allocation behavior on hostile input (zip-bomb style
  images, corrupt or cyclic directory tables, absurd sizes);
- the release bundles and NuGet package built by CI (build integrity).

Out of scope:

- vulnerabilities in third-party dependencies - report those upstream, though a
  heads-up is welcome;
- attacks that require the user to run untrusted code they were given anyway;
- issues only reachable by modifying the local installation or config;
- social engineering and physical access.

## Hardening and supply chain

This repository runs the following controls:

- **Dependabot alerts and security updates** for NuGet and GitHub Actions,
  with weekly grouped version updates (`.github/dependabot.yml`).
- **Code scanning** with CodeQL (`.github/workflows/codeql.yml`) and JetBrains
  InspectCode (`.github/workflows/ci.yml`, SARIF upload).
- **Dependency review** on every pull request
  (`.github/workflows/dependency-review.yml`): high/critical vulnerable
  dependencies block the PR.
- **Secret scanning with push protection** and private vulnerability
  reporting, enabled repository-wide.
- **OpenSSF Scorecard** (`.github/workflows/scorecard.yml`) publishing the
  supply-chain score weekly.
- **Least-privilege workflows**: `contents: read` by default, write
  permissions granted per job, actions pinned to commit SHAs (Dependabot keeps
  the pins current), and the `NUGET_API_KEY` secret scoped to the protected
  `nuget` environment.
- **Reproducible releases**: NuGet packages and the six app bundles are built
  by GitHub Actions from the tagged commit; the bundles are smoke-tested
  before the release is created.

## Accepted Scorecard findings

The weekly OpenSSF Scorecard run reports a few findings that are accepted by
design. They are dismissed in **Security → Code scanning** with this rationale:

| Check | Why it is accepted |
| --- | --- |
| BinaryArtifacts | `tests/XISOSharp.BattleTests/{extract-xiso,xdvdfs,xboxkit}.exe`, `apps/XISOSharpTester/extract-xiso.exe` and `tests/XISOSharp.Tests/Fixtures/test_fixture.iso` are checked-in interop oracles and fixtures the test suite runs against. The comparison tests skip when an oracle is absent, and none of these files ship in the NuGet package or the release bundles. (The 1.6.0 `src`/`apps`/`tests` reorganisation moved the paths, so the finding is re-reported for the new locations.) |
| TokenPermissions | The wiki-sync and release jobs need `contents: write` to push the wiki and create releases. The scope is job-level and the token is short-lived. |
| PinnedDependencies | Two separate sub-findings are accepted. **Package pinning:** every `PackageReference` pins its version, and Dependabot + NuGet audit + the `dependency-audit` CI gate cover known-vulnerable versions; lock files are not used because the 13 RID/TFM publish combinations make `RestoreLockedMode` incompatible with the release matrix (NU1004). **`nugetCommand`:** the flagged command is `dotnet nuget push` in the tag-only `publish-nuget` job, which runs on the .NET SDK installed by a commit-SHA-pinned `actions/setup-dotnet` and selected by `global.json`; there is no downloaded, unpinned `nuget.exe` anywhere in the build. |
| BranchProtection / CodeReview | `master` is protected against deletion and force-push; requiring pull-request approvals would block the maintainer's direct-push workflow, and every push runs the full CI gate. |
| CIIBestPractices | The project is not registered for the OpenSSF Best Practices badge. |
| Fuzzing | The parsers are exercised by the corruption and pathological-TOC suites; there is no OSS-Fuzz integration. |

## Using XISOSharp safely

Xbox images are untrusted input. The parsers are hardened (depth limits,
entry caps, cycle detection, bounded reads, exact-length copies) and hostile
input is expected to fail with a named error rather than crash, but a
malformed image must still be treated as potentially dangerous:

- extract into a directory you control, not over system paths;
- keep backups before using `rewrite`, `repair`, `salvage`, or any in-place
  mode;
- do not run the GUI or CLI as administrator/root to process an image.

If a crafted image crashes the process, hangs it, or writes outside the chosen
destination, that is a security bug - please report it as above.
