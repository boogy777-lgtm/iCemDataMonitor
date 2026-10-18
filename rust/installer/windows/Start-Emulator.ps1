#requires -Version 7.4
[CmdletBinding()]
param([string] $Config = (Join-Path $env:LOCALAPPDATA 'iCemDataMonitor-rust-dev/emule_config.xml'))
$ErrorActionPreference = 'Stop'
$Manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'release-manifest.json') | ConvertFrom-Json
$Templates = ($Manifest.binaries | Where-Object role -EQ 'emulator').templates_directory
$Config = [IO.Path]::GetFullPath($Config)
[IO.Directory]::CreateDirectory((Split-Path $Config -Parent)) | Out-Null
& (Join-Path $PSScriptRoot 'icem-emulator-engine-dev.exe') --desktop --config $Config --templates (Join-Path $PSScriptRoot $Templates)
exit $LASTEXITCODE
