<#
.SYNOPSIS
    Publishes self-contained, trimmed single-file XISOSharp CLI binaries.

.DESCRIPTION
    Publishes XISOSharp.Cli for each requested RID into publish/<rid>/.
    PublishSingleFile/PublishTrimmed come from XISOSharp.Cli.csproj; this script
    just loops RIDs and enforces --self-contained. Requires the .NET SDK pinned
    in global.json (cross-OS/arm64 publishes work from any host).

.EXAMPLE
    ./publish-cli.ps1
    Publishes the default six RIDs (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64).

.EXAMPLE
    ./publish-cli.ps1 -Rid win-x64,win-x86 -Zip
    Publishes 32/64-bit Windows and zips each output dir as XISOSharp.Cli-<rid>.zip.
#>
[CmdletBinding()]
param(
    [string[]]$Rid = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot 'publish'
}

foreach ($r in $Rid) {
    $outDir = Join-Path $OutputRoot $r
    Write-Host "Publishing $r -> $outDir" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $outDir) {
        Remove-Item -LiteralPath $outDir -Recurse -Force
    }
    & dotnet publish (Join-Path $PSScriptRoot 'XISOSharp.Cli/XISOSharp.Cli.csproj') `
        -c $Configuration -r $r --self-contained -o $outDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RID $r (exit $LASTEXITCODE)."
    }

    $exe = if ($r.StartsWith('win-')) { 'XISOSharp.Cli.exe' } else { 'XISOSharp.Cli' }
    $bin = Join-Path $outDir $exe
    if (-not (Test-Path -LiteralPath $bin)) {
        throw "Expected binary missing after publish: $bin"
    }
    $sizeMB = ((Get-Item -LiteralPath $bin).Length / 1MB).ToString('0.0')
    Write-Host "  OK: $bin ($sizeMB MB)" -ForegroundColor Green

    if ($Zip) {
        $zipPath = Join-Path $OutputRoot "XISOSharp.Cli-$r.zip"
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath
        Write-Host "  zipped: $zipPath" -ForegroundColor Green
    }
}

Write-Host "Done. Binaries under $OutputRoot" -ForegroundColor Green
