// CementUnitNetworkFactory — сборка DFB-сети цементировочного агрегата (CTX_CementUnit):
// PumpA, PumpB (OPKO-kickout), Mixer, F300Hpvt, RawIo, Temp, Sequencer. Дефолтные setpoint'ы.

namespace Halliburton.iCemEmule.Dfb;

public sealed class CementUnitNetworkFactory
{
    public static DfbNetwork Create()
    {
        var net = new DfbNetwork();

        var pumpA = new PumpSideBlock("PumpA");
        var pumpB = new PumpSideBlock("PumpB");
        var mixer = new MixerBlock("Mixer");
        var f300 = new F300HpvtBlock("F300Hpvt");
        var rawIo = new RawIoBlock("RawIo");
        var temp = new TemperatureBlock("Temp");
        var seq = new JobSequencerBlock("Sequencer");

        // общие входы сети
        var jobActive = new DfbSignal<bool>("Network.JobActive");
        var resetAll = new DfbSignal<bool>("Network.Reset");
        jobActive.Value = true;   // работа активна по умолчанию

        // дефолтные setpoint'ы
        pumpA.OpkoSetting.Value = 3000.0;
        pumpB.OpkoSetting.Value = 3000.0;
        pumpA.Rate.Value = 3.0;
        pumpB.Rate.Value = 3.0;
        mixer.BlendTarget.Value = 15.8;
        mixer.WaterValveCmd.Value = 60.0;
        mixer.CementValveCmd.Value = 55.0;

        // проводка общих сигналов
        pumpA.Reset.ConnectedFrom = resetAll;
        pumpB.Reset.ConnectedFrom = resetAll;
        mixer.ResetStage.ConnectedFrom = resetAll;
        mixer.JobActive.ConnectedFrom = jobActive;
        seq.JobActive.ConnectedFrom = jobActive;
        seq.Reset.ConnectedFrom = resetAll;

        // F300Hpvt читает Mixer.MixWaterRate (bbl/min → gpm) — топологически после Mixer
        f300.FlowRef.ConnectedFrom = mixer.MixWaterRate;

        // порядок Add = топологический (DfbNetwork упорядочивает сам)
        net.Add(pumpA);
        net.Add(pumpB);
        net.Add(mixer);
        net.Add(f300);
        net.Add(rawIo);
        net.Add(temp);
        net.Add(seq);

        return net;
    }
}
