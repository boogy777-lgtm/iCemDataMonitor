#![forbid(unsafe_code)]
//! Shared application ports; protocol parsing and Windows APIs live elsewhere.
use icem_bus::Snapshot;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Freshness {
    Fresh,
    LastGood,
}

#[derive(Debug, Clone)]
pub struct SnapshotRead {
    pub snapshot: Snapshot,
    pub freshness: Freshness,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SourceError {
    Unavailable,
    AccessDenied,
    InvalidLayout,
    LockTimeout,
    Io,
}

/// Implementations return owned bytes decoded after releasing the native mutex.
/// A failed read may return LastGood, never fabricate live measurements.
pub trait SnapshotSource {
    fn snapshot(&mut self) -> Result<SnapshotRead, SourceError>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LayerKind {
    Modbus,
    Web,
    LanSniffer,
    OpcDa,
}

impl LayerKind {
    pub const fn runtime_toggleable(self) -> bool {
        match self {
            Self::Modbus | Self::LanSniffer => true,
            Self::Web | Self::OpcDa => false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostCommand {
    OpenPanel,
    OpenWeb,
    SetConsoleVisible(bool),
    Restart,
    Close,
}

/// Confirmed command sources: tray and Windows events. RPC uses only its
/// contracted subset; there is deliberately no shutdown RPC.
pub trait HostCommands {
    type Error;
    fn execute(&mut self, command: HostCommand) -> Result<(), Self::Error>;
}
