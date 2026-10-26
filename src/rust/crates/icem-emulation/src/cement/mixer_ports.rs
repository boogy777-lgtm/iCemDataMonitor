//! Typed ports and parameters of MixerBlock from the pinned C# source.
use super::*;
ports! { pub struct MixerInputs {
 blend_target: f64 => scalar "BlendTarget",
 water_valve_cmd: f64 => scalar "WaterValveCmd",
 cement_valve_cmd: f64 => scalar "CementValveCmd",
 job_active: bool => scalar "JobActive",
 reset_stage: bool => scalar "ResetStage",
}}
ports! { pub struct MixerOutputs {
 recirc_density: f64 => scalar "RecircDensity",
 recirc_flow_rate: f64 => scalar "RecircFlowRate",
 recirc_flow_vlv_cmd: f64 => scalar "RecircFlowVlvCmd",
 downhole_density: f64 => scalar "DownholeDensity",
 mix_water_rate: f64 => scalar "MixWaterRate",
 mix_water_stage_total: f64 => scalar "MixWaterStageTotal",
 mix_water_job_total: f64 => scalar "MixWaterJobTotal",
 downhole_tub_level: f64 => scalar "DownholeTubLevel",
 mix_pump_press: f64 => scalar "MixPumpPress",
 mix_water_mnfld_press: f64 => scalar "MixWaterMnfldPress",
 auto_or_manual_mix: u32 => scalar "AutoOrManualMix",
 water_pot: f64 => scalar "WaterPot",
 cement_pot: f64 => scalar "CementPot",
 cement_vlv_cmd: f64 => scalar "CementVlvCmd",
 water_vlv_cmd: f64 => scalar "WaterVlvCmd",
 vol_water_percent: f64 => scalar "VolWaterPercent",
 vol_meter_rate: f64 => scalar "VolMeterRate",
 vol_meter_stage_total: f64 => scalar "VolMeterStageTotal",
 vol_meter_job_total: f64 => scalar "VolMeterJobTotal",
 stage_sacks: f64 => scalar "StageSacks",
 job_sacks: f64 => scalar "JobSacks",
 mud_cup_density: f64 => scalar "MudCupDensity",
 blend_density: f64 => scalar "BlendDensity",
 blend_yield: f64 => scalar "BlendYield",
 blend_wtr_req: f64 => scalar "BlendWtrReq",
 blend_mix_rate: f64 => scalar "BlendMixRate",
 rho12_hat: f64 => scalar "Rho12Hat",
 h2_hat: f64 => scalar "H2Hat",
 vhp_density_hp: f64 => scalar "VhpDensityHp",
}}
ports! { pub struct MixerParameters {
 density_tau_seconds: f64 => scalar "DensityTauSeconds",
 water_rate_gain: f64 => scalar "WaterRateGain",
 pot_tau_seconds: f64 => scalar "PotTauSeconds",
 density_noise_sigma: f64 => scalar "DensityNoiseSigma",
 sacks_per_bbl: f64 => scalar "SacksPerBbl",
 blend_yield_value: f64 => scalar "BlendYieldValue",
 blend_wtr_req_value: f64 => scalar "BlendWtrReqValue",
}}
impl MixerParameters {
    pub fn original_defaults() -> Self {
        Self {
            density_tau_seconds: 7.0,
            water_rate_gain: 2.1,
            pot_tau_seconds: 3.0,
            density_noise_sigma: 0.05,
            sacks_per_bbl: 0.9,
            blend_yield_value: 1.0,
            blend_wtr_req_value: 5.0,
        }
    }
}
