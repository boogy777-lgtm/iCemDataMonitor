param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$Dfb = Join-Path $RepoRoot 'iCemEmule.Core/Dfb'
$Files = @((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'))
$Files += @(Get-ChildItem $Dfb -Filter '*.cs' -File -Recurse | ForEach-Object FullName)
$Files += (Join-Path $PSScriptRoot 'DfbNetworkOracle.cs')
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[DfbNetworkOracle]::Run()
