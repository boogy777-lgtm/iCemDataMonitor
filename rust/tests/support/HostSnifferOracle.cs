using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using iCemDataMonitor.Plugins.Icem;

// Executes the UNMODIFIED active Host decoder, never the emulator's native codec.
public static class HostSnifferOracle
{
    static string? Bits(double? value) => value.HasValue
        ? BitConverter.DoubleToUInt64Bits(value.Value).ToString("x16", CultureInfo.InvariantCulture) : null;
    static object Case(byte[] frame, LantableVectorDef vector, string origin)
    {
        object[]? decoded = null;
        string? error = null;
        try {
            decoded = LantableRegistry.DecodeSignals(frame, vector).Select(d => (object)new {
                name=d.SignalName, bits=Bits(d.Value), text=d.Text, uom=d.Uom, sentinel=d.IsSentinel
            }).ToArray();
        } catch (Exception e) { error=e.GetType().Name; }
        return new { origin, sv=vector.SvNumber, name=vector.Name, equipment_type=vector.EquipmentType,
            signals=vector.Signals.Select(s=>new { byte_offset=s.ByteOffset, size=s.Size,
                value_type=s.ValueType, das_factor=s.DasFactor, das_factor_bits=Bits(s.DasFactor), name=s.Name, uom=s.Uom, unit_class=s.UnitClassId }).ToArray(),
            frame=Convert.ToHexString(frame), decoded, error };
    }
    public static void Run(string root)
    {
        var registry=LantableRegistry.Load(Path.Combine(root,"iCemDataMonitor.Plugins.Icem/templates/lantable_registry.json"));
        var cases=new List<object>();
        foreach(var vector in registry.ServiceVectors.OrderBy(v=>v.SvNumber)) {
            foreach(int seed in new[]{0,17,255,7919}) {
                var frame=Enumerable.Range(0,152).Select(i=>(byte)((i*17+seed)&255)).ToArray();
                frame[0]=3;frame[14]=3;frame[15]=3;frame[16]=(byte)(seed%2==0?8:9);
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24),vector.SvNumber);
                cases.Add(Case(frame,vector,"catalog-pattern"));
            }
        }
        string[] types={"FF","UL","SL","SI","FD","UI","US","SS","SC","HX","UC","unknown"};
        foreach(string type in types) {
            foreach(double factor in new[]{1.0,0.5,-9999.0}) {
                var frame=new byte[152];frame[0]=3;frame[14]=3;frame[15]=3;frame[16]=8;
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(28),type=="FF"?0xC61C3C00u:1u); // -9999f
                var def=new LantableVectorDef(1,type,"test",new[]{new LantableSignalDef(4,8,type,factor,"signal","unit",0)});
                cases.Add(Case(frame,def,"type-factor"));
            }
        }
        foreach(var spec in new[]{(128,1,"FF"),(128,0,"UL"),(4,1,"FF"),(200,4,"FD"),(4,4,"ff")}) {
            var vector=new LantableVectorDef(2,"bounds","test",new[]{new LantableSignalDef(spec.Item1,spec.Item2,spec.Item3,1,"bounds","",0)});
            cases.Add(Case(new byte[152],vector,"span"));
        }
        var pcap=File.ReadAllBytes(Path.Combine(root,"docs/Wireshark/iCemCrio.pcapng.bin"));
        var seen=new HashSet<uint>();
        for(int at=0;at+12<=pcap.Length;) {
            int length=BinaryPrimitives.ReadInt32LittleEndian(pcap.AsSpan(at+4));
            if(length<12||at+length>pcap.Length)throw new InvalidOperationException("invalid pinned pcapng block");
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(pcap.AsSpan(at));
            if(kind==6&&length>=32) {
                int captured=BinaryPrimitives.ReadInt32LittleEndian(pcap.AsSpan(at+20));
                if(captured==152&&28+captured<=length-4) {
                    byte[] frame=pcap.AsSpan(at+28,captured).ToArray();
                    if(frame[14]==3&&frame[15]==3&&(frame[16]==8||frame[16]==9)) {
                        uint sv=BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(24));
                        if(registry.TryGet(sv,out var def)&&seen.Add(sv))cases.Add(Case(frame,def,"pinned-pcap"));
                    }
                }
            }
            at+=length;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{runtime=Environment.Version.ToString(),vectors=registry.Count,pcap_vectors=seen.Count,cases}));
    }
}
