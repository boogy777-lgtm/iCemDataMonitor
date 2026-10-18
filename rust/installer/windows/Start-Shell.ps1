#requires -Version 7.4
[CmdletBinding()]
param([ValidateRange(1, 65535)] [int] $WebPort = 8090)
$Arguments = @('--pipe', 'iCemDataMonitor.Host')
if ($PSBoundParameters.ContainsKey('WebPort')) { $Arguments += @('--web-url', "http://127.0.0.1:$WebPort") }
& (Join-Path $PSScriptRoot 'icem-shell-dev.exe') @Arguments
exit $LASTEXITCODE

