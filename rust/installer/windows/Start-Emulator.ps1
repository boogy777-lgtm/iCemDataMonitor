#requires -Version 7.4
[CmdletBinding()]
param([string] $Config = (Join-Path $env:LOCALAPPDATA 'iCemDataMonitor-rust-dev/emule_config.xml'))
$ErrorActionPreference = 'Stop'
$Config = [IO.Path]::GetFullPath($Config)
[IO.Directory]::CreateDirectory((Split-Path $Config -Parent)) | Out-Null
& (Join-Path $PSScriptRoot 'icem-emulator-engine-dev.exe') --desktop --config $Config --templates (Join-Path $PSScriptRoot 'templates')
exit $LASTEXITCODE
