#![forbid(unsafe_code)]
//! Shared application ports; protocol parsing and Windows APIs live elsewhere.
use icem_bus::Snapshot;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Freshness {
    Fresh,
    LastGood,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SnapshotOrigin {
    Captured,
    Retained,
}

#[derive(Debug, Clone)]
pub struct SnapshotRead {
    pub snapshot: Snapshot,
    /// Compatibility with legacy Stale flag; D09 may retain data with Stale=false.
    pub freshness: Freshness,
    pub origin: SnapshotOrigin,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SourceError {
    Unavailable,
    AccessDenied,
    SectionCapacity,
    InvalidLayout,
    LockTimeout,
    Io,
}

/// Implementations return owned bytes decoded after releasing the native mutex.
/// A failed read may return LastGood, never fabricate live measurements.
pub trait SnapshotSource {
    fn snapshot(&mut self) -> Result<SnapshotRead, SourceError>;
    fn last_error(&self) -> Option<SourceError> {
        None
    }
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
