//! Typed ports and parameters of TemperatureBlock from the pinned C# source.
use super::*;
ports! { pub struct TemperatureInputs {
}}
ports! { pub struct TemperatureOutputs {
 display_temperature: f64 => scalar "DisplayTemperature",
 controller_temperature: f64 => scalar "ControllerTemperature",
}}
ports! { pub struct TemperatureParameters {
 target: f64 => scalar "Target",
 tau_seconds: f64 => scalar "TauSeconds",
}}
impl TemperatureParameters {
    pub fn original_defaults() -> Self {
        Self {
            target: 120.0,
            tau_seconds: 60.0,
        }
    }
}
