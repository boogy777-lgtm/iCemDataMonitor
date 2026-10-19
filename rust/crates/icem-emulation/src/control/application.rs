//! Typed in-process emulator API. Legacy coercions are confined to the parent adapter.
use super::{ControlReply, ControlResult, EmuControl, FailureEffect};
use crate::{Channel, units::Preset};
use serde_json::{Value, json};

impl Channel {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Com => "com",
            Self::Lan => "lan",
        }
    }
}
/// Valid COM/LAN channel; the v1 adapter retains caller spelling for frozen replies.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChannelName(String);
impl From<Channel> for ChannelName {
    fn from(value: Channel) -> Self {
        Self(value.as_str().into())
    }
}
impl ChannelName {
    pub fn as_str(&self) -> &str {
        &self.0
    }
    pub(super) fn legacy(value: &str) -> Self {
        Self(value.into())
    }
}
/// None means both channels. Unknown v1 variable scopes intentionally match neither.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Scope(Option<String>);
impl Scope {
    pub fn channel(channel: Channel) -> Self {
        Self(Some(channel.as_str().into()))
    }
    pub fn as_deref(&self) -> Option<&str> {
        self.0.as_deref()
    }
    pub(super) fn legacy(value: Option<&str>) -> Self {
        Self(value.map(str::to_owned))
    }
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Equipment(pub u32);
/// Typed callers admit finite native values only. The v1 adapter preserves IEEE NaN/Inf.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NativeValue(f64);
impl NativeValue {
    pub fn finite(value: f64) -> Result<Self, &'static str> {
        if value.is_finite() {
            Ok(Self(value))
        } else {
            Err("value must be finite")
        }
    }
    pub const fn get(self) -> f64 {
        self.0
    }
    pub(super) const fn legacy(value: f64) -> Self {
        Self(value)
    }
}
#[derive(Debug, Clone, PartialEq)]
pub struct Variable {
    pub scope: Scope,
    pub name: String,
    pub sv: Option<u32>,
}
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    Status,
    Start(ChannelName),
    Stop(ChannelName),
    Set(Variable, NativeValue),
    Get(Variable),
    Clear(Variable),
    Profiles,
    Profile {
        device_type: Option<String>,
        name: String,
    },
    Variables(Scope),
    Watch,
    Quit,
    Help,
    Ports,
    Adapters,
    SetPort(String),
    SetAdapter(String),
    SetEquipment(Equipment),
    SetFirmware(bool),
    SetEmulation(ChannelName, bool),
    SetPreset(Preset),
    SetUnit {
        group: String,
        unit: Option<String>,
    },
}
impl Command {
    pub const fn name(&self) -> &'static str {
        match self {
            Self::Status => "status",
            Self::Start(_) => "start",
            Self::Stop(_) => "stop",
            Self::Set(..) => "set",
            Self::Get(_) => "get",
            Self::Clear(_) => "clear",
            Self::Profiles => "profiles",
            Self::Profile { .. } => "profile",
            Self::Variables(_) => "vars",
            Self::Watch => "watch",
            Self::Quit => "quit",
            Self::Help => "help",
            Self::Ports => "ports",
            Self::Adapters => "adapters",
            Self::SetPort(_) => "setport",
            Self::SetAdapter(_) => "setadapter",
            Self::SetEquipment(_) => "setequipment",
            Self::SetFirmware(_) => "setfirmware",
            Self::SetEmulation(..) => "setemulate",
            Self::SetPreset(_) => "setpreset",
            Self::SetUnit { .. } => "setunit",
        }
    }
    /// Same request memory budget for typed UI callers and the v1 framing adapter.
    pub fn validate_budget(&self) -> Result<(), &'static str> {
        let variable = |v: &Variable| v.name.len() + v.scope.as_deref().map_or(0, str::len);
        let size = match self {
            Self::Set(v, _) | Self::Get(v) | Self::Clear(v) => variable(v),
            Self::Profile { device_type, name } => {
                name.len() + device_type.as_deref().map_or(0, str::len)
            }
            Self::SetPort(s) | Self::SetAdapter(s) => s.len(),
            Self::SetUnit { group, unit } => group.len() + unit.as_deref().map_or(0, str::len),
            Self::Variables(s) => s.as_deref().map_or(0, str::len),
            Self::Start(c) | Self::Stop(c) | Self::SetEmulation(c, _) => c.as_str().len(),
            Self::Status
            | Self::Profiles
            | Self::Watch
            | Self::Quit
            | Self::Help
            | Self::Ports
            | Self::Adapters
            | Self::SetEquipment(_)
            | Self::SetFirmware(_)
            | Self::SetPreset(_) => 0,
        };
        if size > 65_536 {
            Err("command exceeds 65536 byte text budget")
        } else {
            Ok(())
        }
    }
    pub const fn is_mutation(&self) -> bool {
        match self {
            Self::Status
            | Self::Get(_)
            | Self::Profiles
            | Self::Variables(_)
            | Self::Watch
            | Self::Help
            | Self::Ports
            | Self::Adapters => false,
            Self::Start(_)
            | Self::Stop(_)
            | Self::Set(..)
            | Self::Clear(_)
            | Self::Profile { .. }
            | Self::Quit
            | Self::SetPort(_)
            | Self::SetAdapter(_)
            | Self::SetEquipment(_)
            | Self::SetFirmware(_)
            | Self::SetEmulation(..)
            | Self::SetPreset(_)
            | Self::SetUnit { .. } => true,
        }
    }
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Outcome {
    Completed,
    Rejected,
    Applied,
    AppliedNotPersisted,
    AppliedWithError,
    Unknown,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorCategory {
    InvalidInput,
    Rejected,
    Persistence,
    Unavailable,
    Deadline,
    Disconnected,
    Protocol,
    Internal,
    Cancelled,
}
impl ErrorCategory {
    pub const fn code(self) -> &'static str {
        match self {
            Self::InvalidInput => "invalid_input",
            Self::Rejected => "rejected",
            Self::Persistence => "persistence",
            Self::Unavailable => "unavailable",
            Self::Deadline => "deadline",
            Self::Disconnected => "disconnected",
            Self::Protocol => "protocol",
            Self::Internal => "internal",
            Self::Cancelled => "cancelled",
        }
    }
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ApplicationError {
    pub category: ErrorCategory,
    pub message: String,
}
#[derive(Debug, Clone, PartialEq)]
pub struct Execution {
    pub command: &'static str,
    /// Frozen v1 projection: save failures deliberately do not rewrite this envelope.
    pub result: ControlResult,
    pub outcome: Outcome,
    pub error: Option<ApplicationError>,
}
impl Execution {
    pub fn rejected(
        command: &'static str,
        category: ErrorCategory,
        message: impl Into<String>,
    ) -> Self {
        let message = message.into();
        Self {
            command,
            result: ControlResult::fail(&message),
            outcome: Outcome::Rejected,
            error: Some(ApplicationError { category, message }),
        }
    }
    /// Called by the sole state owner while retaining its engine gate.
    pub fn saved(&mut self, result: Result<(), String>) {
        if let Err(message) = result {
            self.outcome = Outcome::AppliedNotPersisted;
            self.error = Some(ApplicationError {
                category: ErrorCategory::Persistence,
                message,
            });
        }
    }
    pub fn v1(&self) -> ControlReply {
        let mut reply = super::reply(self.command, self.result.clone());
        reply.start_watch = self.command == "watch" && self.result.ok;
        reply.quit = self.command == "quit" && self.result.ok;
        reply
    }
    /// Owned UI data; no JSON stringify/parse and no new external protocol version.
    pub fn ui_value(&self) -> Value {
        let mut value = super::to_value(
            self.result.ok,
            self.command,
            self.result.data.clone(),
            self.result.error.as_deref(),
        );
        value["application"] = json!({"outcome":format!("{:?}", self.outcome),
            "category":self.error.as_ref().map(|e| e.category.code()),
            "message":self.error.as_ref().map(|e| &e.message)});
        value
    }
}
/// The only application dispatch. No timer, persistence owner or engine clone lives here.
pub fn execute(command: Command, control: &mut impl EmuControl) -> Execution {
    let name = command.name();
    if let Err(error) = command.validate_budget() {
        return Execution::rejected(name, ErrorCategory::InvalidInput, error);
    }
    let mutation = command.is_mutation();
    let result = match command {
        Command::Status => {
            let mut data = control.get_status();
            data.insert("proto".into(), json!(super::PROTO_VERSION));
            ControlResult::success(Some(data))
        }
        Command::Start(c) => control.start_channel(c.as_str()),
        Command::Stop(c) => control.stop_channel(c.as_str()),
        Command::Set(v, value) => {
            control.set_variable(v.scope.as_deref(), &v.name, value.get(), v.sv)
        }
        Command::Get(v) => control.get_variable(v.scope.as_deref(), &v.name, v.sv),
        Command::Clear(v) => control.clear_variable(v.scope.as_deref(), &v.name, v.sv),
        Command::Profiles => control.get_profiles(),
        Command::Profile { device_type, name } => {
            control.set_profile(device_type.as_deref(), &name)
        }
        Command::Variables(c) => control.get_variables(c.as_deref()),
        Command::Watch => ControlResult::success(json!({"intervalSec":1}).as_object().cloned()),
        Command::Quit => {
            control.request_quit();
            ControlResult::success(json!({"quitting":true}).as_object().cloned())
        }
        Command::Help => ControlResult::success(
            json!({"commands":super::COMMAND_NAMES})
                .as_object()
                .cloned(),
        ),
        Command::Ports => control.get_ports(),
        Command::Adapters => control.get_adapters(),
        Command::SetPort(p) => control.set_port(&p),
        Command::SetAdapter(a) => control.set_adapter(&a),
        Command::SetEquipment(e) => control.set_equipment(e.0),
        Command::SetFirmware(on) => control.set_firmware(on),
        Command::SetEmulation(c, on) => control.set_emulation(c.as_str(), on),
        Command::SetPreset(p) => control.set_preset(match p {
            Preset::FirmwareDefault => 0,
            Preset::LanNative => 1,
            Preset::Metric => 2,
            Preset::Imperial => 3,
        }),
        Command::SetUnit { group, unit } => control.set_unit(&group, unit.as_deref()),
    };
    let outcome = if result.ok {
        if mutation {
            Outcome::Applied
        } else {
            Outcome::Completed
        }
    } else {
        match result.failure_effect {
            FailureEffect::None => Outcome::Rejected,
            FailureEffect::Applied => Outcome::AppliedWithError,
        }
    };
    let error = result.error.as_ref().map(|message| ApplicationError {
        category: ErrorCategory::Rejected,
        message: message.clone(),
    });
    Execution {
        command: name,
        result,
        outcome,
        error,
    }
}
