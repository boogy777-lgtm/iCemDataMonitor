param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference='Stop'
$Core=Join-Path $RepoRoot 'iCemEmule.Core'
$Files=@((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),(Join-Path $Core 'Control/IEmuControl.cs'),(Join-Path $Core 'Control/CommandProcessor.cs'),(Join-Path $PSScriptRoot 'ControlOracle.cs'))
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[ControlOracle]::Run((Join-Path $PSScriptRoot 'control-cases.json'))
