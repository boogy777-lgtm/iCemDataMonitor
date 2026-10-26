// DfbSignalSource — маппинг wireName (families.lan.wireName) → сигналы сети.
// Таблица = INT_DfbEngine.yaml SignalMap (single source of truth). ¬ знает lantable/payload.

namespace Halliburton.iCemEmule.Dfb;

public sealed class DfbSignalSource
{
    private readonly Dictionary<string, Func<double>> _map;

    public DfbSignalSource(DfbNetwork network)
    {
        _map = BuildMap(network);
    }

    public bool TryGetValue(string wireName, out double value)
    {
        if (wireName is not null && _map.TryGetValue(wireName, out var f))
        {
            value = f();
            return true;
        }
        value = 0.0;
        return false;
    }

    public double GetValue(string wireName) => TryGetValue(wireName, out var v) ? v : 0.0;

    private static Dictionary<string, Func<double>> BuildMap(DfbNetwork n)
    {
        double d(string p) => n.Signal<double>(p).Read();
        double b(string p) => n.Signal<bool>(p).Read() ? 1.0 : 0.0;
        double u(string p) => n.Signal<uint>(p).Read();

        var map = new Dictionary<string, Func<double>>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Lnd Mix Section (0x13120200) ──
            ["Recirc Density"] = () => d("Mixer.RecircDensity"),
            ["Recirc Flow Rate"] = () => d("Mixer.RecircFlowRate"),
            ["Recirc Flow Vlv Cmd"] = () => d("Mixer.RecircFlowVlvCmd"),
            ["Downhole Density"] = () => d("Mixer.DownholeDensity"),
            ["Mix Water Rate"] = () => d("Mixer.MixWaterRate"),
            ["Mix Water Stage Total"] = () => d("Mixer.MixWaterStageTotal"),
            ["Mix Water Job Total"] = () => d("Mixer.MixWaterJobTotal"),
            ["Downhole Tub Level"] = () => d("Mixer.DownholeTubLevel"),
            ["Mix Pump Press"] = () => d("Mixer.MixPumpPress"),
            ["Mix Water Mnfld Press"] = () => d("Mixer.MixWaterMnfldPress"),
            ["Auto or Manual Mix"] = () => u("Mixer.AutoOrManualMix"),
            ["Water Pot"] = () => d("Mixer.WaterPot"),
            ["Cement Pot"] = () => d("Mixer.CementPot"),
            ["Cement Vlv Cmd"] = () => d("Mixer.CementVlvCmd"),
            ["Water Vlv Cmd"] = () => d("Mixer.WaterVlvCmd"),
            ["Vol Water Percent"] = () => d("Mixer.VolWaterPercent"),
            ["Vol Meter Rate"] = () => d("Mixer.VolMeterRate"),
            ["Vol Meter Stage Total"] = () => d("Mixer.VolMeterStageTotal"),
            ["Vol Meter Job Total"] = () => d("Mixer.VolMeterJobTotal"),
            ["Stage Sacks of Cement"] = () => d("Mixer.StageSacks"),
            ["Job Sacks of Cement"] = () => d("Mixer.JobSacks"),
            ["Mud Cup Density"] = () => d("Mixer.MudCupDensity"),
            ["Current Blend Density"] = () => d("Mixer.BlendDensity"),
            ["Current Blend Yield"] = () => d("Mixer.BlendYield"),
            ["Current Blend Wtr Req"] = () => d("Mixer.BlendWtrReq"),
            ["Current Blend Mix Rate"] = () => d("Mixer.BlendMixRate"),
            ["Event Number"] = () => u("Sequencer.EventNumber"),
            // ── Lnd Automation (0x13120203) ──
            ["Unit Number"] = () => u("Sequencer.UnitNumber"),
            ["Control Mode"] = () => u("Sequencer.ControlMode"),
            ["Mix Mode"] = () => u("Sequencer.MixMode"),
            ["rho12_hat"] = () => d("Mixer.Rho12Hat"),
            ["h2_hat"] = () => d("Mixer.H2Hat"),
            ["vhp_density_HP"] = () => d("Mixer.VhpDensityHp"),
            // ── Lnd Pump Sect (0x13120206) — ядро OPKO/kickout ──
            ["Pump A Pressure"] = () => d("PumpA.RawPressure"),
            ["Raw Pmp A Pressure"] = () => d("PumpA.RawPressure"),
            ["OPKO A Setting"] = () => d("PumpA.OpkoSetting"),
            ["Side A Pressure At KO"] = () => d("PumpA.PressureAtKo"),
            ["Side A Kicked Out?"] = () => b("PumpA.KickedOut"),
            ["Pump A Rate"] = () => d("PumpA.SetpointRate"),
            ["Pump A Stage Total"] = () => d("PumpA.StageTotal"),
            ["Pump A Job Total"] = () => d("PumpA.JobTotal"),
            ["Pump B Pressure"] = () => d("PumpB.RawPressure"),
            ["Raw Pmp B Pressure"] = () => d("PumpB.RawPressure"),
            ["OPKO B Setting"] = () => d("PumpB.OpkoSetting"),
            ["Side B Pressure At KO"] = () => d("PumpB.PressureAtKo"),
            ["Side B Kicked Out?"] = () => b("PumpB.KickedOut"),
            ["Pump B Rate"] = () => d("PumpB.SetpointRate"),
            ["Pump B Stage Total"] = () => d("PumpB.StageTotal"),
            ["Pump B Job Total"] = () => d("PumpB.JobTotal"),
            ["Combined Pump Rate"] = () => d("PumpA.SetpointRate") + d("PumpB.SetpointRate"),
            ["Combined Stage Total"] = () => d("PumpA.StageTotal") + d("PumpB.StageTotal"),
            ["Combined Job Total"] = () => d("PumpA.JobTotal") + d("PumpB.JobTotal"),
            ["Highest Pump Pressure"] = () => Math.Max(d("PumpA.RawPressure"), d("PumpB.RawPressure")),
            // ── Lnd RAWIO Data (0x13120209) ──
            ["Relay Out"] = () => u("RawIo.RelayOut"),
            ["Digital Inputs"] = () => u("RawIo.DigitalInputs"),
            ["Digital Outputs"] = () => u("RawIo.DigitalOutputs"),
            ["Display Temperature"] = () => d("Temp.DisplayTemperature"),
            ["Controller Temperature"] = () => d("Temp.ControllerTemperature"),
            // ── Land Addon IO (0x1312020C) ──
            ["Spare Combined 1"] = () => d("RawIo.SpareCombined1"),
            ["Spare Combined 2"] = () => d("RawIo.SpareCombined2"),
            // ── Lnd Ops Track (0x1312020F) ──
            ["Cmnt Ops Track IDNum"] = () => u("Sequencer.TrackId"),
            ["Cmnt Ops Track Descript"] = () => 0.0,   // UC-строка — не аналог
            // ── RT Data A (firmware SV) ──
            ["Controller IP"] = () => 0.0,             // UC-строка — не DFB (симуляция из registry)
            ["CPU_Load"] = () => d("F300Hpvt.CpuLoad"),
            ["HMI Temp"] = () => d("F300Hpvt.HmiTempC"),
            ["Chassis Temp"] = () => d("F300Hpvt.ChassisTempC"),
            ["ControlMode"] = () => u("Sequencer.ControlMode"),
            ["PumpType"] = () => 0.0,
            ["Unit_Number"] = () => u("Sequencer.UnitNumber"),
            ["Tub Level mA"] = () => d("F300Hpvt.TubLevelMa"),
            ["TubLevel ft"] = () => d("Mixer.DownholeTubLevel"),
            ["Filtered Tub Level mA"] = () => d("F300Hpvt.TubLevelMa"),
            ["PS Pressure mA"] = () => d("F300Hpvt.PsPressureMa"),
            ["Raw PS Press psi"] = () => d("PumpA.RawPressure"),
            ["PS Pressure psi"] = () => d("PumpA.FilteredPressure"),
            ["PS HI Limit psi"] = () => d("PumpA.OpkoSetting"),
            ["DS Pressure mA"] = () => d("F300Hpvt.DsPressureMa"),
            ["Raw DS Press psi"] = () => d("PumpB.RawPressure"),
            ["DS Pressure psi"] = () => d("PumpB.FilteredPressure"),
            ["DS HI Limit psi"] = () => d("PumpB.OpkoSetting"),
            ["Cement FSO Setpoint"] = () => d("F300Hpvt.CementFsoSetpoint"),
            ["Water FSO Setpoint"] = () => d("F300Hpvt.WaterFsoSetpoint"),
            ["Tub Level Setpoint ft"] = () => d("F300Hpvt.TubLevelSetpointFt"),
            // ── RT Data B (firmware SV) ──
            ["Cement Valve Position %"] = () => d("Mixer.CementPot"),
            ["Water Valve Position %"] = () => d("Mixer.WaterPot"),
            ["Cement Valve Output %"] = () => d("Mixer.CementVlvCmd"),
            ["Water Valve Output %"] = () => d("Mixer.WaterVlvCmd"),
            ["PS Rate bpm"] = () => d("PumpA.SetpointRate"),
            ["PS Rate Hz"] = () => d("PumpA.SetpointRate") * 10.0,
            ["DS Rate bpm"] = () => d("PumpB.SetpointRate"),
            ["DS Rate Hz"] = () => d("PumpB.SetpointRate") * 10.0,
            ["Combined Pump Rate bpm"] = () => d("PumpA.SetpointRate") + d("PumpB.SetpointRate"),
            ["Mix Water Rate Hz"] = () => d("Mixer.MixWaterRate") * 10.0,
            ["Mix Water Rate gpm"] = () => d("Mixer.MixWaterRate") * 42.0,
            ["MixWater Rate Percent"] = () => d("Mixer.WaterVlvCmd"),
            ["Volumetric Rate bpm"] = () => d("Mixer.VolMeterRate"),
            ["Volumetric Rate Hz"] = () => d("Mixer.VolMeterRate") * 10.0,
            ["Recirc Flow Rate Hz"] = () => d("Mixer.RecircFlowRate") * 10.0,
            ["Recirc Flow Rate gpm"] = () => d("Mixer.RecircFlowRate") * 42.0,
            ["Mag Meter Flow Hz"] = () => d("F300Hpvt.MagMeterFlowHz"),
            // ── RT Data C (firmware SV) — Mud Cup Density уже есть (legacy) ──
            ["F300 Density Hz"] = () => d("F300Hpvt.F300DensityHz"),
            ["F300 MB_Density"] = () => d("F300Hpvt.F300MbDensity"),
            ["F300 MB_MassFlow"] = () => d("F300Hpvt.F300MbMassFlow"),
            ["F300 MB_Temp"] = () => d("F300Hpvt.F300MbTemp"),
            ["F300 Recirc Density ppg"] = () => d("F300Hpvt.F300RecircDensityPpg"),
            ["MB_F300 Volume Flow"] = () => d("F300Hpvt.MbF300VolumeFlow"),
            ["Actual_Cem_Rate"] = () => d("F300Hpvt.ActualCemRate"),
            ["Hfiltered"] = () => d("F300Hpvt.Hfiltered"),
            ["Hard Drive Space Used"] = () => d("F300Hpvt.HardDriveSpaceUsed"),
            ["WaterTest_Setpoint"] = () => d("F300Hpvt.WaterTestSetpoint"),
            ["StartWater_HighestValue"] = () => d("F300Hpvt.StartWaterHighestValue"),
            ["FullOpen_HighestValue"] = () => d("F300Hpvt.FullOpenHighestValue"),
            ["AutoTest_StageNumber"] = () => u("F300Hpvt.AutoTestStageNumber"),
            ["Avg_Stage_Rate_WaterTest"] = () => d("F300Hpvt.AvgStageRateWaterTest"),
            ["Extra Rate1 Flow Hz"] = () => d("F300Hpvt.ExtraRate1FlowHz"),
            ["Extra Rate2 Flow Hz"] = () => d("F300Hpvt.ExtraRate2FlowHz"),
            // ── RT Totals (firmware SV) — Mix Water Job/Stage Total уже есть (legacy) ──
            ["PS Pump Job Total"] = () => d("PumpA.JobTotal"),
            ["PS Pump Stage Total"] = () => d("PumpA.StageTotal"),
            ["DS Pump Job Total"] = () => d("PumpB.JobTotal"),
            ["DS Pump Stage Total"] = () => d("PumpB.StageTotal"),
            ["Combined Pump Job Total"] = () => d("PumpA.JobTotal") + d("PumpB.JobTotal"),
            ["Combined Pump Stage Total"] = () => d("PumpA.StageTotal") + d("PumpB.StageTotal"),
            ["Volumetric Job Total"] = () => d("Mixer.VolMeterJobTotal"),
            ["Volumetric Stage Total"] = () => d("Mixer.VolMeterStageTotal"),
            ["Cement Job Total"] = () => d("Mixer.JobSacks"),
            ["Cement Stage Total"] = () => d("Mixer.StageSacks"),
            ["Water Job Total"] = () => d("Mixer.MixWaterJobTotal") * 42.0,
            ["Water Stage Total"] = () => d("Mixer.MixWaterStageTotal") * 42.0,
            ["Spare 1 Total"] = () => d("RawIo.SpareStageTotal1"),
            ["Spare 2 Total"] = () => d("RawIo.SpareStageTotal2"),
            ["FI Spare 1 Stage Total"] = () => 5.0,
            ["FI Spare 2 Stage Total"] = () => 5.0,
            // ── HPVT (firmware SV) ──
            ["MB_DensHPVT"] = () => d("F300Hpvt.MbDensHpvt"),
            ["MB_DenCHPVT"] = () => d("F300Hpvt.MbDenCHpvt"),
            ["MB_FreqHPVT"] = () => d("F300Hpvt.MbFreqHpvt"),
            ["MB_TflowHPVT"] = () => d("F300Hpvt.MbTflowHpvt"),
            ["MB_TcaseHPVT"] = () => d("F300Hpvt.MbTcaseHpvt"),
            ["MB_CpcHPVTT"] = () => d("F300Hpvt.MbCpcHpvtt"),
            ["MB_VlpoHPVT"] = () => d("F300Hpvt.MbVlpoHpvt"),
            // ── Observer A/B (firmware SV) — rho12_hat/h2_hat/vhp_density_HP уже есть (legacy) ──
            ["rho12_hat_cem_2"] = () => d("F300Hpvt.Rho12HatCem2"),
            ["h2_hat2"] = () => d("F300Hpvt.H2Hat2"),
            ["h2_star_new"] = () => d("F300Hpvt.H2StarNew"),
            ["h2_D_hat"] = () => d("F300Hpvt.H2DHat"),
            ["ve_h2_last"] = () => d("F300Hpvt.VeH2Last"),
            ["water_height"] = () => d("F300Hpvt.WaterHeight"),
            ["mag_hat_slurry"] = () => d("F300Hpvt.MagHatSlurry"),
            ["Vwdot_star"] = () => d("F300Hpvt.VwdotStar"),
            ["Vcdot_star"] = () => d("F300Hpvt.VcdotStar"),
            ["Vcdot_star_HO"] = () => d("F300Hpvt.VcdotStarHo"),
            ["Vcdot_hat"] = () => d("F300Hpvt.VcdotHat"),
            ["V12dot_star"] = () => d("F300Hpvt.V12dotStar"),
            ["V12dot_hat"] = () => d("F300Hpvt.V12dotHat"),
            ["Vindot_star"] = () => d("F300Hpvt.VindotStar"),
            ["Vsdot_LP"] = () => d("F300Hpvt.VsdotLp"),
            ["V12airdot_hat"] = () => d("F300Hpvt.V12airdotHat"),
            ["mD1dot_hat"] = () => d("F300Hpvt.MD1dotHat"),
            ["mD1dot_hat_Cvol"] = () => d("F300Hpvt.MD1dotHatCvol"),
            ["mD1dot_hat_P"] = () => d("F300Hpvt.MD1dotHatP"),
            ["VD2dot_hat"] = () => d("F300Hpvt.VD2dotHat"),
            ["VD2dot_hat_P"] = () => d("F300Hpvt.VD2dotHatP"),
            ["vhp_density_HP2"] = () => d("F300Hpvt.VhpDensityHp2"),
            ["dVcement_den_2"] = () => d("F300Hpvt.DVcementDen2"),
            ["dTimeConstant"] = () => d("F300Hpvt.DTimeConstant"),
            ["dRangeMax"] = () => d("F300Hpvt.DRangeMax"),
            ["dNumStDev"] = () => d("F300Hpvt.DNumStDev"),
            ["P_out"] = () => d("F300Hpvt.POut"),
            ["I_out"] = () => d("F300Hpvt.IOut"),
            ["fault_number"] = () => d("F300Hpvt.FaultNumber"),
            ["fault_percent"] = () => d("F300Hpvt.FaultPercent"),
            ["fault_detect_slurry"] = () => d("F300Hpvt.FaultDetectSlurry"),
            ["fault_detect_height"] = () => d("F300Hpvt.FaultDetectHeight"),
            ["flow_coeff_fault"] = () => d("F300Hpvt.FlowCoeffFault"),
            ["flow_coeff_fault_ave_top"] = () => d("F300Hpvt.FlowCoeffFaultAveTop"),
            ["flow_coeff_fault_ave_bottom"] = () => d("F300Hpvt.FlowCoeffFaultAveBottom"),
            ["ddt_instant_valve_coeff_est"] = () => d("F300Hpvt.DdtInstantValveCoeffEst"),
            ["instant_valve_coeff"] = () => d("F300Hpvt.InstantValveCoeff"),
            ["water_valve_Cv_est"] = () => d("F300Hpvt.WaterValveCvEst"),
            ["Cv_dead_zone"] = () => d("F300Hpvt.CvDeadZone"),
            ["wtr_dead_zone_ave"] = () => d("F300Hpvt.WtrDeadZoneAve"),
            ["coeff_C"] = () => d("F300Hpvt.CoeffC"),
            ["PS_Pressure_Offset"] = () => d("F300Hpvt.PsPressureOffset"),
            ["DS_Pressure_Offset"] = () => d("F300Hpvt.DsPressureOffset"),
            ["PS_Pump_Offset"] = () => d("F300Hpvt.PsPumpOffset"),
            ["Heating_Cooling"] = () => d("F300Hpvt.HeatingCooling"),
            ["vhp_water_percent_premix"] = () => d("F300Hpvt.VhpWaterPercentPremix"),
            ["vhp_water_percent_downh"] = () => d("F300Hpvt.VhpWaterPercentDownh"),
            ["MB_CpcHPVTT g/cc per psi"] = () => d("F300Hpvt.MbCpcHpvtt"),
            // ── Pump Life (firmware SV) ──
            ["HMIValue_HT400DS_Life"] = () => d("F300Hpvt.HMIValueHT400DSLife"),
            ["HMIValue_HT400PS_Life"] = () => d("F300Hpvt.HMIValueHT400PSLife"),
            ["HMIValue_HT400DS_Life_LastRepack"] = () => d("F300Hpvt.HMIValueHT400DSLastRepack"),
            ["HMIValue_HT400PS_Life_LastRepack"] = () => d("F300Hpvt.HMIValueHT400PSLastRepack"),
            ["HMIValue_4x4_Life"] = () => d("F300Hpvt.HMIValue4x4Life"),
            ["HMIValue_6x5_Life"] = () => d("F300Hpvt.HMIValue6x5Life"),
            ["Combined_Pump_Life_Vol"] = () => d("F300Hpvt.CombinedPumpLifeVol"),
            ["MixWater_Pump_Life_Vol"] = () => d("F300Hpvt.MixWaterPumpLife"),
            ["Recirc_Pump_Life_Vol"] = () => d("F300Hpvt.RecircPumpLife"),
            ["HPPumpPSVol"] = () => d("F300Hpvt.HPPumpLifePS"),
            ["HPPumpVolDS"] = () => d("F300Hpvt.HPPumpLifeDS"),
            ["HPCmbPumpVol"] = () => d("F300Hpvt.HPCombinedPumpLife"),
            ["CF4x4PumpHrs"] = () => d("F300Hpvt.CF4x4PumpHrs"),
            ["CF6x5PumpHrs"] = () => d("F300Hpvt.CF6x5PumpHrs"),
            // ── Blend (firmware SV) — Current Blend Density/Yield/Wtr Req уже есть (legacy) ──
            ["View_Blend_Number"] = () => u("F300Hpvt.ViewBlendNumber"),
            ["Current_Blend_Number"] = () => u("F300Hpvt.CurrentBlendNumber"),
            ["Target_Blend_Number"] = () => u("F300Hpvt.TargetBlendNumber"),
            // ── Bool A/B (firmware SV) ──
            ["xF300_Ok"] = () => b("F300Hpvt.XF300Ok"),
            ["xF300_DataRead"] = () => b("F300Hpvt.XF300DataRead"),
            ["xHPVT_DataRead"] = () => b("F300Hpvt.XHpvtDataRead"),
            ["xSD_Enabled"] = () => b("F300Hpvt.XSdEnabled"),
            ["xF300_Modbus_Assigned"] = () => b("F300Hpvt.XF300ModbusAssigned"),
            ["xF300_Analog_Assigned"] = () => b("F300Hpvt.XF300AnalogAssigned"),
            ["xF300_Frequency_Assigned"] = () => b("F300Hpvt.XF300FrequencyAssigned"),
            ["xF300_Flow_1"] = () => b("F300Hpvt.XF300Flow1"),
            ["xF300_Flow_2"] = () => b("F300Hpvt.XF300Flow2"),
            ["xBall_Up_Warning"] = () => b("F300Hpvt.XBallUpWarning"),
            ["xYoke_On"] = () => b("F300Hpvt.XYokeOn"),
            ["xStarSim"] = () => b("F300Hpvt.XStarSim"),
            ["xWater_Test_Active"] = () => b("F300Hpvt.XWaterTestActive"),
            ["xDefragging"] = () => b("F300Hpvt.XDefragging"),
            ["xFilterDynamic"] = () => b("F300Hpvt.XFilterDynamic"),
            ["xPump_with_Maint"] = () => b("F300Hpvt.XPumpWithMaint"),
            ["xMud_Cup_Error"] = () => b("F300Hpvt.XMudCupError"),
            ["xBlink_Recirc_Rate"] = () => b("F300Hpvt.XBlinkRecircRate"),
            ["xWater_Auto_Man"] = () => b("F300Hpvt.XWaterAutoMan"),
            ["xMaster_Water_Auto_Man"] = () => b("F300Hpvt.XMasterWaterAutoMan"),
            ["xCement_Auto_Man"] = () => b("F300Hpvt.XCementAutoMan"),
            ["Start Job"] = () => b("Mixer.JobActive"),   // зарегистрирован в сети (Network.JobActive в _signals нет)
            ["Ack_Start"] = () => 0.0,
            ["NoSD_Acknowledged"] = () => 0.0,
            ["F300_ModbusAssigned"] = () => 1.0,
            ["F300_AnalogAssigned"] = () => 0.0,
        };

        for (int i = 1; i <= 8; i++)
        {
            int idx = i;   // for-loop: per-iteration capture НЕ гарантирован (только foreach)
            map[$"Analog Input {idx}"] = () => d($"RawIo.AnalogIn{idx}");
        }
        for (int i = 1; i <= 4; i++)
        {
            int idx = i;
            map[$"Analog Output {idx}"] = () => d($"RawIo.AnalogOut{idx}");
            map[$"Spare Analog In {idx}"] = () => d($"RawIo.SpareAnalogIn{idx}");
            map[$"Spare Stage Total {idx}"] = () => d($"RawIo.SpareStageTotal{idx}");
            map[$"Spare Job Total {idx}"] = () => d($"RawIo.SpareJobTotal{idx}");
            map[$"Spare Counter Input {idx}"] = () => d($"RawIo.SpareCounterIn{idx}");
        }
        for (int i = 1; i <= 6; i++)
        {
            int idx = i;
            map[$"Count Input {idx}"] = () => d($"RawIo.CountIn{idx}");
        }

        // ── RT Spare AI/FI (firmware SV) ──
        for (int i = 1; i <= 5; i++)
        {
            int idx = i;
            map[$"AI Spare {idx} mA"] = () => 12.0;
            if (idx <= 4)
            {
                map[$"AI Spare {idx} Scaled"] = () => d($"RawIo.SpareAnalogIn{idx}");
                map[$"AI Spare {idx} Filtered"] = () => d($"RawIo.SpareAnalogIn{idx}");
            }
            else
            {
                map[$"AI Spare {idx} Scaled"] = () => 25.0;   // RawIo.SpareAnalogIn — только 1..4
                map[$"AI Spare {idx} Filtered"] = () => 25.0;
            }
        }
        map["FI Spare 1 mA"] = () => 12.0;
        map["FI Spare 1 Scaled"] = () => 50.0;
        map["FI Spare 2 mA"] = () => 12.0;
        map["FI Spare 2 Scaled"] = () => 50.0;

        // ── WaterTest-стадии (firmware SV) ──
        for (int i = 1; i <= 12; i++)
        {
            int idx = i;
            map[$"WtrTest_St{idx}_RateActual"] = () => d($"F300Hpvt.WtrTestRateActual{idx}");
        }

        // ── Target Blend 1..6 (firmware SV) ──
        for (int i = 1; i <= 6; i++)
        {
            int idx = i;
            map[$"Target_Blend_{idx}"] = () => d($"F300Hpvt.TargetBlend{idx}");
        }

        // ── DI / Relay битовые (firmware SV) ──
        for (int i = 0; i <= 15; i++)
        {
            int idx = i;
            map[$"xDI_Spare {idx}"] = () => ((uint)u("RawIo.DigitalInputs") >> idx) & 1;
        }
        for (int i = 0; i <= 7; i++)
        {
            int idx = i;
            map[$"xRelay_CH {idx}"] = () => ((uint)u("RawIo.RelayOut") >> idx) & 1;
        }

        return map;
    }
}
