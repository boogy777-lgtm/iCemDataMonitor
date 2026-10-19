//! Read-only Host decode/cache. Owned by existing Capture; no emitter, IPC or bus writer.
use icem_emulation::lan::Registry;
use icem_protocols::ace::host::{self, Decoded, Kind};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::Path,
    time::Duration,
};
pub const MAX_CATALOG_BYTES: u64 = 2 * 1024 * 1024;
pub const MAX_VECTORS: usize = 256;
pub const MAX_SIGNALS: usize = 16_384;
pub const MAX_SIGNALS_PER_VECTOR: usize = 128;
pub const MAX_EQUIPMENT: usize = 128;
#[derive(Debug, Clone)]
pub struct Signal {
    pub name: String,
    pub uom: String,
    pub decoded: Decoded,
}
#[derive(Debug, Clone)]
pub struct Vector {
    pub id: u32,
    pub number: u32,
    pub name: String,
    pub unknown: bool,
    pub signal_count: usize,
    pub rate: f64,
    pub age_sec: f64,
}
#[derive(Debug, Clone)]
pub struct Equipment {
    pub number: i32,
    pub mac: [u8; 6],
    pub vector_count: usize,
    pub frames: u64,
    pub rate: f64,
}
#[derive(Debug, Clone, Default)]
pub struct Counters {
    pub decoded_frames: u64,
    pub decode_errors: u64,
    pub unknown_vectors: u64,
    pub prefixes: [u64; 2],
    pub tracking_limit_reached: bool,
}
#[derive(Default)]
struct Rate {
    at: Option<Duration>,
    pending: u64,
    actual: f64,
}
impl Rate {
    fn touch(&mut self) {
        self.pending = self.pending.saturating_add(1);
    }
    fn rotate(&mut self, now: Duration) {
        if let Some(at) = self.at {
            let elapsed = now.saturating_sub(at);
            if elapsed < Duration::from_secs(1) {
                return;
            }
            self.actual = self.pending as f64 / elapsed.as_secs_f64();
        }
        self.at = Some(now);
        self.pending = 0;
    }
}
struct VectorState {
    id: u32,
    name: String,
    unknown: bool,
    count: usize,
    rate: Rate,
    seen: Duration,
    signals: BTreeMap<String, Signal>,
}
struct EquipmentState {
    mac: [u8; 6],
    vectors: BTreeSet<u32>,
    frames: u64,
    rate: Rate,
}
pub struct Catalog {
    registry: Registry,
    vectors: BTreeMap<u32, VectorState>,
    equipment: BTreeMap<u32, EquipmentState>,
    counters: Counters,
    stats_due: Duration,
}
impl Catalog {
    /// Host loads only the original lantable file. Firmware merging belongs to emulator.
    pub fn load(path: &Path) -> Result<Self, String> {
        use std::io::Read;
        let file = std::fs::File::open(path)
            .map_err(|e| format!("Host lantable {}: {e}", path.display()))?;
        let mut json = String::new();
        file.take(MAX_CATALOG_BYTES + 1)
            .read_to_string(&mut json)
            .map_err(|e| e.to_string())?;
        if json.len() as u64 > MAX_CATALOG_BYTES {
            return Err("Host lantable exceeds 2 MiB".into());
        }
        Self::new(Registry::from_json(&json).map_err(|e| e.to_string())?)
    }
    pub fn new(registry: Registry) -> Result<Self, String> {
        if registry
            .all()
            .values()
            .any(|v| v.signals.len() > MAX_SIGNALS_PER_VECTOR)
        {
            return Err("Host lantable exceeds per-vector signal budget".into());
        }
        if registry.all().len() > MAX_VECTORS
            || registry
                .all()
                .values()
                .map(|v| v.signals.len())
                .sum::<usize>()
                > MAX_SIGNALS
        {
            return Err("Host lantable exceeds vector/signal budget".into());
        }
        let text_bytes = registry.all().values().fold(0_usize, |total, vector| {
            vector.signals.iter().fold(
                total
                    .saturating_add(vector.name.len())
                    .saturating_add(vector.equipment_type.len()),
                |sum, signal| {
                    sum.saturating_add(signal.name.len())
                        .saturating_add(signal.uom.len())
                        .saturating_add(signal.value_type.len())
                },
            )
        });
        if text_bytes > MAX_CATALOG_BYTES as usize {
            return Err("Host lantable exceeds text budget".into());
        }
        Ok(Self {
            registry,
            vectors: BTreeMap::new(),
            equipment: BTreeMap::new(),
            counters: Counters::default(),
            stats_due: Duration::from_secs(1),
        })
    }
    pub fn definition(&self, number: u32) -> Option<&icem_emulation::lan::VectorDef> {
        self.registry.get(number)
    }
    pub fn counters(&self) -> &Counters {
        &self.counters
    }
    pub fn advance(&mut self, now: Duration) {
        if now < self.stats_due {
            return;
        }
        self.stats_due = now.saturating_add(Duration::from_secs(1));
        for vector in self.vectors.values_mut() {
            vector.rate.rotate(now);
        }
        for equipment in self.equipment.values_mut() {
            equipment.rate.rotate(now);
        }
    }
    /// Only processed multicast frames. No queued frames or callbacks.
    pub fn observe(&mut self, frame: &[u8], now: Duration) {
        if frame.len() >= 17
            && frame[14..16] == [3, 3]
            && let Some(index) = match frame[16] {
                8 => Some(0),
                9 => Some(1),
                _ => None,
            }
        {
            self.counters.prefixes[index] = self.counters.prefixes[index].saturating_add(1);
        }
        let Some(header) = host::header(frame) else {
            return;
        };
        if !self.equipment.contains_key(&header.equipment) && self.equipment.len() < MAX_EQUIPMENT {
            self.equipment.insert(
                header.equipment,
                EquipmentState {
                    mac: header.source,
                    vectors: BTreeSet::new(),
                    frames: 0,
                    rate: Rate::default(),
                },
            );
        }
        if let Some(equipment) = self.equipment.get_mut(&header.equipment) {
            equipment.frames = equipment.frames.saturating_add(1);
            equipment.rate.touch();
            if equipment.vectors.len() < MAX_VECTORS {
                equipment.vectors.insert(header.vector);
            } else if !equipment.vectors.contains(&header.vector) {
                self.counters.tracking_limit_reached = true;
            }
        } else {
            self.counters.tracking_limit_reached = true;
        }
        let definition = self.registry.get(header.vector);
        if definition.is_none() {
            self.counters.unknown_vectors = self.counters.unknown_vectors.saturating_add(1);
        }
        if !self.vectors.contains_key(&header.vector) && self.vectors.len() < MAX_VECTORS {
            let id = self.vectors.len() as u32 + 1;
            self.vectors.insert(
                header.vector,
                VectorState {
                    id,
                    name: definition
                        .map_or_else(|| format!("0x{:08X}", header.vector), |v| v.name.clone()),
                    unknown: definition.is_none(),
                    count: definition.map_or(0, |v| v.signals.len()),
                    rate: Rate::default(),
                    seen: now,
                    signals: BTreeMap::new(),
                },
            );
        }
        if let Some(vector) = self.vectors.get_mut(&header.vector) {
            vector.rate.touch();
            vector.seen = now;
        } else {
            self.counters.tracking_limit_reached = true;
        }
        let Some(definition) = definition else {
            return;
        };
        // Decode all before replacing any last-good signal: frame failure is atomic.
        let decoded: Result<Vec<_>, _> = definition
            .signals
            .iter()
            .map(|signal| {
                host::decode(
                    frame,
                    signal.byte_offset,
                    signal.size,
                    Kind::parse(&signal.value_type),
                    signal.das_factor,
                )
                .map(|decoded| Signal {
                    name: signal.name.clone(),
                    uom: signal.uom.clone(),
                    decoded,
                })
            })
            .collect();
        match decoded {
            Ok(signals) => {
                self.counters.decoded_frames = self.counters.decoded_frames.saturating_add(1);
                if let Some(vector) = self.vectors.get_mut(&header.vector) {
                    vector.signals = signals.into_iter().map(|s| (s.name.clone(), s)).collect();
                }
            }
            Err(_) => {
                self.counters.decode_errors = self.counters.decode_errors.saturating_add(1);
            }
        }
    }
    pub fn vectors(&self, now: Duration) -> Vec<Vector> {
        let mut result: Vec<_> = self
            .vectors
            .iter()
            .map(|(number, v)| Vector {
                id: v.id,
                number: *number,
                name: v.name.clone(),
                unknown: v.unknown,
                signal_count: v.count,
                rate: v.rate.actual,
                age_sec: now.saturating_sub(v.seen).as_secs_f64(),
            })
            .collect();
        result.sort_by_key(|v| v.id);
        result
    }
    pub fn equipment(&self) -> Vec<Equipment> {
        self.equipment
            .iter()
            .map(|(number, e)| Equipment {
                number: number.cast_signed(),
                mac: e.mac,
                vector_count: e.vectors.len(),
                frames: e.frames,
                rate: e.rate.actual,
            })
            .collect()
    }
    pub fn signals(&self, vector_id: u32) -> Vec<Signal> {
        self.vectors
            .values()
            .find(|v| v.id == vector_id)
            .map_or_else(Vec::new, |v| v.signals.values().cloned().collect())
    }
    /// Legacy preview projects textual and unavailable numeric values as zero.
    pub fn preview(&self, vector_id: u32) -> Vec<(String, f64, String, bool)> {
        self.signals(vector_id)
            .into_iter()
            .map(|s| {
                (
                    s.name,
                    if s.decoded.sentinel {
                        -9999.
                    } else {
                        s.decoded.value.unwrap_or(0.)
                    },
                    s.uom,
                    s.decoded.sentinel,
                )
            })
            .collect()
    }
    pub fn witness(&self) -> String {
        let c = &self.counters;
        let mut equipment = self.equipment();
        equipment.sort_by_key(|e| std::cmp::Reverse(e.frames));
        let equipment: Vec<_> = equipment
            .iter()
            .map(|e| format!("0x{:08X}:{}f", e.number, e.frames))
            .collect();
        format!(
            "decode={} errors={} unknown={} prefixes={}/{} vectors={} equipment=[{}] trackingLimit={}",
            c.decoded_frames,
            c.decode_errors,
            c.unknown_vectors,
            c.prefixes[0],
            c.prefixes[1],
            self.vectors.len(),
            equipment.join(" "),
            c.tracking_limit_reached
        )
    }
}
