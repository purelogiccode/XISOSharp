<#
.SYNOPSIS
    Publishes self-contained, single-file XISOSharp.Gui binaries.

.DESCRIPTION
    Publishes XISOSharp.Gui (Avalonia, dark-only) for each requested RID into
    publish-gui/<rid>/. PublishSingleFile comes from XISOSharp.Gui.csproj;
    trimming stays OFF because Avalonia relies on XAML/reflection. Requires the
    .NET SDK pinned in global.json (cross-OS/arm64 publishes work from any host).

.EXAMPLE
    ./publish-gui.ps1
    Publishes the default six RIDs (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64).

.EXAMPLE
    ./publish-gui.ps1 -Rid win-x64 -Zip
    Publishes 64-bit Windows GUI and packages it as
    xisosharp-gui-win-x64.zip (Linux: .tar.gz, macOS: .dmg).
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
    $OutputRoot = Join-Path $PSScriptRoot 'publish-gui'
}

function Get-ArchiveExtension([string]$Rid) {
    if ($Rid.StartsWith('linux-', [StringComparison]::Ordinal)) { return 'tar.gz' }
    if ($Rid.StartsWith('osx-', [StringComparison]::Ordinal)) { return 'dmg' }
    return 'zip'
}

$isMacOsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::OSX)

foreach ($r in $Rid) {
    $outDir = Join-Path $OutputRoot $r
    Write-Host "Publishing $r -> $outDir" -ForegroundColor Cyan
    # AGENTS.md hard rule: publish-gui/<rid> is an artifact store, never deleted.
    # Publish into a temp stage, then merge over the same-named files.
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) "xiso_publish_gui/$r"
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'XISOSharp.Gui/XISOSharp.Gui.csproj') `
        -c $Configuration -r $r --self-contained -o $stage
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RID $r (exit $LASTEXITCODE)."
    }

    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $outDir -Recurse -Force
    Remove-Item -LiteralPath $stage -Recurse -Force

    $exe = if ($r.StartsWith('win-')) { 'XISOSharp.Gui.exe' } else { 'XISOSharp.Gui' }
    $bin = Join-Path $outDir $exe
    if (-not (Test-Path -LiteralPath $bin)) {
        throw "Expected binary missing after publish: $bin"
    }
    $sizeMB = ((Get-Item -LiteralPath $bin).Length / 1MB).ToString('0.0')
    Write-Host "  OK: $bin ($sizeMB MB)" -ForegroundColor Green

    if ($Zip) {
        if ($r.StartsWith('osx-', [StringComparison]::Ordinal) -and -not $isMacOsHost) {
            Write-Warning "Skipping .dmg for ${r}: hdiutil is only available on macOS. Run this on macOS to build the release asset."
            continue
        }

        $extension = Get-ArchiveExtension $r
        $archivePath = Join-Path $OutputRoot "xisosharp-gui-$r.$extension"
        if (Test-Path -LiteralPath $archivePath) {
            Remove-Item -LiteralPath $archivePath -Force
        }

        switch ($extension) {
            'zip' {
                Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $archivePath
            }
            'tar.gz' {
                & tar -czf $archivePath -C $outDir .
                if ($LASTEXITCODE -ne 0) { throw "tar failed for $archivePath (exit $LASTEXITCODE)." }
            }
            'dmg' {
                & hdiutil create -volname "XISOSharp" -srcfolder $outDir `
                    -ov -format UDZO $archivePath
                if ($LASTEXITCODE -ne 0) { throw "hdiutil failed for $archivePath (exit $LASTEXITCODE)." }
            }
        }
        Write-Host "  packaged: $archivePath" -ForegroundColor Green
    }
}

Write-Host "Done. Binaries under $OutputRoot" -ForegroundColor Green
