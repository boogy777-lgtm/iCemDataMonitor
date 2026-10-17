//! Native dashboard state. Wire projections stay owned by the Host REST API.
use serde_json::{Value, json};
use std::collections::{BTreeMap, BTreeSet};

pub const HISTORY_CAPACITY: usize = 600;
pub const PALETTE: [u32; 10] = [
    0xcc3300, 0x339933, 0x006699, 0xff9933, 0x634b9f, 0xffcc05, 0xd86c27, 0x009999, 0x660066,
    0xa1a744,
];
#[derive(Clone, Debug, PartialEq)]
pub struct RegisterRow {
    pub addr: u16,
    pub end: u16,
    pub name: String,
    pub value: String,
    pub label: String,
}
#[derive(Clone, Debug, PartialEq)]
pub struct Sample {
    pub at: i64,
    pub value: f64,
}
#[derive(Clone, Debug)]
pub struct Metric {
    pub value: String,
    pub kind: String,
    pub at: i64,
}
#[derive(Default)]
pub struct Dashboard {
    pub registers: Value,
    pub metrics: BTreeMap<u16, Metric>,
    pub metrics_pending: bool,
    pub metrics_error: Option<String>,
    pub sample_count: u64,
    pub last_sample: i64,
    pub heartbeat: i64,
    pub interval_clamped: bool,
    pub selected: BTreeSet<u16>,
    pub history: BTreeMap<u16, Vec<Sample>>,
    pub collapsed: BTreeSet<String>,
    pub error: Option<String>,
    pub history_error: Option<String>,
    pub loaded: bool,
    pub pending: bool,
}
impl Dashboard {
    pub fn apply_metrics(&mut self, response: Result<Value, String>) {
        self.metrics_pending = false;
        let result = response.and_then(|v| {
            if v["ok"] == true {
                Ok(v["data"].clone())
            } else {
                Err(crate::text(&v["error"]))
            }
        });
        let data = match result {
            Ok(v) => v,
            Err(error) => {
                self.metrics_error = Some(error);
                return;
            }
        };
        let Some(deltas) = data["deltas"].as_array() else {
            self.metrics_error = Some("invalid metrics sample".into());
            return;
        };
        let at = data["at"].as_i64().unwrap_or_default();
        for delta in deltas {
            if let Some(addr) = delta["addr"].as_u64().and_then(|a| u16::try_from(a).ok())
                && (addr < 4 || valid_addr(addr))
            {
                self.metrics.insert(
                    addr,
                    Metric {
                        value: if delta["kind"] == "int32" {
                            crate::text(&delta["value"])
                        } else if let Some(value) = delta["value"]
                            .as_str()
                            .and_then(|s| s.parse::<f64>().ok())
                            .filter(|v| v.is_finite())
                        {
                            format_value(&json!(value))
                        } else {
                            format_value(&delta["value"])
                        },
                        kind: crate::text(&delta["kind"]),
                        at,
                    },
                );
            }
        }
        self.sample_count = self.sample_count.saturating_add(1);
        self.last_sample = at;
        self.heartbeat = data["heartbeat"].as_i64().unwrap_or_default();
        self.interval_clamped = data["clamped"] == true;
        self.metrics_error = None;
    }
    pub fn toggle(&mut self, addr: u16) {
        if valid_addr(addr) && !self.selected.remove(&addr) {
            self.selected.insert(addr);
        }
        self.history.retain(|addr, _| self.selected.contains(addr));
    }
    pub fn clear(&mut self) {
        self.selected.clear();
        self.history.clear();
        self.history_error = None;
    }
    pub fn request(&self) -> Value {
        json!({"cmd":"dashboard", "selected":self.selected})
    }
    pub fn apply(&mut self, response: Result<Value, String>) {
        self.pending = false;
        let result = response.and_then(|v| {
            if v["ok"] == true {
                Ok(v["data"].clone())
            } else {
                Err(crate::text(&v["error"]))
            }
        });
        let data = match result {
            Ok(data) => data,
            Err(error) => {
                self.error = Some(error);
                return;
            }
        };
        if !data["registers"]["system"]["scalars"].is_array() {
            self.error = Some("неверный ответ регистров".into());
            return;
        }
        self.registers = data["registers"].clone();
        self.loaded = true;
        self.error = None;
        if let Some(error) = data["historyError"].as_str() {
            self.history_error = Some(error.to_owned());
            return;
        }
        let mut history = BTreeMap::new();
        for &addr in &self.selected {
            let Some(items) = data["history"][addr.to_string()].as_array() else {
                continue;
            };
            let mut samples = Vec::new();
            for item in items
                .iter()
                .skip(items.len().saturating_sub(HISTORY_CAPACITY))
            {
                let sample = item["ts"]
                    .as_str()
                    .and_then(|ts| chrono::DateTime::parse_from_rfc3339(ts).ok())
                    .zip(item["v"].as_f64().filter(|v| v.is_finite()));
                let Some((time, value)) = sample else {
                    self.history_error = Some(format!("неверная точка истории addr {addr}"));
                    return;
                };
                samples.push(Sample {
                    at: time.timestamp_millis(),
                    value,
                });
            }
            history.insert(addr, samples);
        }
        self.history = history;
        self.history_error = None;
    }
    pub fn rows(&self, zone: &str) -> Vec<RegisterRow> {
        let source = if zone == "system" {
            &self.registers["system"]["scalars"]
        } else {
            &self.registers["zones"][zone]["pairs"]
        };
        source
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|p| {
                let addr =
                    u16::try_from(p[if zone == "system" { "reg" } else { "addr" }].as_u64()?)
                        .ok()?;
                Some(RegisterRow {
                    addr,
                    end: p["addrEnd"]
                        .as_u64()
                        .and_then(|n| u16::try_from(n).ok())
                        .unwrap_or(addr),
                    name: crate::text(&p["name"]),
                    value: if zone == "system" {
                        crate::text(&p["value"])
                    } else {
                        format_value(&p["value"])
                    },
                    label: p["label"].as_str().unwrap_or("").into(),
                })
            })
            .collect()
    }
    pub fn selected_rows(&self) -> Vec<RegisterRow> {
        ["com", "lan", "raw"]
            .into_iter()
            .flat_map(|z| self.rows(z))
            .filter(|r| self.selected.contains(&r.addr))
            .collect()
    }
    pub fn chart(&self) -> Option<Chart> {
        let mut samples = self
            .history
            .iter()
            .filter(|(addr, _)| self.selected.contains(addr))
            .flat_map(|(_, s)| s);
        let first = samples.next()?;
        let mut chart = Chart {
            t_min: first.at,
            t_max: first.at,
            v_min: first.value,
            v_max: first.value,
        };
        for s in samples {
            chart.t_min = chart.t_min.min(s.at);
            chart.t_max = chart.t_max.max(s.at);
            chart.v_min = chart.v_min.min(s.value);
            chart.v_max = chart.v_max.max(s.value);
        }
        if chart.v_min == chart.v_max {
            chart.v_min -= 1.;
            chart.v_max += 1.;
        }
        Some(chart)
    }
    pub fn restore_selection(&mut self, bytes: &[u8]) -> Result<(), String> {
        if bytes.len() > 4096 {
            return Err("selection file exceeds 4096 bytes".into());
        }
        let values: Vec<u16> = serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
        if values.iter().any(|&a| !valid_addr(a)) {
            return Err("invalid dashboard register address".into());
        }
        self.selected = values.into_iter().collect();
        Ok(())
    }
    pub fn selection_bytes(&self) -> Result<Vec<u8>, serde_json::Error> {
        serde_json::to_vec(&self.selected)
    }
}
pub fn valid_addr(addr: u16) -> bool {
    (4..600).contains(&addr) && addr.is_multiple_of(2)
}
pub fn format_value(value: &Value) -> String {
    if let Some(n) = value.as_f64() {
        if n.abs() >= 1000. {
            format!("{n:.1}")
        } else {
            format!("{n:.4}")
                .trim_end_matches('0')
                .trim_end_matches('.')
                .into()
        }
    } else {
        crate::text(value)
    }
}
#[derive(Clone, Copy, Debug)]
pub struct Chart {
    pub t_min: i64,
    pub t_max: i64,
    pub v_min: f64,
    pub v_max: f64,
}
impl Chart {
    pub fn point(&self, sample: &Sample) -> (f32, f32) {
        // Scale first to avoid overflow for opposing large, finite values.
        let scale = self.v_min.abs().max(self.v_max.abs()).max(1.);
        let min = self.v_min / scale;
        let max = self.v_max / scale;
        let y = if max > min {
            1. - (sample.value / scale - min) / (max - min)
        } else {
            0.5
        };
        (
            ((sample.at as f64 - self.t_min as f64)
                / (self.t_max as f64 - self.t_min as f64).max(1.)) as f32,
            y as f32,
        )
    }
}
