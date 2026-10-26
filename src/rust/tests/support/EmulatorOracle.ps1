param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$Core = Join-Path $RepoRoot 'iCemEmule.Core'
$Files = @(
    (Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),
    (Join-Path $Core 'Core/DeviceTemplate.cs'),
    (Join-Path $Core 'Core/TemplateCatalog.cs'),
    (Join-Path $Core 'Core/SimulationEngine.cs'),
    (Join-Path $Core 'Core/SimFileResolver.cs'),
    (Join-Path $Core 'Core/SignalWireCodec.cs'),
    (Join-Path $Core 'Core/LantableRegistry.cs'),
    (Join-Path $Core 'Core/UnitConversion.cs'),
    (Join-Path $Core 'Emitters/AceFrameBuilder.cs'),
    (Join-Path $Core 'Dfb/DfbSignal.cs'),
    (Join-Path $Core 'Dfb/DfbParameter.cs'),
    (Join-Path $Core 'Dfb/DfbContext.cs'),
    (Join-Path $Core 'Dfb/IDfbBlock.cs'),
    (Join-Path $Core 'Dfb/Blocks/FirstOrderLagBlock.cs'),
    (Join-Path $Core 'Dfb/Blocks/KickoutLatchBlock.cs'),
    (Join-Path $Core 'Dfb/Blocks/TotalizerBlock.cs'),
    (Join-Path $PSScriptRoot 'EmulatorOracle.cs')
)
$Files += @((Join-Path $RepoRoot 'src/net/iCemDataMonitor.Sdk/Diagnostics/OwnedFileLog.cs'),(Join-Path $RepoRoot 'src/net/iCemDataMonitor.Sdk/Diagnostics/StationFileLog.cs'))
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[EmulatorOracle]::Run($RepoRoot)
