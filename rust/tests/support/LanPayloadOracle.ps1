param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$Core = Join-Path $RepoRoot 'iCemEmule.Core'
$Files = @((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'))
$Files += @('DeviceTemplate','SimFileResolver','SignalWireCodec','LantableRegistry','UnitConversion','AcePayloadProvider','DfbAcePayloadProvider','FrozenPayloadProvider','ManualOverridePayloadProvider','UnitConvertingPayloadProvider') | ForEach-Object { Join-Path $Core "Core/$_.cs" }
$Files += @(Get-ChildItem (Join-Path $Core 'Dfb') -Filter '*.cs' -File -Recurse | ForEach-Object FullName)
$Files += (Join-Path $PSScriptRoot 'LanPayloadOracle.cs')
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[LanPayloadOracle]::Run($RepoRoot)
