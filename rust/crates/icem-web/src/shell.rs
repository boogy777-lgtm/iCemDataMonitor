//! In-process Shell contract; the frozen protobuf remains the RPC schema.
use crate::proto;
use serde_json::Value;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Layer {
    Modbus,
    Web,
    LanSniffer,
    OpcDa,
}
impl From<Layer> for proto::LayerKind {
    fn from(layer: Layer) -> Self {
        match layer {
            Layer::Modbus => Self::Modbus,
            Layer::Web => Self::Web,
            Layer::LanSniffer => Self::LanSniffer,
            Layer::OpcDa => Self::OpcDa,
        }
    }
}
impl TryFrom<i32> for Layer {
    type Error = &'static str;
    fn try_from(value: i32) -> Result<Self, Self::Error> {
        match proto::LayerKind::try_from(value) {
            Ok(proto::LayerKind::Modbus) => Ok(Self::Modbus),
            Ok(proto::LayerKind::Web) => Ok(Self::Web),
            Ok(proto::LayerKind::LanSniffer) => Ok(Self::LanSniffer),
            Ok(proto::LayerKind::OpcDa) => Ok(Self::OpcDa),
            _ => Err("invalid Shell layer"),
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Selection(Vec<u16>);
impl Selection {
    pub fn new(addresses: Vec<u16>) -> Result<Self, &'static str> {
        if addresses.len() > 298
            || addresses
                .iter()
                .any(|a| !(4..600).contains(a) || !a.is_multiple_of(2))
        {
            return Err("invalid dashboard selection");
        }
        Ok(Self(addresses))
    }
    pub fn addresses(&self) -> &[u16] {
        &self.0
    }
}
pub use icem_logging::Command as LogCommand;
#[derive(Clone, Debug)]
pub enum Request {
    Status,
    Prefs,
    Theme(bool),
    Layer(Layer),
    Console(bool),
    Autostart(bool),
    Navigate,
    Boot,
    Metrics,
    Dashboard(Selection),
    Logs(LogCommand),
    LocalLogs(LogCommand),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Operation {
    Status,
    Prefs,
    Theme,
    Layer,
    Console,
    Autostart,
    Navigate,
    Boot,
    Metrics,
    Dashboard,
    Logs,
    LocalLogs,
}
impl Request {
    pub fn operation(&self) -> Operation {
        match self {
            Self::Status => Operation::Status,
            Self::Prefs => Operation::Prefs,
            Self::Theme(_) => Operation::Theme,
            Self::Layer(_) => Operation::Layer,
            Self::Console(_) => Operation::Console,
            Self::Autostart(_) => Operation::Autostart,
            Self::Navigate => Operation::Navigate,
            Self::Boot => Operation::Boot,
            Self::Metrics => Operation::Metrics,
            Self::Dashboard(_) => Operation::Dashboard,
            Self::Logs(_) => Operation::Logs,
            Self::LocalLogs(_) => Operation::LocalLogs,
        }
    }
}
#[derive(Clone, Debug)]
pub struct Status {
    pub bus_open: bool,
    pub addin_alive: bool,
    pub heartbeat: i32,
    pub zones: [proto::ZoneHealth; 3],
    pub layers: proto::LayerState,
    pub console_visible: bool,
    pub autostart_enabled: bool,
}
impl TryFrom<proto::HostStatus> for Status {
    type Error = String;
    fn try_from(value: proto::HostStatus) -> Result<Self, String> {
        let layers = value.layers.ok_or("missing Host status layers")?;
        let mut zones: [Option<proto::ZoneHealth>; 3] = [None, None, None];
        for zone in value.zones {
            let slot = match proto::BusZoneKind::try_from(zone.kind) {
                Ok(proto::BusZoneKind::BusZoneCom) => 0,
                Ok(proto::BusZoneKind::BusZoneLan) => 1,
                Ok(proto::BusZoneKind::BusZoneRaw) => 2,
                _ => return Err("invalid Host status zone".into()),
            };
            if zones[slot].replace(zone).is_some() {
                return Err("duplicate Host status zone".into());
            }
        }
        let [com, lan, raw] = zones;
        Ok(Self {
            bus_open: value.bus_open,
            addin_alive: value.addin_alive,
            heartbeat: value.heartbeat,
            zones: [
                com.ok_or("missing COM zone")?,
                lan.ok_or("missing LAN zone")?,
                raw.ok_or("missing RAW zone")?,
            ],
            layers,
            console_visible: value.console_visible,
            autostart_enabled: value.autostart_enabled,
        })
    }
}
#[derive(Clone, Debug)]
pub struct BootBatch {
    pub stages: Vec<proto::BootStage>,
    pub reset: bool,
}
#[derive(Clone, Debug)]
pub enum Response {
    Status(Status),
    Prefs(proto::UiPrefs),
    Theme(proto::UiPrefs),
    Layer(proto::LayerState),
    Console(Status),
    Autostart(Status),
    Navigate(proto::NavigateUiResponse),
    Boot(BootBatch),
    Metrics(proto::MetricsSample),
    // Isolated adapter payloads for the next separate refactoring increments.
    Dashboard(Value),
    Logs(Result<icem_logging::Reply, icem_logging::ControlError>),
    LocalLogs(Result<icem_logging::Reply, icem_logging::ControlError>),
}
impl Response {
    pub fn status(value: proto::HostStatus) -> Result<Self, String> {
        value.try_into().map(Self::Status)
    }
    pub fn operation(&self) -> Operation {
        match self {
            Self::Status(_) => Operation::Status,
            Self::Prefs(_) => Operation::Prefs,
            Self::Theme(_) => Operation::Theme,
            Self::Layer(_) => Operation::Layer,
            Self::Console(_) => Operation::Console,
            Self::Autostart(_) => Operation::Autostart,
            Self::Navigate(_) => Operation::Navigate,
            Self::Boot(_) => Operation::Boot,
            Self::Metrics(_) => Operation::Metrics,
            Self::Dashboard(_) => Operation::Dashboard,
            Self::Logs(_) => Operation::Logs,
            Self::LocalLogs(_) => Operation::LocalLogs,
        }
    }
}
