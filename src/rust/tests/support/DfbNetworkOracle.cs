using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Halliburton.iCemEmule.Dfb;

// Oracle executes every UNMODIFIED original Dfb/*.cs and Dfb/Blocks/*.cs.
// Reflection observes the finite source inventory; it does not copy model/RNG/map logic.
public static class DfbNetworkOracle {
    static string Bits(double v) => BitConverter.DoubleToUInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture);
    static object Enc(object? v) => v switch {
        double d => new { kind="double", bits=Bits(d) },
        uint u => (object)new {kind="uint", value=u},
        bool b => new {kind="bool", value=b},
        string s => new {kind="string", value=s},
        null => new {kind="string", value=(string?)null},
        _ => throw new InvalidOperationException("Unknown DFB value type")
    };
    static object Port(DfbSignal s, string direction) => s switch {
        DfbSignal<double> d => new {path=s.Path,direction,source=s.ConnectedFrom?.Path,stored=Enc(d.Value),read=Enc(d.Read())},
        DfbSignal<uint> u => (object)new {path=s.Path,direction,source=s.ConnectedFrom?.Path,stored=Enc(u.Value),read=Enc(u.Read())},
        DfbSignal<bool> b => new {path=s.Path,direction,source=s.ConnectedFrom?.Path,stored=Enc(b.Value),read=Enc(b.Read())},
        DfbSignal<string> t => new {path=s.Path,direction,source=s.ConnectedFrom?.Path,stored=Enc(t.Value),read=Enc(t.Read())},
        _ => throw new InvalidOperationException("Unknown DFB port type")
    };
    static T Block<T>(DfbNetwork n) where T:IDfbBlock => n.Blocks.OfType<T>().First();
    static PumpSideBlock Pump(DfbNetwork n, string name) => n.Blocks.OfType<PumpSideBlock>().Single(b=>b.Name==name);
    static Dictionary<string,Func<double>> Map(DfbSignalSource s) => (Dictionary<string,Func<double>>)typeof(DfbSignalSource).GetField("_map",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(s)!;
    static object Snapshot(DfbNetwork n, DfbSignalSource s) {
        var ports=new List<object>(); var parameters=new List<object>();
        foreach(var b in n.Blocks) {
            ports.AddRange(b.Inputs.Select(p=>Port(p,"input"))); ports.AddRange(b.Outputs.Select(p=>Port(p,"output")));
            foreach(var prop in b.GetType().GetProperties()) {
                var t=prop.PropertyType;
                if(t.IsGenericType && t.GetGenericTypeDefinition()==typeof(DfbParameter<>)) {
                    var value=Enc(t.GetProperty("Value")!.GetValue(prop.GetValue(b)));
                    parameters.Add(new {path=b.Name+"."+prop.Name,direction="parameter",source=(string?)null,stored=value,read=value});
                }
            }
        }
        var wire=new Dictionary<string,string>();
        foreach(var key in Map(s).Keys) {
            if(!s.TryGetValue(key.ToLowerInvariant(),out var value)) throw new InvalidOperationException(key);
            wire[key]=Bits(value);
        }
        return new {ports,parameters,wire};
    }
    public static string Run() {
        int[] seeds={7,0,-7,int.MinValue,int.MaxValue,123456789,-123456789};
        long[] offsets={0,-1,int.MaxValue,(long)int.MaxValue+1,long.MaxValue,long.MinValue,-2147483648L};
        var random=new List<object>(); var contexts=new List<object>(); var trajectories=new List<object>();
        foreach(int seed in seeds) {
            var rng=new Random(seed);
            random.Add(new {seed,values=Enumerable.Range(0,512).Select(_=>Bits(rng.NextDouble())).ToArray()});
            foreach(long tick in offsets) {
                var ctx=DfbContext.Create(0.5,0.25,tick,seed);
                contexts.Add(new {seed,tick,values=Enumerable.Range(0,64).Select(_=>Bits(ctx.Random.NextDouble())).ToArray()});
            }
        }
        for(int scenario=0;scenario<seeds.Length;scenario++) {
            var net=CementUnitNetworkFactory.Create(); var source=new DfbSignalSource(net); var rows=new List<object>();
            rows.Add(new {step=0,state=Snapshot(net,source)});
            double elapsed=0;
            for(int i=1;i<=32;i++) {
                var a=Pump(net,"PumpA"); var b=Pump(net,"PumpB"); var mixer=Block<MixerBlock>(net); var raw=Block<RawIoBlock>(net); var temp=Block<TemperatureBlock>(net); var seq=Block<JobSequencerBlock>(net);
                switch(i) {
                    case 3: a.Rate.Value=12; a.OpkoSetting.Value=1000; break;
                    case 4: a.Rate.Value=0; break;
                    case 5: ((DfbSignal<bool>)a.Reset.ConnectedFrom!).Value=true; break;
                    case 6: ((DfbSignal<bool>)a.Reset.ConnectedFrom!).Value=false; break;
                    case 7: ((DfbSignal<bool>)mixer.JobActive.ConnectedFrom!).Value=false; break;
                    case 8: ((DfbSignal<bool>)mixer.JobActive.ConnectedFrom!).Value=true; break;
                    case 9: mixer.WaterValveCmd.Value=-50; mixer.CementValveCmd.Value=150; break;
                    case 10: mixer.WaterValveCmd.Value=125; mixer.CementValveCmd.Value=-25; break;
                    case 11: seq.UnitNumberValue=new DfbParameter<uint>(uint.MaxValue); break;
                    case 12:
                        a.Resistance.Value=225; a.PressureBase.Value=-500; a.LagTauSeconds.Value=0.25; a.NoiseSigma.Value=0.05;
                        mixer.DensityTauSeconds.Value=0.5; mixer.WaterRateGain.Value=4.2; mixer.PotTauSeconds.Value=0.1; mixer.DensityNoiseSigma.Value=0.1;
                        mixer.SacksPerBbl.Value=1.1; mixer.BlendYieldValue.Value=2; mixer.BlendWtrReqValue.Value=6;
                        temp.Target.Value=-30; temp.TauSeconds.Value=0.1; seq.UnitNumberValue.Value=42; break;
                    case 13: raw.SpareAnalogIn[3].Value=12.125; raw.SpareStageTotal[0].Value=-7; raw.SpareJobTotal[1].Value=83; raw.SpareCounterIn[2].Value=65537; raw.SpareCombined1.Value=-0.0; raw.SpareCombined2.Value=1.5; break;
                    case 14: a.Reset.Value=true; b.Reset.Value=true; mixer.JobActive.Value=false; mixer.ResetStage.Value=true; seq.JobActive.Value=false; seq.Reset.Value=true; Block<F300HpvtBlock>(net).FlowRef.Value=999; break;
                    case 15: ((DfbSignal<bool>)a.Reset.ConnectedFrom!).Value=true; ((DfbSignal<bool>)mixer.JobActive.ConnectedFrom!).Value=false; break;
                    case 16: ((DfbSignal<bool>)a.Reset.ConnectedFrom!).Value=false; ((DfbSignal<bool>)mixer.JobActive.ConnectedFrom!).Value=true; break;
                    case 17: b.Rate.Value=-2; break;
                    case 18: b.OpkoSetting.Value=0; break;
                }
                double dt=(i%5) switch {0=>0,1=>1,2=>0.25,3=>2.5,_=>-0.25}; elapsed+=dt;
                long tick=unchecked(offsets[scenario]+i);
                var ctx=DfbContext.Create(elapsed,dt,tick,seeds[scenario]); net.Execute(ctx);
                rows.Add(new {step=i,dt,elapsed,tick,sentinel=Bits(ctx.Random.NextDouble()),state=Snapshot(net,source)});
            }
            trajectories.Add(new {seed=seeds[scenario],rows});
        }
        var edges=new List<object>();
        double[] values={0.0,-0.0,-1.0,0.25,double.NaN,double.PositiveInfinity,double.NegativeInfinity,double.MaxValue};
        foreach(var v in values) {
            foreach(string target in new[]{"dt","water","blend","elapsed","pump"}) {
                var net=CementUnitNetworkFactory.Create(); var source=new DfbSignalSource(net);
                if(target=="water") Block<MixerBlock>(net).WaterValveCmd.Value=v;
                if(target=="blend") Block<MixerBlock>(net).BlendTarget.Value=v;
                if(target=="pump") Pump(net,"PumpA").Rate.Value=v;
                var ctx=DfbContext.Create(target=="elapsed"?v:1,target=="dt"?v:1,1,7);net.Execute(ctx);
                edges.Add(new {target,input=Bits(v),sentinel=Bits(ctx.Random.NextDouble()),state=Snapshot(net,source)});
            }
        }
        var probe=CementUnitNetworkFactory.Create();var rawProbe=Block<RawIoBlock>(probe);
        rawProbe.DigitalInputs.Value=0xa55a;rawProbe.RelayOut.Value=0x96;
        rawProbe.DigitalOutputs.Value=uint.MaxValue;rawProbe.SpareAnalogIn[0].Value=42.5;
        var probeState=Snapshot(probe,new DfbSignalSource(probe));
        return JsonSerializer.Serialize(new {runtime=Environment.Version.ToString(),blocks=probe.Blocks.Select(b=>b.Name).ToArray(),random,contexts,trajectories,edges,probe=probeState});
    }
}
