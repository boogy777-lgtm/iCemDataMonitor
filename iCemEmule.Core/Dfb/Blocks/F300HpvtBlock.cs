// F300HpvtBlock — имитация F300/HPVT-измерителей + observer/watertest/pump-life
// констант (14 новых firmware SV-таблиц). Блок независим от Mixer, кроме расходов:
// FlowRef = Mixer.MixWaterRate (bbl/min) → gpm ×42. Шум — детерминированный (ctx.Random).

namespace Halliburton.iCemEmule.Dfb;

public sealed class F300HpvtBlock : DfbBlockBase
{
    // вход
    public DfbSignal<double> FlowRef { get; }   // Mixer.MixWaterRate, bbl/min

    // ── измерители F300 ──
    public DfbSignal<double> F300DensityHz { get; }
    public DfbSignal<double> F300MbDensity { get; }
    public DfbSignal<double> F300MbMassFlow { get; }
    public DfbSignal<double> F300MbTemp { get; }
    public DfbSignal<double> F300RecircDensityPpg { get; }
    public DfbSignal<double> MbF300VolumeFlow { get; }
    public DfbSignal<double> MbDensHpvt { get; }
    public DfbSignal<double> MbDenCHpvt { get; }
    public DfbSignal<double> MbFreqHpvt { get; }
    public DfbSignal<double> MbTflowHpvt { get; }
    public DfbSignal<double> MbTcaseHpvt { get; }
    public DfbSignal<double> MbCpcHpvtt { get; }
    public DfbSignal<double> MbVlpoHpvt { get; }

    // ── water test ──
    public DfbSignal<double>[] WtrTestRateActual { get; }
    public DfbSignal<double> WaterTestSetpoint { get; }
    public DfbSignal<double> StartWaterHighestValue { get; }
    public DfbSignal<double> FullOpenHighestValue { get; }
    public DfbSignal<uint> AutoTestStageNumber { get; }
    public DfbSignal<double> AvgStageRateWaterTest { get; }

    // ── прочие аналоговые / mA ──
    public DfbSignal<double> ActualCemRate { get; }
    public DfbSignal<double> Hfiltered { get; }
    public DfbSignal<double> HardDriveSpaceUsed { get; }
    public DfbSignal<double> CpuLoad { get; }
    public DfbSignal<double> HmiTempC { get; }
    public DfbSignal<double> ChassisTempC { get; }
    public DfbSignal<double> CementFsoSetpoint { get; }
    public DfbSignal<double> WaterFsoSetpoint { get; }
    public DfbSignal<double> TubLevelSetpointFt { get; }
    public DfbSignal<double> MixWaterRatePercent { get; }
    public DfbSignal<double> MagMeterFlowHz { get; }
    public DfbSignal<double> ExtraRate1FlowHz { get; }
    public DfbSignal<double> ExtraRate2FlowHz { get; }
    public DfbSignal<double> MixWaterRateGpm { get; }
    public DfbSignal<double> RecircFlowRateGpm { get; }
    public DfbSignal<double> VwdotStar { get; }
    public DfbSignal<double> VcdotStar { get; }
    public DfbSignal<double> V12dotStar { get; }
    public DfbSignal<double> MD1dotHat { get; }
    public DfbSignal<double> TubLevelMa { get; }
    public DfbSignal<double> PsPressureMa { get; }
    public DfbSignal<double> DsPressureMa { get; }

    // ── observer-константы ──
    public DfbSignal<double> Rho12HatCem2 { get; }
    public DfbSignal<double> H2Hat2 { get; }
    public DfbSignal<double> H2StarNew { get; }
    public DfbSignal<double> H2DHat { get; }
    public DfbSignal<double> VeH2Last { get; }
    public DfbSignal<double> WaterHeight { get; }
    public DfbSignal<double> MagHatSlurry { get; }
    public DfbSignal<double> VcdotStarHo { get; }
    public DfbSignal<double> VcdotHat { get; }
    public DfbSignal<double> V12dotHat { get; }
    public DfbSignal<double> VindotStar { get; }
    public DfbSignal<double> VsdotLp { get; }
    public DfbSignal<double> V12airdotHat { get; }
    public DfbSignal<double> MD1dotHatCvol { get; }
    public DfbSignal<double> MD1dotHatP { get; }
    public DfbSignal<double> VD2dotHat { get; }
    public DfbSignal<double> VD2dotHatP { get; }
    public DfbSignal<double> VhpDensityHp2 { get; }
    public DfbSignal<double> DVcementDen2 { get; }
    public DfbSignal<double> DTimeConstant { get; }
    public DfbSignal<double> DRangeMax { get; }
    public DfbSignal<double> DNumStDev { get; }
    public DfbSignal<double> POut { get; }
    public DfbSignal<double> IOut { get; }
    public DfbSignal<double> FaultNumber { get; }
    public DfbSignal<double> FaultPercent { get; }
    public DfbSignal<double> FaultDetectSlurry { get; }
    public DfbSignal<double> FaultDetectHeight { get; }
    public DfbSignal<double> FlowCoeffFault { get; }
    public DfbSignal<double> FlowCoeffFaultAveTop { get; }
    public DfbSignal<double> FlowCoeffFaultAveBottom { get; }
    public DfbSignal<double> DdtInstantValveCoeffEst { get; }
    public DfbSignal<double> InstantValveCoeff { get; }
    public DfbSignal<double> WaterValveCvEst { get; }
    public DfbSignal<double> CvDeadZone { get; }
    public DfbSignal<double> WtrDeadZoneAve { get; }
    public DfbSignal<double> CoeffC { get; }
    public DfbSignal<double> PsPressureOffset { get; }
    public DfbSignal<double> DsPressureOffset { get; }
    public DfbSignal<double> PsPumpOffset { get; }
    public DfbSignal<double> HeatingCooling { get; }
    public DfbSignal<double> VhpWaterPercentPremix { get; }
    public DfbSignal<double> VhpWaterPercentDownh { get; }

    // ── pump-life ──
    public DfbSignal<double> HMIValueHT400DSLife { get; }
    public DfbSignal<double> HMIValueHT400PSLife { get; }
    public DfbSignal<double> HMIValueHT400DSLastRepack { get; }
    public DfbSignal<double> HMIValueHT400PSLastRepack { get; }
    public DfbSignal<double> HMIValue4x4Life { get; }
    public DfbSignal<double> HMIValue6x5Life { get; }
    public DfbSignal<double> CombinedPumpLifeVol { get; }
    public DfbSignal<double> MixWaterPumpLife { get; }
    public DfbSignal<double> RecircPumpLife { get; }
    public DfbSignal<double> HPPumpLifePS { get; }
    public DfbSignal<double> HPPumpLifeDS { get; }
    public DfbSignal<double> HPCombinedPumpLife { get; }
    public DfbSignal<double> CF4x4PumpHrs { get; }
    public DfbSignal<double> CF6x5PumpHrs { get; }

    // ── blend ──
    public DfbSignal<uint> ViewBlendNumber { get; }
    public DfbSignal<uint> CurrentBlendNumber { get; }
    public DfbSignal<uint> TargetBlendNumber { get; }
    public DfbSignal<double>[] TargetBlend { get; }
    public DfbSignal<double> CurrentBlendDensity { get; }
    public DfbSignal<double> CurrentBlendYield { get; }
    public DfbSignal<double> CurrentBlendWtrReq { get; }

    // ── status HX-флаги ──
    public DfbSignal<bool> XF300Ok { get; }
    public DfbSignal<bool> XF300DataRead { get; }
    public DfbSignal<bool> XHpvtDataRead { get; }
    public DfbSignal<bool> XSdEnabled { get; }
    public DfbSignal<bool> XF300ModbusAssigned { get; }
    public DfbSignal<bool> XF300AnalogAssigned { get; }
    public DfbSignal<bool> XF300FrequencyAssigned { get; }
    public DfbSignal<bool> XF300Flow1 { get; }
    public DfbSignal<bool> XF300Flow2 { get; }
    public DfbSignal<bool> XBallUpWarning { get; }
    public DfbSignal<bool> XYokeOn { get; }
    public DfbSignal<bool> XStarSim { get; }
    public DfbSignal<bool> XWaterTestActive { get; }
    public DfbSignal<bool> XDefragging { get; }
    public DfbSignal<bool> XFilterDynamic { get; }
    public DfbSignal<bool> XPumpWithMaint { get; }
    public DfbSignal<bool> XMudCupError { get; }
    public DfbSignal<bool> XBlinkRecircRate { get; }
    public DfbSignal<bool> XWaterAutoMan { get; }
    public DfbSignal<bool> XMasterWaterAutoMan { get; }
    public DfbSignal<bool> XCementAutoMan { get; }

    public F300HpvtBlock(string name) : base(name)
    {
        FlowRef = new DfbSignal<double>($"{name}.FlowRef");
        Inputs = new DfbSignal[] { FlowRef };

        // измерители F300
        F300DensityHz = new DfbSignal<double>($"{name}.F300DensityHz");
        F300MbDensity = new DfbSignal<double>($"{name}.F300MbDensity");
        F300MbMassFlow = new DfbSignal<double>($"{name}.F300MbMassFlow");
        F300MbTemp = new DfbSignal<double>($"{name}.F300MbTemp");
        F300RecircDensityPpg = new DfbSignal<double>($"{name}.F300RecircDensityPpg");
        MbF300VolumeFlow = new DfbSignal<double>($"{name}.MbF300VolumeFlow");
        MbDensHpvt = new DfbSignal<double>($"{name}.MbDensHpvt");
        MbDenCHpvt = new DfbSignal<double>($"{name}.MbDenCHpvt");
        MbFreqHpvt = new DfbSignal<double>($"{name}.MbFreqHpvt");
        MbTflowHpvt = new DfbSignal<double>($"{name}.MbTflowHpvt");
        MbTcaseHpvt = new DfbSignal<double>($"{name}.MbTcaseHpvt");
        MbCpcHpvtt = new DfbSignal<double>($"{name}.MbCpcHpvtt");
        MbVlpoHpvt = new DfbSignal<double>($"{name}.MbVlpoHpvt");

        // water test
        WtrTestRateActual = NewArray(name, "WtrTestRateActual", 12);
        WaterTestSetpoint = new DfbSignal<double>($"{name}.WaterTestSetpoint");
        StartWaterHighestValue = new DfbSignal<double>($"{name}.StartWaterHighestValue");
        FullOpenHighestValue = new DfbSignal<double>($"{name}.FullOpenHighestValue");
        AutoTestStageNumber = new DfbSignal<uint>($"{name}.AutoTestStageNumber");
        AvgStageRateWaterTest = new DfbSignal<double>($"{name}.AvgStageRateWaterTest");

        // прочие аналоговые / mA
        ActualCemRate = new DfbSignal<double>($"{name}.ActualCemRate");
        Hfiltered = new DfbSignal<double>($"{name}.Hfiltered");
        HardDriveSpaceUsed = new DfbSignal<double>($"{name}.HardDriveSpaceUsed");
        CpuLoad = new DfbSignal<double>($"{name}.CpuLoad");
        HmiTempC = new DfbSignal<double>($"{name}.HmiTempC");
        ChassisTempC = new DfbSignal<double>($"{name}.ChassisTempC");
        CementFsoSetpoint = new DfbSignal<double>($"{name}.CementFsoSetpoint");
        WaterFsoSetpoint = new DfbSignal<double>($"{name}.WaterFsoSetpoint");
        TubLevelSetpointFt = new DfbSignal<double>($"{name}.TubLevelSetpointFt");
        MixWaterRatePercent = new DfbSignal<double>($"{name}.MixWaterRatePercent");
        MagMeterFlowHz = new DfbSignal<double>($"{name}.MagMeterFlowHz");
        ExtraRate1FlowHz = new DfbSignal<double>($"{name}.ExtraRate1FlowHz");
        ExtraRate2FlowHz = new DfbSignal<double>($"{name}.ExtraRate2FlowHz");
        MixWaterRateGpm = new DfbSignal<double>($"{name}.MixWaterRateGpm");
        RecircFlowRateGpm = new DfbSignal<double>($"{name}.RecircFlowRateGpm");
        VwdotStar = new DfbSignal<double>($"{name}.VwdotStar");
        VcdotStar = new DfbSignal<double>($"{name}.VcdotStar");
        V12dotStar = new DfbSignal<double>($"{name}.V12dotStar");
        MD1dotHat = new DfbSignal<double>($"{name}.MD1dotHat");
        TubLevelMa = new DfbSignal<double>($"{name}.TubLevelMa");
        PsPressureMa = new DfbSignal<double>($"{name}.PsPressureMa");
        DsPressureMa = new DfbSignal<double>($"{name}.DsPressureMa");

        // observer
        Rho12HatCem2 = new DfbSignal<double>($"{name}.Rho12HatCem2");
        H2Hat2 = new DfbSignal<double>($"{name}.H2Hat2");
        H2StarNew = new DfbSignal<double>($"{name}.H2StarNew");
        H2DHat = new DfbSignal<double>($"{name}.H2DHat");
        VeH2Last = new DfbSignal<double>($"{name}.VeH2Last");
        WaterHeight = new DfbSignal<double>($"{name}.WaterHeight");
        MagHatSlurry = new DfbSignal<double>($"{name}.MagHatSlurry");
        VcdotStarHo = new DfbSignal<double>($"{name}.VcdotStarHo");
        VcdotHat = new DfbSignal<double>($"{name}.VcdotHat");
        V12dotHat = new DfbSignal<double>($"{name}.V12dotHat");
        VindotStar = new DfbSignal<double>($"{name}.VindotStar");
        VsdotLp = new DfbSignal<double>($"{name}.VsdotLp");
        V12airdotHat = new DfbSignal<double>($"{name}.V12airdotHat");
        MD1dotHatCvol = new DfbSignal<double>($"{name}.MD1dotHatCvol");
        MD1dotHatP = new DfbSignal<double>($"{name}.MD1dotHatP");
        VD2dotHat = new DfbSignal<double>($"{name}.VD2dotHat");
        VD2dotHatP = new DfbSignal<double>($"{name}.VD2dotHatP");
        VhpDensityHp2 = new DfbSignal<double>($"{name}.VhpDensityHp2");
        DVcementDen2 = new DfbSignal<double>($"{name}.DVcementDen2");
        DTimeConstant = new DfbSignal<double>($"{name}.DTimeConstant");
        DRangeMax = new DfbSignal<double>($"{name}.DRangeMax");
        DNumStDev = new DfbSignal<double>($"{name}.DNumStDev");
        POut = new DfbSignal<double>($"{name}.POut");
        IOut = new DfbSignal<double>($"{name}.IOut");
        FaultNumber = new DfbSignal<double>($"{name}.FaultNumber");
        FaultPercent = new DfbSignal<double>($"{name}.FaultPercent");
        FaultDetectSlurry = new DfbSignal<double>($"{name}.FaultDetectSlurry");
        FaultDetectHeight = new DfbSignal<double>($"{name}.FaultDetectHeight");
        FlowCoeffFault = new DfbSignal<double>($"{name}.FlowCoeffFault");
        FlowCoeffFaultAveTop = new DfbSignal<double>($"{name}.FlowCoeffFaultAveTop");
        FlowCoeffFaultAveBottom = new DfbSignal<double>($"{name}.FlowCoeffFaultAveBottom");
        DdtInstantValveCoeffEst = new DfbSignal<double>($"{name}.DdtInstantValveCoeffEst");
        InstantValveCoeff = new DfbSignal<double>($"{name}.InstantValveCoeff");
        WaterValveCvEst = new DfbSignal<double>($"{name}.WaterValveCvEst");
        CvDeadZone = new DfbSignal<double>($"{name}.CvDeadZone");
        WtrDeadZoneAve = new DfbSignal<double>($"{name}.WtrDeadZoneAve");
        CoeffC = new DfbSignal<double>($"{name}.CoeffC");
        PsPressureOffset = new DfbSignal<double>($"{name}.PsPressureOffset");
        DsPressureOffset = new DfbSignal<double>($"{name}.DsPressureOffset");
        PsPumpOffset = new DfbSignal<double>($"{name}.PsPumpOffset");
        HeatingCooling = new DfbSignal<double>($"{name}.HeatingCooling");
        VhpWaterPercentPremix = new DfbSignal<double>($"{name}.VhpWaterPercentPremix");
        VhpWaterPercentDownh = new DfbSignal<double>($"{name}.VhpWaterPercentDownh");

        // pump-life
        HMIValueHT400DSLife = new DfbSignal<double>($"{name}.HMIValueHT400DSLife");
        HMIValueHT400PSLife = new DfbSignal<double>($"{name}.HMIValueHT400PSLife");
        HMIValueHT400DSLastRepack = new DfbSignal<double>($"{name}.HMIValueHT400DSLastRepack");
        HMIValueHT400PSLastRepack = new DfbSignal<double>($"{name}.HMIValueHT400PSLastRepack");
        HMIValue4x4Life = new DfbSignal<double>($"{name}.HMIValue4x4Life");
        HMIValue6x5Life = new DfbSignal<double>($"{name}.HMIValue6x5Life");
        CombinedPumpLifeVol = new DfbSignal<double>($"{name}.CombinedPumpLifeVol");
        MixWaterPumpLife = new DfbSignal<double>($"{name}.MixWaterPumpLife");
        RecircPumpLife = new DfbSignal<double>($"{name}.RecircPumpLife");
        HPPumpLifePS = new DfbSignal<double>($"{name}.HPPumpLifePS");
        HPPumpLifeDS = new DfbSignal<double>($"{name}.HPPumpLifeDS");
        HPCombinedPumpLife = new DfbSignal<double>($"{name}.HPCombinedPumpLife");
        CF4x4PumpHrs = new DfbSignal<double>($"{name}.CF4x4PumpHrs");
        CF6x5PumpHrs = new DfbSignal<double>($"{name}.CF6x5PumpHrs");

        // blend
        ViewBlendNumber = new DfbSignal<uint>($"{name}.ViewBlendNumber");
        CurrentBlendNumber = new DfbSignal<uint>($"{name}.CurrentBlendNumber");
        TargetBlendNumber = new DfbSignal<uint>($"{name}.TargetBlendNumber");
        TargetBlend = NewArray(name, "TargetBlend", 6);
        CurrentBlendDensity = new DfbSignal<double>($"{name}.CurrentBlendDensity");
        CurrentBlendYield = new DfbSignal<double>($"{name}.CurrentBlendYield");
        CurrentBlendWtrReq = new DfbSignal<double>($"{name}.CurrentBlendWtrReq");

        // status HX-флаги
        XF300Ok = new DfbSignal<bool>($"{name}.XF300Ok");
        XF300DataRead = new DfbSignal<bool>($"{name}.XF300DataRead");
        XHpvtDataRead = new DfbSignal<bool>($"{name}.XHpvtDataRead");
        XSdEnabled = new DfbSignal<bool>($"{name}.XSdEnabled");
        XF300ModbusAssigned = new DfbSignal<bool>($"{name}.XF300ModbusAssigned");
        XF300AnalogAssigned = new DfbSignal<bool>($"{name}.XF300AnalogAssigned");
        XF300FrequencyAssigned = new DfbSignal<bool>($"{name}.XF300FrequencyAssigned");
        XF300Flow1 = new DfbSignal<bool>($"{name}.XF300Flow1");
        XF300Flow2 = new DfbSignal<bool>($"{name}.XF300Flow2");
        XBallUpWarning = new DfbSignal<bool>($"{name}.XBallUpWarning");
        XYokeOn = new DfbSignal<bool>($"{name}.XYokeOn");
        XStarSim = new DfbSignal<bool>($"{name}.XStarSim");
        XWaterTestActive = new DfbSignal<bool>($"{name}.XWaterTestActive");
        XDefragging = new DfbSignal<bool>($"{name}.XDefragging");
        XFilterDynamic = new DfbSignal<bool>($"{name}.XFilterDynamic");
        XPumpWithMaint = new DfbSignal<bool>($"{name}.XPumpWithMaint");
        XMudCupError = new DfbSignal<bool>($"{name}.XMudCupError");
        XBlinkRecircRate = new DfbSignal<bool>($"{name}.XBlinkRecircRate");
        XWaterAutoMan = new DfbSignal<bool>($"{name}.XWaterAutoMan");
        XMasterWaterAutoMan = new DfbSignal<bool>($"{name}.XMasterWaterAutoMan");
        XCementAutoMan = new DfbSignal<bool>($"{name}.XCementAutoMan");

        var outputs = new List<DfbSignal>
        {
            F300DensityHz, F300MbDensity, F300MbMassFlow, F300MbTemp, F300RecircDensityPpg,
            MbF300VolumeFlow, MbDensHpvt, MbDenCHpvt, MbFreqHpvt, MbTflowHpvt, MbTcaseHpvt,
            MbCpcHpvtt, MbVlpoHpvt,
            WaterTestSetpoint, StartWaterHighestValue, FullOpenHighestValue, AutoTestStageNumber,
            AvgStageRateWaterTest,
            ActualCemRate, Hfiltered, HardDriveSpaceUsed, CpuLoad, HmiTempC, ChassisTempC,
            CementFsoSetpoint, WaterFsoSetpoint, TubLevelSetpointFt, MixWaterRatePercent,
            MagMeterFlowHz, ExtraRate1FlowHz, ExtraRate2FlowHz,
            MixWaterRateGpm, RecircFlowRateGpm, VwdotStar, VcdotStar, V12dotStar, MD1dotHat,
            TubLevelMa, PsPressureMa, DsPressureMa,
            Rho12HatCem2, H2Hat2, H2StarNew, H2DHat, VeH2Last, WaterHeight, MagHatSlurry,
            VcdotStarHo, VcdotHat, V12dotHat, VindotStar, VsdotLp, V12airdotHat,
            MD1dotHatCvol, MD1dotHatP, VD2dotHat, VD2dotHatP, VhpDensityHp2, DVcementDen2,
            DTimeConstant, DRangeMax, DNumStDev, POut, IOut,
            FaultNumber, FaultPercent, FaultDetectSlurry, FaultDetectHeight,
            FlowCoeffFault, FlowCoeffFaultAveTop, FlowCoeffFaultAveBottom,
            DdtInstantValveCoeffEst, InstantValveCoeff, WaterValveCvEst, CvDeadZone,
            WtrDeadZoneAve, CoeffC, PsPressureOffset, DsPressureOffset, PsPumpOffset,
            HeatingCooling, VhpWaterPercentPremix, VhpWaterPercentDownh,
            HMIValueHT400DSLife, HMIValueHT400PSLife, HMIValueHT400DSLastRepack,
            HMIValueHT400PSLastRepack, HMIValue4x4Life, HMIValue6x5Life, CombinedPumpLifeVol,
            MixWaterPumpLife, RecircPumpLife, HPPumpLifePS, HPPumpLifeDS, HPCombinedPumpLife,
            CF4x4PumpHrs, CF6x5PumpHrs,
            ViewBlendNumber, CurrentBlendNumber, TargetBlendNumber,
            CurrentBlendDensity, CurrentBlendYield, CurrentBlendWtrReq,
            XF300Ok, XF300DataRead, XHpvtDataRead, XSdEnabled,
            XF300ModbusAssigned, XF300AnalogAssigned, XF300FrequencyAssigned,
            XF300Flow1, XF300Flow2, XBallUpWarning, XYokeOn, XStarSim, XWaterTestActive,
            XDefragging, XFilterDynamic, XPumpWithMaint, XMudCupError, XBlinkRecircRate,
            XWaterAutoMan, XMasterWaterAutoMan, XCementAutoMan
        };
        outputs.AddRange(WtrTestRateActual);
        outputs.AddRange(TargetBlend);
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
        double flow = FlowRef.Read();

        // измерители F300/HPVT
        F300DensityHz.Value = 500.0 + (ctx.Random.NextDouble() - 0.5) * 4.0;
        F300MbDensity.Value = 8.7;
        F300MbMassFlow.Value = 1200.0;
        F300MbTemp.Value = 40.0;
        F300RecircDensityPpg.Value = 15.8 + 0.1 * Math.Sin(ctx.ElapsedSeconds * 0.1);
        MbF300VolumeFlow.Value = 6.0;
        MbDensHpvt.Value = 1.19;
        MbDenCHpvt.Value = 9.9;
        MbFreqHpvt.Value = 300.0;
        MbTflowHpvt.Value = 25.0;
        MbTcaseHpvt.Value = 30.0;
        MbCpcHpvtt.Value = 0.00002;
        MbVlpoHpvt.Value = 0.35;

        // water test
        for (int i = 0; i < WtrTestRateActual.Length; i++)
            WtrTestRateActual[i].Value = 3.0 + (ctx.Random.NextDouble() - 0.5) * 0.05;
        WaterTestSetpoint.Value = 3.0;
        StartWaterHighestValue.Value = 50.0;
        FullOpenHighestValue.Value = 220.0;
        AvgStageRateWaterTest.Value = 100.0;
        AutoTestStageNumber.Value = 0u;
        ActualCemRate.Value = 3.2;
        Hfiltered.Value = 6.5;
        HardDriveSpaceUsed.Value = 45.0;
        CpuLoad.Value = 35.0;
        HmiTempC.Value = 25.0;
        ChassisTempC.Value = 30.0;
        CementFsoSetpoint.Value = 25.0;
        WaterFsoSetpoint.Value = 25.0;
        TubLevelSetpointFt.Value = 6.5;
        MixWaterRatePercent.Value = 60.0;
        MagMeterFlowHz.Value = 100.0;
        ExtraRate1FlowHz.Value = 60.0;
        ExtraRate2FlowHz.Value = 60.0;

        // расходы, привязанные к Mixer.MixWaterRate (bbl/min → gpm ×42)
        double mixGpm = flow * 42.0;
        MixWaterRateGpm.Value = mixGpm;
        RecircFlowRateGpm.Value = (flow + 0.05) * 42.0;
        VwdotStar.Value = mixGpm * 0.7;
        VcdotStar.Value = mixGpm * 0.3;
        V12dotStar.Value = mixGpm;
        MD1dotHat.Value = 950.0 + (ctx.Random.NextDouble() - 0.5) * 10.0;

        // mA-сигналы
        TubLevelMa.Value = 12.0;
        PsPressureMa.Value = 12.0;
        DsPressureMa.Value = 12.0;

        // observer-константы
        Rho12HatCem2.Value = 15.9;
        H2Hat2.Value = 6.4;
        H2StarNew.Value = 6.5;
        H2DHat.Value = 6.5;
        VeH2Last.Value = 6.5;
        WaterHeight.Value = 6.5;
        MagHatSlurry.Value = 15.8;
        VcdotStarHo.Value = VcdotStar.Value;
        VcdotHat.Value = VcdotStar.Value;
        V12dotHat.Value = V12dotStar.Value - 1.0;
        VindotStar.Value = 2.0;
        VsdotLp.Value = 60.0;
        V12airdotHat.Value = 0.5;
        MD1dotHatCvol.Value = 940.0;
        MD1dotHatP.Value = 960.0;
        VD2dotHat.Value = 59.0;
        VD2dotHatP.Value = 58.0;
        VhpDensityHp2.Value = 15.8;
        DVcementDen2.Value = 0.02;
        DTimeConstant.Value = 7.0;
        DRangeMax.Value = 0.5;
        DNumStDev.Value = 2.0;
        POut.Value = 55.0;
        IOut.Value = 10.0;
        FaultNumber.Value = 0.0;
        FaultPercent.Value = 0.0;
        FaultDetectSlurry.Value = 0.0;
        FaultDetectHeight.Value = 0.0;
        FlowCoeffFault.Value = 0.9;
        FlowCoeffFaultAveTop.Value = 0.9;
        FlowCoeffFaultAveBottom.Value = 0.8;
        DdtInstantValveCoeffEst.Value = 0.01;
        InstantValveCoeff.Value = 0.01;
        WaterValveCvEst.Value = 42.0;
        CvDeadZone.Value = 5.0;
        WtrDeadZoneAve.Value = 5.0;
        CoeffC.Value = 0.9;
        PsPressureOffset.Value = 0.0;
        DsPressureOffset.Value = 0.0;
        PsPumpOffset.Value = 0.0;
        HeatingCooling.Value = 0.0;
        VhpWaterPercentPremix.Value = 65.0;
        VhpWaterPercentDownh.Value = 45.0;

        // pump-life
        HMIValueHT400DSLife.Value = 1000.0;
        HMIValueHT400PSLife.Value = 1200.0;
        HMIValueHT400DSLastRepack.Value = 300.0;
        HMIValueHT400PSLastRepack.Value = 400.0;
        HMIValue4x4Life.Value = 4000.0;
        HMIValue6x5Life.Value = 6000.0;
        CombinedPumpLifeVol.Value = 150.0;
        MixWaterPumpLife.Value = 80.0;
        RecircPumpLife.Value = 70.0;
        HPPumpLifePS.Value = 100.0;
        HPPumpLifeDS.Value = 100.0;
        HPCombinedPumpLife.Value = 200.0;
        CF4x4PumpHrs.Value = 500.0;
        CF6x5PumpHrs.Value = 800.0;

        // blend
        ViewBlendNumber.Value = 1u;
        CurrentBlendNumber.Value = 1u;
        TargetBlendNumber.Value = 1u;
        for (int i = 0; i < TargetBlend.Length; i++)
            TargetBlend[i].Value = 15.5 + (i + 1) * 0.2;
        CurrentBlendDensity.Value = 15.8;
        CurrentBlendYield.Value = 1.0;
        CurrentBlendWtrReq.Value = 5.0;

        // status HX-флаги
        XF300Ok.Value = true;
        XF300DataRead.Value = true;
        XHpvtDataRead.Value = true;
        XSdEnabled.Value = true;
        XF300ModbusAssigned.Value = true;
        XF300AnalogAssigned.Value = false;
        XF300FrequencyAssigned.Value = false;
        XF300Flow1.Value = false;
        XF300Flow2.Value = false;
        XBallUpWarning.Value = false;
        XYokeOn.Value = false;
        XStarSim.Value = false;
        XWaterTestActive.Value = false;
        XDefragging.Value = false;
        XFilterDynamic.Value = false;
        XPumpWithMaint.Value = false;
        XMudCupError.Value = false;
        XBlinkRecircRate.Value = false;
        XWaterAutoMan.Value = true;
        XMasterWaterAutoMan.Value = true;
        XCementAutoMan.Value = true;
    }
}
