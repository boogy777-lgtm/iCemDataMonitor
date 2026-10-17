#![forbid(unsafe_code)]
//! Independent emulator domain: pure simulation, unit conversion and cement DFB.
//! Hardware emitters, production control and GPUI are separate migration gates.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Channel {
    Com,
    Lan,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct StartupChannels {
    pub com_enabled: bool,
    pub lan_enabled: bool,
}

pub const CONTROL_PIPE: &str = "iCemEmule.Control";
pub const PROTOCOL_VERSION: u16 = 1;
pub const COMMANDS: &[&str] = &[
    "status",
    "start",
    "stop",
    "set",
    "get",
    "clear",
    "profiles",
    "profile",
    "vars",
    "watch",
    "quit",
    "help",
    "ports",
    "adapters",
    "setport",
    "setadapter",
    "setequipment",
    "setfirmware",
    "setemulate",
    "setpreset",
    "setunit",
];

pub mod dfb;
pub mod simulation;
pub mod units;

pub mod cement;

pub mod lan;
pub mod lan_pipeline;

pub mod control;
pub mod runtime;
pub mod runtime_ports;
