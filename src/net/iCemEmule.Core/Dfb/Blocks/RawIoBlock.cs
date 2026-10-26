// RawIoBlock — цифровой/аналоговый ввод-вывод (SV 0x13120209, 0x1312020C):
// RelayOut, Digital I/O, Analog In/Out, Count In, Spare-слоты.

namespace Halliburton.iCemEmule.Dfb;

public sealed class RawIoBlock : DfbBlockBase
{
    public DfbSignal<uint> RelayOut { get; }
    public DfbSignal<uint> DigitalInputs { get; }
    public DfbSignal<uint> DigitalOutputs { get; }
    public DfbSignal<double>[] AnalogIn { get; }
    public DfbSignal<double>[] AnalogOut { get; }
    public DfbSignal<double>[] CountIn { get; }
    public DfbSignal<double>[] SpareAnalogIn { get; }
    public DfbSignal<double>[] SpareStageTotal { get; }
    public DfbSignal<double>[] SpareJobTotal { get; }
    public DfbSignal<double>[] SpareCounterIn { get; }
    public DfbSignal<double> SpareCombined1 { get; }
    public DfbSignal<double> SpareCombined2 { get; }

    public RawIoBlock(string name) : base(name)
    {
        RelayOut = new DfbSignal<uint>($"{name}.RelayOut");
        DigitalInputs = new DfbSignal<uint>($"{name}.DigitalInputs");
        DigitalOutputs = new DfbSignal<uint>($"{name}.DigitalOutputs");
        AnalogIn = NewArray(name, "AnalogIn", 8);
        AnalogOut = NewArray(name, "AnalogOut", 4);
        CountIn = NewArray(name, "CountIn", 6);
        SpareAnalogIn = NewArray(name, "SpareAnalogIn", 4);
        SpareStageTotal = NewArray(name, "SpareStageTotal", 4);
        SpareJobTotal = NewArray(name, "SpareJobTotal", 4);
        SpareCounterIn = NewArray(name, "SpareCounterIn", 4);
        SpareCombined1 = new DfbSignal<double>($"{name}.SpareCombined1");
        SpareCombined2 = new DfbSignal<double>($"{name}.SpareCombined2");

        var outputs = new List<DfbSignal>
        {
            RelayOut, DigitalInputs, DigitalOutputs, SpareCombined1, SpareCombined2
        };
        outputs.AddRange(AnalogIn);
        outputs.AddRange(AnalogOut);
        outputs.AddRange(CountIn);
        outputs.AddRange(SpareAnalogIn);
        outputs.AddRange(SpareStageTotal);
        outputs.AddRange(SpareJobTotal);
        outputs.AddRange(SpareCounterIn);
        Outputs = outputs;
    }

    private static DfbSignal<double>[] NewArray(string name, string role, int count)
    {
        var arr = new DfbSignal<double>[count];
        for (int i = 0; i < count; i++)
            arr[i] = new DfbSignal<double>($"{name}.{role}{i + 1}");
        return arr;
    }

    public override void Execute(DfbContext ctx)
    {
        RelayOut.Value = 0u;
        DigitalInputs.Value = 0u;
        DigitalOutputs.Value = 0u;

        for (int i = 0; i < AnalogIn.Length; i++)
            AnalogIn[i].Value = 25.0 + (ctx.Random.NextDouble() - 0.5) * 2.0;
        for (int i = 0; i < AnalogOut.Length; i++)
            AnalogOut[i].Value = 50.0 + (ctx.Random.NextDouble() - 0.5) * 1.0;
        for (int i = 0; i < CountIn.Length; i++)
            CountIn[i].Value = ((long)(ctx.ElapsedSeconds * 10.0 + i * 137) % 10000);
        // Spare-слоты остаются 0 (по умолчанию)
    }
}
