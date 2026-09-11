<#
.SYNOPSIS
    Regenerates GitHub-wiki pages from docs/ (Docsify site source).
.DESCRIPTION
    Copies every docs/*.md page into the wiki checkout, rewrites links into
    wiki form (page links lose the .md suffix, ../ paths become absolute blob
    URLs), and generates Home.md + _Sidebar.md. Idempotent: re-running with an
    unchanged docs/ produces no diff, so the scheduled sync workflow commits
    only on real changes. Docsify-only files (index.html, .nojekyll,
    _sidebar.md) are never copied.
.PARAMETER DocsDir
    Path to the docs/ folder in the main checkout.
.PARAMETER WikiDir
    Path to the root of the .wiki.git checkout.
#>
param(
    [Parameter(Mandatory = $true)][string]$DocsDir,
    [Parameter(Mandatory = $true)][string]$WikiDir
)

$ErrorActionPreference = 'Stop'
$blobBase = 'https://github.com/purelogiccode/XISOSharp/blob/master'

if (-not (Test-Path -LiteralPath $DocsDir -PathType Container)) {
    throw "Docs directory not found: $DocsDir"
}
if (-not (Test-Path -LiteralPath $WikiDir -PathType Container)) {
    throw "Wiki directory not found: $WikiDir"
}

# Start clean so renamed/removed pages disappear from the wiki too.
Get-ChildItem -LiteralPath $WikiDir -Filter '*.md' -File |
    Remove-Item -Force

# 1. Plain pages (Home + sidebar are generated below).
Get-ChildItem -LiteralPath $DocsDir -Filter '*.md' -File |
    Where-Object { $_.Name -ne '_sidebar.md' -and $_.Name -ne 'README.md' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $WikiDir $_.Name) }

# 2. Home.md from the docs index, with Docsify-isms reworded for the wiki.
$homeText = Get-Content -LiteralPath (Join-Path $DocsDir 'README.md') -Raw
$homeText = $homeText -replace 'provided by \[`_sidebar\.md`\]\(_sidebar\.md\) and \[`index\.html`\]\((index\.html)\)\.', 'provided by the wiki sidebar.'
$homeText = $homeText -replace 'the sidebar \(`_sidebar\.md`\) is the canonical', 'the wiki sidebar is the canonical'
$homeText = $homeText -replace '\[`docs/_sidebar\.md`\]\((docs/_sidebar\.md)\) is the sidebar\.', 'the wiki sidebar.'
$homeText | Set-Content -LiteralPath (Join-Path $WikiDir 'Home.md') -NoNewline

# 3. _Sidebar.md from the Docsify sidebar (drop the HTML comment, Home is a page).
$sidebar = Get-Content -LiteralPath (Join-Path $DocsDir '_sidebar.md') |
    Where-Object { $_ -notmatch '^\s*<!--' } |
    ForEach-Object { $_ -replace '\[Home\]\(README\.md\)', '[Home](Home)' }
$sidebar | Set-Content -LiteralPath (Join-Path $WikiDir '_Sidebar.md')

# 4. Wiki-ify links in every generated page:
#    (a) ../<path> -> absolute blob URL (LICENSE, global.json, ...),
#    (b) relative page.md links -> page (strip .md, keep #anchors).
Get-ChildItem -LiteralPath $WikiDir -Filter '*.md' -File | ForEach-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw
    $text = $text -replace '\]\(\.\./([^()]+)\)', "]($blobBase/`$1)"
    $text = $text -replace '\]\((?![a-zA-Z][\w+.-]*://)([^()]*?)\.md([)#])', ']($1$2'
    $text | Set-Content -LiteralPath $_.FullName -NoNewline
}

Write-Output "Wiki pages regenerated in ${WikiDir}:"
Get-ChildItem -LiteralPath $WikiDir -Filter '*.md' -File | ForEach-Object { Write-Output "  $($_.Name)" }
