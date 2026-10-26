// MixerBlock — смеситель (CTX_CementUnit.Mixer): плотность как 1st-order lag к
// BlendTarget (τ=7s) + noise; расходы от ValveCmd; интегралы Mix Water / Vol Meter;
// мешки цемента; фиксация режима Auto/Manual.

namespace Halliburton.iCemEmule.Dfb;

public sealed class MixerBlock : DfbBlockBase
{
    // входы
    public DfbSignal<double> BlendTarget { get; }
    public DfbSignal<double> WaterValveCmd { get; }
    public DfbSignal<double> CementValveCmd { get; }
    public DfbSignal<bool> JobActive { get; }
    public DfbSignal<bool> ResetStage { get; }
    // выходы
    public DfbSignal<double> RecircDensity { get; }
    public DfbSignal<double> RecircFlowRate { get; }
    public DfbSignal<double> RecircFlowVlvCmd { get; }
    public DfbSignal<double> DownholeDensity { get; }
    public DfbSignal<double> MixWaterRate { get; }
    public DfbSignal<double> MixWaterStageTotal { get; }
    public DfbSignal<double> MixWaterJobTotal { get; }
    public DfbSignal<double> DownholeTubLevel { get; }
    public DfbSignal<double> MixPumpPress { get; }
    public DfbSignal<double> MixWaterMnfldPress { get; }
    public DfbSignal<uint> AutoOrManualMix { get; }
    public DfbSignal<double> WaterPot { get; }
    public DfbSignal<double> CementPot { get; }
    public DfbSignal<double> CementVlvCmd { get; }
    public DfbSignal<double> WaterVlvCmd { get; }
    public DfbSignal<double> VolWaterPercent { get; }
    public DfbSignal<double> VolMeterRate { get; }
    public DfbSignal<double> VolMeterStageTotal { get; }
    public DfbSignal<double> VolMeterJobTotal { get; }
    public DfbSignal<double> StageSacks { get; }
    public DfbSignal<double> JobSacks { get; }
    public DfbSignal<double> MudCupDensity { get; }
    public DfbSignal<double> BlendDensity { get; }
    public DfbSignal<double> BlendYield { get; }
    public DfbSignal<double> BlendWtrReq { get; }
    public DfbSignal<double> BlendMixRate { get; }
    public DfbSignal<double> Rho12Hat { get; }
    public DfbSignal<double> H2Hat { get; }
    public DfbSignal<double> VhpDensityHp { get; }

    public DfbParameter<double> DensityTauSeconds { get; set; } = new(7.0);
    public DfbParameter<double> WaterRateGain { get; set; } = new(2.1);   // bpm @ 100% valve
    public DfbParameter<double> PotTauSeconds { get; set; } = new(3.0);
    public DfbParameter<double> DensityNoiseSigma { get; set; } = new(0.05);
    public DfbParameter<double> SacksPerBbl { get; set; } = new(0.9);
    public DfbParameter<double> BlendYieldValue { get; set; } = new(1.0);
    public DfbParameter<double> BlendWtrReqValue { get; set; } = new(5.0);

    private double _densityLagged;
    private double _waterPot;
    private double _cementPot;
    private double _mwStage;
    private double _mwJob;

    public MixerBlock(string name) : base(name)
    {
        BlendTarget = new DfbSignal<double>($"{name}.BlendTarget");
        WaterValveCmd = new DfbSignal<double>($"{name}.WaterValveCmd");
        CementValveCmd = new DfbSignal<double>($"{name}.CementValveCmd");
        JobActive = new DfbSignal<bool>($"{name}.JobActive");
        ResetStage = new DfbSignal<bool>($"{name}.ResetStage");
        Inputs = new DfbSignal[] { BlendTarget, WaterValveCmd, CementValveCmd, JobActive, ResetStage };

        RecircDensity = new DfbSignal<double>($"{name}.RecircDensity");
        RecircFlowRate = new DfbSignal<double>($"{name}.RecircFlowRate");
        RecircFlowVlvCmd = new DfbSignal<double>($"{name}.RecircFlowVlvCmd");
        DownholeDensity = new DfbSignal<double>($"{name}.DownholeDensity");
        MixWaterRate = new DfbSignal<double>($"{name}.MixWaterRate");
        MixWaterStageTotal = new DfbSignal<double>($"{name}.MixWaterStageTotal");
        MixWaterJobTotal = new DfbSignal<double>($"{name}.MixWaterJobTotal");
        DownholeTubLevel = new DfbSignal<double>($"{name}.DownholeTubLevel");
        MixPumpPress = new DfbSignal<double>($"{name}.MixPumpPress");
        MixWaterMnfldPress = new DfbSignal<double>($"{name}.MixWaterMnfldPress");
        AutoOrManualMix = new DfbSignal<uint>($"{name}.AutoOrManualMix");
        WaterPot = new DfbSignal<double>($"{name}.WaterPot");
        CementPot = new DfbSignal<double>($"{name}.CementPot");
        CementVlvCmd = new DfbSignal<double>($"{name}.CementVlvCmd");
        WaterVlvCmd = new DfbSignal<double>($"{name}.WaterVlvCmd");
        VolWaterPercent = new DfbSignal<double>($"{name}.VolWaterPercent");
        VolMeterRate = new DfbSignal<double>($"{name}.VolMeterRate");
        VolMeterStageTotal = new DfbSignal<double>($"{name}.VolMeterStageTotal");
        VolMeterJobTotal = new DfbSignal<double>($"{name}.VolMeterJobTotal");
        StageSacks = new DfbSignal<double>($"{name}.StageSacks");
        JobSacks = new DfbSignal<double>($"{name}.JobSacks");
        MudCupDensity = new DfbSignal<double>($"{name}.MudCupDensity");
        BlendDensity = new DfbSignal<double>($"{name}.BlendDensity");
        BlendYield = new DfbSignal<double>($"{name}.BlendYield");
        BlendWtrReq = new DfbSignal<double>($"{name}.BlendWtrReq");
        BlendMixRate = new DfbSignal<double>($"{name}.BlendMixRate");
        Rho12Hat = new DfbSignal<double>($"{name}.Rho12Hat");
        H2Hat = new DfbSignal<double>($"{name}.H2Hat");
        VhpDensityHp = new DfbSignal<double>($"{name}.VhpDensityHp");
        Outputs = new DfbSignal[]
        {
            RecircDensity, RecircFlowRate, RecircFlowVlvCmd, DownholeDensity, MixWaterRate,
            MixWaterStageTotal, MixWaterJobTotal, DownholeTubLevel, MixPumpPress, MixWaterMnfldPress,
            AutoOrManualMix, WaterPot, CementPot, CementVlvCmd, WaterVlvCmd, VolWaterPercent,
            VolMeterRate, VolMeterStageTotal, VolMeterJobTotal, StageSacks, JobSacks, MudCupDensity,
            BlendDensity, BlendYield, BlendWtrReq, BlendMixRate, Rho12Hat, H2Hat, VhpDensityHp
        };
    }

    public override void Execute(DfbContext ctx)
    {
        double jobActive = JobActive.Read() ? 1.0 : 0.0;
        double target = BlendTarget.Read();
        double tau = Math.Max(ctx.Dt, DensityTauSeconds.Value);
        _densityLagged += (target - _densityLagged) * (ctx.Dt / tau);

        double n1 = (ctx.Random.NextDouble() * 2.0 - 1.0) * DensityNoiseSigma.Value;
        double n2 = (ctx.Random.NextDouble() * 2.0 - 1.0) * DensityNoiseSigma.Value;

        RecircDensity.Value = Math.Max(0.0, _densityLagged + n1);
        DownholeDensity.Value = Math.Max(0.0, _densityLagged - 0.1 + n2);
        MudCupDensity.Value = Math.Max(0.0, _densityLagged + 0.2);
        BlendDensity.Value = _densityLagged;
        Rho12Hat.Value = _densityLagged + 0.1;
        VhpDensityHp.Value = _densityLagged + 0.3;

        double wv = WaterValveCmd.Read();
        double cv = CementValveCmd.Read();
        WaterVlvCmd.Value = wv;
        CementVlvCmd.Value = cv;
        RecircFlowVlvCmd.Value = wv;
        VolWaterPercent.Value = wv;

        double pt = Math.Max(ctx.Dt, PotTauSeconds.Value);
        _waterPot += (Clamp01(wv / 100.0) * 100.0 - _waterPot) * (ctx.Dt / pt);
        _cementPot += (Clamp01(cv / 100.0) * 100.0 - _cementPot) * (ctx.Dt / pt);
        WaterPot.Value = _waterPot;
        CementPot.Value = _cementPot;

        double rate = jobActive * WaterRateGain.Value * (wv / 100.0);
        MixWaterRate.Value = rate;
        VolMeterRate.Value = rate;
        RecircFlowRate.Value = rate + 0.05;

        MixPumpPress.Value = 120.0 + 60.0 * (wv / 100.0) + (ctx.Random.NextDouble() - 0.5) * 4.0;
        MixWaterMnfldPress.Value = 90.0 + 40.0 * (wv / 100.0) + (ctx.Random.NextDouble() - 0.5) * 3.0;
        DownholeTubLevel.Value = 6.5 + (ctx.Random.NextDouble() - 0.5) * 0.4;
        H2Hat.Value = 5000.0 + (ctx.Random.NextDouble() - 0.5) * 20.0;

        AutoOrManualMix.Value = jobActive > 0 ? 1u : 0u;
        BlendMixRate.Value = rate;
        BlendYield.Value = BlendYieldValue.Value;
        BlendWtrReq.Value = BlendWtrReqValue.Value;

        _mwStage += rate * ctx.Dt;
        _mwJob += rate * ctx.Dt;
        if (ResetStage.Read()) _mwStage = 0.0;
        MixWaterStageTotal.Value = _mwStage;
        MixWaterJobTotal.Value = _mwJob;
        VolMeterStageTotal.Value = _mwStage;
        VolMeterJobTotal.Value = _mwJob;
        StageSacks.Value = _mwStage * SacksPerBbl.Value;
        JobSacks.Value = _mwJob * SacksPerBbl.Value;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
