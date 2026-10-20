//! Shell-specific port and presentation. Emulator commands retain their own contract.
use crate::{
    BootStageRow, Model,
    worker::{Worker, WorkerCompletion},
};
use icem_web::{
    proto::register_delta,
    shell::{Operation, Request, Response},
};
use std::sync::Arc;
pub trait Port: Send + Sync + 'static {
    fn request(&self, command: Request) -> Result<Response, String>;
}
pub struct Reply {
    pub operation: Operation,
    pub result: Result<Response, String>,
}
pub type Bridge = Worker<Request, Reply>;
pub fn start(port: Arc<dyn Port>) -> std::io::Result<(Bridge, WorkerCompletion)> {
    let logs = icem_logging::SharedLogs::new(icem_logging::Owner::Shell);
    Worker::start(move |request: Request| {
        let operation = request.operation();
        let result = if let Request::LocalLogs(command) = request {
            Ok(Response::LocalLogs(logs.request(command)))
        } else {
            let result = port.request(request);
            match &result {
                Err(error) => logs.record(error),
                // Host log transport failures remain Shell diagnostics. Local logger
                // failures and owner Incomplete replies must never recurse into logging.
                Ok(Response::Logs(Err(error))) => logs.record(&error.message),
                _ => {}
            }
            result
        };
        Reply { operation, result }
    })
}
impl Model {
    pub fn apply_host(&mut self, operation: Operation, response: Result<Response, String>) {
        let result = response.and_then(|reply| {
            if reply.operation() == operation {
                Ok(reply)
            } else {
                Err("Host reply does not match request".into())
            }
        });
        let reply = match result {
            Ok(reply) => reply,
            Err(error) => {
                match operation {
                    Operation::Boot => {
                        self.boot_stages.clear();
                        self.boot_ready = false;
                        self.boot_error = Some(error);
                    }
                    Operation::Status | Operation::Console | Operation::Autostart => {
                        self.connected = false;
                        self.host_logs.fresh = false;
                        self.boot_stages.clear();
                        self.boot_ready = false;
                        self.note = format!("⚠ {error}");
                    }
                    Operation::Navigate => {
                        self.navigation = None;
                        self.note = format!(
                            "Navigation: {error}; результат неизвестен, автоматический повтор отключён"
                        );
                    }
                    Operation::Metrics => {
                        self.dashboard.metrics_pending = false;
                        self.dashboard.metrics_error = Some(error);
                    }
                    Operation::Dashboard => {
                        self.dashboard.pending = false;
                        self.dashboard.error = Some(error);
                    }
                    Operation::Logs => self
                        .host_logs
                        .apply(Err(icem_logging::ControlError::unknown(error))),
                    Operation::LocalLogs => self
                        .local_logs
                        .apply(Err(icem_logging::ControlError::unknown(error))),
                    Operation::Prefs | Operation::Theme | Operation::Layer => {
                        self.note = format!("⚠ {error}")
                    }
                }
                return;
            }
        };
        match reply {
            Response::Status(status) | Response::Console(status) | Response::Autostart(status) => {
                self.host = Some(status);
                self.connected = true;
            }
            Response::Prefs(prefs) | Response::Theme(prefs) => self.light = prefs.theme_light,
            Response::Layer(layers) => {
                self.note = layers.last_error.clone();
                if let Some(status) = &mut self.host {
                    status.layers = layers;
                }
            }
            Response::Navigate(reply) => {
                self.navigation = match icem_web::proto::UiSection::try_from(reply.pending) {
                    Ok(icem_web::proto::UiSection::Web) => Some(true),
                    Ok(icem_web::proto::UiSection::Status) => Some(false),
                    _ => None,
                };
            }
            Response::Boot(batch) => {
                if batch.reset {
                    self.boot_stages.clear();
                }
                for stage in batch.stages {
                    if (1..=64).contains(&stage.ordinal) {
                        self.boot_stages.insert(
                            stage.ordinal,
                            BootStageRow {
                                id: stage.id,
                                title: stage.title,
                                completed: stage.completed,
                                detail: stage.detail,
                            },
                        );
                    }
                }
                self.boot_ready = self
                    .boot_stages
                    .values()
                    .any(|s| s.id == "ready" && s.completed);
                self.boot_error = None;
            }
            Response::Metrics(sample) => {
                self.dashboard.metrics_pending = false;
                let Some(timestamp) = sample.timestamp_utc else {
                    self.dashboard.metrics_error = Some("missing metrics timestamp".into());
                    return;
                };
                let at = timestamp
                    .seconds
                    .saturating_mul(1000)
                    .saturating_add(i64::from(timestamp.nanos) / 1_000_000);
                for delta in sample.deltas {
                    let Ok(addr) = u16::try_from(delta.addr) else {
                        continue;
                    };
                    if !(addr < 4 || crate::dashboard::valid_addr(addr)) {
                        continue;
                    }
                    let (value, kind) = match delta.value {
                        Some(register_delta::Value::Int32Value(v)) => (v.to_string(), "int32"),
                        Some(register_delta::Value::FloatValue(v)) => {
                            let value = if v.is_nan() {
                                "NaN".into()
                            } else if v == f32::INFINITY {
                                "Infinity".into()
                            } else if v == f32::NEG_INFINITY {
                                "-Infinity".into()
                            } else {
                                let number = v.to_string().parse::<f64>().unwrap_or(f64::from(v));
                                crate::dashboard::format_number(number)
                            };
                            (value, "float")
                        }
                        None => continue,
                    };
                    self.dashboard.metrics.insert(
                        addr,
                        crate::dashboard::Metric {
                            value,
                            kind: kind.into(),
                            at,
                        },
                    );
                }
                self.dashboard.sample_count = self.dashboard.sample_count.saturating_add(1);
                self.dashboard.last_sample = at;
                self.dashboard.heartbeat = i64::from(sample.heartbeat);
                self.dashboard.interval_clamped = sample.interval_clamped;
                self.dashboard.metrics_error = None;
            }
            Response::Dashboard(data) => self.dashboard.apply(Ok(*data)),
            Response::Logs(value) => self.host_logs.apply(value),
            Response::LocalLogs(value) => self.local_logs.apply(value),
        }
    }
}

/// Scalar display facts derived from the acknowledged typed Host snapshot.
/// A transport fault keeps last-known text; Model.connected separately reports freshness.
pub struct StatusView {
    pub bus_open: bool,
    pub addin_alive: bool,
    pub zones: [ZoneView; 3],
    pub modbus: String,
    pub web: String,
    pub sniffer: String,
    pub opc_da: String,
    pub last_error: String,
}
pub struct ZoneView {
    pub active: bool,
    pub age_ms: String,
}
impl Model {
    pub fn host_display(&self) -> StatusView {
        let status = self.host.as_ref();
        let layers = status.map(|s| &s.layers);
        let flag = |value: Option<bool>| value.map_or_else(|| "—".into(), |v| v.to_string());
        StatusView {
            bus_open: status.is_some_and(|s| s.bus_open),
            addin_alive: status.is_some_and(|s| s.addin_alive),
            zones: std::array::from_fn(|i| ZoneView {
                active: status.is_some_and(|s| s.zones[i].active),
                age_ms: status.map_or_else(|| "—".into(), |s| s.zones[i].age_ms.to_string()),
            }),
            modbus: flag(layers.map(|s| s.modbus)),
            web: flag(layers.map(|s| s.web)),
            sniffer: flag(layers.map(|s| s.sniffer)),
            opc_da: flag(layers.map(|s| s.opc_da)),
            last_error: layers.map_or_else(|| "—".into(), |s| s.last_error.clone()),
        }
    }
}
