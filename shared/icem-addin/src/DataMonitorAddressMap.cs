// -----------------------------------------------------------------------------
// GENERATED FILE - do not hand-edit. Regenerate:
//   powershell -ExecutionPolicy Bypass -File gen_datamonitor_map.ps1
// Source of truth: C:\Users\HALLIBURTON\iCemPOC\templates\variable_registry.json
// LAYOUT v5 (bus register map v5, user decree 2026-08-17):
//   ComMap = flecs family: address = 4 + index x 2 (com_stream 4..199).
//   LanMap = lan family idx 0..95: address = 200 + index x 2 (lan_stream_crio
//   200..399; 96 cRIO signals of the observed ACE superframe per
//   Wireshark/PROTOCOL.md; idx >= 96 excluded from the curated view).
//   VENDOR ALIASES (additive-only, user dispatch 2026-08-18): abbreviated
//   cRIO display names from the vendor LAN source (live POLLUNIV harvest,
//   specs/FlecsEmu/??2c_lan_zone_classification.csv) ??? sidecar
//   lan_vendor_aliases.csv; alias => the EXISTING addr=200+idx*2 entry;
//   never a new address, never a moved entry.
//   ONE name -> ONE address per map; uniqueness asserted at generation.
// DEFECT-1 NORMALIZATION: NormMaps are built at RUNTIME from the maps via
// NameNormalizer (ASCII alnum-only, lowercase). TryGet* = exact-name fast
// path, then normalized fallback. Ambiguous norm keys (same norm key,
// different addresses) are excluded - exact path only for them.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Burservis.iCem.DataMonitor.Bus
{
    internal static class DataMonitorAddressMap
    {
        private static readonly Dictionary<string, int> ComMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Time & Date", 4 },   // idx 0 UnixTimestamp
            { "UnixTimestamp", 4 },   // idx 0 UnixTimestamp
            { "Unit Num", 6 },   // idx 1 UnitNum
            { "UnitNum", 6 },   // idx 1 UnitNum
            { "Slurry Density", 8 },   // idx 2 SlurryDensity
            { "SlurryDensity", 8 },   // idx 2 SlurryDensity
            { "Recirc Density", 8 },   // idx 2 SlurryDensity
            { "DH Density", 10 },   // idx 3 DH_Density
            { "DH_Density", 10 },   // idx 3 DH_Density
            { "Mix Water Rate", 12 },   // idx 4 MixWaterRate
            { "MixWaterRate", 12 },   // idx 4 MixWaterRate
            { "Pump Rate", 14 },   // idx 5 PumpRate
            { "PumpRate", 14 },   // idx 5 PumpRate
            { "Comb Pump Rate", 14 },   // idx 5 PumpRate
            { "Pump Pressure", 16 },   // idx 6 PumpPressure
            { "PumpPressure", 16 },   // idx 6 PumpPressure
            { "PS Pump Press", 16 },   // idx 6 PumpPressure
            { "DS Pump Press", 18 },   // idx 7 DSPumpPress
            { "DSPumpPress", 18 },   // idx 7 DSPumpPress
            { "Water Stg Tot", 20 },   // idx 8 WaterStgTot
            { "WaterStgTot", 20 },   // idx 8 WaterStgTot
            { "Mix Water Total", 22 },   // idx 9 MixWaterTotal
            { "MixWaterTotal", 22 },   // idx 9 MixWaterTotal
            { "Pump Stg Tot", 24 },   // idx 10 PumpStgTot
            { "PumpStgTot", 24 },   // idx 10 PumpStgTot
            { "Comb Pump Total", 26 },   // idx 11 CombPumpTotal
            { "CombPumpTotal", 26 },   // idx 11 CombPumpTotal
            { "Cmnt Valve Psn", 28 },   // idx 12 CementValvePsn
            { "CementValvePsn", 28 },   // idx 12 CementValvePsn
            { "Water Valve Psn", 30 },   // idx 13 WaterValvePsn
            { "WaterValvePsn", 30 },   // idx 13 WaterValvePsn
            { "PS Pump Rate", 32 },   // idx 14 PSPumpRate
            { "PSPumpRate", 32 },   // idx 14 PSPumpRate
            { "DS Pump Rate", 34 },   // idx 15 DSPumpRate
            { "DSPumpRate", 34 },   // idx 15 DSPumpRate
            { "Dig out", 36 },   // idx 16 DigOut
            { "DigOut", 36 },   // idx 16 DigOut
            { "HalWin Event", 38 },   // idx 17 HalWinEvent
            { "HalWinEvent", 38 },   // idx 17 HalWinEvent
            { "Temperature", 40 },   // idx 18 Temperature
            { "rho12_hat", 42 },   // idx 19 rho12_hat
            { "h2_hat", 44 },   // idx 20 h2_hat
            { "vhp_density_HP", 46 },   // idx 21 vhp_density_HP
            { "Tub Height", 48 },   // idx 22 TubHeight
            { "TubHeight", 48 },   // idx 22 TubHeight
            { "Extra Press 1", 50 },   // idx 23 ExtraPress1
            { "ExtraPress1", 50 },   // idx 23 ExtraPress1
            { "Extra Press 2", 52 },   // idx 24 ExtraPress2
            { "ExtraPress2", 52 },   // idx 24 ExtraPress2
            { "Extra Rate 1", 54 },   // idx 25 ExtraRate1
            { "ExtraRate1", 54 },   // idx 25 ExtraRate1
            { "Extra Total 1", 56 },   // idx 26 ExtraTotal1
            { "ExtraTotal1", 56 },   // idx 26 ExtraTotal1
            { "Extra Rate 2", 58 },   // idx 27 ExtraRate2
            { "ExtraRate2", 58 },   // idx 27 ExtraRate2
            { "Extra Total 2", 60 },   // idx 28 ExtraTotal2
            { "ExtraTotal2", 60 },   // idx 28 ExtraTotal2
            { "Wtr/slry Ratio", 62 },   // idx 29 WtrSlryRatio
            { "WtrSlryRatio", 62 },   // idx 29 WtrSlryRatio
            { "MudCup Density", 64 },   // idx 30 MudCupDensity
            { "MudCupDensity", 64 },   // idx 30 MudCupDensity
            { "PS Pmp Stg Tot", 66 },   // idx 31 PSPmpStgTot
            { "PSPmpStgTot", 66 },   // idx 31 PSPmpStgTot
            { "DS Pmp Stg Tot", 68 },   // idx 32 DSPmpStgTot
            { "DSPmpStgTot", 68 },   // idx 32 DSPmpStgTot
            { "VolumetricMeter", 70 },   // idx 33 VolumetricMeter
        };

        private static readonly Dictionary<string, int> LanMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Recirc Density", 200 },   // idx 0 Lan/RecircDensity
            { "Lan/RecircDensity", 200 },   // idx 0 Lan/RecircDensity
            { "Rcrc Density", 200 },   // idx 0 Lan/RecircDensity
            { "Recirc Flow Rate", 202 },   // idx 1 Lan/RecircFlowRate
            { "Lan/RecircFlowRate", 202 },   // idx 1 Lan/RecircFlowRate
            { "Recirc Fl Rte", 202 },   // idx 1 Lan/RecircFlowRate
            { "Recirc Flow Vlv Cmd", 204 },   // idx 2 Lan/RecircFlowVlvCmd
            { "Lan/RecircFlowVlvCmd", 204 },   // idx 2 Lan/RecircFlowVlvCmd
            { "Rec Vlv Cmd", 204 },   // idx 2 Lan/RecircFlowVlvCmd
            { "Downhole Density", 206 },   // idx 3 Lan/DownholeDensity
            { "Lan/DownholeDensity", 206 },   // idx 3 Lan/DownholeDensity
            { "Dwnhole Density", 206 },   // idx 3 Lan/DownholeDensity
            { "Mix Water Rate", 208 },   // idx 4 Lan/MixWaterRate
            { "Lan/MixWaterRate", 208 },   // idx 4 Lan/MixWaterRate
            { "Mix Water Stage Total", 210 },   // idx 5 Lan/MixWaterStageTotal
            { "Lan/MixWaterStageTotal", 210 },   // idx 5 Lan/MixWaterStageTotal
            { "Mix Wtr Stg Tot", 210 },   // idx 5 Lan/MixWaterStageTotal
            { "Mix Water Job Total", 212 },   // idx 6 Lan/MixWaterJobTotal
            { "Lan/MixWaterJobTotal", 212 },   // idx 6 Lan/MixWaterJobTotal
            { "Mix Wtr Job Tot", 212 },   // idx 6 Lan/MixWaterJobTotal
            { "Downhole Tub Level", 214 },   // idx 7 Lan/DownholeTubLevel
            { "Lan/DownholeTubLevel", 214 },   // idx 7 Lan/DownholeTubLevel
            { "Dwnhole Tub Lvl", 214 },   // idx 7 Lan/DownholeTubLevel
            { "Mix Pump Press", 216 },   // idx 8 Lan/MixPumpPress
            { "Lan/MixPumpPress", 216 },   // idx 8 Lan/MixPumpPress
            { "Mix Pump Pres", 216 },   // idx 8 Lan/MixPumpPress
            { "Mix Water Mnfld Press", 218 },   // idx 9 Lan/MixWaterMnfldPress
            { "Lan/MixWaterMnfldPress", 218 },   // idx 9 Lan/MixWaterMnfldPress
            { "Mix Water Pres", 218 },   // idx 9 Lan/MixWaterMnfldPress
            { "Auto or Manual Mix", 220 },   // idx 10 Lan/AutoOrManualMix
            { "Lan/AutoOrManualMix", 220 },   // idx 10 Lan/AutoOrManualMix
            { "Auto or Man Mix", 220 },   // idx 10 Lan/AutoOrManualMix
            { "Water Pot", 222 },   // idx 11 Lan/WaterPot
            { "Lan/WaterPot", 222 },   // idx 11 Lan/WaterPot
            { "Cement Pot", 224 },   // idx 12 Lan/CementPot
            { "Lan/CementPot", 224 },   // idx 12 Lan/CementPot
            { "Cement Vlv Cmd", 226 },   // idx 13 Lan/CementVlvCmd
            { "Lan/CementVlvCmd", 226 },   // idx 13 Lan/CementVlvCmd
            { "Cmnt Vlv Cmd", 226 },   // idx 13 Lan/CementVlvCmd
            { "Water Vlv Cmd", 228 },   // idx 14 Lan/WaterVlvCmd
            { "Lan/WaterVlvCmd", 228 },   // idx 14 Lan/WaterVlvCmd
            { "Vol Water Percent", 230 },   // idx 15 Lan/VolWaterPercent
            { "Lan/VolWaterPercent", 230 },   // idx 15 Lan/VolWaterPercent
            { "Vol Water %", 230 },   // idx 15 Lan/VolWaterPercent
            { "Vol Meter Rate", 232 },   // idx 16 Lan/VolMeterRate
            { "Lan/VolMeterRate", 232 },   // idx 16 Lan/VolMeterRate
            { "Vol Mtr Rate", 232 },   // idx 16 Lan/VolMeterRate
            { "Vol Meter Stage Total", 234 },   // idx 17 Lan/VolMeterStageTotal
            { "Lan/VolMeterStageTotal", 234 },   // idx 17 Lan/VolMeterStageTotal
            { "Vol Mtr Stg Tot", 234 },   // idx 17 Lan/VolMeterStageTotal
            { "Vol Meter Job Total", 236 },   // idx 18 Lan/VolMeterJobTotal
            { "Lan/VolMeterJobTotal", 236 },   // idx 18 Lan/VolMeterJobTotal
            { "Vol Mtr Job Tot", 236 },   // idx 18 Lan/VolMeterJobTotal
            { "Stage Sacks of Cement", 238 },   // idx 19 Lan/StageSacksOfCement
            { "Lan/StageSacksOfCement", 238 },   // idx 19 Lan/StageSacksOfCement
            { "Stage Sacks Cmt", 238 },   // idx 19 Lan/StageSacksOfCement
            { "Job Sacks of Cement", 240 },   // idx 20 Lan/JobSacksOfCement
            { "Lan/JobSacksOfCement", 240 },   // idx 20 Lan/JobSacksOfCement
            { "Job Sacks Cmt", 240 },   // idx 20 Lan/JobSacksOfCement
            { "Mud Cup Density", 242 },   // idx 21 Lan/MudCupDensity
            { "Lan/MudCupDensity", 242 },   // idx 21 Lan/MudCupDensity
            { "Mud Cup Dens", 242 },   // idx 21 Lan/MudCupDensity
            { "Current Blend Density", 244 },   // idx 22 Lan/CurrentBlendDensity
            { "Lan/CurrentBlendDensity", 244 },   // idx 22 Lan/CurrentBlendDensity
            { "CurBld Density", 244 },   // idx 22 Lan/CurrentBlendDensity
            { "Current Blend Yield", 246 },   // idx 23 Lan/CurrentBlendYield
            { "Lan/CurrentBlendYield", 246 },   // idx 23 Lan/CurrentBlendYield
            { "CurBld Yield", 246 },   // idx 23 Lan/CurrentBlendYield
            { "Current Blend Wtr Req", 248 },   // idx 24 Lan/CurrentBlendWtrReq
            { "Lan/CurrentBlendWtrReq", 248 },   // idx 24 Lan/CurrentBlendWtrReq
            { "CurBld Wtr Req", 248 },   // idx 24 Lan/CurrentBlendWtrReq
            { "Current Blend Mix Rate", 250 },   // idx 25 Lan/CurrentBlendMixRate
            { "Lan/CurrentBlendMixRate", 250 },   // idx 25 Lan/CurrentBlendMixRate
            { "CurBld Mix Rate", 250 },   // idx 25 Lan/CurrentBlendMixRate
            { "Event Number", 252 },   // idx 26 Lan/EventNumber
            { "Lan/EventNumber", 252 },   // idx 26 Lan/EventNumber
            { "Unit Number", 254 },   // idx 27 Lan/UnitNumber
            { "Lan/UnitNumber", 254 },   // idx 27 Lan/UnitNumber
            { "Unit Num", 254 },   // idx 27 Lan/UnitNumber
            { "Control Mode", 256 },   // idx 28 Lan/ControlMode
            { "Lan/ControlMode", 256 },   // idx 28 Lan/ControlMode
            { "Mix Mode", 258 },   // idx 29 Lan/MixMode
            { "Lan/MixMode", 258 },   // idx 29 Lan/MixMode
            { "rho12_hat", 260 },   // idx 30 Lan/Rho12_hat
            { "Lan/Rho12_hat", 260 },   // idx 30 Lan/Rho12_hat
            { "h2_hat", 262 },   // idx 31 Lan/H2_hat
            { "Lan/H2_hat", 262 },   // idx 31 Lan/H2_hat
            { "vhp_density_HP", 264 },   // idx 32 Lan/Vhp_density_hp
            { "Lan/Vhp_density_hp", 264 },   // idx 32 Lan/Vhp_density_hp
            { "Pump A Pressure", 266 },   // idx 33 Lan/PumpAPressure
            { "Lan/PumpAPressure", 266 },   // idx 33 Lan/PumpAPressure
            { "Raw Pmp A Pressure", 268 },   // idx 34 Lan/RawPmpAPressure
            { "Lan/RawPmpAPressure", 268 },   // idx 34 Lan/RawPmpAPressure
            { "Raw Pmp A Press", 268 },   // idx 34 Lan/RawPmpAPressure
            { "OPKO A Setting", 270 },   // idx 35 Lan/OpkoASetting
            { "Lan/OpkoASetting", 270 },   // idx 35 Lan/OpkoASetting
            { "Side A Pressure At KO", 272 },   // idx 36 Lan/SideAPressureAtKo
            { "Lan/SideAPressureAtKo", 272 },   // idx 36 Lan/SideAPressureAtKo
            { "SideA Pr At KO", 272 },   // idx 36 Lan/SideAPressureAtKo
            { "Side A Kicked Out?", 274 },   // idx 37 Lan/SideAKickedOut
            { "Lan/SideAKickedOut", 274 },   // idx 37 Lan/SideAKickedOut
            { "SideA KO?", 274 },   // idx 37 Lan/SideAKickedOut
            { "Pump A Rate", 276 },   // idx 38 Lan/PumpARate
            { "Lan/PumpARate", 276 },   // idx 38 Lan/PumpARate
            { "Pump A Stage Total", 278 },   // idx 39 Lan/PumpAStageTotal
            { "Lan/PumpAStageTotal", 278 },   // idx 39 Lan/PumpAStageTotal
            { "Pump A Stg Tot", 278 },   // idx 39 Lan/PumpAStageTotal
            { "Pump A Job Total", 280 },   // idx 40 Lan/PumpAJobTotal
            { "Lan/PumpAJobTotal", 280 },   // idx 40 Lan/PumpAJobTotal
            { "Pump A Job Tot", 280 },   // idx 40 Lan/PumpAJobTotal
            { "Pump B Pressure", 282 },   // idx 41 Lan/PumpBPressure
            { "Lan/PumpBPressure", 282 },   // idx 41 Lan/PumpBPressure
            { "Raw Pmp B Pressure", 284 },   // idx 42 Lan/RawPmpBPressure
            { "Lan/RawPmpBPressure", 284 },   // idx 42 Lan/RawPmpBPressure
            { "Raw Pmp B Press", 284 },   // idx 42 Lan/RawPmpBPressure
            { "OPKO B Setting", 286 },   // idx 43 Lan/OpkoBSetting
            { "Lan/OpkoBSetting", 286 },   // idx 43 Lan/OpkoBSetting
            { "Side B Pressure At KO", 288 },   // idx 44 Lan/SideBPressureAtKo
            { "Lan/SideBPressureAtKo", 288 },   // idx 44 Lan/SideBPressureAtKo
            { "SideB Pr At KO", 288 },   // idx 44 Lan/SideBPressureAtKo
            { "Side B Kicked Out?", 290 },   // idx 45 Lan/SideBKickedOut
            { "Lan/SideBKickedOut", 290 },   // idx 45 Lan/SideBKickedOut
            { "SideB KO?", 290 },   // idx 45 Lan/SideBKickedOut
            { "Pump B Rate", 292 },   // idx 46 Lan/PumpBRate
            { "Lan/PumpBRate", 292 },   // idx 46 Lan/PumpBRate
            { "Pump B Stage Total", 294 },   // idx 47 Lan/PumpBStageTotal
            { "Lan/PumpBStageTotal", 294 },   // idx 47 Lan/PumpBStageTotal
            { "Pump B Stg Tot", 294 },   // idx 47 Lan/PumpBStageTotal
            { "Pump B Job Total", 296 },   // idx 48 Lan/PumpBJobTotal
            { "Lan/PumpBJobTotal", 296 },   // idx 48 Lan/PumpBJobTotal
            { "Pump B Job Tot", 296 },   // idx 48 Lan/PumpBJobTotal
            { "Combined Pump Rate", 298 },   // idx 49 Lan/CombinedPumpRate
            { "Lan/CombinedPumpRate", 298 },   // idx 49 Lan/CombinedPumpRate
            { "Cmb Pump Rate", 298 },   // idx 49 Lan/CombinedPumpRate
            { "Combined Stage Total", 300 },   // idx 50 Lan/CombinedStageTotal
            { "Lan/CombinedStageTotal", 300 },   // idx 50 Lan/CombinedStageTotal
            { "Cmb Stg Total", 300 },   // idx 50 Lan/CombinedStageTotal
            { "Combined Job Total", 302 },   // idx 51 Lan/CombinedJobTotal
            { "Lan/CombinedJobTotal", 302 },   // idx 51 Lan/CombinedJobTotal
            { "Cmb Job Total", 302 },   // idx 51 Lan/CombinedJobTotal
            { "Highest Pump Pressure", 304 },   // idx 52 Lan/HighestPumpPressure
            { "Lan/HighestPumpPressure", 304 },   // idx 52 Lan/HighestPumpPressure
            { "Highest Pmp P", 304 },   // idx 52 Lan/HighestPumpPressure
            { "Relay Out", 306 },   // idx 53 Lan/RelayOut
            { "Lan/RelayOut", 306 },   // idx 53 Lan/RelayOut
            { "Digital Inputs", 308 },   // idx 54 Lan/DigitalInputs
            { "Lan/DigitalInputs", 308 },   // idx 54 Lan/DigitalInputs
            { "Digital In", 308 },   // idx 54 Lan/DigitalInputs
            { "Digital Outputs", 310 },   // idx 55 Lan/DigitalOutputs
            { "Lan/DigitalOutputs", 310 },   // idx 55 Lan/DigitalOutputs
            { "Digital Out", 310 },   // idx 55 Lan/DigitalOutputs
            { "Display Temperature", 312 },   // idx 56 Lan/DisplayTemperature
            { "Lan/DisplayTemperature", 312 },   // idx 56 Lan/DisplayTemperature
            { "Display Temp", 312 },   // idx 56 Lan/DisplayTemperature
            { "Controller Temperature", 314 },   // idx 57 Lan/ControllerTemperature
            { "Lan/ControllerTemperature", 314 },   // idx 57 Lan/ControllerTemperature
            { "Controller Temp", 314 },   // idx 57 Lan/ControllerTemperature
            { "Analog Input 1", 316 },   // idx 58 Lan/AnalogInput1
            { "Lan/AnalogInput1", 316 },   // idx 58 Lan/AnalogInput1
            { "Analog In 1", 316 },   // idx 58 Lan/AnalogInput1
            { "Analog Input 2", 318 },   // idx 59 Lan/AnalogInput2
            { "Lan/AnalogInput2", 318 },   // idx 59 Lan/AnalogInput2
            { "Analog In 2", 318 },   // idx 59 Lan/AnalogInput2
            { "Analog Input 3", 320 },   // idx 60 Lan/AnalogInput3
            { "Lan/AnalogInput3", 320 },   // idx 60 Lan/AnalogInput3
            { "Analog In 3", 320 },   // idx 60 Lan/AnalogInput3
            { "Analog Input 4", 322 },   // idx 61 Lan/AnalogInput4
            { "Lan/AnalogInput4", 322 },   // idx 61 Lan/AnalogInput4
            { "Analog In 4", 322 },   // idx 61 Lan/AnalogInput4
            { "Analog Input 5", 324 },   // idx 62 Lan/AnalogInput5
            { "Lan/AnalogInput5", 324 },   // idx 62 Lan/AnalogInput5
            { "Analog In 5", 324 },   // idx 62 Lan/AnalogInput5
            { "Analog Input 6", 326 },   // idx 63 Lan/AnalogInput6
            { "Lan/AnalogInput6", 326 },   // idx 63 Lan/AnalogInput6
            { "Analog In 6", 326 },   // idx 63 Lan/AnalogInput6
            { "Analog Input 7", 328 },   // idx 64 Lan/AnalogInput7
            { "Lan/AnalogInput7", 328 },   // idx 64 Lan/AnalogInput7
            { "Analog In 7", 328 },   // idx 64 Lan/AnalogInput7
            { "Analog Input 8", 330 },   // idx 65 Lan/AnalogInput8
            { "Lan/AnalogInput8", 330 },   // idx 65 Lan/AnalogInput8
            { "Analog In 8", 330 },   // idx 65 Lan/AnalogInput8
            { "Analog Output 1", 332 },   // idx 66 Lan/AnalogOutput1
            { "Lan/AnalogOutput1", 332 },   // idx 66 Lan/AnalogOutput1
            { "Analog Out 1", 332 },   // idx 66 Lan/AnalogOutput1
            { "Analog Output 2", 334 },   // idx 67 Lan/AnalogOutput2
            { "Lan/AnalogOutput2", 334 },   // idx 67 Lan/AnalogOutput2
            { "Analog Out 2", 334 },   // idx 67 Lan/AnalogOutput2
            { "Analog Output 3", 336 },   // idx 68 Lan/AnalogOutput3
            { "Lan/AnalogOutput3", 336 },   // idx 68 Lan/AnalogOutput3
            { "Analog Out 3", 336 },   // idx 68 Lan/AnalogOutput3
            { "Analog Output 4", 338 },   // idx 69 Lan/AnalogOutput4
            { "Lan/AnalogOutput4", 338 },   // idx 69 Lan/AnalogOutput4
            { "Analog Out 4", 338 },   // idx 69 Lan/AnalogOutput4
            { "Count Input 1", 340 },   // idx 70 Lan/CountInput1
            { "Lan/CountInput1", 340 },   // idx 70 Lan/CountInput1
            { "Count In 1", 340 },   // idx 70 Lan/CountInput1
            { "Count Input 2", 342 },   // idx 71 Lan/CountInput2
            { "Lan/CountInput2", 342 },   // idx 71 Lan/CountInput2
            { "Count In 2", 342 },   // idx 71 Lan/CountInput2
            { "Count Input 3", 344 },   // idx 72 Lan/CountInput3
            { "Lan/CountInput3", 344 },   // idx 72 Lan/CountInput3
            { "Count In 3", 344 },   // idx 72 Lan/CountInput3
            { "Count Input 4", 346 },   // idx 73 Lan/CountInput4
            { "Lan/CountInput4", 346 },   // idx 73 Lan/CountInput4
            { "Count In 4", 346 },   // idx 73 Lan/CountInput4
            { "Count Input 5", 348 },   // idx 74 Lan/CountInput5
            { "Lan/CountInput5", 348 },   // idx 74 Lan/CountInput5
            { "Count In 5", 348 },   // idx 74 Lan/CountInput5
            { "Count Input 6", 350 },   // idx 75 Lan/CountInput6
            { "Lan/CountInput6", 350 },   // idx 75 Lan/CountInput6
            { "Count In 6", 350 },   // idx 75 Lan/CountInput6
            { "Spare Analog In 1", 352 },   // idx 76 Lan/SpareAnalogIn1
            { "Lan/SpareAnalogIn1", 352 },   // idx 76 Lan/SpareAnalogIn1
            { "Spare Analog In 2", 354 },   // idx 77 Lan/SpareAnalogIn2
            { "Lan/SpareAnalogIn2", 354 },   // idx 77 Lan/SpareAnalogIn2
            { "Spare Analog In 3", 356 },   // idx 78 Lan/SpareAnalogIn3
            { "Lan/SpareAnalogIn3", 356 },   // idx 78 Lan/SpareAnalogIn3
            { "Spare Analog In 4", 358 },   // idx 79 Lan/SpareAnalogIn4
            { "Lan/SpareAnalogIn4", 358 },   // idx 79 Lan/SpareAnalogIn4
            { "Spare Counter Input 1", 360 },   // idx 80 Lan/SpareCounterInput1
            { "Lan/SpareCounterInput1", 360 },   // idx 80 Lan/SpareCounterInput1
            { "Spare Count 1", 360 },   // idx 80 Lan/SpareCounterInput1
            { "Spare Stage Total 1", 362 },   // idx 81 Lan/SpareStageTotal1
            { "Lan/SpareStageTotal1", 362 },   // idx 81 Lan/SpareStageTotal1
            { "Spare StgTot1", 362 },   // idx 81 Lan/SpareStageTotal1
            { "Spare Job Total 1", 364 },   // idx 82 Lan/SpareJobTotal1
            { "Lan/SpareJobTotal1", 364 },   // idx 82 Lan/SpareJobTotal1
            { "Spare JobTot1", 364 },   // idx 82 Lan/SpareJobTotal1
            { "Spare Counter Input 2", 366 },   // idx 83 Lan/SpareCounterInput2
            { "Lan/SpareCounterInput2", 366 },   // idx 83 Lan/SpareCounterInput2
            { "Spare Count 2", 366 },   // idx 83 Lan/SpareCounterInput2
            { "Spare Stage Total 2", 368 },   // idx 84 Lan/SpareStageTotal2
            { "Lan/SpareStageTotal2", 368 },   // idx 84 Lan/SpareStageTotal2
            { "Spare StgTot2", 368 },   // idx 84 Lan/SpareStageTotal2
            { "Spare Job Total 2", 370 },   // idx 85 Lan/SpareJobTotal2
            { "Lan/SpareJobTotal2", 370 },   // idx 85 Lan/SpareJobTotal2
            { "Spare JobTot2", 370 },   // idx 85 Lan/SpareJobTotal2
            { "Spare Counter Input 3", 372 },   // idx 86 Lan/SpareCounterInput3
            { "Lan/SpareCounterInput3", 372 },   // idx 86 Lan/SpareCounterInput3
            { "Spare Count 3", 372 },   // idx 86 Lan/SpareCounterInput3
            { "Spare Stage Total 3", 374 },   // idx 87 Lan/SpareStageTotal3
            { "Lan/SpareStageTotal3", 374 },   // idx 87 Lan/SpareStageTotal3
            { "Spare StgTot3", 374 },   // idx 87 Lan/SpareStageTotal3
            { "Spare Job Total 3", 376 },   // idx 88 Lan/SpareJobTotal3
            { "Lan/SpareJobTotal3", 376 },   // idx 88 Lan/SpareJobTotal3
            { "Spare JobTot3", 376 },   // idx 88 Lan/SpareJobTotal3
            { "Spare Counter Input 4", 378 },   // idx 89 Lan/SpareCounterInput4
            { "Lan/SpareCounterInput4", 378 },   // idx 89 Lan/SpareCounterInput4
            { "Spare Count 4", 378 },   // idx 89 Lan/SpareCounterInput4
            { "Spare Stage Total 4", 380 },   // idx 90 Lan/SpareStageTotal4
            { "Lan/SpareStageTotal4", 380 },   // idx 90 Lan/SpareStageTotal4
            { "Spare StgTot4", 380 },   // idx 90 Lan/SpareStageTotal4
            { "Spare Job Total 4", 382 },   // idx 91 Lan/SpareJobTotal4
            { "Lan/SpareJobTotal4", 382 },   // idx 91 Lan/SpareJobTotal4
            { "Spare JobTot4", 382 },   // idx 91 Lan/SpareJobTotal4
            { "Spare Combined 1", 384 },   // idx 92 Lan/SpareCombined1
            { "Lan/SpareCombined1", 384 },   // idx 92 Lan/SpareCombined1
            { "Spare Combined 2", 386 },   // idx 93 Lan/SpareCombined2
            { "Lan/SpareCombined2", 386 },   // idx 93 Lan/SpareCombined2
            { "Cmnt Ops Track IDNum", 388 },   // idx 94 Lan/CmntOpsTrackIdnum
            { "Lan/CmntOpsTrackIdnum", 388 },   // idx 94 Lan/CmntOpsTrackIdnum
            { "Op Track IDNum", 388 },   // idx 94 Lan/CmntOpsTrackIdnum
            { "Cmnt Ops Track Descript", 390 },   // idx 95 Lan/CmntOpsTrackDescript
            { "Lan/CmntOpsTrackDescript", 390 },   // idx 95 Lan/CmntOpsTrackDescript
            { "Cmnt Ops Track", 390 },   // idx 95 Lan/CmntOpsTrackDescript
        };

        // ?????? DEFECT-1 normalization fallbacks (built at runtime from the maps) ??????
        private static string[] _ambiguousComNormKeys;
        private static string[] _ambiguousLanNormKeys;
        private static readonly Dictionary<string, int> ComNormMap = BuildNormMap(ComMap, out _ambiguousComNormKeys);
        private static readonly Dictionary<string, int> LanNormMap = BuildNormMap(LanMap, out _ambiguousLanNormKeys);

        private static Dictionary<string, int> BuildNormMap(Dictionary<string, int> map, out string[] ambiguousKeys)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var conflicts = new List<string>();
            foreach (KeyValuePair<string, int> pair in map)
            {
                string norm = NameNormalizer.Normalize(pair.Key);
                if (norm.Length == 0 || conflicts.Contains(norm))
                {
                    continue;
                }
                int existing;
                if (result.TryGetValue(norm, out existing))
                {
                    if (existing != pair.Value)
                    {
                        // ambiguous: one normalized key, two addresses - exclude
                        result.Remove(norm);
                        conflicts.Add(norm);
                    }
                }
                else
                {
                    result[norm] = pair.Value;
                }
            }
            ambiguousKeys = conflicts.ToArray();
            return result;
        }

        /// <summary>com_stream lookup (flecs family). Exact-name fast path first;
        /// miss = normalized fallback (DEFECT-1).</summary>
        public static bool TryGetComAddress(string name, out int address)
        {
            return TryGetAddressIn(ComMap, ComNormMap, name, out address);
        }

        /// <summary>lan_stream_crio lookup (lan family idx 0..95). Exact-name fast
        /// path first; miss = normalized fallback (DEFECT-1).</summary>
        public static bool TryGetLanAddress(string name, out int address)
        {
            return TryGetAddressIn(LanMap, LanNormMap, name, out address);
        }

        private static bool TryGetAddressIn(Dictionary<string, int> map, Dictionary<string, int> normMap, string name, out int address)
        {
            if (name != null && map.TryGetValue(name, out address))
            {
                return true;
            }
            string norm = NameNormalizer.Normalize(name);
            if (norm.Length > 0 && normMap.TryGetValue(norm, out address))
            {
                return true;
            }
            address = 0;
            return false;
        }

        /// <summary>Debug surface: every com (name, address) pair (tests + TapOut trace).</summary>
        public static IEnumerable<KeyValuePair<string, int>> DebugComEntries()
        {
            return ComMap;
        }

        /// <summary>Debug surface: every lan (name, address) pair (tests + TapOut trace).</summary>
        public static IEnumerable<KeyValuePair<string, int>> DebugLanEntries()
        {
            return LanMap;
        }

        /// <summary>Diagnostics: normalized key counts + ambiguous exclusions (DEFECT-1).</summary>
        public static int DebugComNormCount()
        {
            return ComNormMap.Count;
        }

        public static int DebugLanNormCount()
        {
            return LanNormMap.Count;
        }

        public static string[] DebugComAmbiguous()
        {
            return _ambiguousComNormKeys ?? new string[0];
        }

        public static string[] DebugLanAmbiguous()
        {
            return _ambiguousLanNormKeys ?? new string[0];
        }
    }
}

