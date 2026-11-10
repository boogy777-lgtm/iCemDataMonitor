#requires -Version 7.4
# Read-only/local policy fixture. The mutating fixture must refuse before any output.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = Split-Path $PSScriptRoot -Parent
$Fixture = Join-Path $PSScriptRoot 'Test-SharedOpcRuntime.ps1'
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($Fixture, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$ExpectedCases = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component', 'clients')
$Cases = Get-Content -LiteralPath (Join-Path $Root 'installer/opc-core/acceptance-cases.json') -Raw | ConvertFrom-Json
if ((($Cases.cases | Sort-Object) -join ',') -cne (($ExpectedCases | Sort-Object) -join ',')) { throw 'Acceptance coverage drift' }
$authoring = [xml](Get-Content -LiteralPath (Join-Path $Root 'installer/opc-core/SharedRuntime.wxs') -Raw)
$blocks = @($authoring.SelectNodes('//*[local-name()="Launch"]') | Where-Object { $_.Condition -ceq 'Installed' })
if ($blocks.Count -ne 1) { throw 'Production fresh-install protection was removed' }
$Output = Join-Path ([IO.Path]::GetTempPath()) ('icem-opc-refusal-' + [guid]::NewGuid().ToString('N'))
$Rejected = $false
$SavedActions = $env:GITHUB_ACTIONS
try {
    # Process-local negative input only; never persist environment variables.
    $env:GITHUB_ACTIONS = 'false'
    try { & $Fixture -Msi (Join-Path $Output 'missing.msi') -Case lifecycle -EvidenceDirectory $Output }
    catch {
        if (-not $_.Exception.Message.StartsWith('Refusing installation:', [StringComparison]::Ordinal)) { throw }
        $Rejected = $true
    }
} finally { $env:GITHUB_ACTIONS = $SavedActions }
if (-not $Rejected -or (Test-Path -LiteralPath $Output)) { throw 'Acceptance fixture touched a non-disposable machine' }
Write-Output 'PASS: parser, case coverage, production block and refusal before file/registry/service/MSI effects.'
