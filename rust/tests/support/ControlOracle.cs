using System.Text.Json;
using Halliburton.iCemEmule.Control;
public static class ControlOracle {
 private sealed class Fake:IEmuControl {
 public List<object> Calls=new();
 private ControlResult Call(string method,params object?[] args){Calls.Add(new{method,args});if(args.Any(v=>v is string s && s=="fail"))return ControlResult.Fail("backend rejected");return ControlResult.Success(new(){["method"]=method,["args"]=args});}
 public Dictionary<string,object?> GetStatus(){Call("GetStatus");return new(){["state"]="ready",["proto"]=99};}
 public ControlResult StartChannel(string c)=>Call("StartChannel",c);
 public ControlResult StopChannel(string c)=>Call("StopChannel",c);
 public ControlResult SetVariable(string? c,string n,double v,uint? sv)=>Call("SetVariable",c,n,BitConverter.DoubleToUInt64Bits(v).ToString("X16"),sv);
 public ControlResult ClearVariable(string? c,string n,uint? sv)=>Call("ClearVariable",c,n,sv);
 public ControlResult GetVariable(string? c,string n,uint? sv)=>Call("GetVariable",c,n,sv);
 public ControlResult GetProfiles()=>Call("GetProfiles");
 public ControlResult SetProfile(string? d,string n)=>Call("SetProfile",d,n);
 public ControlResult GetVariables(string? c)=>Call("GetVariables",c);
 public ControlResult GetPorts()=>Call("GetPorts");
 public ControlResult GetAdapters()=>Call("GetAdapters");
 public ControlResult SetPort(string p)=>Call("SetPort",p);
 public ControlResult SetAdapter(string a)=>Call("SetAdapter",a);
 public ControlResult SetEquipment(uint e)=>Call("SetEquipment",e);
 public ControlResult SetFirmware(bool on)=>Call("SetFirmware",on);
 public ControlResult SetEmulation(string c,bool on)=>Call("SetEmulation",c,on);
 public ControlResult SetPreset(int p)=>Call("SetPreset",p);
 public ControlResult SetUnit(string g,string? u)=>Call("SetUnit",g,u);
 public void RequestQuit(){Calls.Add(new{method="RequestQuit",args=Array.Empty<object>()});}
 }
 public static string Run(string path){var cases=JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;var rows=new List<object>();foreach(var line in cases){var fake=new Fake();var r=CommandProcessor.ProcessLine(line,fake);rows.Add(new{line,cmd=r.Cmd,reply=JsonSerializer.Deserialize<JsonElement>(r.Json),start_watch=r.StartWatch,quit=r.Quit,calls=fake.Calls});}return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),rows});}
}
