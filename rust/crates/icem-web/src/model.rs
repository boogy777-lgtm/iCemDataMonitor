use chrono::{DateTime, Utc};
use icem_bus::Zone;
use icem_core::{Freshness, SnapshotRead};
use std::collections::{BTreeMap, VecDeque};
use std::path::PathBuf;
use std::sync::{Arc, Mutex, mpsc};
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::oneshot;

#[derive(Clone)]
pub struct BusReply {
    pub read: Option<SnapshotRead>,
    pub is_open: bool,
}
pub struct LayerRequest {
    pub layer: icem_core::LayerKind,
    pub response: oneshot::Sender<Result<(), String>>,
}
pub struct ReadRequest {
    pub response: oneshot::Sender<BusReply>,
}
/// Owned Host projection. No capture handle or platform dependency enters WebState.
#[derive(Clone, Default)]
pub struct HostSniffer {
    pub available: bool,
    pub running: bool,
    pub enabled: bool,
    pub state: String,
    pub adapter: String,
    pub last_error: String,
    pub total_frames: u64,
    pub ace_frames: u64,
    pub distinct_macs: usize,
    pub mac_limit_reached: bool,
    pub frames_per_sec: f64,
    pub last_frame_age_sec: f64,
}
#[derive(Clone, Default)]
pub struct HostLayers {
    pub sniffer: Option<HostSniffer>,
    pub modbus_bound: bool,
    pub modbus_port: u16,
    /// Session plan for the next Host start, not the current HTTP listener.
    pub web: bool,
    pub opc_da: bool,
    pub set_as_default: bool,
    pub last_error: String,
}
#[derive(Default)]
pub(crate) struct Health {
    pub bus_open: bool,
    pub alive: bool,
    pub header: [i32; 4],
    pub zones: [(bool, i64); 3],
    pub at: i64,
    baseline: Option<i32>,
    stagnant: u32,
}
pub(crate) struct Store {
    pub last: Option<SnapshotRead>,
    pub health: Health,
    pub history: BTreeMap<u16, VecDeque<(i64, f32)>>,
}
#[derive(Clone)]
pub struct WebState {
    pub(crate) log_requests: Option<mpsc::SyncSender<crate::log_control::LogRequest>>,
    /// Read-once startup capability. Deferred Web toggles cannot change routes/history.
    pub(crate) web_enabled: bool,
    pub(crate) boot: crate::BootStages,
    pub(crate) navigation: crate::navigation::Navigation,
    pub(crate) requests: mpsc::SyncSender<ReadRequest>,
    pub(crate) layer_requests: Option<mpsc::SyncSender<LayerRequest>>,
    pub(crate) store: Arc<Mutex<Store>>,
    pub(crate) pub_layers: Arc<Mutex<HostLayers>>,
    pub(crate) names: Arc<BTreeMap<u16, String>>,
    pub(crate) theme: PathBuf,
    pub(crate) streams: Arc<tokio::sync::Semaphore>,
    pub(crate) shutdown: tokio::sync::watch::Sender<bool>,
}
impl WebState {
    pub fn new(
        layers: HostLayers,
        theme: PathBuf,
        names: BTreeMap<u16, String>,
    ) -> (Self, mpsc::Receiver<ReadRequest>) {
        let (requests, receiver) = mpsc::sync_channel(64);
        let (shutdown, _) = tokio::sync::watch::channel(false);
        (
            Self {
                log_requests: None,
                web_enabled: layers.web,
                boot: crate::BootStages::default(),
                navigation: crate::navigation::Navigation::default(),
                requests,
                layer_requests: None,
                store: Arc::new(Mutex::new(Store {
                    last: None,
                    health: Health {
                        at: now_ms(),
                        ..Health::default()
                    },
                    history: BTreeMap::new(),
                })),
                pub_layers: Arc::new(Mutex::new(layers)),
                names: Arc::new(names),
                theme,
                streams: Arc::new(tokio::sync::Semaphore::new(64)),
                shutdown,
            },
            receiver,
        )
    }
    pub fn attach_navigation(&mut self, signals: Arc<dyn crate::navigation::ShellSignals>) {
        self.navigation = crate::navigation::Navigation::new(signals);
    }
    pub fn attach_boot(&mut self, boot: crate::BootStages) {
        self.boot = boot;
    }
    pub fn enable_layer_control(&mut self) -> mpsc::Receiver<LayerRequest> {
        let (sender, receiver) = mpsc::sync_channel(16);
        self.layer_requests = Some(sender);
        receiver
    }
    pub fn observe(&self, reply: &BusReply) {
        if let Some(read) = &reply.read
            && let Ok(mut store) = self.store.lock()
        {
            store.last = Some(read.clone());
        }
    }
    /// One immediate, then 1 Hz sample. Freshness follows legacy Stale, including D09.
    pub fn probe(&self, reply: &BusReply, at: i64) -> Result<(), &'static str> {
        let mut store = self.store.lock().map_err(|_| "web store poisoned")?;
        let health = &mut store.health;
        health.bus_open = reply.is_open;
        health.at = at;
        let Some(read) = &reply.read else {
            return Ok(());
        };
        let snapshot = &read.snapshot;
        health.zones = Zone::ALL.map(|zone| {
            let z = snapshot.zone(zone);
            (z.is_live(dotnet_ticks(at)), z.age_ms(dotnet_ticks(at)))
        });
        if read.freshness == Freshness::Fresh {
            let header = [0, 1, 2, 3].map(|i| i32::from(snapshot.words()[i].cast_signed()));
            health.alive = match health.baseline {
                None => {
                    health.stagnant = 0;
                    false
                }
                Some(previous) if previous != header[3] => {
                    health.stagnant = 0;
                    true
                }
                Some(_) => {
                    health.stagnant = health.stagnant.saturating_add(1);
                    health.stagnant < 3
                }
            };
            health.baseline = Some(header[3]);
            health.header = header;
            if self.web_enabled {
                for addr in (4..600).step_by(2) {
                    let value = if addr == 4 {
                        snapshot.unix_seconds().map(|x| x as f32)
                    } else {
                        snapshot.float_pair(addr)
                    };
                    if let Ok(value) = value {
                        let ring = store.history.entry(addr).or_default();
                        if ring.len() == 600 {
                            ring.pop_front();
                        }
                        ring.push_back((at, value));
                    }
                }
            }
        }
        store.last = Some(read.clone());
        Ok(())
    }
    pub fn set_modbus_bound(&self, bound: bool) -> Result<(), &'static str> {
        self.pub_layers
            .lock()
            .map_err(|_| "layer lock poisoned")?
            .modbus_bound = bound;
        Ok(())
    }
    pub fn set_sniffer(&self, sniffer: Option<HostSniffer>) -> Result<(), &'static str> {
        self.pub_layers
            .lock()
            .map_err(|_| "layer lock poisoned")?
            .sniffer = sniffer;
        Ok(())
    }
    /// Owned projection for the serialized Host owner and RPC/REST adapters.
    pub fn layers(&self) -> Result<HostLayers, &'static str> {
        self.pub_layers
            .lock()
            .map(|x| x.clone())
            .map_err(|_| "layer lock poisoned")
    }
    pub fn toggle_web_plan(&self) -> Result<(), &'static str> {
        let mut layers = self.pub_layers.lock().map_err(|_| "layer lock poisoned")?;
        layers.web = !layers.web;
        Ok(())
    }
    pub fn set_layer_error(&self, error: Option<String>) -> Result<(), &'static str> {
        self.pub_layers
            .lock()
            .map_err(|_| "layer lock poisoned")?
            .last_error = error.unwrap_or_default();
        Ok(())
    }
    pub(crate) async fn read(&self) -> Result<BusReply, tonic::Status> {
        let (response, wait) = oneshot::channel();
        self.requests
            .try_send(ReadRequest { response })
            .map_err(|_| tonic::Status::resource_exhausted("bus request queue full or stopped"))?;
        tokio::select! {
            result = wait => result.map_err(|_| tonic::Status::unavailable("bus owner stopped")),
            _ = tokio::time::sleep(std::time::Duration::from_secs(5)) => Err(tonic::Status::deadline_exceeded("bus owner did not answer")),
        }
    }
    pub(crate) fn theme_light(&self) -> bool {
        std::fs::read_to_string(&self.theme)
            .is_ok_and(|text| text.trim_start_matches('\u{feff}').trim() == "light")
    }
    pub(crate) fn set_theme(&self, light: bool) {
        if let Err(error) = std::fs::write(&self.theme, if light { "light" } else { "dark" }) {
            eprintln!("ui-theme write failed: {error}");
        }
        #[cfg(windows)]
        if let Err(error) = icem_platform_windows::signal_theme_changed() {
            eprintln!("ui-theme poke failed: {error}");
        }
    }
}
pub(crate) fn now_ms() -> i64 {
    match SystemTime::now().duration_since(UNIX_EPOCH) {
        Ok(value) => i64::try_from(value.as_millis()).unwrap_or(i64::MAX),
        Err(error) => -i64::try_from(error.duration().as_millis()).unwrap_or(i64::MAX),
    }
}
pub(crate) fn dotnet_ticks(at: i64) -> i64 {
    at.saturating_mul(10_000)
        .saturating_add(621_355_968_000_000_000)
}
pub(crate) fn iso(at: i64) -> String {
    DateTime::<Utc>::from_timestamp_millis(at)
        .map(|d| d.format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string())
        .unwrap_or_default()
}
pub(crate) fn history_iso(at: i64) -> String {
    DateTime::<Utc>::from_timestamp_millis(at)
        .map(|d| {
            format!(
                "{}.{:03}0000+00:00",
                d.format("%Y-%m-%dT%H:%M:%S"),
                d.timestamp_subsec_millis()
            )
        })
        .unwrap_or_default()
}
pub(crate) fn timestamp(at: i64) -> prost_types::Timestamp {
    prost_types::Timestamp {
        seconds: at.div_euclid(1000),
        nanos: (at.rem_euclid(1000) * 1_000_000) as i32,
    }
}
