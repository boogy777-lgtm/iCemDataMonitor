using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Dfb;
using Halliburton.iCemEmule.Emitters;

// Tests execute the UNMODIFIED original classes. No copied reference algorithms.
public static class EmulatorOracle {
    static string Bits(double v) => BitConverter.DoubleToUInt64Bits(v).ToString("x16",CultureInfo.InvariantCulture);
    public static string Run(string root) {
        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var options=new JsonSerializerOptions {PropertyNameCaseInsensitive=true};
        string folder=Path.Combine(root,"src/net/iCemEmulator","templates");
        var registry=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"variable_registry.json")));
        var field=typeof(TemplateCatalog).GetField("_registryVars",flags)!;
        field.SetValue(null,JsonSerializer.Deserialize(registry.RootElement.GetProperty("variables").GetRawText(),field.FieldType,options));
        var build=typeof(TemplateCatalog).GetMethod("BuildDeviceTemplate",flags)!;
        var templates=new List<object>();
        foreach(var file in new[]{"flecs_4.53.json","plc_basic.json"}) {
            var input=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(folder,file)),build.GetParameters()[0].ParameterType,options);
            var template=(DeviceTemplate)build.Invoke(null,new[]{input})!;
            typeof(TemplateCatalog).GetField("_templates",flags)!.SetValue(null,new List<DeviceTemplate>{template});
            var engine=new SimulationEngine();
            var ticks=new List<object>();
            for(int tick=0;tick<5;tick++) {
                if(tick==1) engine.SetOverride(template.Variables[2].Name,1234.125);
                if(tick==3) engine.ClearOverride(template.Variables[2].Name);
                if(tick==4) engine.ResetClock();
                var values=engine.Tick();
                ticks.Add(new{values=values.Select(Bits).ToArray(),line=SignalWireCodec.FormatLine(values,template)});
            }
            templates.Add(new{file,template.Name,template.DeviceType,
                names=template.Variables.Select(v=>v.Name).ToArray(),
                indices=template.Variables.Select(v=>v.Index).ToArray(),ticks});
        }
        string[] names={"bbl/min","gpm","gal/min","m³/min","m3/min","°F","degF","f","°C","degC","c","ppg","lb/gal","g/cc","g/cm3","kg/m³","kg/m3","bbl","gal","m³","m3","ft","m","psi","psia","bar","kPa","MPa","bpm","psi/mA","mA","","unknown"};
        double[] inputs={-123.5,-0.0,0.0,1.0,212.0,1000000.0,double.NaN,double.PositiveInfinity,double.NegativeInfinity};
        var units=new List<object>();
        foreach(string from in names) foreach(string to in names) foreach(double value in inputs)
            units.Add(new{from,to,input=Bits(value),value=Bits(UnitConversion.Convert(value,from,to))});
        var settings=new List<object>();
        foreach(UnitPreset preset in Enum.GetValues<UnitPreset>()) foreach(string native in names) {
            var config=new UnitSettings {Preset=preset,PressureOverride="bar",TemperatureOverride="°F"};
            var group=UnitConversion.GroupOf(native);
            settings.Add(new{preset=(int)preset,native,target=config.TargetUnit(group),needs=UnitConversion.NeedsConversion(native,config),value=Bits(UnitConversion.ToTarget(32.0,native,config)),wire=Bits(UnitConversion.ToWire(32.0,native,config))});
        }
        var lag=new FirstOrderLagBlock("Lag"){TauSeconds=new DfbParameter<double>(2.0)};
        var latch=new KickoutLatchBlock("Latch");var total=new TotalizerBlock("Totals");var dfb=new List<object>();
        for(int i=0;i<32;i++) {
            double dt=i%2==0?1.0:0.25;double target=i<12?100.0:25.0;double pressure=i*55.0;double rate=i%7-2;bool reset=i==13||i==27;
            var context=DfbContext.Create(i,dt,i,123);
            lag.Target.Value=target;lag.Execute(context);
            latch.RawPressure.Value=pressure;latch.Setting.Value=500.0;latch.Reset.Value=reset;latch.Execute(context);
            total.Rate.Value=rate;total.ResetStage.Value=reset;total.Execute(context);
            dfb.Add(new{dt,target,pressure,rate,reset,lag=Bits(lag.Lagged.Value),trip=latch.Tripped.Value,captured=Bits(latch.PressureAtKo.Value),stage=Bits(total.StageTotal.Value),job=Bits(total.JobTotal.Value)});
        }
        var edges=new List<object>();
        foreach(double dt in new[]{0.0,-0.0,0.25,-1.0,double.NaN,double.PositiveInfinity}) foreach(double tau in new[]{0.0,-0.0,2.0,double.NaN}) {
            var block=new FirstOrderLagBlock("Edge"){TauSeconds=new DfbParameter<double>(tau)};block.Target.Value=10.0;block.Execute(DfbContext.Create(0,dt,0,0));
            edges.Add(new{dt=Bits(dt),tau=Bits(tau),value=Bits(block.Lagged.Value)});
        }
        var formats=new List<object>();
        foreach(string format in new[]{"elapsed","int","f2"}) foreach(double input in new[]{1.25,2.5,3.5,-2.5,1.005,2.675,-0.005,-0.0,double.NaN,double.PositiveInfinity,double.NegativeInfinity,2147483648.0,-2147483649.0}) {
            var template=new DeviceTemplate{Variables=new[]{new TemplateVariable{Format=format}}};
            formats.Add(new{format,input=Bits(input),line=SignalWireCodec.FormatLine(new[]{input},template)});
        }
        var ace=new List<object>();
        foreach(int size in new[]{0,1,124,125,200}) {
            var payload=Enumerable.Range(0,size).Select(i=>(byte)i).ToArray();
            var frame=AceFrameBuilder.BuildFrame(new byte[]{0,1,2,3,4,5},0x12345678,0x13120200,payload);
            ace.Add(new{size,frame=Convert.ToHexString(frame).ToLowerInvariant()});
        }
        var decode=new List<object>();var p=Enumerable.Range(0,124).Select(i=>(byte)(255-i)).ToArray();
        foreach(string type in new[]{"FF","ff","UL","UI","HX","SI","SL","UC","unknown"}) foreach(int width in new[]{1,2,3,4,8}) foreach(int offset in new[]{3,4,120,128}) {
            var sig=new LantableRegistry.SignalDef(offset,width,type,0.125,"test","");
            bool ok=SignalWireCodec.TryDecodeNative(p,sig,out double value);
            decode.Add(new{type,width,offset,ok,value=Bits(value)});
        }
        return JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),templates,units,settings,dfb,edges,formats,ace,decode});
    }
}
