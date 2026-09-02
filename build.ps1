<#
.SYNOPSIS
  Thin build wrapper at the repo root.

.DESCRIPTION
  Always builds the .NET solution. With -Version it additionally builds the
  MSI via installer\build-msi.ps1.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
  powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.4.4

.NOTES
  PowerShell 5.1 compatible (no &&, no ??, no ternary). ASCII only.
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$Repo = $PSScriptRoot
$sln = Join-Path $Repo 'iCemDataMonitor.sln'
if (-not (Test-Path -LiteralPath $sln)) { throw "Solution not found: $sln" }

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must match x.y.z (e.g. 1.4.4)."
}

Write-Host "== dotnet build ($Configuration)"
dotnet build $sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Version) {
    Write-Host "== MSI build ($Version, $Configuration)"
    & (Join-Path $Repo 'installer\build-msi.ps1') -MsiVersion $Version -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

exit 0
