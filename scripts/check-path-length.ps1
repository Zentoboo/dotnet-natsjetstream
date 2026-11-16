<#
.SYNOPSIS
    Checks that no build intermediate path exceeds the Windows MAX_PATH limit.

.DESCRIPTION
    Windows file APIs fail on paths longer than ~260 characters unless the
    LongPathsEnabled registry setting is enabled. This solution redirects its
    intermediate (obj) output to a short repository-level folder
    (artifacts/obj/<ProjectName>/) via Directory.Build.props, so the longest
    path stays comfortably below the limit even when the repo is cloned into
    a deep folder.

    Run this from the repository root (or pass -Root). Exits with code 1 if a
    path would exceed the limit, with instructions for both fixes.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's folder.

.PARAMETER MaxLength
    Maximum allowed path length. Defaults to 258 (Windows fails at 260).
    Paths above 250 but within the limit are reported as a heads-up.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [int]$MaxLength = 258
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Root = Split-Path -Parent $scriptDir
}
$Root = (Resolve-Path -Path $Root).Path

$longPathsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem'
$longPathsEnabled = $false
try {
    $longPathsEnabled = (Get-ItemProperty -Path $longPathsKey -Name LongPathsEnabled -ErrorAction Stop).LongPathsEnabled -eq 1
}
catch {
    $longPathsEnabled = $false
}

# Mirror the value from Directory.Build.props so the check tracks the real config.
$useArtifactsObj = $true
$buildProps = Join-Path $Root 'Directory.Build.props'
if (Test-Path $buildProps) {
    $useArtifactsObj = [bool](Select-String -Path $buildProps -Pattern 'BaseIntermediateOutputPath' -Quiet)
}

$projects = Get-ChildItem -Path $Root -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\|node_modules' }

$results = foreach ($project in $projects) {
    $name = $project.BaseName
    $objDir = if ($useArtifactsObj) {
        Join-Path $Root "artifacts\obj\$name"
    }
    else {
        Join-Path $project.DirectoryName 'obj'
    }

    $path = Join-Path $objDir "Debug\net10.0\$name.GeneratedMSBuildEditorConfig.editorconfig"
    [PSCustomObject]@{
        Length  = $path.Length
        Project = $name
        Path    = $path
    }
}

$results = $results | Sort-Object Length -Descending
$longest = $results | Select-Object -First 1

Write-Host "Projects checked : $($results.Count)"
Write-Host "Intermediate root: $(if ($useArtifactsObj) { Join-Path $Root 'artifacts\obj' } else { '<project>\obj' })"
Write-Host "Longest path     : $($longest.Length) chars"
Write-Host "  $($longest.Path)"
Write-Host "Long paths (OS)  : $(if ($longPathsEnabled) { 'enabled' } else { 'disabled' })"
Write-Host "Limit            : $MaxLength chars"

$over = $results | Where-Object { $_.Length -gt $MaxLength }
if (-not $over) {
    $tight = $results | Where-Object { $_.Length -gt 250 }
    if ($tight) {
        Write-Host ""
        Write-Host "Heads-up: $($tight.Count) path(s) are above 250 chars (still under the Windows 260 limit)." -ForegroundColor Yellow
        Write-Host "Keep the repo at this depth, or enable long paths / move it to a shorter folder."
    }
    Write-Host "OK: every intermediate path fits." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "FAIL: $($over.Count) project(s) exceed the safe limit:" -ForegroundColor Red
$over | ForEach-Object { Write-Host ("  {0,4} chars  {1}" -f $_.Length, $_.Project) }

if (-not $longPathsEnabled) {
    Write-Host ""
    Write-Host "Fix one of:" -ForegroundColor Yellow
    Write-Host "  1. Keep the repo in a shorter folder, e.g. C:\src\NatsSyncLab"
    Write-Host "  2. Enable Windows long paths (admin PowerShell, then sign out):"
    Write-Host "     reg add `"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem`" /v LongPathsEnabled /t REG_DWORD /d 1 /f"
}

exit 1
