//! Separate startup-only configuration. Frozen station INI is never extended.
use serde::Deserialize;
use std::{io::Read, net::SocketAddr, path::Path, time::Duration};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Input {
    broker: SocketAddr,
    client_id: String,
    topic: String,
    qos: u8,
    retain: bool,
    publish_interval_ms: u64,
    reconnect_delay_ms: u64,
}
#[derive(Clone)]
pub struct Config {
    pub(super) broker: SocketAddr,
    pub(super) client_id: String,
    pub(super) topic: String,
    pub(super) qos: rumqttc::QoS,
    pub(super) retain: bool,
    pub(super) interval: Duration,
    pub(super) reconnect: Duration,
}
impl Config {
    pub fn load(path: &Path) -> Result<Self, String> {
        let mut bytes = Vec::new();
        std::fs::File::open(path)
            .map_err(|e| format!("MQTT config: {e}"))?
            .take(16_385)
            .read_to_end(&mut bytes)
            .map_err(|e| format!("MQTT config: {e}"))?;
        if bytes.len() > 16_384 {
            return Err("MQTT config exceeds 16 KiB".into());
        }
        Self::parse(&bytes)
    }
    pub(super) fn parse(bytes: &[u8]) -> Result<Self, String> {
        let input: Input =
            serde_json::from_slice(bytes).map_err(|e| format!("MQTT config: {e}"))?;
        if input.broker.port() == 0
            || input.broker.ip().is_unspecified()
            || input.broker.ip().is_multicast()
            || matches!(input.broker.ip(), std::net::IpAddr::V4(ip) if ip.is_broadcast())
        {
            return Err("MQTT broker requires a unicast IP and nonzero port".into());
        }
        if input.client_id.is_empty()
            || input.client_id.len() > 128
            || input.client_id.chars().any(char::is_control)
        {
            return Err("MQTT client_id must be 1..128 UTF-8 bytes without controls".into());
        }
        if input.topic.is_empty()
            || input.topic.len() > 1024
            || input
                .topic
                .chars()
                .any(|c| c.is_control() || c == '+' || c == '#')
        {
            return Err("MQTT topic must be 1..1024 bytes without controls or wildcards".into());
        }
        let qos = match input.qos {
            0 => rumqttc::QoS::AtMostOnce,
            1 => rumqttc::QoS::AtLeastOnce,
            2 => rumqttc::QoS::ExactlyOnce,
            _ => return Err("MQTT qos must be 0, 1 or 2".into()),
        };
        if !(50..=60_000).contains(&input.publish_interval_ms)
            || !(50..=60_000).contains(&input.reconnect_delay_ms)
        {
            return Err("MQTT intervals must be 50..60000 ms".into());
        }
        Ok(Self {
            broker: input.broker,
            client_id: input.client_id,
            topic: input.topic,
            qos,
            retain: input.retain,
            interval: Duration::from_millis(input.publish_interval_ms),
            reconnect: Duration::from_millis(input.reconnect_delay_ms),
        })
    }
}
#[cfg(test)]
#[allow(clippy::unwrap_used)]
mod tests {
    use super::*;
    pub(super) const GOOD: &str = r#"{"broker":"127.0.0.1:1883","client_id":"icem-test","topic":"icem/state","qos":1,"retain":true,"publish_interval_ms":50,"reconnect_delay_ms":50}"#;
    #[test]
    fn strict_typed_startup_config() {
        assert!(Config::parse(GOOD.as_bytes()).is_ok());
        for (key, bad) in [
            ("broker", serde_json::json!("0.0.0.0:1883")),
            ("broker", serde_json::json!("localhost:1883")),
            ("client_id", serde_json::json!("")),
            ("topic", serde_json::json!("x/#")),
            ("qos", serde_json::json!(3)),
            ("publish_interval_ms", serde_json::json!(0)),
            ("reconnect_delay_ms", serde_json::json!(60001)),
            ("retain", serde_json::Value::Null),
        ] {
            let mut input: serde_json::Value = serde_json::from_str(GOOD).unwrap();
            input[key] = bad;
            assert!(
                Config::parse(&serde_json::to_vec(&input).unwrap()).is_err(),
                "{key}"
            );
        }
        let extra = GOOD.replace("{", "{\"unknown\":true,");
        assert!(Config::parse(extra.as_bytes()).is_err());
        let duplicate = GOOD.replace("{", "{\"qos\":0,");
        assert!(Config::parse(duplicate.as_bytes()).is_err());
    }
}
