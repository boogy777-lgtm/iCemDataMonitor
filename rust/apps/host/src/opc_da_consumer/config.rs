use serde::Deserialize;
use std::{
    io::Read,
    path::{Path, PathBuf},
    time::Duration,
};
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Input {
    sidecar: PathBuf,
    update_interval_ms: u64,
    watchdog_ms: u64,
}
pub struct Config {
    pub(super) sidecar: PathBuf,
    pub(super) interval: Duration,
    pub(super) watchdog: Duration,
}
impl Config {
    pub fn load(path: &Path) -> Result<Self, String> {
        let mut bytes = Vec::new();
        std::fs::File::open(path)
            .map_err(|e| e.to_string())?
            .take(16385)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        if bytes.len() > 16384 {
            return Err("OPC DA config exceeds16KiB".into());
        }
        Self::parse(&bytes, path.parent().unwrap_or(Path::new(".")))
    }
    fn parse(bytes: &[u8], base: &Path) -> Result<Self, String> {
        let input: Input =
            serde_json::from_slice(bytes).map_err(|e| format!("OPC DA config: {e}"))?;
        if !(50..=60000).contains(&input.update_interval_ms)
            || !(200..=5000).contains(&input.watchdog_ms)
            || input.sidecar.as_os_str().is_empty()
        {
            return Err(
                "OPC DA requires sidecar path, interval50..60000ms, watchdog200..5000ms".into(),
            );
        }
        Ok(Self {
            sidecar: base.join(input.sidecar),
            interval: Duration::from_millis(input.update_interval_ms),
            watchdog: Duration::from_millis(input.watchdog_ms),
        })
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn strict_configuration() {
        let valid = br#"{"sidecar":"sidecar.exe","update_interval_ms":200,"watchdog_ms":1000}"#;
        assert!(Config::parse(valid, Path::new(".")).is_ok());
        for invalid in [
            br#"{}"#.as_slice(),
            br#"{"sidecar":"","update_interval_ms":200,"watchdog_ms":1000}"#,
            br#"{"sidecar":"x","update_interval_ms":200,"watchdog_ms":0}"#,
            br#"{"sidecar":"x","update_interval_ms":200,"watchdog_ms":1000,"extra":1}"#,
        ] {
            assert!(Config::parse(invalid, Path::new(".")).is_err());
        }
    }
}
