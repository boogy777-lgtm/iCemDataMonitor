$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Build-OpcCore.ps1'
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw "PowerShell parse errors: $errors" }

# Existing output is refused before invoking any external build tool or changing it.
$before = (Get-Item -LiteralPath $PSScriptRoot).LastWriteTimeUtc
$rejected = $false
try { & $script -SourceRepository $PSScriptRoot -OutputDirectory $PSScriptRoot }
catch {
    if ($_.Exception.Message -notlike '*OutputDirectory must not already exist*') { throw }
    $rejected = $true
}
if (-not $rejected -or (Get-Item -LiteralPath $PSScriptRoot).LastWriteTimeUtc -ne $before) { throw 'Existing output guard failed.' }
Write-Output 'PASS: parser and existing-output refusal (no system mutations).'
