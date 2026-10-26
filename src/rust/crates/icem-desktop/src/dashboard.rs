//! Native dashboard state. Wire projections stay owned by the Host REST API.
use icem_web::dashboard_client::{Address, PairValue, Read, Registers, Zone};
pub use icem_web::dashboard_client::{HISTORY_CAPACITY, Sample};
use std::collections::{BTreeMap, BTreeSet};

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
#[derive(Clone, Debug)]
pub struct Metric {
    pub value: String,
    pub kind: String,
    pub at: i64,
}
#[derive(Default)]
pub struct Dashboard {
    pub registers: Option<Registers>,
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
    pub pending: bool,
}
impl Dashboard {
    pub fn loaded(&self) -> bool {
        self.registers.is_some()
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
    pub fn apply(&mut self, response: Result<Read, String>) {
        self.pending = false;
        let data = match response {
            Ok(data) => data,
            Err(error) => {
                self.error = Some(error);
                return;
            }
        };
        self.registers = Some(data.registers);
        self.error = None;
        match data.history {
            Ok(history) => {
                self.history = history
                    .series()
                    .filter(|(address, _)| self.selected.contains(&address.get()))
                    .map(|(address, samples)| (address.get(), samples.to_vec()))
                    .collect();
                self.history_error = None;
            }
            Err(error) => self.history_error = Some(error),
        }
    }
    pub fn rows(&self, zone: &str) -> Vec<RegisterRow> {
        let Some(registers) = &self.registers else {
            return Vec::new();
        };
        if zone == "system" {
            return registers
                .scalars()
                .iter()
                .map(|scalar| RegisterRow {
                    addr: scalar.reg(),
                    end: scalar.reg(),
                    name: scalar.name().into(),
                    value: scalar.value().to_string(),
                    label: scalar.label().unwrap_or("").into(),
                })
                .collect();
        }
        let zone = match zone {
            "com" => Zone::Com,
            "lan" => Zone::Lan,
            "raw" => Zone::Raw,
            _ => return Vec::new(),
        };
        registers
            .zone(zone)
            .pairs()
            .iter()
            .map(|pair| RegisterRow {
                addr: pair.address().get(),
                end: pair.address().get() + 1,
                name: pair.name().unwrap_or("—").into(),
                value: match pair.value() {
                    PairValue::Float(value) => format_number(*value),
                    PairValue::UnixUtc(text) => text.clone(),
                },
                label: String::new(),
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
    Address::new(addr).is_ok()
}
pub fn format_number(n: f64) -> String {
    if n.abs() >= 1000. {
        format!("{n:.1}")
    } else {
        format!("{n:.4}")
            .trim_end_matches('0')
            .trim_end_matches('.')
            .into()
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
