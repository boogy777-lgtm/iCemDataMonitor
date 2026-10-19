using System.Text.Json;
using Halliburton.iCemEmule.Control;
// Domain-shaped deterministic backend fixtures; original CommandProcessor is unchanged.
public static class ControlOracle {
 private sealed class Fake:IEmuControl {
 public List<object> Calls=new();
 static Dictionary<string,object?> D(params (string,object?)[] fields)=>fields.ToDictionary(f=>f.Item1,f=>f.Item2);
 static string Sv(uint n)=>$"0x{n:X8}";
 static string Ch(string c)=>c.Equals("lan",StringComparison.OrdinalIgnoreCase)?"lan":"com";
 static object Variable(string name="Pressure")=>new {name,uom="psi",value=12.5,overridden=true,excludeFromTelemetry=false};
 private ControlResult Call(string method,Dictionary<string,object?> data,params object?[] args){Calls.Add(new{method,args});if(args.Any(v=>v is string s && s=="fail"))return ControlResult.Fail("backend rejected");return ControlResult.Success(data);}
 public Dictionary<string,object?> GetStatus(){Calls.Add(new{method="GetStatus",args=Array.Empty<object>()});return D(("proto",99),("uptimeSec",3.5),("pid",7),("version","fixture"),("selfTest",null),
 ("com",new {streaming=false,emulating=false,portOpen=false,port=(string?)null,baud=0,linesSent=0,linesPerSec=0.25,deviceType=(string?)null,template=(string?)null,variableCount=0,values=new Dictionary<string,double>()}),
 ("lan",new {streaming=false,emulating=false,adapterOpen=false,adapter=(string?)null,framesSent=0,framesPerSec=0.5,npcapAvailable=false,equipment=Sv(0),svCount=0,firmwareSv=false,svs=Array.Empty<string>(),targets=Array.Empty<string>()}),
 ("units",new {preset="",flowOverride=(string?)null,temperatureOverride=(string?)null,densityOverride=(string?)null,volumeOverride=(string?)null,levelOverride=(string?)null,pressureOverride=(string?)null}),("overrides",Array.Empty<object>()));}
 public ControlResult StartChannel(string c)=>Call("StartChannel",Ch(c)=="com"?D(("channel","com"),("port","COM12"),("streaming",true)):D(("channel","lan"),("adapter","Ethernet"),("streaming",true)),c);
 public ControlResult StopChannel(string c)=>Call("StopChannel",D(("channel",Ch(c)),("wasStreaming",true),("streaming",false)),c);
 public ControlResult SetVariable(string? c,string n,double v,uint? sv)=>Call("SetVariable",D(("applied",new[]{c!=null&&Ch(c)=="lan"?D(("channel","lan"),("sv",Sv(sv??42)),("name",n),("uom","psi")):D(("channel","com"),("name",n),("uom","psi"))}),("value",12.5),("units","native"),("note","LAN overrides are pre-unit-conversion (native); the wire value follows the Units preset")),c,n,BitConverter.DoubleToUInt64Bits(v).ToString("X16"),sv);
 public ControlResult ClearVariable(string? c,string n,uint? sv)=>Call("ClearVariable",D(("cleared",new[]{c==null?"com":Ch(c)})),c,n,sv);
 public ControlResult GetVariable(string? c,string n,uint? sv)=>Call("GetVariable",c!=null&&Ch(c)=="lan"?D(("channel","lan"),("sv",Sv(sv??42)),("name",n),("uom","psi"),("value",12.5),("native",true),("overridden",true)):D(("channel","com"),("name",n),("uom","psi"),("value",12.5),("overridden",true),("excludeFromTelemetry",false)),c,n,sv);
 public ControlResult GetProfiles()=>Call("GetProfiles",D(("profiles",new[]{new {deviceType="FLECS",name="Test",variableCount=1,active=true}})));
 public ControlResult SetProfile(string? d,string n)=>Call("SetProfile",D(("deviceType",d??"fixture"),("name",n),("note","template swap is on-the-fly; COM stream not interrupted")),d,n);
 public ControlResult GetVariables(string? c){var data=D();if(c==null||string.Equals(c,"com",StringComparison.OrdinalIgnoreCase))data["com"]=new {template="Test",variables=new[]{Variable()}};if(c==null||string.Equals(c,"lan",StringComparison.OrdinalIgnoreCase))data["lan"]=new {variables=new[]{new {sv=Sv(42),name="Pressure",uom="psi",value=12.5,valueType="float",byteOffset=4,size=4,transmittable=true,overridden=true}}};return Call("GetVariables",data,c);}
 public ControlResult GetPorts()=>Call("GetPorts",D(("ports",new[]{new{name="COM12",open=true}})));
 public ControlResult GetAdapters()=>Call("GetAdapters",D(("adapters",new[]{new{name="Ethernet",open=true}}),("npcapAvailable",true)));
 public ControlResult SetPort(string p)=>Call("SetPort",D(("port",p),("streaming",true)),p);
 public ControlResult SetAdapter(string a)=>Call("SetAdapter",D(("adapter",a),("streaming",true)),a);
 public ControlResult SetEquipment(uint e)=>Call("SetEquipment",D(("equipment",Sv(e))),e);
 public ControlResult SetFirmware(bool on)=>Call("SetFirmware",D(("firmwareSv",on)),on);
 public ControlResult SetEmulation(string c,bool on)=>Call("SetEmulation",D(("channel",Ch(c)),("emulating",on)),c,on);
 public ControlResult SetPreset(int p)=>Call("SetPreset",D(("preset",new[]{"FirmwareDefault","LanNative","Metric","Imperial"}[p])),p);
 public ControlResult SetUnit(string g,string? u)=>Call("SetUnit",D(("group",g),("unit",u)),g,u);
 public void RequestQuit(){Calls.Add(new{method="RequestQuit",args=Array.Empty<object>()});}
 }
 public static string Run(string path){var cases=JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;var rows=new List<object>();foreach(var line in cases){var fake=new Fake();var r=CommandProcessor.ProcessLine(line,fake);rows.Add(new{line,cmd=r.Cmd,reply=JsonSerializer.Deserialize<JsonElement>(r.Json),start_watch=r.StartWatch,quit=r.Quit,calls=fake.Calls});}return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),rows});}
}
