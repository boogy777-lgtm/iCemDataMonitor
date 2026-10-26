param([Parameter(Mandatory)][string]$RepoRoot, [string]$LoadingFixtures)
$ErrorActionPreference = 'Stop'
$Core = Join-Path $RepoRoot 'iCemEmule.Core'
$Files = @((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'))
$Files += @('DeviceTemplate','ISignalValueProvider','SimFileResolver','SignalWireCodec','LantableRegistry','UnitConversion','AcePayloadProvider','DfbAcePayloadProvider','FrozenPayloadProvider','ManualOverridePayloadProvider','UnitConvertingPayloadProvider') | ForEach-Object { Join-Path $Core "Core/$_.cs" }
$Files += @(Get-ChildItem (Join-Path $Core 'Dfb') -Filter '*.cs' -File -Recurse | ForEach-Object FullName)
$Files += (Join-Path $PSScriptRoot 'LanPayloadOracle.cs')
$Files += @((Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/OwnedFileLog.cs'),(Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/StationFileLog.cs'))
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
if ($LoadingFixtures) { [LanPayloadOracle]::Loading($LoadingFixtures) } else { [LanPayloadOracle]::Run($RepoRoot) }
