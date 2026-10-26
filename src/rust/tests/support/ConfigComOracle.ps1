param([Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$InputDir,[Parameter(Mandatory)][string]$OutputDir)
$ErrorActionPreference='Stop'
$Core=Join-Path $RepoRoot 'iCemEmule.Core/Core'
$Files=@((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),(Join-Path $Core 'EmuSettings.cs'),(Join-Path $Core 'DeviceTemplate.cs'),(Join-Path $Core 'SimulationEngine.cs'),(Join-Path $Core 'TemplateCatalog.cs'),(Join-Path $Core 'SimFileResolver.cs'),(Join-Path $Core 'UnitConversion.cs'))
$Source=Get-Content (Join-Path $Core 'EmulatorStep.cs') -Raw
$Start=$Source.IndexOf('    private double[] FrozenComValues()')
$End=$Source.IndexOf('    internal PreparedLanStep? PrepareLan',$Start)
if($Start -lt 0 -or $End -lt 0){throw 'Original FrozenComValues missing'}
$Method=$Source.Substring($Start,$End-$Start).Replace('ComEngine','_comEngine')
$LoopStart=$Source.IndexOf('        for (int i = 0; i < values.Length && i < template.Variables.Length; i++)')
$LoopEnd=$Source.IndexOf('        return SignalWireCodec.FormatLine(', $LoopStart)
if($LoopStart -lt 0 -or $LoopEnd -lt 0){throw 'Original COM unit loop missing'}
$UnitLoop=$Source.Substring($LoopStart,$LoopEnd-$LoopStart).Replace(', Units)', ', _unitSettings)')
$Wrapper=@'
using System;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using Halliburton.iCemEmule.Core;
public sealed class OriginalComFreezeOracle {
 private readonly SimulationEngine _comEngine;
 private readonly UnitSettings _unitSettings=new UnitSettings { Preset=UnitPreset.Metric };
 public OriginalComFreezeOracle(){
  var template=(DeviceTemplate)typeof(TemplateCatalog).GetMethod("BuildFallbackFlecs453",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null)!;
  typeof(TemplateCatalog).GetField("_templates",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,new System.Collections.Generic.List<DeviceTemplate>{template});
  _comEngine=new SimulationEngine();
 }
 private double[] Project(bool emulate) {
  var values=emulate?_comEngine.Tick():FrozenComValues();
  var template=_comEngine.Current;
  if(template is not null && values.Length>0) {
'@
$Wrapper+=$UnitLoop+"`n } return values; }`n"
$Wrapper+=@'
 public static string Run(){
  var o=new OriginalComFreezeOracle();var steps=new System.Collections.Generic.List<object>();
  void Add(double[] values){steps.Add(new {values=values.Select(v=>BitConverter.DoubleToUInt64Bits(v).ToString("X16")).ToArray(),cache=o._comEngine.LastValues.Select(v=>BitConverter.DoubleToUInt64Bits(v).ToString("X16")).ToArray()});}
  Add(o.Project(false)); Add(o.Project(false));
  o._comEngine.SetOverride("PumpPressure",1250.0);Add(o.Project(false));
  o._comEngine.ClearOverride("PumpPressure");Add(o.Project(false));
  o._comEngine.SetOverride("PumpPressure",2222.0);Add(o.Project(true));
  o._comEngine.ClearOverride("PumpPressure");Add(o.Project(false));
  o._comEngine.ResetClock();Add(o.Project(false));Add(o.Project(true));
  return JsonSerializer.Serialize(new {steps,template=o._comEngine.Current,runtime=Environment.Version.ToString()});
 }
'@
$Wrapper+=$Method+"`n}"
New-Item -ItemType Directory -Path $OutputDir -Force|Out-Null
$WrapperPath=Join-Path $OutputDir 'OriginalComFreezeOracle.cs'
$Wrapper|Set-Content $WrapperPath -Encoding utf8NoBOM
$Files += @((Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/OwnedFileLog.cs'),(Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/StationFileLog.cs'))
Add-Type -Path ($Files+@($WrapperPath)) -CompilerOptions '/nullable:enable' -IgnoreWarnings
foreach($File in Get-ChildItem $InputDir -File -Filter '*.xml'){
 [Halliburton.iCemEmule.Core.EmuSettings]::SettingsPathOverride=$File.FullName
 $Settings=[Halliburton.iCemEmule.Core.EmuSettings]::Load()
 [Halliburton.iCemEmule.Core.EmuSettings]::SettingsPathOverride=Join-Path $OutputDir $File.Name
 $Settings.Save()
}
[OriginalComFreezeOracle]::Run()|Set-Content (Join-Path $OutputDir 'com.json') -Encoding utf8NoBOM
