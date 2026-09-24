# bump-version.ps1
# Bumps the patch number in frte2tg/version.txt if the app's source files changed since the last bump.
#
# Change detection: SHA256 per file, stored in frte2tg/.src-hash (committed together with version.txt).
#
# Usage:
#   .\scripts\bump-version.ps1             - bump if sources changed
#   .\scripts\bump-version.ps1 -Force      - bump regardless
#   .\scripts\bump-version.ps1 -Diagnose   - list changed files, no bump
#   .\scripts\bump-version.ps1 -Init       - record current sources as the baseline, no bump

param(
    [switch]$Force,
    [switch]$Diagnose,
    [switch]$Init
)

$root       = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'frte2tg'
$versionFile = Join-Path $projectDir 'version.txt'
$hashFile    = Join-Path $projectDir '.src-hash'
$utf8NoBom   = [Text.UTF8Encoding]::new($false)

function Get-FileHash256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    return [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path))).Replace('-', '').ToLower()
}

# relative path -> hash, for the files that make up the app
function Get-SourceFileHashes {
    $abs = [IO.Path]::GetFullPath($projectDir)
    $result = [ordered]@{}
    Get-ChildItem $projectDir -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.json' -or $_.Name -eq 'Dockerfile' } |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($abs.Length).TrimStart('\', '/')
            if ($rel -match '^(bin|obj|Properties)[/\\]') { return }
            $result[$rel] = Get-FileHash256 $_.FullName
        }
    return $result
}

function Read-StoredHashes {
    $result = [ordered]@{}
    if (-not (Test-Path $hashFile)) { return $result }
    foreach ($line in (Get-Content $hashFile)) {
        $eq = $line.IndexOf('=')
        if ($eq -gt 0) { $result[$line.Substring(0, $eq)] = $line.Substring($eq + 1) }
    }
    return $result
}

$version = (Get-Content $versionFile -Raw).Trim()
$current = Get-SourceFileHashes
$stored  = Read-StoredHashes

$changed = @()
foreach ($f in $current.Keys) {
    if (-not $stored.Contains($f))        { $changed += "+ $f" }
    elseif ($stored[$f] -ne $current[$f]) { $changed += "~ $f" }
}
foreach ($f in $stored.Keys) {
    if (-not $current.Contains($f))       { $changed += "- $f" }
}

if ($Diagnose) {
    Write-Host "frte2tg v$version"
    if ($changed.Count -eq 0) { Write-Host "  No changes." } else { $changed | ForEach-Object { Write-Host "  $_" } }
    exit 0
}

function Write-Hashes {
    [IO.File]::WriteAllText($hashFile, (($current.Keys | ForEach-Object { "$_=$($current[$_])" }) -join "`n"), $utf8NoBom)
}

if ($Init) {
    Write-Hashes
    Write-Host "  frte2tg: $version (baseline recorded)"
    exit 0
}

if ($changed.Count -eq 0 -and -not $Force) {
    Write-Host "  frte2tg: $version (no changes, skipped)"
    exit 0
}

$parts = $version.Split('.')
$parts[2] = [int]$parts[2] + 1
$newVersion = $parts -join '.'
[IO.File]::WriteAllText($versionFile, $newVersion, $utf8NoBom)
Write-Hashes
Write-Host "  frte2tg: $version -> $newVersion"
