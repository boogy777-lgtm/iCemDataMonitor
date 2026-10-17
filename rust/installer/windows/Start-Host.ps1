#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $StationConfig)
$ErrorActionPreference = 'Stop'
$Config = (Resolve-Path -LiteralPath $StationConfig).Path
& (Join-Path $PSScriptRoot 'icem-host-dev.exe') --station-config $Config --duration-ms 0 --rpc-pipe iCemDataMonitor.Host --registry (Join-Path $PSScriptRoot 'templates/variable_registry.json') --tray-icon (Join-Path $PSScriptRoot 'assets/iCemDataMonitor.ico')
exit $LASTEXITCODE
