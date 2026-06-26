<#
.SYNOPSIS
    Pack the backend (NuGet) and frontend (npm) packages together into ./artifacts.

.DESCRIPTION
    Local counterpart to .github/workflows/release.yml: produces the .nupkg files
    (dotnet pack) and the @dignite/vault-extract .tgz (nx build + npm pack) in one run,
    so both halves of a release are always cut from the same version.

    The version is read from its single sources of truth — <Version> in common.props for
    NuGet and "version" in angular/packages/vault-extract/package.json for npm — exactly as
    CI does; this script never edits them. Output lands in ./artifacts (git-ignored).

.EXAMPLE
    pwsh ./build/pack-all.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Resolve the repository root from this script's location so it runs from anywhere.
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'

Write-Host "==> Cleaning $artifacts" -ForegroundColor Cyan
if (Test-Path $artifacts) { Remove-Item "$artifacts/*" -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Write-Host "==> dotnet pack (NuGet)" -ForegroundColor Cyan
& dotnet pack (Join-Path $repoRoot 'Dignite.Vault.Extract.slnx') --configuration $Configuration -o $artifacts
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed ($LASTEXITCODE)" }

$angular = Join-Path $repoRoot 'angular'
Push-Location $angular
try {
    Write-Host "==> nx build vault-extract (npm)" -ForegroundColor Cyan
    & npx nx build vault-extract --configuration production
    if ($LASTEXITCODE -ne 0) { throw "nx build failed ($LASTEXITCODE)" }

    Write-Host "==> npm pack (tarball)" -ForegroundColor Cyan
    & npm pack ./dist/vault-extract --pack-destination $artifacts
    if ($LASTEXITCODE -ne 0) { throw "npm pack failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

Write-Host "`n==> Packages in $artifacts" -ForegroundColor Green
Get-ChildItem $artifacts -Include *.nupkg, *.tgz -Recurse | ForEach-Object { Write-Host "    $($_.Name)" }
