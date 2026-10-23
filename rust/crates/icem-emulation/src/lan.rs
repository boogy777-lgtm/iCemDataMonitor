//! Original LAN catalog and base waveforms, without transport or installation probing.
//! Numeric encoding intentionally keeps the original four-byte-first behavior.
use serde_json::Value;
use std::{collections::BTreeMap, ops::Range, path::Path};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
pub const LEGACY_SVS: [u32; 6] = [
    0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020c, 0x1312020f,
];
pub const FIRMWARE_SVS: [u32; 14] = [
    0x13120212, 0x13120215, 0x13120218, 0x1312021b, 0x1312021e, 0x13120221, 0x13120224, 0x13120227,
    0x1312022a, 0x1312022d, 0x13120230, 0x13120233, 0x13120236, 0x13120239,
];
#[derive(Debug, Clone, PartialEq)]
pub struct SignalDef {
    pub byte_offset: i32,
    pub size: i32,
    pub value_type: String,
    pub das_factor: f64,
    pub name: String,
    pub uom: String,
}
impl SignalDef {
    /// Checked O−4 mapping. Invalid widths/offsets never mutate the payload.
    pub fn span(&self, payload_len: usize) -> Option<Range<usize>> {
        let start = usize::try_from(self.byte_offset.checked_sub(4)?).ok()?;
        let size = usize::try_from(self.size).ok()?;
        let end = start.checked_add(size)?;
        (end <= payload_len).then_some(start..end)
    }
}
#[derive(Debug, Clone, PartialEq)]
pub struct VectorDef {
    pub sv_number: u32,
    pub name: String,
    pub equipment_type: String,
    pub signals: Vec<SignalDef>,
}
#[derive(Debug, Clone, PartialEq)]
pub struct VectorData {
    pub sv_number: u32,
    pub name: String,
    pub payload: [u8; 124],
}
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Registry {
    by_sv: BTreeMap<u32, VectorDef>,
    diagnostics: Vec<String>,
}
impl Registry {
    /// Existing legacy definitions win over duplicate legacy and firmware SVs.
    /// Each source file is best-effort, as in LantableRegistry.LoadFile:
    /// retain completed vectors, stop that file on error, then attempt firmware.
    pub fn load(legacy: &Path, firmware: Option<&Path>) -> Result<Self> {
        let mut result = Self::default();
        // Source catches around each file, after mutations already completed.
        for path in std::iter::once(legacy).chain(firmware) {
            if let Err(error) = result.load_file(path) {
                result
                    .diagnostics
                    .push(format!("LAN registry {}: {error}", path.display()));
            }
        }
        Ok(result)
    }
    pub fn from_vectors(vectors: Vec<VectorDef>) -> Self {
        let mut result = Self::default();
        for vector in vectors {
            result.by_sv.entry(vector.sv_number).or_insert(vector);
        }
        result
    }
    /// At most one owned diagnostic per source file (legacy and optional firmware).
    /// The application routes these to its existing logger; no install-root writes.
    pub fn diagnostics(&self) -> &[String] {
        &self.diagnostics
    }
    pub fn get(&self, sv: u32) -> Option<&VectorDef> {
        self.by_sv.get(&sv)
    }
    pub fn all(&self) -> &BTreeMap<u32, VectorDef> {
        &self.by_sv
    }
    /// Strict decoded JSON text/first-wins parser; BOMs belong to the file decoder.
    /// Production file recovery does not hide errors here.
    pub fn from_json(json: &str) -> Result<Self> {
        let mut result = Self::default();
        result.load_value(serde_json::from_str(json)?)?;
        Ok(result)
    }
    fn load_file(&mut self, path: &Path) -> Result<()> {
        if !path.is_file() {
            return Err("file not found".into());
        }
        self.load_value(read_registry_json(path)?)
    }
    fn load_value(&mut self, root: Value) -> Result<()> {
        for vector in array(&root, "serviceVectors")? {
            let sv_number = u32::try_from(
                vector
                    .get("svNumber")
                    .and_then(Value::as_u64)
                    .ok_or("svNumber must be uint32")?,
            )?;
            let mut signals = Vec::new();
            if vector.get("signals").is_some() {
                for sig in array(vector, "signals")? {
                    signals.push(SignalDef {
                        byte_offset: int32(sig, "byteOffset")?,
                        size: int32(sig, "size")?,
                        value_type: string(sig, "valueType", true)?,
                        das_factor: number(sig, "dasFactor", 1.0)?,
                        name: string(sig, "name", true)?,
                        uom: string(sig, "uom", false)?,
                    });
                }
            }
            let definition = VectorDef {
                sv_number,
                name: string(vector, "name", true)?,
                equipment_type: string(vector, "equipmentType", false)?,
                signals,
            };
            self.by_sv.entry(sv_number).or_insert(definition);
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq)]
struct SimEntry {
    base: f64,
    amplitude: f64,
    frequency: f64,
    format: String,
    toggle_period: f64,
    string_value: Option<String>,
}
impl SimEntry {
    fn compute(&self, elapsed: f64) -> f64 {
        if self.format.eq_ignore_ascii_case("elapsed") {
            return elapsed;
        }
        if self.toggle_period > 0.0 {
            return f64::from(((elapsed / self.toggle_period) as i32) % 2 != 0);
        }
        if self.amplitude > 0.0 && self.frequency > 0.0 {
            return self.base
                + self.amplitude * (2.0 * std::f64::consts::PI * self.frequency * elapsed).sin();
        }
        self.base
    }
}
#[derive(Debug, Clone, PartialEq)]
pub struct BaseProvider {
    registry: Registry,
    lookup: BTreeMap<String, SimEntry>,
    pub include_firmware_tables: bool,
}
impl BaseProvider {
    /// Load the explicit variable registry (or existing emulator override), then
    /// adjacent firmware_variables.json. No current directory/install-root search.
    pub fn load(
        registry: Registry,
        variable_registry: &Path,
        emulator_override: Option<&Path>,
    ) -> Result<Self> {
        let sibling = variable_registry.with_file_name("variable_registry.emulator.json");
        let override_path = emulator_override.unwrap_or(&sibling);
        let selected = if variable_registry
            .file_name()
            .and_then(|s| s.to_str())
            .is_some_and(|s| s.eq_ignore_ascii_case("variable_registry.json"))
            && override_path.is_file()
        {
            override_path
        } else {
            variable_registry
        };
        let mut provider = Self {
            registry,
            lookup: BTreeMap::new(),
            include_firmware_tables: false,
        };
        // AcePayloadProvider.LoadSimFile catches per file and keeps the lookup prefix.
        let _ = provider.load_sim(selected);
        let _ = provider.load_sim(&variable_registry.with_file_name("firmware_variables.json"));
        Ok(provider)
    }
    pub fn registry(&self) -> &Registry {
        &self.registry
    }
    pub fn active_sv_numbers(&self) -> Vec<u32> {
        let mut result = LEGACY_SVS.to_vec();
        if self.include_firmware_tables {
            result.extend(FIRMWARE_SVS);
        }
        result
    }
    pub fn superframe(&self, _equipment: u32, elapsed: f64) -> Vec<VectorData> {
        self.active_sv_numbers()
            .into_iter()
            .map(|sv_number| {
                let mut payload = [0; 124];
                let definition = self.registry.get(sv_number);
                if let Some(definition) = definition {
                    for sig in &definition.signals {
                        let Some(span) = sig.span(payload.len()) else {
                            continue;
                        };
                        let entry = self.lookup.get(&sig.name.to_ascii_lowercase());
                        let bytes = if sig.value_type.eq_ignore_ascii_case("UC")
                            && let Some(text) = entry.and_then(|e| e.string_value.as_deref())
                        {
                            encode_ascii(text, sig.size)
                        } else {
                            encode_signal(
                                entry.map_or(0.0, |e| e.compute(elapsed)),
                                &sig.value_type,
                                sig.das_factor,
                                sig.size,
                            )
                        };
                        if let Some(bytes) = bytes {
                            let count = bytes.len().min(span.len());
                            payload[span.start..span.start + count]
                                .copy_from_slice(&bytes[..count]);
                        }
                    }
                }
                VectorData {
                    sv_number,
                    name: definition
                        .map_or_else(|| format!("SV_{sv_number:08X}"), |v| v.name.clone()),
                    payload,
                }
            })
            .collect()
    }
    fn load_sim(&mut self, path: &Path) -> Result<()> {
        if !path.is_file() {
            return Ok(());
        }
        let root = read_simulation_json(path)?;
        if root.get("variables").is_none() {
            return Ok(());
        }
        for variable in array(&root, "variables")? {
            let name = string(variable, "name", true)?;
            let display = string(variable, "display", true)?;
            // C# reads these even though they do not affect LAN payload generation.
            let _ = string(variable, "uom", false)?;
            if let Some(exclude) = variable.get("excludeFromTelemetry") {
                exclude
                    .as_bool()
                    .ok_or("excludeFromTelemetry must be bool")?;
            }
            let sim = variable.get("simulation");
            if let Some(sim) = sim {
                sim.as_object().ok_or("simulation must be an object")?;
            }
            let empty = Value::Object(Default::default());
            let sim = sim.unwrap_or(&empty);
            let string_value = match variable.get("stringValue") {
                None | Some(Value::Null) => None,
                Some(v) => Some(v.as_str().ok_or("stringValue must be string")?.to_owned()),
            };
            let entry = SimEntry {
                base: number(sim, "base", 0.0)?,
                amplitude: number(sim, "amplitude", 0.0)?,
                frequency: number(sim, "frequency", 0.0)?,
                format: string(sim, "format", false)?,
                toggle_period: number(sim, "togglePeriod", 0.0)?,
                string_value,
            };
            let families = variable.get("families");
            if let Some(families) = families {
                families.as_object().ok_or("families must be an object")?;
            }
            if let Some(lan) = families.and_then(|f| f.get("lan")) {
                lan.as_object().ok_or("lan must be an object")?;
                let wire = string(lan, "wireName", false)?;
                let alias = string(lan, "alias", false)?;
                if !wire.is_empty() {
                    self.lookup.insert(wire.to_ascii_lowercase(), entry.clone());
                }
                if !alias.is_empty() && !alias.eq_ignore_ascii_case(&wire) {
                    self.lookup
                        .insert(alias.to_ascii_lowercase(), entry.clone());
                }
            }
            if !name.is_empty() {
                self.lookup
                    .entry(name.to_ascii_lowercase())
                    .or_insert_with(|| entry.clone());
            }
            if !display.is_empty() && !display.eq_ignore_ascii_case(&name) {
                self.lookup
                    .entry(display.to_ascii_lowercase())
                    .or_insert(entry);
            }
        }
        Ok(())
    }
}

/// The original numeric encoders always emit 4 bytes, independent of declared Size.
/// The caller copies only the prefix when Size is smaller; unknown types emit zeros.
pub fn encode_signal(value: f64, kind: &str, factor: f64, size: i32) -> Option<Vec<u8>> {
    usize::try_from(size).ok()?;
    Some(match kind.to_ascii_uppercase().as_str() {
        "FF" => (value as f32).to_be_bytes().to_vec(),
        "UL" | "UI" => ((value * factor).round_ties_even() as u32)
            .to_be_bytes()
            .to_vec(),
        "SI" | "SL" => ((value * factor).round_ties_even() as i32)
            .to_be_bytes()
            .to_vec(),
        "HX" => (value.round_ties_even() as u32).to_be_bytes().to_vec(),
        "UC" => {
            let text = if value.is_nan() {
                "NaN".to_owned()
            } else if value == f64::INFINITY {
                "Infinity".to_owned()
            } else if value == f64::NEG_INFINITY {
                "-Infinity".to_owned()
            } else {
                format!("{value:.0}")
            };
            encode_ascii(&text, size)?
        }
        _ => vec![0; 4],
    })
}
/// ASCII replacement and truncation are measured in UTF-16 units, as in GetBytes.
pub fn encode_ascii(text: &str, size: i32) -> Option<Vec<u8>> {
    let size = usize::try_from(size).ok()?;
    let mut bytes = Vec::new();
    bytes.try_reserve_exact(size).ok()?;
    bytes.resize(size, 0);
    for (out, unit) in bytes.iter_mut().zip(text.encode_utf16()) {
        *out = if unit <= 0x7f { unit as u8 } else { b'?' };
    }
    Some(bytes)
}
pub fn patch_value(payload: &mut [u8], sig: &SignalDef, value: f64) -> bool {
    let Some(span) = sig.span(payload.len()) else {
        return false;
    };
    let Some(encoded) = encode_signal(value, &sig.value_type, sig.das_factor, sig.size) else {
        return false;
    };
    let count = span.len().min(encoded.len());
    payload[span.start..span.start + count].copy_from_slice(&encoded[..count]);
    true
}
// LantableRegistry uses JsonDocument.Parse(Stream), not File.ReadAllText.
// Preserve UTF-8-only JSON and consume at most one initial UTF-8 BOM. Rust's
// deliberate malformed-input policy rejects invalid UTF-8 anywhere in the file,
// including unused JSON strings, before any registry records are inserted.
fn read_registry_json(path: &Path) -> Result<Value> {
    let bytes = std::fs::read(path)?;
    let body = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
    let json = std::str::from_utf8(body)?;
    Ok(serde_json::from_str(json)?)
}
// AcePayloadProvider.LoadSimFile uses File.ReadAllText for both variable files.
fn read_simulation_json(path: &Path) -> Result<Value> {
    let json = icem_config::text_file::read_all_text(path)?;
    Ok(serde_json::from_str(&json)?)
}
fn array<'a>(value: &'a Value, name: &str) -> Result<&'a Vec<Value>> {
    value
        .get(name)
        .and_then(Value::as_array)
        .ok_or_else(|| format!("{name} must be an array").into())
}
fn string(value: &Value, name: &str, required: bool) -> Result<String> {
    match value.get(name) {
        Some(Value::String(text)) => Ok(text.clone()),
        Some(Value::Null) => Ok(String::new()),
        None if !required => Ok(String::new()),
        _ => Err(format!("{name} must be a string").into()),
    }
}
fn number(value: &Value, name: &str, default: f64) -> Result<f64> {
    match value.get(name) {
        None => Ok(default),
        Some(v) => v
            .as_f64()
            .ok_or_else(|| format!("{name} must be a number").into()),
    }
}
fn int32(value: &Value, name: &str) -> Result<i32> {
    Ok(i32::try_from(
        value
            .get(name)
            .and_then(Value::as_i64)
            .ok_or_else(|| format!("{name} must be int32"))?,
    )?)
}
