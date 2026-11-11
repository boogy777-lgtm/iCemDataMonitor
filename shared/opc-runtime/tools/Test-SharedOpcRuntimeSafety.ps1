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
$ExpectedCases = @('lifecycle', 'rollback', 'foreign-registry', 'foreign-component', 'clients', 'shared-owner', 'descriptor-repair', 'descriptor-adversarial')
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
$workflow = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../../.github/workflows/opc-runtime-acceptance.yml'))
function Assert-PrivateWorkflow([string] $text) {
    $start = $text.IndexOf("jobs:`n", [StringComparison]::Ordinal)
    if ($start -lt 0) { $start = $text.IndexOf("jobs:`r`n", [StringComparison]::Ordinal) }
    if ($start -lt 0) { throw 'Runtime workflow has no jobs.' }
    $jobText = $text.Substring($start)
    $matches = [regex]::Matches($jobText, '(?ms)^  (?<name>[A-Za-z0-9-]+):[ \t]*\r?\n(?<body>.*?)(?=^  [A-Za-z0-9-]+:[ \t]*\r?$|\z)')
    $jobs = @{}
    foreach ($match in $matches) {
        $name = $match.Groups['name'].Value
        if ($jobs.ContainsKey($name)) { throw 'Duplicate Runtime CI job.' }
        $jobs[$name] = $match.Groups['body'].Value
    }
    $expectedJobs = @('require-private-ci', 'build-blocked-runtime', 'disposable-installation')
    if ((($jobs.Keys | Sort-Object) -join ',') -cne (($expectedJobs | Sort-Object) -join ',') -or
        $jobs['require-private-ci'] -notmatch '\bAssert-SharedOpcPrivateCi\b' -or
        $jobs['require-private-ci'] -notmatch '\bGITHUB_EVENT_PATH\b' -or
        $jobs['require-private-ci'] -match '(?i)upload-artifact|msiexec|dotnet build|wix build' -or
        $jobs['build-blocked-runtime'] -notmatch '(?m)^    needs: require-private-ci\s*$' -or
        $jobs['disposable-installation'] -notmatch '(?m)^    needs: build-blocked-runtime\s*$') {
        throw 'Every Runtime CI job must be downstream of the private-repository gate.'
    }
}
Assert-PrivateWorkflow $workflow
foreach ($mutation in @(
    $workflow.Replace('needs: require-private-ci', 'needs: unrelated'),
    $workflow.Replace('needs: build-blocked-runtime', 'needs: unrelated'),
    $workflow.Replace('Assert-SharedOpcPrivateCi', 'MissingPrivateCheck'),
    ($workflow + "`n  unguarded-job:`n    runs-on: windows-2022`n")
)) {
    $rejected = $false
    try { Assert-PrivateWorkflow $mutation } catch { $rejected = $true }
    if (-not $rejected) { throw 'Private Runtime workflow gate accepted a negative mutation.' }
}
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
$candidateSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../installer/tests/TestOnlyRepairSnapshot.cpp'))
$productionSources = $authoring.OuterXml +
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../installer/New-OpcRuntimeModule.ps1')) +
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../installer/Build-SharedRuntime.ps1'))
function Assert-CandidateIsolation([string] $production, [string] $fixture, [string] $source) {
    $copy = $fixture.IndexOf('Copy-Item -LiteralPath $Msi -Destination $Candidate', [StringComparison]::Ordinal)
    $insert = $fixture.IndexOf("Add-TestBinary 'IcemTestSnapshot'", [StringComparison]::Ordinal)
    $privacy = $fixture.IndexOf('Assert-SharedOpcPrivateCi ', [StringComparison]::Ordinal)
    if ($production.Contains('IcemTestSnapshot', [StringComparison]::Ordinal) -or
        $production.Contains('TestOnlyRepairSnapshot', [StringComparison]::Ordinal) -or
        $privacy -lt 0 -or $copy -le $privacy -or $insert -le $copy -or
        -not $source.Contains('TEST ONLY', [StringComparison]::Ordinal) -or
        $source -notmatch '\bReadRepairSnapshot\s*\(' -or $source -notmatch '\bMsiSetPropertyW\s*\(' -or
        $source -match '\b(RegSet|RegCreate|RegDelete|AdjustTokenPrivileges|WriteProcessMemory)\w*\s*\(') {
        throw 'Snapshot writer fixture must remain test-only and confined to the disposable MSI copy.'
    }
}
Assert-CandidateIsolation $productionSources $fixtureSource $candidateSource
$negativeCandidates = @(
    [pscustomobject]@{ production = ($productionSources + 'IcemTestSnapshot'); fixture = $fixtureSource; source = $candidateSource }
    [pscustomobject]@{ production = $productionSources; fixture = ("Add-TestBinary 'IcemTestSnapshot'" + $fixtureSource); source = $candidateSource }
    [pscustomobject]@{ production = $productionSources; fixture = $fixtureSource; source = ($candidateSource + '; RegSetValueExW(') }
    [pscustomobject]@{ production = $productionSources; fixture = $fixtureSource; source = $candidateSource.Replace('ReadRepairSnapshot(', 'OtherReader(') }
    [pscustomobject]@{ production = $productionSources; fixture = $fixtureSource.Replace('Assert-SharedOpcPrivateCi ', 'MissingPrivateCheck '); source = $candidateSource }
)
foreach ($mutation in $negativeCandidates) {
    $rejected = $false
    try { Assert-CandidateIsolation $mutation.production $mutation.fixture $mutation.source } catch { $rejected = $true }
    if (-not $rejected) { throw 'Snapshot fixture isolation accepted a negative mutation.' }
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
