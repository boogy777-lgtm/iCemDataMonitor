//! Pure LAN provider composition from the original EmuEngineHost.
//!
//! Generation and DFB clock advancement are separate operations: callers execute the
//! network explicitly. Freeze captures base + DFB bytes before manual overrides and
//! units. Both downstream layers operate on owned copies, so clearing an override
//! reveals the pristine cached value even after changing unit settings.
use std::collections::BTreeMap;

use crate::{
    cement::CementNetwork,
    lan::{BaseProvider, SignalDef, VectorData, VectorDef, patch_value},
    units::Settings,
};
use icem_protocols::ace::{PAYLOAD_SIZE, decode_native};

#[derive(Debug, Clone)]
pub struct LanPipeline {
    pub base: BaseProvider,
    /// `None` reproduces the original bridge's base-only fallback.
    pub network: Option<CementNetwork>,
    /// Like FrozenPayloadProvider, the pure gate defaults to pass-through.
    /// EmuEngineHost explicitly enables freezing at construction.
    pub frozen: bool,
    pub settings: Settings,
    cached: Option<Vec<VectorData>>,
    overrides: BTreeMap<(u32, String), f64>,
}
impl LanPipeline {
    pub fn new(base: BaseProvider) -> Self {
        Self {
            base,
            network: Some(CementNetwork::default()),
            frozen: false,
            settings: Settings::default(),
            cached: None,
            overrides: BTreeMap::new(),
        }
    }
    /// Always forwards the current base list, even while frozen payloads retain
    /// the list captured before a firmware toggle, matching the C# decorator.
    pub fn active_sv_numbers(&self) -> Vec<u32> {
        self.base.active_sv_numbers()
    }
    pub fn can_transmit(&self, sv: u32, name: &str) -> Result<(), String> {
        let vector = self
            .base
            .registry()
            .get(sv)
            .ok_or_else(|| format!("SV 0x{sv:08X} not in lantable"))?;
        let signal = vector
            .signals
            .iter()
            .find(|s| s.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| format!("signal '{name}' not in SV 0x{sv:08X}"))?;
        if signal.span(PAYLOAD_SIZE).is_none() {
            let end = i64::from(signal.byte_offset) - 4 + i64::from(signal.size);
            return Err(format!(
                "not transmittable: (ByteOffset {} − 4) + Size {} = {end} > {PAYLOAD_SIZE}",
                signal.byte_offset, signal.size,
            ));
        }
        Ok(())
    }
    /// Source dictionary keys are case-sensitive. CanTransmit is deliberately
    /// case-insensitive; a caller must resolve the canonical name before setting.
    /// No implicit validation is added: unknown overrides are stored but unused.
    pub fn set_override(&mut self, sv: u32, name: impl Into<String>, value: f64) {
        self.overrides.insert((sv, name.into()), value);
    }
    pub fn clear_override(&mut self, sv: u32, name: &str) {
        self.overrides.remove(&(sv, name.to_owned()));
    }
    pub fn snapshot_overrides(&self) -> BTreeMap<(u32, String), f64> {
        self.overrides.clone()
    }
    /// Native/pre-unit projection used by the original get/vars operations.
    /// Like the original call through the override provider, this can capture or
    /// refresh the freeze cache. It never executes the DFB network itself.
    pub fn native_superframe(&mut self, equipment: u32, elapsed: f64) -> Vec<VectorData> {
        let mut vectors = if self.frozen {
            self.cached.clone()
        } else {
            None
        }
        .unwrap_or_else(|| {
            let mut generated = self.base.superframe(equipment, elapsed);
            if let Some(network) = &self.network {
                for vector in &mut generated {
                    if let Some(definition) = self.base.registry().get(vector.sv_number) {
                        for signal in &definition.signals {
                            if let Some(value) = network.try_wire_value(&signal.name) {
                                patch_value(&mut vector.payload, signal, value);
                            }
                        }
                    }
                }
            }
            self.cached = Some(generated.clone());
            generated
        });
        for vector in &mut vectors {
            if let Some(definition) = self.base.registry().get(vector.sv_number) {
                for signal in &definition.signals {
                    if let Some(value) =
                        self.overrides.get(&(vector.sv_number, signal.name.clone()))
                    {
                        patch_value(&mut vector.payload, signal, *value);
                    }
                }
            }
        }
        vectors
    }
    pub fn superframe(&mut self, equipment: u32, elapsed: f64) -> Vec<VectorData> {
        let mut vectors = self.native_superframe(equipment, elapsed);
        for vector in &mut vectors {
            if let Some(definition) = self.base.registry().get(vector.sv_number) {
                convert_payload(&mut vector.payload, definition, &self.settings);
            }
        }
        vectors
    }
}

/// UnitConvertingPayloadProvider's per-vector operation. Unknown/non-numeric
/// fields, unsupported widths and out-of-bounds definitions retain their bytes.
pub fn convert_payload(payload: &mut [u8], definition: &VectorDef, settings: &Settings) {
    for signal in &definition.signals {
        convert_signal(payload, signal, settings);
    }
}

fn convert_signal(payload: &mut [u8], signal: &SignalDef, settings: &Settings) {
    let Some(span) = signal.span(payload.len()) else {
        return;
    };
    if !settings.needs_conversion(&signal.uom) {
        return;
    }
    // This layer intentionally does NOT use base/manual EncodeSignal. Its law is
    // decode raw * factor -> convert units -> round/clamp(value / factor), with
    // full-width integer writes. The base layer always emits a four-byte integer.
    let Some(native) = decode_native(
        payload,
        signal.byte_offset,
        signal.size,
        &signal.value_type,
        signal.das_factor,
    ) else {
        return;
    };
    let converted = settings.to_target(native, &signal.uom);
    let Some(bytes) = encode_converted(converted, signal) else {
        return;
    };
    payload[span].copy_from_slice(&bytes);
}

fn encode_converted(value: f64, signal: &SignalDef) -> Option<Vec<u8>> {
    let scaled = (value / signal.das_factor).round_ties_even();
    match signal.value_type.to_ascii_uppercase().as_str() {
        "FF" if signal.size == 4 => Some((value as f32).to_be_bytes().to_vec()),
        "UL" | "UI" | "HX" => match signal.size {
            1 => Some(vec![scaled.clamp(0.0, f64::from(u8::MAX)) as u8]),
            2 => Some(
                (scaled.clamp(0.0, f64::from(u16::MAX)) as u16)
                    .to_be_bytes()
                    .to_vec(),
            ),
            4 => Some(
                (scaled.clamp(0.0, f64::from(u32::MAX)) as u32)
                    .to_be_bytes()
                    .to_vec(),
            ),
            8 => Some(
                (scaled.clamp(0.0, u64::MAX as f64) as u64)
                    .to_be_bytes()
                    .to_vec(),
            ),
            _ => None,
        },
        "SI" | "SL" => match signal.size {
            1 => Some(vec![
                (scaled.clamp(f64::from(i8::MIN), f64::from(i8::MAX)) as i8).cast_unsigned(),
            ]),
            2 => Some(
                (scaled.clamp(f64::from(i16::MIN), f64::from(i16::MAX)) as i16)
                    .to_be_bytes()
                    .to_vec(),
            ),
            4 => Some(
                (scaled.clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32)
                    .to_be_bytes()
                    .to_vec(),
            ),
            8 => Some(
                (scaled.clamp(i64::MIN as f64, i64::MAX as f64) as i64)
                    .to_be_bytes()
                    .to_vec(),
            ),
            _ => None,
        },
        _ => None,
    }
}
