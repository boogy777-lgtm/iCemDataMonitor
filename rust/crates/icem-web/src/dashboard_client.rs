//! Read-only native dashboard adapter for the existing local REST projections.
//! No browser, redirects or remote endpoint discovery. One bounded response at a time.
use http_body_util::{BodyExt, Empty, Limited};
use hyper::{Request, body::Bytes};
use hyper_util::rt::TokioIo;
use serde_json::Value;
use std::{net::SocketAddr, time::Duration};
const MAX_RESPONSE: usize = 16 * 1024 * 1024;
async fn get(endpoint: SocketAddr, path: &str) -> Result<Bytes, String> {
    if !endpoint.ip().is_loopback() {
        return Err("dashboard endpoint must be loopback".into());
    }
    tokio::time::timeout(Duration::from_secs(3), async {
        let stream = tokio::net::TcpStream::connect(endpoint)
            .await
            .map_err(|e| e.to_string())?;
        let (mut sender, connection) = hyper::client::conn::http1::handshake(TokioIo::new(stream))
            .await
            .map_err(|e| e.to_string())?;
        let task = tokio::spawn(connection);
        let response = async {
            let request = Request::builder()
                .uri(path)
                .header("Host", endpoint.to_string())
                .header("Connection", "close")
                .body(Empty::<Bytes>::new())
                .map_err(|e| e.to_string())?;
            let response = sender
                .send_request(request)
                .await
                .map_err(|e| e.to_string())?;
            let status = response.status();
            let bytes = Limited::new(response.into_body(), MAX_RESPONSE)
                .collect()
                .await
                .map_err(|e| e.to_string())?
                .to_bytes();
            if !status.is_success() {
                let value: Value =
                    serde_json::from_slice(&bytes).map_err(|e| format!("HTTP {status}: {e}"))?;
                return Err(format!(
                    "HTTP {status}: {}",
                    value["error"]
                        .as_str()
                        .unwrap_or("dashboard request failed")
                ));
            }
            Ok(bytes)
        }
        .await;
        task.abort();
        response
    })
    .await
    .map_err(|_| "dashboard request timed out".to_owned())?
}
pub async fn read(
    endpoint: SocketAddr,
    selected: &crate::shell::Selection,
) -> Result<Read, String> {
    let registers = Registers::decode(&get(endpoint, "/api/regs").await?)?;
    if selected.addresses().is_empty() {
        return Ok(Read {
            registers,
            history: Ok(History::default()),
        });
    }
    let query = selected
        .addresses()
        .iter()
        .map(|a| format!("addr={a}"))
        .collect::<Vec<_>>()
        .join("&");
    let history = match get(endpoint, &format!("/api/regs/history?{query}")).await {
        Ok(bytes) => History::decode(&bytes, selected),
        Err(error) => Err(error),
    };
    Ok(Read { registers, history })
}
use chrono::{DateTime, Utc};
pub use icem_bus::Zone;
use serde::{Deserialize, Deserializer};
use std::collections::BTreeMap;

pub const HISTORY_CAPACITY: usize = 600;
/// Base address of a live register pair. System words are deliberately separate.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Address(u16);
impl Address {
    pub fn new(value: u16) -> Result<Self, &'static str> {
        if (4..600).contains(&value) && value.is_multiple_of(2) {
            Ok(Self(value))
        } else {
            Err("invalid dashboard register address")
        }
    }
    pub fn get(self) -> u16 {
        self.0
    }
}
#[derive(Clone, Debug, PartialEq)]
pub enum PairValue {
    Float(f64),
    /// Original checked UTC text, preserving the REST display format.
    UnixUtc(String),
}
#[derive(Clone, Debug)]
pub struct Scalar {
    reg: u16,
    name: String,
    value: u16,
    label: Option<String>,
}
impl Scalar {
    pub fn reg(&self) -> u16 {
        self.reg
    }
    pub fn name(&self) -> &str {
        &self.name
    }
    pub fn value(&self) -> u16 {
        self.value
    }
    pub fn label(&self) -> Option<&str> {
        self.label.as_deref()
    }
}
#[derive(Clone, Debug)]
pub struct Pair {
    address: Address,
    name: Option<String>,
    value: PairValue,
}
impl Pair {
    pub fn address(&self) -> Address {
        self.address
    }
    pub fn name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    pub fn value(&self) -> &PairValue {
        &self.value
    }
}
#[derive(Clone, Debug)]
pub struct Liveness {
    pub active: bool,
    pub producer_flag: bool,
    pub age_ms: i64,
    pub write_count: u32,
}
#[derive(Clone, Debug)]
pub struct RegisterZone {
    pairs: Vec<Pair>,
    liveness: Liveness,
}
impl RegisterZone {
    pub fn pairs(&self) -> &[Pair] {
        &self.pairs
    }
    pub fn liveness(&self) -> &Liveness {
        &self.liveness
    }
}
#[derive(Clone, Debug)]
pub enum Freshness {
    Fresh(String),
    LastGood,
}
#[derive(Clone, Debug)]
pub struct Registers {
    scalars: [Scalar; 4],
    zones: [RegisterZone; 3],
    freshness: Freshness,
}
impl Registers {
    pub fn scalars(&self) -> &[Scalar; 4] {
        &self.scalars
    }
    pub fn zone(&self, zone: Zone) -> &RegisterZone {
        &self.zones[match zone {
            Zone::Com => 0,
            Zone::Lan => 1,
            Zone::Raw => 2,
        }]
    }
    pub fn freshness(&self) -> &Freshness {
        &self.freshness
    }
    pub fn stale(&self) -> bool {
        matches!(self.freshness, Freshness::LastGood)
    }
    pub fn decode(bytes: &[u8]) -> Result<Self, String> {
        if bytes.len() > MAX_RESPONSE {
            return Err("dashboard response exceeds limit".into());
        }
        let wire: WireRegisters = serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
        wire.try_into()
    }
}
#[derive(Clone, Debug, PartialEq)]
pub struct Sample {
    pub at: i64,
    pub value: f64,
}
#[derive(Clone, Debug, Default)]
pub struct History(BTreeMap<Address, Vec<Sample>>);
impl History {
    pub fn series(&self) -> impl Iterator<Item = (Address, &[Sample])> {
        self.0
            .iter()
            .map(|(&address, samples)| (address, samples.as_slice()))
    }
    pub fn decode(bytes: &[u8], selected: &crate::shell::Selection) -> Result<Self, String> {
        if bytes.len() > MAX_RESPONSE {
            return Err("dashboard response exceeds limit".into());
        }
        let wire: BTreeMap<String, Vec<WireSample>> =
            serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
        let mut result = BTreeMap::new();
        for (key, samples) in wire {
            let address = key.parse::<u16>().map_err(|_| "invalid history address")?;
            let address = Address::new(address)?;
            if key != address.get().to_string() || !selected.addresses().contains(&address.get()) {
                return Err("unexpected history address".into());
            }
            let mut points = Vec::with_capacity(samples.len().min(HISTORY_CAPACITY));
            let skip = samples.len().saturating_sub(HISTORY_CAPACITY);
            for (index, sample) in samples.into_iter().enumerate() {
                let at = utc_time(&sample.ts)?.timestamp_millis();
                if !sample.v.is_finite() {
                    return Err("non-finite history value".into());
                }
                if index >= skip {
                    points.push(Sample {
                        at,
                        value: sample.v,
                    });
                }
            }
            result.insert(address, points);
        }
        if selected.addresses().iter().any(|&address| {
            Address::new(address).is_ok_and(|address| !result.contains_key(&address))
        }) {
            return Err("missing selected history address".into());
        }
        Ok(Self(result))
    }
}
/// Register and history outcomes are independent after the register read succeeds.
#[derive(Clone, Debug)]
pub struct Read {
    pub registers: Registers,
    pub history: Result<History, String>,
}
fn required_nullable<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
}
fn utc_time(text: &str) -> Result<DateTime<Utc>, String> {
    let time = DateTime::parse_from_rfc3339(text).map_err(|e| e.to_string())?;
    if time.offset().local_minus_utc() != 0 {
        return Err("expected UTC timestamp".into());
    }
    Ok(time.with_timezone(&Utc))
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct WireRegisters {
    system: WireSystem,
    zones: Zones<WireZone>,
    liveness: Zones<WireLiveness>,
    #[serde(deserialize_with = "required_nullable")]
    updated_at_utc: Option<String>,
    stale: bool,
}
#[derive(Deserialize)]
struct WireSystem {
    scalars: [WireScalar; 4],
    #[serde(rename = "liveness")]
    _liveness: (),
}
#[derive(Deserialize)]
struct Zones<T> {
    com: T,
    lan: T,
    raw: T,
}
impl<T> Zones<T> {
    fn into_array(self) -> [T; 3] {
        [self.com, self.lan, self.raw]
    }
}
#[derive(Deserialize)]
struct WireScalar {
    reg: u16,
    name: String,
    value: u16,
    #[serde(deserialize_with = "required_nullable")]
    label: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct WireZone {
    label: String,
    base_reg: u16,
    len: u16,
    pairs: Vec<WirePair>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct WirePair {
    idx: usize,
    addr: u16,
    addr_end: u16,
    #[serde(deserialize_with = "required_nullable")]
    name: Option<String>,
    #[serde(flatten)]
    value: WirePairValue,
}
#[derive(Deserialize)]
#[serde(tag = "kind", content = "value", rename_all = "lowercase")]
enum WirePairValue {
    Float(f64),
    Unix(String),
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct WireLiveness {
    active: bool,
    producer_flag: bool,
    age_ms: i64,
    write_count: u32,
}
#[derive(Deserialize)]
struct WireSample {
    ts: String,
    v: f64,
}
impl TryFrom<WireRegisters> for Registers {
    type Error = String;
    fn try_from(wire: WireRegisters) -> Result<Self, String> {
        let freshness = match (wire.stale, wire.updated_at_utc) {
            (true, None) => Freshness::LastGood,
            (false, Some(text)) => {
                utc_time(&text)?;
                Freshness::Fresh(text)
            }
            _ => return Err("inconsistent register freshness".into()),
        };
        let mut scalars = Vec::with_capacity(4);
        for (reg, scalar) in wire.system.scalars.into_iter().enumerate() {
            if usize::from(scalar.reg) != reg {
                return Err("invalid system register identity".into());
            }
            scalars.push(Scalar {
                reg: scalar.reg,
                name: scalar.name,
                value: scalar.value,
                label: scalar.label,
            });
        }
        let mut zones = Vec::with_capacity(3);
        for ((zone, source), live) in Zone::ALL
            .into_iter()
            .zip(wire.zones.into_array())
            .zip(wire.liveness.into_array())
        {
            let (base, end) = zone.range();
            if usize::from(source.base_reg) != base
                || usize::from(source.len) != end - base
                || source.pairs.len() != (end - base) / 2
            {
                return Err("invalid register zone geometry".into());
            }
            // Labels/names are presentation data, not catalog identities.
            let _ = source.label;
            if live.age_ms < 0 || live.active != (live.producer_flag && live.age_ms < 5000) {
                return Err("inconsistent register liveness".into());
            }
            let mut pairs = Vec::with_capacity(source.pairs.len());
            for (idx, pair) in source.pairs.into_iter().enumerate() {
                let address = Address::new(pair.addr)?;
                if pair.idx != idx
                    || usize::from(pair.addr) != base + idx * 2
                    || pair.addr_end != pair.addr + 1
                {
                    return Err("invalid register pair identity".into());
                }
                let value = match (pair.addr, pair.value) {
                    (4, WirePairValue::Unix(text)) => {
                        let time = utc_time(&text)?;
                        if i32::try_from(time.timestamp()).is_err()
                            || time.timestamp_subsec_nanos() != 0
                            || time.format("%Y-%m-%dT%H:%M:%SZ").to_string() != text
                        {
                            return Err("invalid Unix register timestamp".into());
                        }
                        PairValue::UnixUtc(text)
                    }
                    (4, _) | (_, WirePairValue::Unix(_)) => {
                        return Err("invalid register value kind".into());
                    }
                    (_, WirePairValue::Float(value)) if value.is_finite() => {
                        PairValue::Float(value)
                    }
                    _ => return Err("non-finite register value".into()),
                };
                pairs.push(Pair {
                    address,
                    name: pair.name,
                    value,
                });
            }
            zones.push(RegisterZone {
                pairs,
                liveness: Liveness {
                    active: live.active,
                    producer_flag: live.producer_flag,
                    age_ms: live.age_ms,
                    write_count: live.write_count,
                },
            });
        }
        Ok(Self {
            scalars: scalars.try_into().map_err(|_| "invalid scalar count")?,
            zones: zones.try_into().map_err(|_| "invalid zone count")?,
            freshness,
        })
    }
}
