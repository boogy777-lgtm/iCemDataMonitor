#requires -Version 7.4
# Read-only/local policy fixture. The mutating fixture must refuse before any output.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Fixture = Join-Path $PSScriptRoot 'Test-SharedOpcRuntime.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Fixture, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$ExpectedCases = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component', 'clients', 'shared-owner', 'descriptor-repair')
$Cases = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/acceptance-cases.json') -Raw | ConvertFrom-Json
if ((($Cases.cases | Sort-Object) -join ',') -cne (($ExpectedCases | Sort-Object) -join ',')) { throw 'Acceptance coverage drift' }
$dispatch = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.SwitchStatementAst] -and $node.Condition.Extent.Text -ceq '$Case' }, $true))
if ($dispatch.Count -ne 1 -or $null -eq $dispatch[0].Default) { throw 'Missing exhaustive acceptance dispatcher.' }
$implemented = @($dispatch[0].Clauses | ForEach-Object {
    if ($_.Item1 -isnot [Management.Automation.Language.StringConstantExpressionAst]) { throw 'Nonliteral acceptance case.' }
    $_.Item1.Value
})
if ((($implemented | Sort-Object) -join ',') -cne (($ExpectedCases | Sort-Object) -join ',')) { throw 'Acceptance dispatcher coverage drift' }
$authoring = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/SharedRuntime.wxs') -Raw)
$blocks = @($authoring.SelectNodes('//*[local-name()="Launch"]') | Where-Object { $_.Condition -ceq 'Installed' })
if ($blocks.Count -ne 1) { throw 'Production fresh-install protection was removed' }
$probeSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../installer/tests/BoundaryProbe.cpp'))
$fixtureSource = [IO.File]::ReadAllText($Fixture)
function Assert-ProbeIsolation([string] $production, [string] $fixture, [string] $source) {
    $copy = $fixture.IndexOf('Copy-Item -LiteralPath $Msi -Destination $Candidate', [StringComparison]::Ordinal)
    if ($production.Contains('IcemBoundaryProbe', [StringComparison]::Ordinal) -or
        $source -match '\b(RegSet|RegCreate|RegDelete|MsiSetProperty|AdjustTokenPrivileges|WriteProcessMemory)\w*\s*\(' -or
        $source -notmatch '\bMsiGetPropertyW\s*\(' -or $source -notmatch '\bRegOpenKeyExW\s*\(' -or
        $copy -lt 0 -or $fixture.IndexOf('IcemBoundaryProbe', [StringComparison]::Ordinal) -le $copy) {
        throw 'Boundary diagnostics must remain read-only and confined to the disposable MSI copy.'
    }
}
Assert-ProbeIsolation $authoring.OuterXml $fixtureSource $probeSource
$negativeProbes = @(
    [pscustomobject]@{ production = ($authoring.OuterXml + 'IcemBoundaryProbe'); fixture = $fixtureSource; source = $probeSource }
    [pscustomobject]@{ production = $authoring.OuterXml; fixture = ('IcemBoundaryProbe' + $fixtureSource); source = $probeSource }
    [pscustomobject]@{ production = $authoring.OuterXml; fixture = $fixtureSource; source = ($probeSource + '; RegSetValueExW(') }
)
foreach ($mutation in $negativeProbes) {
    $rejected = $false
    try { Assert-ProbeIsolation $mutation.production $mutation.fixture $mutation.source } catch { $rejected = $true }
    if (-not $rejected) { throw 'Probe isolation guard accepted a negative mutation.' }
}
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
