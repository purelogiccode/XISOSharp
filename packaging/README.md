# Packaging manifests

Generated distribution metadata for the GitHub release assets. Regenerate the
files after publishing a release:

```powershell
./scripts/update-packaging.ps1 -Version <version>
```

| Path | Purpose |
|---|---|
| `winget/manifests/p/purelogiccode/XISOSharp/<version>/` | winget manifest set (version, installer, en-US locale) |
| `scoop/xisosharp.json` | Scoop manifest (source copy) |
| `homebrew/xisosharp.rb` | Homebrew cask |
| `../bucket/xisosharp.json` | The same Scoop manifest at the repository-root bucket path |

The generator reads each asset's SHA-256 digest from the GitHub release and
writes the exact `release_<version>_<rid>.zip` URLs. It never deletes older
winget version directories.

## Install from the checked-in files

```powershell
# winget (enable local manifests once; requires an elevated shell)
winget settings --enable LocalManifestFiles
winget install --manifest .\packaging\winget\manifests\p\purelogiccode\XISOSharp\<version>

# Scoop: this repository root is a bucket
scoop bucket add xisosharp https://github.com/purelogiccode/XISOSharp
scoop install xisosharp/xisosharp
```

```bash
# Homebrew cask (a tap can host this file unchanged)
brew install --cask ./packaging/homebrew/xisosharp.rb
```

## Signing

NuGet author signing, Windows Authenticode and macOS codesign/notarization are
optional and gated on repository secrets. See
[Building → Distribution artifacts](../docs/building.md#distribution-artifacts).
Unsigned builds remain the default.
