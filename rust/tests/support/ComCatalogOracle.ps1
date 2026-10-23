param([Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$InputDir,[Parameter(Mandatory)][string]$OutputDir)
$ErrorActionPreference='Stop'
$Core=Join-Path $RepoRoot 'iCemEmule.Core/Core'
$Source=Get-Content (Join-Path $Core 'TemplateCatalog.cs') -Raw
# Bind only the two ambient file locations. Every method body and DTO remains
# byte-identical to the original source; no test-authored catch or deserializer.
$TemplatesField=[regex]::Match($Source,'private static readonly string TemplatesDir\s*=\s*Path.Combine\(AppDomain.CurrentDomain.BaseDirectory, "templates"\);')
$LogField=[regex]::Match($Source,'private static readonly string LogPath\s*=\s*Path.Combine\(AppDomain.CurrentDomain.BaseDirectory, "iCemEmule.templates.log"\);')
if(-not $TemplatesField.Success -or -not $LogField.Success){throw 'Original TemplateCatalog path bindings not found'}
$TemplatesBinding='private static string TemplatesDir => global::ComCatalogFixturePaths.Current;'
$LogBinding='private static string LogPath => Path.Combine(global::ComCatalogFixturePaths.Current, "oracle.log");'
$Generated=$Source.Replace($TemplatesField.Value,$TemplatesBinding).Replace($LogField.Value,$LogBinding)
if($Generated.Replace($TemplatesBinding,$TemplatesField.Value).Replace($LogBinding,$LogField.Value) -cne $Source){throw 'Oracle changed more than the two path bindings'}
New-Item -ItemType Directory -Path $OutputDir -Force|Out-Null
$CatalogPath=Join-Path $OutputDir 'OriginalTemplateCatalog.cs'
$Generated|Set-Content $CatalogPath -Encoding utf8NoBOM
$Wrapper=@'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using Halliburton.iCemEmule.Core;
public static class ComCatalogFixturePaths { public static string Current=""; }
public static class OriginalComCatalogOracle {
 public static string Run(string input) {
  var flags=BindingFlags.NonPublic|BindingFlags.Static;
  var cases=new List<object>();
  foreach(var directory in Directory.GetDirectories(input).OrderBy(p=>p,StringComparer.Ordinal)) {
   ComCatalogFixturePaths.Current=directory;
   typeof(TemplateCatalog).GetField("_templates",flags)!.SetValue(null,null);
   typeof(TemplateCatalog).GetField("_registryVars",flags)!.SetValue(null,null);
   var log=Path.Combine(directory,"oracle.log");
   if(File.Exists(log)) File.Delete(log);
   var templates=TemplateCatalog.Load(); // actual LoadRegistryVariables/Load/build/resolve/fallback bodies
   var warnings=File.Exists(log)?File.ReadAllLines(log):Array.Empty<string>();
   var counts=new[]{
    warnings.Count(s=>s.Contains("WARN: failed to parse variable_registry.json:")),
    warnings.Count(s=>s.Contains("WARN: variable_registry.json not found")),
    warnings.Count(s=>s.Contains("WARN: failed to load template '")),
    warnings.Count(s=>s.Contains("WARN: registry variable '")),
    warnings.Count(s=>s.Contains("WARN: zero template files loaded"))};
   if(counts.Sum()!=warnings.Length) throw new InvalidOperationException("Unclassified original catalog warning");
   var rows=templates.Select(t=>new{template=t,line=SignalWireCodec.FormatLine(t.Variables.Select(v=>SimulationEngine.ComputeValue(v,1)).ToArray(),t)}).ToArray();
   cases.Add(new{label=Path.GetFileName(directory),templates=rows,warnings=counts});
  }
  return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),cases});
 }
}
'@
$WrapperPath=Join-Path $OutputDir 'OriginalComCatalogOracle.cs'
$Wrapper|Set-Content $WrapperPath -Encoding utf8NoBOM
$Files=@((Join-Path $PSScriptRoot 'EmulatorOracleGlobals.cs'),(Join-Path $Core 'DeviceTemplate.cs'),(Join-Path $Core 'SimulationEngine.cs'),(Join-Path $Core 'SimFileResolver.cs'),(Join-Path $Core 'SignalWireCodec.cs'),(Join-Path $Core 'LantableRegistry.cs'),$CatalogPath,$WrapperPath)
Add-Type -Path $Files -CompilerOptions '/nullable:enable' -IgnoreWarnings
[OriginalComCatalogOracle]::Run($InputDir)
