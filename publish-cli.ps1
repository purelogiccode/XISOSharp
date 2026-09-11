<#
.SYNOPSIS
    Publishes self-contained, trimmed single-file XISOSharp CLI binaries.

.DESCRIPTION
    Publishes XISOSharp.Cli for each requested RID into publish/<rid>/.
    The published single-file binary is named XISOSharp(.exe) (renamed by the
    csproj RenamePublishedExeToXisoSharp target; the build AssemblyName stays
    XISOSharp.Cli to avoid colliding with the XISOSharp library DLL).
    PublishSingleFile/PublishTrimmed come from XISOSharp.Cli.csproj; this script
    just loops RIDs and enforces --self-contained. Requires the .NET SDK pinned
    in global.json (cross-OS/arm64 publishes work from any host).

.EXAMPLE
    ./publish-cli.ps1
    Publishes the default seven RIDs (win-x86, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64).

.EXAMPLE
    ./publish-cli.ps1 -Rid win-x64,win-x86 -Zip
    Publishes 32/64-bit Windows and zips each output dir as release-<version>-<rid>.zip
    (e.g. release_1.0.0_win-x64.zip), matching the asset names the CLI update
    checker looks for on the GitHub release.
#>
[CmdletBinding()]
param(
    [string[]]$Rid = @('win-x86', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot 'publish'
}

# Version for the -Zip asset names (release_<version>_<rid>.zip), from the
# latest git tag so it matches the GitHub release the zips attach to.
$cliVersion = (& git -C $PSScriptRoot describe --tags --abbrev=0 2>$null)
if ([string]::IsNullOrWhiteSpace($cliVersion)) {
    $cliVersion = '0.0.0-dev'
}
$cliVersion = $cliVersion -replace '^[vV]', ''

# Release-asset RID fragments (UpdateChecker convention): macOS ships as
# MacOsX-*, RIDs without a convention entry fall back to the raw RID.
$zipRidMap = @{
    'win-x64'     = 'win-x64'
    'win-x86'     = 'win-x86'
    'win-arm64'   = 'win-arm64'
    'linux-x64'   = 'linux-x64'
    'linux-arm64' = 'linux-arm64'
    'osx-x64'     = 'MacOsX-x64'
    'osx-arm64'   = 'MacOsX-arm64'
}

foreach ($r in $Rid) {
    $outDir = Join-Path $OutputRoot $r
    Write-Host "Publishing $r -> $outDir" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $outDir) {
        Remove-Item -LiteralPath $outDir -Recurse -Force
    }
    # NOTE: -f net10.0 is required — XISOSharp.Cli multi-targets (net8/9/10) so the
    # shippable closure can be referenced by the test suite on every TFM, but only
    # net10.0 ships as a self-contained binary.
    & dotnet publish (Join-Path $PSScriptRoot 'XISOSharp.Cli/XISOSharp.Cli.csproj') `
        -c $Configuration -f net10.0 -r $r --self-contained -o $outDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RID $r (exit $LASTEXITCODE)."
    }

    $exe = if ($r.StartsWith('win-')) { 'XISOSharp.exe' } else { 'XISOSharp' }
    $bin = Join-Path $outDir $exe
    if (-not (Test-Path -LiteralPath $bin)) {
        throw "Expected binary missing after publish: $bin (expected the published single-file exe to be renamed to XISOSharp(.exe) by the csproj RenamePublishedExeToXisoSharp target)"
    }
    $sizeMB = ((Get-Item -LiteralPath $bin).Length / 1MB).ToString('0.0')
    Write-Host "  OK: $bin ($sizeMB MB)" -ForegroundColor Green

    if ($Zip) {
        $zipRid = if ($zipRidMap.ContainsKey($r)) { $zipRidMap[$r] } else { $r }
        $zipPath = Join-Path $OutputRoot "release_${cliVersion}_${zipRid}.zip"
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath
        Write-Host "  zipped: $zipPath" -ForegroundColor Green
    }
}

Write-Host "Done. Binaries under $OutputRoot" -ForegroundColor Green
