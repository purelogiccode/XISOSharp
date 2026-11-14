#requires -Version 7.0
<#
.SYNOPSIS
  Reproducible create/extract benchmark comparison: XISOSharp vs extract-xiso
  vs xdvdfs (and XboxKit where a Redump-layout input is available).

.DESCRIPTION
  Generates a deterministic, copyright-free source tree under the temp corpus
  root, measures each tool with one warmup and N iterations, and prints the
  median wall-clock time plus throughput. The script only deletes its own temp
  corpus/output directories.
#>
param(
    [int]$Iterations = 3,
    [int]$SizeMiB = 128,
    [string]$CorpusRoot = (Join-Path $env:TEMP 'xiso_bench_compare')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cli = Join-Path $repo 'XISOSharp.Cli/bin/Release/net11.0/XISOSharp.exe'
$extractXiso = Join-Path $repo 'XISOSharp.BattleTests/extract-xiso.exe'
$xdvdfs = Join-Path $repo 'References/xdvdfs-0.8.3/xdvdfs.exe'
$xboxkit = Join-Path $repo 'XISOSharp.BattleTests/xboxkit.exe'

foreach ($tool in @($extractXiso, $xdvdfs)) {
    if (-not (Test-Path $tool)) {
        throw "Missing reference tool: $tool"
    }
}

if (-not (Test-Path $cli)) {
    dotnet build (Join-Path $repo 'XISOSharp.Cli/XISOSharp.Cli.csproj') -c Release -f net11.0 -m:1 | Out-Host
}

$source = Join-Path $CorpusRoot 'source'
$xiso = Join-Path $CorpusRoot 'bench.iso'
$ready = Join-Path $CorpusRoot 'corpus.ready'
if (-not (Test-Path $ready)) {
    if (Test-Path $CorpusRoot) { Remove-Item -Recurse -Force $CorpusRoot }
    New-Item -ItemType Directory -Path (Join-Path $source 'sub') -Force | Out-Null
    $payload = New-Object byte[] (1MB)
    $rng = [Random]::new(42)
    $files = 8
    $perFile = [Math]::Max(1, [int](($SizeMiB * 1MB) / $files))
    for ($i = 0; $i -lt $files; $i++) {
        $rng.NextBytes($payload)
        $target = if ($i % 2 -eq 0) { Join-Path $source "file$i.bin" } else { Join-Path $source "sub/file$i.bin" }
        $stream = [IO.File]::Create($target)
        try {
            $remaining = $perFile
            while ($remaining -gt 0) {
                $chunk = [Math]::Min($payload.Length, $remaining)
                $stream.Write($payload, 0, $chunk)
                $remaining -= $chunk
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    Set-Content -LiteralPath (Join-Path $source 'readme.txt') -Value "benchmark corpus`n" -NoNewline
    Set-Content -LiteralPath $ready -Value "ready"
}

function Invoke-Tool {
    param([string]$Name, [string]$Output, [scriptblock]$Action)
    $times = [Collections.Generic.List[double]]::new()
    # Warmup.
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    for ($i = 0; $i -lt $Iterations; $i++) {
        if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $Action
        $sw.Stop()
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
        $times.Add($sw.Elapsed.TotalMilliseconds)
    }

    $sorted = @($times | Sort-Object)
    $median = if ($sorted.Count % 2 -eq 1) {
        $sorted[[int][Math]::Floor($sorted.Count / 2)]
    }
    else {
        ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
    }

    [pscustomobject]@{
        Tool = $Name
        MedianMs = [Math]::Round($median, 1)
        MinMs = [Math]::Round($sorted[0], 1)
        MaxMs = [Math]::Round($sorted[-1], 1)
        MiBPerSecond = [Math]::Round(($SizeMiB / ($median / 1000)), 1)
    }
}

$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1)
$memoryGiB = [Math]::Round(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB), 1)
$systemLetter = $env:SystemDrive.TrimEnd(':')
$disk = try { Get-Partition -DriveLetter $systemLetter | Get-Disk | Select-Object -First 1 } catch { $null }
Write-Host "Host: $([Environment]::OSVersion.VersionString) | $($cpu.Name.Trim()) ($($cpu.NumberOfCores)C/$($cpu.NumberOfLogicalProcessors)T) | ${memoryGiB} GiB RAM"
if ($disk) { Write-Host "System disk: $($disk.FriendlyName), media=$($disk.MediaType), bus=$($disk.BusType)" }
Write-Host "Corpus: $SizeMiB MiB, $Iterations measured iteration(s), warmup included`n"

$createResults = [Collections.Generic.List[object]]::new()
$outIso = Join-Path $CorpusRoot 'out.iso'
$createResults.Add((Invoke-Tool 'XISOSharp create' $outIso {
            if (Test-Path $outIso) { Remove-Item -Force $outIso }
            & $cli -q -y -c $source $outIso *> $null
        }))
$createResults.Add((Invoke-Tool 'extract-xiso create' $outIso {
            if (Test-Path $outIso) { Remove-Item -Force $outIso }
            & $extractXiso -Q -c $source $outIso *> $null
        }))
$createResults.Add((Invoke-Tool 'xdvdfs pack' $outIso {
            if (Test-Path $outIso) { Remove-Item -Force $outIso }
            & $xdvdfs pack $source $outIso *> $null
        }))

# Build one reference ISO for extraction (XISOSharp output).
if (Test-Path $xiso) { Remove-Item -Force $xiso }
& $cli -q -y -c $source $xiso *> $null
if ($LASTEXITCODE -ne 0) { throw "Could not create the extraction source ISO" }

$extractResults = [Collections.Generic.List[object]]::new()
$outDir = Join-Path $CorpusRoot 'out'
$extractResults.Add((Invoke-Tool 'XISOSharp extract' $outDir {
            if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
            & $cli -q -y -x -d $outDir $xiso *> $null
        }))
$extractResults.Add((Invoke-Tool 'extract-xiso extract' $outDir {
            if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
            & $extractXiso -Q -x -d $outDir $xiso *> $null
        }))
$extractResults.Add((Invoke-Tool 'xdvdfs unpack' $outDir {
            if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
            & $xdvdfs unpack $xiso $outDir *> $null
        }))

Write-Host '## Create (source tree -> ISO)'
$createResults | Format-Table -AutoSize | Out-String | Write-Host
Write-Host '## Extract (ISO -> source tree)'
$extractResults | Format-Table -AutoSize | Out-String | Write-Host

if (Test-Path $xboxkit) {
    Write-Host 'XboxKit: skipped for the plain-XISO comparison; its extractor requires a Redump-layout image.'
}
