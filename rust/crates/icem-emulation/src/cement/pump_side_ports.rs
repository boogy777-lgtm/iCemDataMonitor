//! Typed ports and parameters of PumpSideBlock from the pinned C# source.
use super::*;
ports! { pub struct PumpSideInputs {
 rate: f64 => scalar "SetpointRate",
 opko_setting: f64 => scalar "OpkoSetting",
 reset: bool => scalar "Reset",
}}
ports! { pub struct PumpSideOutputs {
 raw_pressure: f64 => scalar "RawPressure",
 filtered_pressure: f64 => scalar "FilteredPressure",
 pressure_at_ko: f64 => scalar "PressureAtKo",
 kicked_out: bool => scalar "KickedOut",
 stage_total: f64 => scalar "StageTotal",
 job_total: f64 => scalar "JobTotal",
}}
ports! { pub struct PumpSideParameters {
 pressure_base: f64 => scalar "PressureBase",
 resistance: f64 => scalar "Resistance",
 lag_tau_seconds: f64 => scalar "LagTauSeconds",
 noise_sigma: f64 => scalar "NoiseSigma",
}}
impl PumpSideParameters {
    pub fn original_defaults() -> Self {
        Self {
            pressure_base: 0.0,
            resistance: 400.0,
            lag_tau_seconds: 2.0,
            noise_sigma: 0.005,
        }
    }
}
