# AGENTS.md

Project instructions for AI agents working in this repository. Read this before
building, cleaning, or releasing anything.

## Hard rule: never delete files in Release output paths

Release output trees are artifact stores, not disposable caches. They hold
previously shipped bundles (e.g.
`XISOSharp.Gui/bin/Release/release_1.0.1_win-x64.zip`) that may not be
reproducible or re-downloadable. Never delete them.

- Never delete or remove files under a project's Release output paths:
  - any `*/bin/Release/**` (library, CLI, GUI, Tests, Tester, Benchmarks,
    BattleTests, TestDataGenerator)
  - `publish/**`, `publish-gui/**` (publish-script outputs)
  - any `release_*.zip`, `*.nupkg`, `*.snupkg` file anywhere
- Never run destructive cleanups against them: `dotnet clean -c Release`,
  `Remove-Item -Recurse` on `bin`/`obj`/`publish*`, `git clean -xdf`, etc.
- Fresh builds go through a temp staging directory
  (`%TEMP%\xiso_bundle\<rid>` — safe to wipe) and only write/overwrite the
  exact artifact being rebuilt in the Release path. Creating new files and
  overwriting the same-named artifact for the current version is fine;
  deleting older or unrelated artifacts is not.
- If a clean of a Release path seems required (corrupt output, TFM/RID change),
  stop and ask the user first.

## CI

- `.github/workflows/ci.yml` builds the full solution on ubuntu/windows/macos
  and runs the test suite on `windows-latest` only: the suite mutates the
  process-wide current directory, which is not safe to parallelize on Unix
  runners. Cross-platform test hardening is a known follow-up.
- Tag pushes trigger the tag-only `publish-nuget` job; PR/branch runs stop at
  pack.

## Routine: publish a library package (NuGet)

### Versioning facts

- Version comes from git tags via **MinVer**. Never add or edit a `Version`
  property in a csproj.
- Release tags are bare semver: `1.0.0`, `1.0.1`, `1.0.2` (`v1.0.2` also
  works). N commits after tag `1.0.1` report `1.0.2-alpha.0.N+<sha>`.
- NuGet versions are immutable: never reuse one. `--skip-duplicate` makes
  re-runs idempotent.
- CI is `.github/workflows/ci.yml`; its tag-only `publish-nuget` job packs the
  library and pushes `XISOSharp.<version>.nupkg` + `.snupkg` using the
  `NUGET_API_KEY` secret in the `nuget` environment (already configured).

### Steps

1. Finalize documentation first — the package bakes it in:
   - `XISOSharp/README.md` is packed into the nupkg (`PackageReadmeFile`), and
     `XISOSharp/XISOSharp.csproj` holds the nuget.org `Description`.
   - Add the release section to `docs/release-notes.md`, refresh `WhatsNew.md`,
     and update the READMEs (root, library, CLI, Tests, Tester) plus any docs
     the change touched.
   - Upstream parity references use the dated build (`extract-xiso` build
     `202609111233`), not `v2.7.1`. Never change the on-disk optimized tag
     `in!xiso!2.7.1 (01.11.14)` or `Constants.ExisoVersion`.
2. Run the full suite: `dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj -c Release`
   (currently 1400 tests: 1396 passed / 4 skipped, across net8.0, net9.0, net10.0).
3. Commit + push `master` (only when the user asks), then tag and push:
   `git tag <version> && git push origin <version>`.
4. The tag push triggers CI pack + publish. If the `nuget` environment has
   protection rules, approve the deployment in GitHub Actions. Verify with
   `gh run list --workflow=ci.yml` and
   `https://api.nuget.org/v3-flatcontainer/xisosharp/index.json` (indexing can
   lag a few minutes).
5. Manual fallback (CI unavailable or local verification):

   ```powershell
   dotnet pack XISOSharp/XISOSharp.csproj -c Release -o artifacts\<version>
   dotnet nuget push "artifacts\<version>\XISOSharp.<version>.nupkg" --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
   dotnet nuget push "artifacts\<version>\XISOSharp.<version>.snupkg" --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
   ```

   Push the nupkg before the snupkg (symbols require the package to exist).

## Routine: publish a GitHub release

GitHub releases carry only the six RID app bundles; the library packages stay
NuGet-only (user decision). The CLI's `[UPDATE]` check downloads
`release_<version>_<rid>.zip` from the latest release, so asset names and the
release tag must match the version exactly.

1. Build the bundles (recipe below) for: `win-x64`, `win-arm64`, `linux-x64`,
   `linux-arm64`, `MacOsX-x64`, `MacOsX-arm64`.
2. Create the release from the pushed tag. The body is the version's section of
   `docs/release-notes.md`:

   ```bash
   gh release create <version> --title <version> --notes-file <version-notes.md>
   ```

3. Upload all six bundles:

   ```bash
   gh release upload <version> XISOSharp.Gui/bin/Release/release_<version>_win-x64.zip \
     XISOSharp.Gui/bin/Release/release_<version>_win-arm64.zip \
     XISOSharp.Gui/bin/Release/release_<version>_linux-x64.zip \
     XISOSharp.Gui/bin/Release/release_<version>_linux-arm64.zip \
     XISOSharp.Gui/bin/Release/release_<version>_MacOsX-x64.zip \
     XISOSharp.Gui/bin/Release/release_<version>_MacOsX-arm64.zip
   ```

4. Verify:
   - `gh release view <version> --json assets` lists exactly the six zips.
   - `https://github.com/purelogiccode/XISOSharp/releases/download/<version>/release_<version>_win-x64.zip`
     returns HTTP 200.
5. Never attach `*.nupkg`/`*.snupkg` to the release, and never delete existing
   release assets unless the user asks.

## Bundle build recipe (framework-dependent)

Outputs need the .NET 10 runtime (Desktop Runtime for the GUI). For each RID,
stage into `%TEMP%` (safe to wipe) and produce the zip under
`XISOSharp.Gui/bin/Release/`:

```powershell
$stage = "$env:TEMP\xiso_bundle\<rid>"
# create/clear ONLY the temp stage
dotnet publish XISOSharp.Gui/XISOSharp.Gui.csproj -c Release -r <rid> --no-self-contained -o $stage
dotnet publish XISOSharp.Cli/XISOSharp.Cli.csproj -c Release -f net10.0 -r <rid> --no-self-contained -p:PublishTrimmed=false -o $stage
Get-ChildItem $stage -Include *.pdb,*.xml -Recurse | Remove-Item -Force
Copy-Item README.md $stage
Copy-Item LICENSE $stage
Compress-Archive "$stage\*" "XISOSharp.Gui\bin\Release\release_<version>_<rid>.zip" -Force
```

Each zip must contain exactly the GUI app, `XISOSharp(.exe)`, `README.md`, and
`LICENSE` — no `*.pdb` / `*.xml`.

Smoke-test from an extracted zip:

- `XISOSharp.exe -v` →
  `XISOSharp v<version> for <platform> - https://github.com/purelogiccode/XISOSharp`
- `XISOSharp.Gui.exe --probe-cli` → `XISOSharp <version>`
- `XISOSharp.Gui.exe --self-test <path-to-cli>` → `SELF-TEST: all passed`
