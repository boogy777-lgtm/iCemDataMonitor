//! Typed ports and parameters of JobSequencerBlock from the pinned C# source.
use super::*;
ports! { pub struct JobSequencerInputs {
 job_active: bool => scalar "JobActive",
 reset: bool => scalar "Reset",
}}
ports! { pub struct JobSequencerOutputs {
 stage_index: u32 => scalar "StageIndex",
 event_number: u32 => scalar "EventNumber",
 unit_number: u32 => scalar "UnitNumber",
 control_mode: u32 => scalar "ControlMode",
 mix_mode: u32 => scalar "MixMode",
 track_id: u32 => scalar "TrackId",
 track_descript: Option<String> => scalar "TrackDescript",
}}
ports! { pub struct JobSequencerParameters {
 unit_number_value: u32 => scalar "UnitNumberValue",
}}
impl JobSequencerParameters {
    pub fn original_defaults() -> Self {
        Self {
            unit_number_value: 1,
        }
    }
}
