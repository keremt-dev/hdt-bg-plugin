<#
.SYNOPSIS
  Build-time helper. Finds the latest installed Hearthstone Deck Tracker
  and copies its reference binaries (HearthstoneDeckTracker.exe,
  HearthDb.dll) into the local refs/ folder so MSBuild can resolve them
  via HintPath. Called automatically by HsDecktrackBgReader.csproj.

.PARAMETER RefsDir
  Where to put the copied binaries. Defaults to ./refs next to this script.

.PARAMETER HdtRoot
  Override the HDT install root. If omitted, we scan
  %LocalAppData%\HearthstoneDeckTracker and %ProgramFiles%\HearthstoneDeckTracker.

.EXAMPLE
  pwsh -File stage-hdt-refs.ps1
  pwsh -File stage-hdt-refs.ps1 -HdtRoot 'D:\Games\HDT\app-1.32.5'
#>

[CmdletBinding()]
param(
    [string]$RefsDir,
    [string]$HdtRoot = ''
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot may be empty in some host configurations; fall back to the
# invocation path so a default RefsDir always resolves.
if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $RefsDir) {
    $RefsDir = Join-Path $PSScriptRoot 'refs'
}

function Resolve-LatestHdtAppDir {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) { return $null }

    # Squirrel installs put each version into app-<version>\. Pick the
    # most-recently-written folder so we follow updates without parsing
    # version strings (which would break on app-1.32.10 vs app-1.32.5).
    $appDirs = Get-ChildItem -LiteralPath $Root -Directory -Filter 'app-*' -ErrorAction SilentlyContinue
    if ($appDirs.Count -gt 0) {
        return ($appDirs | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    # Some users have a flat install (standalone installer, not Squirrel).
    # If the binaries live directly in $Root, use that.
    if (Test-Path -LiteralPath (Join-Path $Root 'HearthstoneDeckTracker.exe')) {
        return $Root
    }
    return $null
}

# 1) Figure out where HDT lives.
$candidates = @()
if ($HdtRoot)                              { $candidates += $HdtRoot }
if ($env:LOCALAPPDATA)                     { $candidates += (Join-Path $env:LOCALAPPDATA 'HearthstoneDeckTracker') }
if ($env:ProgramFiles)                     { $candidates += (Join-Path $env:ProgramFiles 'HearthstoneDeckTracker') }
if (${env:ProgramFiles(x86)})              { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'HearthstoneDeckTracker') }

$hdtAppDir = $null
foreach ($c in $candidates) {
    $hdtAppDir = Resolve-LatestHdtAppDir -Root $c
    if ($hdtAppDir) { Write-Host "[stage-hdt-refs] using HDT install at $hdtAppDir" ; break }
}

if (-not $hdtAppDir) {
    Write-Error @"
Couldn't locate Hearthstone Deck Tracker on this machine.
Looked in:
  $($candidates -join "`n  ")
Either install HDT to its default location, drop the two reference binaries
into '$RefsDir' by hand, or pass -HdtRoot <path>.
"@
}

# 2) Copy the two binaries the project references.
$needed = @('HearthstoneDeckTracker.exe', 'HearthDb.dll')
$missing = @()
foreach ($name in $needed) {
    $src = Join-Path $hdtAppDir $name
    if (-not (Test-Path -LiteralPath $src)) { $missing += $src; continue }
    if (-not (Test-Path -LiteralPath $RefsDir)) { New-Item -ItemType Directory -Path $RefsDir | Out-Null }
    $dst = Join-Path $RefsDir $name
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Host "[stage-hdt-refs] copied $name"
}

if ($missing.Count -gt 0) {
    Write-Error "HDT install found but missing required files:`n  $($missing -join "`n  ")"
}
