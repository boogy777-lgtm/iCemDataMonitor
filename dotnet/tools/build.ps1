<#
.SYNOPSIS
  Build wrapper for the .NET family sources.

.DESCRIPTION
  Always builds the .NET solution. With -Version it additionally builds the
  MSI via dotnet\installer\build-msi.ps1.

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

$DotNetRoot = Split-Path $PSScriptRoot -Parent
$Repo = Split-Path $DotNetRoot -Parent
$sln = Join-Path $DotNetRoot 'iCemDataMonitor.sln'
if (-not (Test-Path -LiteralPath $sln)) { throw "Solution not found: $sln" }

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must match x.y.z (e.g. 1.4.4)."
}

Write-Host "== dotnet build ($Configuration)"
Push-Location $DotNetRoot
try { dotnet build $sln -c $Configuration --nologo } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Version) {
    Write-Host "== MSI build ($Version, $Configuration)"
    & (Join-Path $DotNetRoot 'installer\build-msi.ps1') -MsiVersion $Version -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

exit 0
