param([Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$PairsPath)
$ErrorActionPreference='Stop'
$Core=Join-Path $RepoRoot 'iCemEmule.Core/Core'
# Compile unmodified original classes. Reflection supplies constructor-owned
# state to avoid ambient installation discovery; every invoked method is original.
$Wrapper=@'
public static class UnicodeIdentityOracle {
 public static string Run(string pairsPath) {
  var rows=new System.Collections.Generic.List<object>();
  using var doc=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(pairsPath));
  foreach(var row in doc.RootElement.EnumerateArray()) {
   var name=row.GetProperty("name").GetString()!;var query=row.GetProperty("query").GetString()!;
   var actual=new Halliburton.iCemEmule.Core.DeviceTemplate {Name=name,DeviceType="FLECS",Variables=new[]{new Halliburton.iCemEmule.Core.TemplateVariable{Name=name,LegacyName="alias-"+name,BaseValue=11}}};
   var sentinel=new Halliburton.iCemEmule.Core.DeviceTemplate{Name="sentinel",DeviceType="FLECS"};
   var engine=(Halliburton.iCemEmule.Core.SimulationEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Halliburton.iCemEmule.Core.SimulationEngine));
   var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
   typeof(Halliburton.iCemEmule.Core.SimulationEngine).GetField("_lock",flags)!.SetValue(engine,new object());
   typeof(Halliburton.iCemEmule.Core.SimulationEngine).GetField("_overrides",flags)!.SetValue(engine,new System.Collections.Generic.Dictionary<string,double>(System.StringComparer.OrdinalIgnoreCase));
   typeof(Halliburton.iCemEmule.Core.SimulationEngine).GetField("<Templates>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(engine,new[]{sentinel,actual});
   engine.SetTemplate("flecs",query);var selected=engine.Current!.Name;
   engine.SetTemplate("flecs",name);
   bool found=engine.TryFindVariable(query,out _,out _);
   bool alias=engine.TryFindVariable("ALIAS-"+query,out _,out _);
   engine.SetOverride(query,13);engine.SetOverride(name,19);
   var overrides=engine.SnapshotOverrides();var keys=overrides.Keys.ToArray();var updated=overrides[query];
   var tick=engine.Tick();bool cleared=engine.ClearOverride(query);var after=engine.Tick();
   rows.Add(new{name,query,equal=System.StringComparer.OrdinalIgnoreCase.Equals(name,query),selected,found,alias,count=overrides.Count,keys,updated,tick,cleared,after});
  }
  return System.Text.Json.JsonSerializer.Serialize(new{runtime=System.Environment.Version.ToString(),rows});
 }
}
'@
$Files=@((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),(Join-Path $Core 'DeviceTemplate.cs'),(Join-Path $Core 'SimulationEngine.cs'),(Join-Path $Core 'TemplateCatalog.cs'),(Join-Path $Core 'SimFileResolver.cs'))
# C# source fixture injected as a separate compilation unit; original files unmodified.
$temp=Join-Path ([System.IO.Path]::GetTempPath()) ('icem-identity-oracle-'+[guid]::NewGuid()+'.cs')
try {
 $Wrapper|Set-Content $temp -Encoding utf8NoBOM
 $Files += @((Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/OwnedFileLog.cs'),(Join-Path $RepoRoot 'iCemDataMonitor.Sdk/Diagnostics/StationFileLog.cs'))
 Add-Type -Path ($Files+@($temp)) -CompilerOptions '/nullable:enable' -IgnoreWarnings
 [UnicodeIdentityOracle]::Run($PairsPath)
} finally { Remove-Item $temp -ErrorAction SilentlyContinue }
