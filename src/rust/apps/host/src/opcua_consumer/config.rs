use serde::Deserialize;
use std::{io::Read, net::SocketAddr, path::Path, time::Duration};
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Input {
    bind: SocketAddr,
    profile: String,
    update_interval_ms: u64,
}
#[derive(Clone)]
pub struct Config {
    pub(super) bind: SocketAddr,
    pub(super) interval: Duration,
}
impl Config {
    pub fn load(path: &Path) -> Result<Self, String> {
        let mut bytes = Vec::new();
        std::fs::File::open(path)
            .map_err(|e| format!("OPC UA config: {e}"))?
            .take(16385)
            .read_to_end(&mut bytes)
            .map_err(|e| format!("OPC UA config: {e}"))?;
        if bytes.len() > 16384 {
            return Err("OPC UA config exceeds 16 KiB".into());
        }
        Self::parse(&bytes)
    }
    pub(super) fn parse(bytes: &[u8]) -> Result<Self, String> {
        let input: Input =
            serde_json::from_slice(bytes).map_err(|e| format!("OPC UA config: {e}"))?;
        if !input.bind.ip().is_loopback()
            || input.bind.port() == 0
            || input.profile != "loopback-anonymous-none"
            || !(50..=60000).contains(&input.update_interval_ms)
        {
            return Err("OPC UA requires loopback/nonzero port, explicit loopback-anonymous-none profile, interval 50..60000 ms".into());
        }
        Ok(Self {
            bind: input.bind,
            interval: Duration::from_millis(input.update_interval_ms),
        })
    }
}
