using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Dfb;

// Executes UNMODIFIED source providers. Only data-loading seams are supplied here:
// constructors otherwise read/log in PowerShell's installation directory.
public static class LanPayloadOracle {
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static string Bits(double v) => BitConverter.DoubleToUInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture);
    static string Hex(byte[] p) => Convert.ToHexString(p).ToLowerInvariant();
    static void Field(object o,string n,object value) => o.GetType().GetField(n,Private)!.SetValue(o,value);
    static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static LantableRegistry Registry(string templates) {
        var r=Empty<LantableRegistry>(); var data=new Dictionary<uint,LantableRegistry.ServiceVectorDef>();
        foreach(var file in new[]{"lantable_registry.json","firmware_registry.json"}) {
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(templates,file)));
            foreach(var v in doc.RootElement.GetProperty("serviceVectors").EnumerateArray()) {
                uint sv=v.GetProperty("svNumber").GetUInt32();
                if(data.ContainsKey(sv)) continue;
                var signals=v.GetProperty("signals").EnumerateArray().Select(s=>new LantableRegistry.SignalDef(
                    s.GetProperty("byteOffset").GetInt32(),s.GetProperty("size").GetInt32(),s.GetProperty("valueType").GetString()??"",
                    s.TryGetProperty("dasFactor",out var f)?f.GetDouble():1,s.GetProperty("name").GetString()??"",
                    s.TryGetProperty("uom",out var u)?u.GetString()??"":"")).ToArray();
                data.Add(sv,new(sv,v.GetProperty("name").GetString()??"",v.TryGetProperty("equipmentType",out var e)?e.GetString()??"":"",signals));
            }
        }
        Field(r,"_bySv",data);return r;
    }
    static AcePayloadProvider Base(LantableRegistry r,string templates) {
        var b=Empty<AcePayloadProvider>(); Field(b,"_lantable",r);
        var lookup=Activator.CreateInstance(typeof(AcePayloadProvider).GetNestedType("LanSimLookup",BindingFlags.NonPublic)!,true)!;
        var load=typeof(AcePayloadProvider).GetMethod("LoadSimFile",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var file in new[]{"variable_registry.json","firmware_variables.json"}) load.Invoke(null,new[]{lookup,(object)Path.Combine(templates,file)});
        Field(b,"_simLookup",lookup); return b;
    }
    static DfbAcePayloadProvider Dfb(AcePayloadProvider b,LantableRegistry r,DfbNetwork n) {
        var d=Empty<DfbAcePayloadProvider>();Field(d,"_base",b);Field(d,"_lantable",r);Field(d,"_values",new DfbSignalSourceAdapter(new DfbSignalSource(n)));return d;
    }
    static ManualOverridePayloadProvider Manual(IAcePayloadProvider inner,LantableRegistry r) {
        var m=Empty<ManualOverridePayloadProvider>();Field(m,"_inner",inner);Field(m,"_lantable",r);
        Field(m,"_overrides",new Dictionary<(uint Sv,string Name),double>());Field(m,"_overrideLock",new object());return m;
    }
    static object Frames(IReadOnlyList<AceVectorData> v) => v.Select(x=>new{sv=x.SvNumber,name=x.Name,payload=Hex(x.Payload)}).ToArray();
    static UnitSettings Settings(int preset,bool custom=false) => new UnitSettings{Preset=(UnitPreset)preset,
        FlowOverride=custom?"gpm":null,TemperatureOverride=custom?"°F":null,DensityOverride=custom?"kg/m3":null,
        VolumeOverride=custom?"gal":null,LevelOverride=custom?"m":null,PressureOverride=custom?"kPa":null};
    sealed class Fixture : IAcePayloadProvider {
        readonly uint sv; readonly byte[] payload;
        public Fixture(uint n,byte[] p){sv=n;payload=p;}
        public IReadOnlyList<uint> ActiveSvNumbers => new[]{sv};
        public IReadOnlyList<AceVectorData> GetSuperframe(uint e,double t)=>new[]{new AceVectorData(sv,"fixture",payload)};
    }
    static object Def(LantableRegistry.SignalDef s)=>new{byte_offset=s.ByteOffset,size=s.Size,value_type=s.ValueType,das_factor=s.DasFactor,name=s.Name,uom=s.UoM};
    public static string Run(string root) {
        var templates=Path.Combine(root,"iCemEmulator","templates");var registry=Registry(templates);var b=Base(registry,templates);
        var inventory=registry.All.Values.Select(v=>new{sv=v.SvNumber,name=v.Name,equipment_type=v.EquipmentType,signals=v.Signals.Select(Def).ToArray()}).ToArray();
        var bases=new List<object>();
        foreach(bool firmware in new[]{false,true}) foreach(double elapsed in new[]{0.0,0.25,1.0,37.5,1000.0}) {
            b.IncludeFirmwareTables=firmware;bases.Add(new{firmware,elapsed,frames=Frames(b.GetSuperframe(1,elapsed))});
        }
        var n=CementUnitNetworkFactory.Create();b=Base(registry,templates);var freeze=new FrozenPayloadProvider(Dfb(b,registry,n));
        var manual=Manual(freeze,registry);var units=new UnitConvertingPayloadProvider(manual,UnitPreset.LanNative,registry);
        var chosen=registry.GetBySvNumber(0x13120203)!.Signals.First(s=>s.ValueType=="FF");
        var transitions=new List<object>();
        // Frozen before first call still captures one frame. Cached shape follows
        // capture, while ActiveSvNumbers always follows the inner firmware flag.
        for(int step=0;step<16;step++) {
            switch(step) {
                case 0: freeze.Frozen=true;break;
                case 1: b.IncludeFirmwareTables=true;break;
                case 2: manual.SetOverride(0x13120203,chosen.Name.ToLowerInvariant(),1234.5);break;
                case 3: manual.SetOverride(0x13120203,chosen.Name,87.125);break;
                case 4: units.Settings=Settings(2);break;
                case 5: manual.ClearOverride(0x13120203,chosen.Name);break;
                case 6: freeze.Frozen=false;break;
                case 7: freeze.Frozen=true;b.IncludeFirmwareTables=false;break;
                case 8: units.Settings=Settings(0);break;
                case 9: units.Settings=Settings(1);break;
                case 10: units.Settings=Settings(3);break;
                case 11: units.Settings=Settings(2,true);break;
                case 12: freeze.Frozen=false;break;
                case 13: b.IncludeFirmwareTables=true;break;
                case 14: manual.SetOverride(0x13120203,chosen.Name,-50.5);break;
                case 15: manual.ClearOverride(0x13120203,chosen.Name.ToLowerInvariant());break;
            }
            n.Execute(DfbContext.Create(step*0.25,0.25,step,7));
            var output=units.GetSuperframe(0xffffffff,step*0.25);
            transitions.Add(new{step,active=units.ActiveSvNumbers.ToArray(),frames=Frames(output),overrides=manual.SnapshotOverrides().Select(k=>new{sv=k.Key.Sv,name=k.Key.Name,value=Bits(k.Value)}).ToArray()});
            // A downstream caller must not corrupt the freeze cache through clones.
            output[0].Payload[0]^=0xff;
        }
        var can=new List<object>();
        foreach(var v in registry.All.Values) foreach(var s in v.Signals) foreach(string name in new[]{s.Name,s.Name.ToLowerInvariant()}) {
            bool ok=manual.CanTransmit(v.SvNumber,name,out var reason);can.Add(new{sv=v.SvNumber,name,ok,reason});
        }
        foreach(uint sv in new uint[]{0x13120203,0xffffffff}) {bool ok=manual.CanTransmit(sv,"missing signal",out var reason);can.Add(new{sv,name="missing signal",ok,reason});}
        var codec=new List<object>();
        double[] values={0.0,-0.0,0.5,1.5,2.5,-0.5,-1.5,42.125,-2147483649.0,2147483648.0,4294967296.0,1e20,double.MaxValue,double.NaN,double.PositiveInfinity,double.NegativeInfinity};
        var encode=typeof(AcePayloadProvider).GetMethod("EncodeSignal",BindingFlags.NonPublic|BindingFlags.Static)!;
        var dfbEncode=typeof(DfbAcePayloadProvider).GetMethod("Encode",BindingFlags.NonPublic|BindingFlags.Static)!;
        var manualEncode=typeof(ManualOverridePayloadProvider).GetMethod("EncodeSignal",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var value in values) foreach(string type in new[]{"FF","UL","UI","SI","SL","HX","UC","unknown","ff"}) foreach(int size in new[]{1,2,4,8,120}) foreach(double factor in new[]{0.0,0.1,1.0,10.0,-2.0}) {
            object[] args={value,type,factor,size};var bytes=(byte[])encode.Invoke(null,args)!;
            if(!bytes.SequenceEqual((byte[])dfbEncode.Invoke(null,args)!) || !bytes.SequenceEqual((byte[])manualEncode.Invoke(null,args)!)) throw new InvalidOperationException("source codec divergence");
            codec.Add(new{value=Bits(value),type,size,factor,bytes=Hex(bytes)});
        }
        var ascii=new List<object>();var asciiEncode=typeof(AcePayloadProvider).GetMethod("EncodeAsciiNulPad",BindingFlags.NonPublic|BindingFlags.Static,null,new[]{typeof(string),typeof(int)},null)!;
        foreach(string text in new[]{"","Unit 1","abcéΩЯ","x😀z","\0A\0B"}) foreach(int size in new[]{0,1,2,3,4,8,120}) ascii.Add(new{text,size,bytes=Hex((byte[])asciiEncode.Invoke(null,new object[]{text,size})!)});
        var conversions=new List<object>();
        foreach(string type in new[]{"FF","UL","UI","HX","SI","SL","UC","unknown"}) foreach(int size in new[]{1,2,3,4,8}) foreach(double factor in new[]{0.0,0.1,1.0,-2.0}) foreach(string uom in new[]{"psi","°F","bbl/min"}) {
            var signal=new LantableRegistry.SignalDef(8,size,type,factor,"probe",uom);
            var rr=Empty<LantableRegistry>();Field(rr,"_bySv",new Dictionary<uint,LantableRegistry.ServiceVectorDef>{{0x13120203,new(0x13120203,"fixture","",new[]{signal})}});
            var input=Enumerable.Range(0,124).Select(i=>(byte)(i*17+11)).ToArray();
            foreach(bool nan in new[]{false,true}) {
                if(nan) Array.Copy(new byte[]{0xff,0xc0,0,0,0xff,0xff,0xff,0xff},0,input,4,8);
                foreach(int preset in new[]{0,1,2,3,4}) {
                    var converter=new UnitConvertingPayloadProvider(new Fixture(0x13120203,input),UnitPreset.LanNative,rr){Settings=Settings(preset%4,preset==4)};
                    conversions.Add(new{signal=Def(signal),input=Hex(input),preset=preset%4,custom=preset==4,output=Hex(converter.GetSuperframe(0,0)[0].Payload)});
                }
            }
        }
        return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),inventory,bases,override_name=chosen.Name,transitions,can,codec,ascii,conversions});
    }
}
