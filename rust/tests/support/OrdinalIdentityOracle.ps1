# Independent BCL witness. It reads neither Rust source nor generator output.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
public static class OrdinalIdentityOracle {
 static bool? RuntimeFlag(string name) {
  var type=typeof(string).Assembly.GetType("System.Globalization.GlobalizationMode");
  var property=type?.GetProperty(name,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
  return property?.GetValue(null) as bool?;
 }
 public static string Run() {
  var classes = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
  var map = new List<int[]>(); int count=0;
  for(int scalar=0;scalar<=0x10ffff;scalar++) {
   if(scalar>=0xd800 && scalar<=0xdfff) continue;
   count++; var text=char.ConvertFromUtf32(scalar);
   if(classes.TryGetValue(text,out int canonical)) map.Add(new[]{scalar,canonical});
   else classes.Add(text,scalar);
  }
  bool useNls; bool nlsSet=AppContext.TryGetSwitch("System.Globalization.UseNls",out useNls);
  bool invariant; bool invariantSet=AppContext.TryGetSwitch("System.Globalization.Invariant",out invariant);
  return JsonSerializer.Serialize(new {scalarCount=count,classes=classes.Count,map,
   runtime=Environment.Version.ToString(),framework=RuntimeInformation.FrameworkDescription,
   os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
   invariantActual=RuntimeFlag("Invariant"),useNlsActual=RuntimeFlag("UseNls"),
   provider=RuntimeFlag("Invariant")==true?"Invariant":RuntimeFlag("UseNls")==true?"NLS":RuntimeFlag("UseNls")==false || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)?"ICU":"Unknown (see switches)",
   culture=CultureInfo.InvariantCulture.Name,sortVersion=CultureInfo.InvariantCulture.CompareInfo.Version.FullVersion,
   sortId=CultureInfo.InvariantCulture.CompareInfo.Version.SortId,
   useNls=nlsSet?(bool?)useNls:null,invariant=invariantSet?(bool?)invariant:null,
   nlsEnvironment=Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_USENLS"),
   invariantEnvironment=Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")});
 }
}
'@
[OrdinalIdentityOracle]::Run()
