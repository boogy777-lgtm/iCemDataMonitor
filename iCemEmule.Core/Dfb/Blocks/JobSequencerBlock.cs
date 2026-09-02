// JobSequencerBlock — автомат работы: JobActive⇑ ⇒ StageIndex=1 ∧ EventNumber++;
// ControlMode=1 (Downhole) / MixMode=2 (CMT_Cement) — дефолты реального простаивающего
// cRIO (iCemCrio.pcapng.bin); Unit/Track-идентификаторы.

namespace Halliburton.iCemEmule.Dfb;

public sealed class JobSequencerBlock : DfbBlockBase
{
    public DfbSignal<bool> JobActive { get; }
    public DfbSignal<bool> Reset { get; }
    public DfbSignal<uint> StageIndex { get; }
    public DfbSignal<uint> EventNumber { get; }
    public DfbSignal<uint> UnitNumber { get; }
    public DfbSignal<uint> ControlMode { get; }
    public DfbSignal<uint> MixMode { get; }
    public DfbSignal<uint> TrackId { get; }
    public DfbSignal<string> TrackDescript { get; }

    public DfbParameter<uint> UnitNumberValue { get; set; } = new(1);

    private bool _prevActive;
    private uint _event;

    public JobSequencerBlock(string name) : base(name)
    {
        JobActive = new DfbSignal<bool>($"{name}.JobActive");
        Reset = new DfbSignal<bool>($"{name}.Reset");
        StageIndex = new DfbSignal<uint>($"{name}.StageIndex");
        EventNumber = new DfbSignal<uint>($"{name}.EventNumber");
        UnitNumber = new DfbSignal<uint>($"{name}.UnitNumber");
        ControlMode = new DfbSignal<uint>($"{name}.ControlMode");
        MixMode = new DfbSignal<uint>($"{name}.MixMode");
        TrackId = new DfbSignal<uint>($"{name}.TrackId");
        TrackDescript = new DfbSignal<string>($"{name}.TrackDescript");
        Inputs = new DfbSignal[] { JobActive, Reset };
        Outputs = new DfbSignal[] { StageIndex, EventNumber, UnitNumber, ControlMode, MixMode, TrackId, TrackDescript };
    }

    public override void Execute(DfbContext ctx)
    {
        bool active = JobActive.Read();

        if (Reset.Read())
        {
            _prevActive = false;
            _event = 0;
            StageIndex.Value = 0;
        }
        else if (active && !_prevActive)
        {
            StageIndex.Value = 1;
            _event++;
        }
        _prevActive = active;

        EventNumber.Value = _event;
        UnitNumber.Value = UnitNumberValue.Value;
        // Дефолты реального cRIO (простаивающий, iCemCrio.pcapng.bin — кадр 36):
        // ControlMode = 1 (Downhole), MixMode = 2 (CMT_Cement). Значения постоянные
        // (реальная машина шлёт 1/2 даже в простое), независимо от JobActive.
        ControlMode.Value = 1u;   // 1 = Downhole (real idle cRIO)
        MixMode.Value = 2u;       // 2 = CMT_Cement (real idle cRIO)
        TrackId.Value = 38534;
        TrackDescript.Value = "Unit " + UnitNumberValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
