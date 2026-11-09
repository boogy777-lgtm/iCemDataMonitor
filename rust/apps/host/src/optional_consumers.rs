//! Host-owned runtime control; the frozen SetLayer contract is unrelated.
use crate::{
    consumers::{ConsumerId, Lifecycle, Registry, Status},
    mqtt_consumer, opc_da_consumer, opcua_consumer,
};
use icem_core::publication::SnapshotReader;
pub use icem_web::protocol_control::Protocol;
use icem_web::protocol_control::{Command, MAX_ERROR_BYTES, Reply};
use std::time::{Duration, Instant};

fn id(protocol: Protocol) -> ConsumerId {
    match protocol {
        Protocol::Mqtt => ConsumerId("mqtt"),
        Protocol::OpcUa => ConsumerId("opcua"),
        Protocol::OpcDa => ConsumerId("opc-da"),
    }
}
fn config_flag(protocol: Protocol) -> &'static str {
    match protocol {
        Protocol::Mqtt => "--mqtt-config",
        Protocol::OpcUa => "--opcua-config",
        Protocol::OpcDa => "--opc-da-config",
    }
}

fn bounded_error(error: String) -> String {
    const SUFFIX: &str = " [truncated]";
    if error.len() <= MAX_ERROR_BYTES {
        return error;
    }
    let end = error.floor_char_boundary(MAX_ERROR_BYTES - SUFFIX.len());
    format!("{}{SUFFIX}", &error[..end])
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{TcpListener, TcpStream};

    #[cfg(windows)]
    #[test]
    #[ignore = "requires separately built release sidecar and available COM class"]
    fn real_da_sidecar_owned_start_and_stop() -> Result<(), Box<dyn std::error::Error>> {
        let executable = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/release/icem-opc-da-sidecar.exe");
        if !executable.is_file() {
            return Err("release sidecar binary is required for this test".into());
        }
        let path =
            std::env::temp_dir().join(format!("icem-optional-da-{}.json", std::process::id()));
        let config_json = serde_json::json!({"sidecar": executable, "update_interval_ms": 50, "watchdog_ms": 5000});
        std::fs::write(&path, serde_json::to_vec(&config_json)?)?;
        let config = opc_da_consumer::Config::load(&path)?;
        std::fs::remove_file(path)?;
        let (_, reader) = icem_core::publication::latest_snapshot();
        let mut optional = OptionalConsumers::new(None, None, Some(config), reader);
        let mut registry = Registry::new();
        let protocol = icem_web::protocol_control::Protocol::OpcDa;
        for _ in 0..2 {
            let start = optional.request(
                &mut registry,
                Command::Set {
                    protocol,
                    enabled: true,
                },
            );
            assert!(
                start.ok && start.opcda.enabled && start.opcda.running,
                "{start:?}"
            );
            let stop = optional.request(
                &mut registry,
                Command::Set {
                    protocol,
                    enabled: false,
                },
            );
            assert!(
                stop.ok && !stop.opcda.enabled && !stop.opcda.running,
                "{stop:?}"
            );
            assert!(registry.status(ConsumerId("opc-da")).is_none());
        }
        Ok(())
    }

    #[test]
    fn actual_opcua_owner_starts_stops_and_absent_da_is_rejected()
    -> Result<(), Box<dyn std::error::Error>> {
        let listener = TcpListener::bind("127.0.0.1:0")?;
        let address = listener.local_addr()?;
        let path =
            std::env::temp_dir().join(format!("icem-optional-ua-{}.json", std::process::id()));
        std::fs::write(
            &path,
            format!(
                r#"{{"bind":"{address}","profile":"loopback-anonymous-none","update_interval_ms":200}}"#
            ),
        )?;
        let config = opcua_consumer::Config::load(&path)?;
        std::fs::remove_file(path)?;
        let (_, reader) = icem_core::publication::latest_snapshot();
        let mut optional = OptionalConsumers::new(None, Some(config), None, reader);
        let mut registry = Registry::new();
        assert!(
            optional
                .set_enabled(&mut registry, Protocol::OpcDa, true)
                .is_err()
        );
        let missing = optional.state(&registry, Protocol::OpcDa);
        assert!(!missing.configured && !missing.running);
        assert!(
            missing
                .error
                .ok_or("missing explanation")?
                .contains("--opc-da-config")
        );
        let command = Command::Set {
            protocol: Protocol::OpcUa,
            enabled: true,
        };
        let failure = optional.request(&mut registry, command);
        assert!(!failure.ok && !failure.opcua.enabled && !failure.opcua.error.is_empty());
        drop(listener);
        let observed = optional.request(&mut registry, Command::Get);
        assert!(!observed.opcua.enabled && !observed.opcua.error.is_empty());
        for _ in 0..2 {
            optional.set_enabled(&mut registry, Protocol::OpcUa, true)?;
            assert!(optional.state(&registry, Protocol::OpcUa).running);
            assert!(TcpStream::connect(address).is_ok());
            optional.set_enabled(&mut registry, Protocol::OpcUa, false)?;
            assert!(!optional.state(&registry, Protocol::OpcUa).enabled);
            drop(TcpListener::bind(address)?);
        }
        Ok(())
    }

    #[test]
    fn unicode_diagnostic_is_bounded_without_changing_owner_state()
    -> Result<(), Box<dyn std::error::Error>> {
        let (_, reader) = icem_core::publication::latest_snapshot();
        let mut optional = OptionalConsumers::new(None, None, None, reader);
        optional.errors[0] = Some("ошибка".repeat(200));
        let mut registry = Registry::new();
        // Provide a configured slot so the stored diagnostic is projected.
        let path =
            std::env::temp_dir().join(format!("icem-optional-mqtt-{}.json", std::process::id()));
        std::fs::write(&path, br#"{"broker":"127.0.0.1:1883","client_id":"test","topic":"x","qos":0,"retain":false,"publish_interval_ms":50,"reconnect_delay_ms":50}"#)?;
        optional.mqtt = Some(mqtt_consumer::Config::load(&path)?);
        std::fs::remove_file(path)?;
        let reply = optional.request(&mut registry, Command::Get);
        assert!(reply.mqtt.configured && !reply.mqtt.enabled && !reply.mqtt.running);
        assert!(
            reply.mqtt.error.len() <= MAX_ERROR_BYTES && reply.mqtt.error.ends_with(" [truncated]")
        );
        assert!(
            optional.errors[0]
                .as_ref()
                .is_some_and(|e| e.len() > MAX_ERROR_BYTES)
        );
        assert!(reply.validate(Command::Get).is_ok());
        Ok(())
    }
}

#[derive(Debug, Clone)]
pub struct State {
    pub configured: bool,
    pub enabled: bool,
    pub running: bool,
    pub error: Option<String>,
}

pub struct OptionalConsumers {
    mqtt: Option<mqtt_consumer::Config>,
    opcua: Option<opcua_consumer::Config>,
    opc_da: Option<opc_da_consumer::Config>,
    reader: SnapshotReader,
    errors: [Option<String>; 3],
}
impl OptionalConsumers {
    pub fn new(
        mqtt: Option<mqtt_consumer::Config>,
        opcua: Option<opcua_consumer::Config>,
        opc_da: Option<opc_da_consumer::Config>,
        reader: SnapshotReader,
    ) -> Self {
        Self {
            mqtt,
            opcua,
            opc_da,
            reader,
            errors: [None, None, None],
        }
    }
    fn index(protocol: Protocol) -> usize {
        match protocol {
            Protocol::Mqtt => 0,
            Protocol::OpcUa => 1,
            Protocol::OpcDa => 2,
        }
    }
    fn configured(&self, protocol: Protocol) -> bool {
        match protocol {
            Protocol::Mqtt => self.mqtt.is_some(),
            Protocol::OpcUa => self.opcua.is_some(),
            Protocol::OpcDa => self.opc_da.is_some(),
        }
    }
    pub fn state(&self, registry: &Registry, protocol: Protocol) -> State {
        let configured = self.configured(protocol);
        let status = registry.status(id(protocol));
        let error = if !configured {
            Some(format!("Not configured: supply {}", config_flag(protocol)))
        } else {
            self.errors[Self::index(protocol)].clone().or_else(|| {
                (status == Some(Status::Faulted))
                    .then(|| "Runtime is not running or is disconnected".into())
            })
        };
        State {
            configured,
            enabled: status.is_some(),
            running: status == Some(Status::Running),
            error,
        }
    }
    pub fn set_enabled(
        &mut self,
        registry: &mut Registry,
        protocol: Protocol,
        enabled: bool,
    ) -> Result<(), String> {
        let index = Self::index(protocol);
        let result = self.apply(registry, protocol, enabled);
        self.errors[index] = result.as_ref().err().cloned();
        result
    }
    fn apply(
        &self,
        registry: &mut Registry,
        protocol: Protocol,
        enabled: bool,
    ) -> Result<(), String> {
        if !enabled {
            return registry.stop_one(id(protocol), Instant::now() + Duration::from_secs(2));
        }
        if registry.status(id(protocol)).is_some() {
            return match registry.status(id(protocol)) {
                Some(Status::Running) => Ok(()),
                _ => Err(
                    "Runtime already owned but not running; stop it before starting again".into(),
                ),
            };
        }
        let reader = self.reader.clone();
        let owner: Box<dyn Lifecycle> = match protocol {
            Protocol::Mqtt => Box::new(mqtt_consumer::start(
                self.mqtt
                    .clone()
                    .ok_or_else(|| format!("Not configured: supply {}", config_flag(protocol)))?,
                reader,
            )?),
            Protocol::OpcUa => Box::new(opcua_consumer::start(
                self.opcua
                    .clone()
                    .ok_or_else(|| format!("Not configured: supply {}", config_flag(protocol)))?,
                reader,
            )?),
            Protocol::OpcDa => Box::new(opc_da_consumer::start(
                self.opc_da
                    .clone()
                    .ok_or_else(|| format!("Not configured: supply {}", config_flag(protocol)))?,
                reader,
            )?),
        };
        registry
            .register(id(protocol), owner)
            .map_err(|_| "Consumer registry duplicate or full".to_owned())
    }
    pub fn request(&mut self, registry: &mut Registry, command: Command) -> Reply {
        let operation = match command {
            Command::Get => true,
            Command::Set { protocol, enabled } => {
                let result = self.set_enabled(registry, protocol, enabled);
                let state = self.state(registry, protocol);
                result.is_ok()
                    && state.configured
                    && state.enabled == enabled
                    && (!enabled || state.running)
            }
        };
        let convert = |state: State| icem_web::protocol_control::Status {
            configured: state.configured,
            enabled: state.enabled,
            running: state.running,
            error: bounded_error(state.error.unwrap_or_default()),
        };
        Reply {
            ok: operation,
            mqtt: convert(self.state(registry, Protocol::Mqtt)),
            opcua: convert(self.state(registry, Protocol::OpcUa)),
            opcda: convert(self.state(registry, Protocol::OpcDa)),
        }
    }
}
