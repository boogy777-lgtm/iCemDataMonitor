// Same vectors run portably and in the exact-source C#7.3 / CLR4 executable.
using System;
using System.Collections.Generic;
using Burservis.iCem.DataMonitor.Bus;

internal static class PairPreparationGolden
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static KeyValuePair<string, object> Candidate(string name, object value)
    { return new KeyValuePair<string, object>(name, value); }
    private static PollResolution.LanSourceBatch Batch(string key, bool fresh, double value)
    {
        var batch = new PollResolution.LanSourceBatch { SourceKey = key, Fresh = fresh };
        batch.Candidates.Add(Candidate("Pump A Pressure", value));
        return batch;
    }

    internal static void RunVectors()
    {
        var com = PollResolution.Resolve(new List<KeyValuePair<string, object>> {
            Candidate("Pump Pressure", "bad"), Candidate("Time & Date", 639226512000000000d),
            Candidate("Unit Num", 1), Candidate("Pump Pressure", 15.79f), Candidate("PS Pump Press", 2f) });
        Check(com.BadType == 1 && com.Dup == 1 && com.Entries.Count == 3, "resolution diagnostics");
        var time = PollPairPreparation.PrepareCom(com.Entries[0]);
        Check(time.Zone == ZoneKind.ComMain && time.Address == 4, "timestamp address/order");
        uint timestamp = (uint)time.Low | ((uint)time.High << 16);
        Check(timestamp == 1787054400u, "timestamp raw int32 CDAB");
        var unit = PollPairPreparation.PrepareCom(com.Entries[1]);
        Check(unit.Address == 6 && unit.Low == 0 && unit.High == 0x3F80, "Unit Num float32");
        var pressure = PollPairPreparation.PrepareCom(com.Entries[2]);
        Check(pressure.Address == 16 && pressure.Low == 0xA3D7 && pressure.High == 0x417C, "float32 CDAB");
        var beforeEpoch = PollPairPreparation.PrepareCom(new PollResolution.Entry(4, "Time & Date", UnixTimestamp.EpochTicks - 10000000d));
        Check(beforeEpoch.Low == 65535 && beforeEpoch.High == 65535, "negative timestamp");
        var negativeZero = PollPairPreparation.PrepareLan(new PollResolution.Entry(200, "zero", -0.0));
        Check(negativeZero.Low == 0 && negativeZero.High == 0x8000, "signed zero preserved");
        var large = PollPairPreparation.PrepareCom(new PollResolution.Entry(16, "large", double.MaxValue));
        Check(large.Low == 0 && large.High == 0x7F80, "float cast overflow policy preserved");

        var lan = PollResolution.ResolveLan(new List<PollResolution.LanSourceBatch> {
            Batch("frozen", false, 1), Batch("fresh-first", true, -12.5), Batch("fresh-later", true, 9) });
        Check(lan.Entries.Count == 1 && lan.Dup == 2 && lan.BadType == 0, "LAN diagnostics");
        Check(lan.Entries[0].SourceKey == "fresh-first" && lan.WinsBySource["fresh-first"] == 1, "fresh-first source law");
        var pair = PollPairPreparation.PrepareLan(lan.Entries[0]);
        Check(pair.Zone == ZoneKind.Lan && pair.Address == 266 && pair.Low == 0 && pair.High == 0xC148, "LAN ordered full float32 CDAB");
        var frozen = PollResolution.ResolveLan(new List<PollResolution.LanSourceBatch> {
            Batch("first", false, 1), Batch("second", false, 2) });
        Check(PollPairPreparation.PrepareLan(frozen.Entries[0]).High == 0x3F80, "all frozen fallback");
    }

}
