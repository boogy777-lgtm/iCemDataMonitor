#requires -Version 7.4
[CmdletBinding()]
param([ValidateRange(1, 65535)] [int] $WebPort = 8090)
& (Join-Path $PSScriptRoot 'icem-shell-dev.exe') --pipe iCemDataMonitor.Host --web-url "http://127.0.0.1:$WebPort"
exit $LASTEXITCODE
