param([Parameter(Mandatory)][string]$InputDir)
$ErrorActionPreference = 'Stop'
# Isolated BCL evidence for the two distinct APIs used by the original loaders.
# No production catalog/parser method is reconstructed here.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
public static class FileEncodingOracle {
 public static string Run(string directory) {
  var rows=new List<object>();
  foreach(var file in Directory.GetFiles(directory,"*.json").OrderBy(p=>p,StringComparer.Ordinal)) {
   var text=File.ReadAllText(file);
   bool streamAccepted;
   try { using var stream=File.OpenRead(file); using var doc=JsonDocument.Parse(stream); streamAccepted=true; }
   catch(JsonException) { streamAccepted=false; }
   rows.Add(new{label=Path.GetFileNameWithoutExtension(file),text,streamAccepted});
  }
  return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),files=rows});
 }
}
'@
[FileEncodingOracle]::Run($InputDir)
