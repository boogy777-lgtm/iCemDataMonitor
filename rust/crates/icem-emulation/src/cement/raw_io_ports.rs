//! Typed ports and parameters of RawIoBlock from the pinned C# source.
use super::*;
ports! { pub struct RawIoInputs {
}}
ports! { pub struct RawIoOutputs {
 relay_out: u32 => scalar "RelayOut",
 digital_inputs: u32 => scalar "DigitalInputs",
 digital_outputs: u32 => scalar "DigitalOutputs",
 spare_combined1: f64 => scalar "SpareCombined1",
 spare_combined2: f64 => scalar "SpareCombined2",
 analog_in: [f64; 8] => array "AnalogIn",
 analog_out: [f64; 4] => array "AnalogOut",
 count_in: [f64; 6] => array "CountIn",
 spare_analog_in: [f64; 4] => array "SpareAnalogIn",
 spare_stage_total: [f64; 4] => array "SpareStageTotal",
 spare_job_total: [f64; 4] => array "SpareJobTotal",
 spare_counter_in: [f64; 4] => array "SpareCounterIn",
}}
