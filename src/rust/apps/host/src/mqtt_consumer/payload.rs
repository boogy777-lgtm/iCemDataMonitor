//! Lazy typed serialization. No secondary value array or per-variable name allocation.
use icem_bus::{Snapshot, Value, Variable, Zone};
use icem_core::publication::{CaptureStatus, PublishedSnapshot};
use serde::{
    Serialize, Serializer,
    ser::{SerializeSeq, SerializeStruct},
};
use std::{
    fmt,
    io::{self, Write},
    time::{Duration, Instant},
};

pub(super) const MAX_PAYLOAD: usize = 16 * 1024 * 1024;
const AGE_LIMIT: Duration = Duration::from_secs(5);
struct Limited(Vec<u8>);
impl Write for Limited {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.0.len().saturating_add(bytes.len()) > MAX_PAYLOAD {
            return Err(io::Error::other("MQTT payload exceeds 16 MiB"));
        }
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
pub(super) fn encode(state: &PublishedSnapshot, now: Instant) -> Result<Vec<u8>, String> {
    let mut output = Limited(Vec::with_capacity(
        state
            .snapshot
            .as_ref()
            .map_or(1024, |s| s.variable_count() * 144 + 4096),
    ));
    let mut serializer = serde_json::Serializer::new(&mut output);
    Payload { state, now }
        .serialize(&mut serializer)
        .map_err(|e| format!("MQTT payload: {e}"))?;
    Ok(output.0)
}
struct DisplayValue<T>(T);
impl<T: fmt::Display> Serialize for DisplayValue<T> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_str(&self.0)
    }
}
#[derive(Serialize)]
struct Capture<'a> {
    generation: u64,
    generation_scope: &'static str,
    status: &'static str,
    source_error: Option<DisplayValue<DebugValue<icem_core::SourceError>>>,
    capture_age_ms: Option<u128>,
    completion_age_ms: Option<u128>,
    quality: &'a str,
    quality_scope: &'static str,
    age_clock: &'static str,
}
struct DebugValue<T>(T);
impl<T: fmt::Debug> fmt::Display for DebugValue<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}", self.0)
    }
}
struct Payload<'a> {
    state: &'a PublishedSnapshot,
    now: Instant,
}
impl Serialize for Payload<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let (status, error) = match self.state.status {
            CaptureStatus::Initial => ("initial", None),
            CaptureStatus::Captured => ("captured", None),
            CaptureStatus::Retained { error } => ("retained", error),
            CaptureStatus::Failed(error) => ("failed", Some(error)),
        };
        let capture_age = self.state.capture_age(self.now);
        let completion_age = self
            .state
            .completed_at_monotonic
            .and_then(|at| self.now.checked_duration_since(at));
        let quality = if self.state.snapshot.is_none() {
            "absent"
        } else if !matches!(self.state.status, CaptureStatus::Captured) {
            "retained"
        } else if capture_age.is_none_or(|age| age >= AGE_LIMIT)
            || completion_age.is_none_or(|age| age >= AGE_LIMIT)
        {
            "expired"
        } else {
            "captured"
        };
        let mut object = serializer.serialize_struct("State", 6)?;
        object.serialize_field("schema", "icem.mqtt.state.v1")?;
        object.serialize_field(
            "coherence",
            "one_synchronized_memory_copy_not_one_measurement",
        )?;
        object.serialize_field(
            "capture",
            &Capture {
                generation: self.state.capture_generation,
                generation_scope: "local_successful_capture_not_producer_sequence",
                status,
                source_error: error.map(|e| DisplayValue(DebugValue(e))),
                capture_age_ms: capture_age.map(|d| d.as_millis()),
                completion_age_ms: completion_age.map(|d| d.as_millis()),
                quality,
                quality_scope: "acquisition_only_not_stream_health",
                age_clock: "local_monotonic_age_at_serialization_not_delivery",
            },
        )?;
        object.serialize_field(
            "com_source_time",
            &self.state.snapshot.as_ref().map(|s| ComTime(s)),
        )?;
        object.serialize_field("zones", &self.state.snapshot.as_ref().map(|s| Zones(s)))?;
        object.serialize_field(
            "variables",
            &self.state.snapshot.as_ref().map(|s| Variables(s)),
        )?;
        object.end()
    }
}
struct ComTime<'a>(&'a Snapshot);
impl Serialize for ComTime<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut object = serializer.serialize_struct("ComTime", 5)?;
        object.serialize_field("variable", "var4")?;
        object.serialize_field("unix_seconds", &self.0.service().com_source_time())?;
        object.serialize_field("provenance", "COM_Time_and_Date_registers_4_5")?;
        object.serialize_field("scope", "COM_source_field_not_LAN_RAW_measurement_time")?;
        object.serialize_field("validity", "raw_source_field_not_independently_verified")?;
        object.end()
    }
}
struct Zones<'a>(&'a Snapshot);
impl Serialize for Zones<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut seq = serializer.serialize_seq(Some(3))?;
        for zone in Zone::ALL {
            let metadata = self.0.zone(zone);
            #[derive(Serialize)]
            struct Metadata {
                zone: &'static str,
                last_write_utc_ticks: i64,
                clock: &'static str,
                provenance: &'static str,
                producer_active: bool,
                write_count: u32,
                write_policy: &'static str,
                upstream_measurement_time: Option<()>,
            }
            seq.serialize_element(&Metadata {
                zone: match zone {
                    Zone::Com => "COM",
                    Zone::Lan => "LAN",
                    Zone::Raw => "RAW",
                },
                last_write_utc_ticks: metadata.last_write_utc_ticks,
                clock: "dotnet_utc_ticks_100ns_since_0001",
                provenance: "AddIn_zone_writer_last_write_not_measurement_time",
                producer_active: metadata.producer_active,
                write_count: metadata.write_count,
                write_policy: if zone == Zone::Raw {
                    "nonempty_raw_line"
                } else {
                    "changed_pair_bits"
                },
                upstream_measurement_time: None,
            })?;
        }
        seq.end()
    }
}
struct Variables<'a>(&'a Snapshot);
impl Serialize for Variables<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut seq = serializer.serialize_seq(Some(self.0.variable_count()))?;
        for variable in self.0.variables() {
            seq.serialize_element(&TypedVariable(variable))?;
        }
        seq.end()
    }
}
struct TypedVariable(Variable);
impl Serialize for TypedVariable {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let variable = self.0;
        let mut object = serializer.serialize_struct("Variable", 6)?;
        object.serialize_field("id", &DisplayValue(variable.id))?;
        object.serialize_field("register_address", &variable.id.register_address())?;
        object.serialize_field("raw_bits", &variable.value.raw_bits())?;
        match variable.value {
            Value::Word(value) => {
                object.serialize_field("type", "u16")?;
                object.serialize_field("value", &value)?;
                object.serialize_field("non_finite", &None::<&str>)?;
            }
            Value::ComUnixSeconds(value) => {
                object.serialize_field("type", "i32_com_unix_seconds")?;
                object.serialize_field("value", &value)?;
                object.serialize_field("non_finite", &None::<&str>)?;
            }
            Value::Float32(value) => {
                object.serialize_field("type", "ieee754_binary32")?;
                object.serialize_field("value", &value.is_finite().then_some(value))?;
                object.serialize_field(
                    "non_finite",
                    &if value.is_nan() {
                        Some("nan")
                    } else if value == f32::INFINITY {
                        Some("positive_infinity")
                    } else if value == f32::NEG_INFINITY {
                        Some("negative_infinity")
                    } else {
                        None
                    },
                )?;
            }
        }
        object.end()
    }
}
