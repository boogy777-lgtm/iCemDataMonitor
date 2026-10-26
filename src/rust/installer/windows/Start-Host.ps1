#requires -Version 7.4
[CmdletBinding()]
param([string] $StationConfig)
$ErrorActionPreference = 'Stop'
$Manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'release-manifest.json') | ConvertFrom-Json
$Templates = ($Manifest.binaries | Where-Object role -EQ 'host').templates_directory
$Arguments = @('--autostart', '--registry', (Join-Path $PSScriptRoot "$Templates/variable_registry.json"))
if ($StationConfig) { $Arguments += @('--ini', [IO.Path]::GetFullPath($StationConfig)) }
& (Join-Path $PSScriptRoot 'icem-host-dev.exe') @Arguments
exit $LASTEXITCODE

