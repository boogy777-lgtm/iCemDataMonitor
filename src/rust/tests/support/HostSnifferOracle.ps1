param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$Files = @(
    (Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),
    (Join-Path $RepoRoot 'iCemDataMonitor.Plugins.Icem/LantableRegistry.cs'),
    (Join-Path $PSScriptRoot 'HostSnifferOracle.cs')
)
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[HostSnifferOracle]::Run($RepoRoot)
