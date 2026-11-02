# Contributing

Thanks for considering a contribution! The full guide lives in
[docs/contributing.md](docs/contributing.md) — it covers the byte-faithfulness
contract, code style, tests, and compatibility discipline.

Quick version:

1. Fork and branch from `master`.
2. Build and test:
   ```bash
   dotnet build CSharp_XISOSharp.sln -c Release -m:1
   dotnet test tests/XISOSharp.Tests/XISOSharp.Tests.csproj -c Release
   dotnet format whitespace CSharp_XISOSharp.sln --verify-no-changes --no-restore
   ```
3. Keep output byte-identical to the reference tools for existing behavior.
4. Add tests for every functional change and update `docs/` when user-facing
   behavior changes.
5. Open a pull request against `master`; CI runs on Windows, Linux and macOS.

Security issues: do **not** open a public issue; see [SECURITY.md](SECURITY.md).
