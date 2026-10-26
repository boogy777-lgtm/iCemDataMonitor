//! Ordered COM DTO decoding and checked, owned domain projection.
use crate::{
    identity::eq_ignore_case,
    simulation::{Template, Variable},
};
use serde::{
    Deserialize, Deserializer,
    de::{self, IgnoredAny, MapAccess, Visitor},
};
use serde_json::{Value, value::RawValue};
use std::fmt;
type Error = Box<dyn std::error::Error>;

// The external integer grammar includes lexical -0, but excludes decimals and
// exponents. serde_json's numeric visitor classifies -0 as a float, so preserve
// the token at this narrow boundary and parse the exact signed int32 spelling.
#[derive(Debug)]
struct Int32(i32);
impl<'de> Deserialize<'de> for Int32 {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let raw = Box::<RawValue>::deserialize(deserializer)?;
        raw.get()
            .parse::<i32>()
            .map(Self)
            .map_err(de::Error::custom)
    }
}

// Local COM schemas: typed assignments are checked in encounter order; an
// invalid occurrence cannot hide behind a later duplicate. Unknowns are skipped.
macro_rules! dto {
    ($name:ident { $($field:ident : $ty:ty = $default:expr => $wire:literal),* $(,)? }) => {
        #[derive(Debug)]
        struct $name { $( $field: $ty, )* }
        impl $name { const FIELDS: &'static [&'static str] = &[$($wire),*]; }
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
                struct DtoVisitor;
                impl<'de> Visitor<'de> for DtoVisitor {
                    type Value = $name;
                    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                        f.write_str(concat!(stringify!($name), " object"))
                    }
                    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                        let mut out = $name { $($field: $default,)* };
                        while let Some(key) = map.next_key::<String>()? {
                            match key.to_ascii_lowercase().as_str() {
                                $($wire => out.$field = map.next_value()?,)*
                                _ => { map.next_value::<IgnoredAny>()?; }
                            }
                        }
                        Ok(out)
                    }
                }
                deserializer.deserialize_map(DtoVisitor)
            }
        }
    }
}
dto!(Profile {
    name: Option<String> = None => "name",
    device_type: Option<String> = None => "devicetype",
    auto_select: Option<Vec<Int32>> = None => "autoselectindices",
    variables: Option<Vec<Box<RawValue>>> = None => "variables",
});
dto!(Registry { variables: Option<Vec<RegistryVariable>> = None => "variables" });
dto!(RegistryVariable {
    name: Option<String> = Some(String::new()) => "name",
    display: Option<String> = Some(String::new()) => "display",
    unit: Option<String> = None => "uom",
    exclude: bool = false => "excludefromtelemetry",
    simulation: Option<Simulation> = None => "simulation",
    families: Option<Families> = None => "families",
});
dto!(Simulation {
    base: f64 = 0.0 => "base",
    amplitude: f64 = 0.0 => "amplitude",
    frequency: f64 = 0.0 => "frequency",
    format: Option<String> = None => "format",
    toggle_period: f64 = 0.0 => "toggleperiod",
});
dto!(Families {
    flecs: Option<Family> = None => "flecs",
    plc: Option<Family> = None => "plc",
    lan: Option<Family> = None => "lan",
});
dto!(Family {
    index: Int32 = Int32(0) => "index",
    alias: Option<String> = None => "alias",
    // Validated as part of the DTO even though COM does not project wireName.
    _wire_name: Option<String> = None => "wirename",
});
dto!(Inline {
    index: Int32 = Int32(0) => "index",
    name: Option<String> = None => "name",
    legacy_name: Option<String> = None => "legacyname",
    unit: Option<String> = None => "uom",
    base: f64 = 0.0 => "base",
    amplitude: f64 = 0.0 => "amplitude",
    frequency: f64 = 0.0 => "frequency",
    format: Option<String> = None => "format",
    toggle_period: f64 = 0.0 => "toggleperiod",
    exclude: bool = false => "excludefromtelemetry",
});
#[derive(Debug)]
enum VariableInput {
    Reference { query: String, index: i32 },
    Inline(Inline),
}
impl VariableInput {
    fn parse(raw: &RawValue) -> Result<Self, serde_json::Error> {
        struct ReferenceProbe {
            query: Option<Box<RawValue>>,
            index: Option<Box<RawValue>>,
        }
        impl<'de> Deserialize<'de> for ReferenceProbe {
            fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
                struct ProbeVisitor;
                impl<'de> Visitor<'de> for ProbeVisitor {
                    type Value = ReferenceProbe;
                    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                        f.write_str("COM variable object")
                    }
                    fn visit_map<M: MapAccess<'de>>(
                        self,
                        mut map: M,
                    ) -> Result<Self::Value, M::Error> {
                        let mut out = ReferenceProbe {
                            query: None,
                            index: None,
                        };
                        while let Some(key) = map.next_key::<String>()? {
                            match key.as_str() {
                                "registry" => out.query = Some(map.next_value()?),
                                "index" => out.index = Some(map.next_value()?),
                                _ => {
                                    map.next_value::<IgnoredAny>()?;
                                }
                            }
                        }
                        Ok(out)
                    }
                }
                deserializer.deserialize_map(ProbeVisitor)
            }
        }
        let probe: ReferenceProbe = serde_json::from_str(raw.get())?;
        if let Some(query) = probe.query {
            let query: Option<String> = serde_json::from_str(query.get())?;
            let index = probe
                .index
                .map(|v| serde_json::from_str::<Int32>(v.get()))
                .transpose()?
                .unwrap_or(Int32(0))
                .0;
            Ok(Self::Reference {
                query: query.unwrap_or_default(),
                index,
            })
        } else {
            serde_json::from_str(raw.get()).map(Self::Inline)
        }
    }
}
/// Eager decoding checks all registry records, including unreferenced records.
pub(crate) struct CheckedRegistry(Option<Registry>);
impl CheckedRegistry {
    pub(crate) fn parse(text: &str) -> Result<Self, Error> {
        Ok(Self(serde_json::from_str(text)?))
    }
    pub(crate) fn empty() -> Self {
        Self(None)
    }
}
pub(crate) struct ProfileDocument(Option<Profile>);
impl ProfileDocument {
    pub(crate) fn parse(text: &str) -> Result<Self, Error> {
        Ok(Self(serde_json::from_str(text)?))
    }
    pub(crate) fn has_identity(&self) -> bool {
        self.0
            .as_ref()
            .is_some_and(|p| p.name.is_some() && p.device_type.is_some())
    }
    pub(crate) fn build(
        self,
        registry: &CheckedRegistry,
        warnings: &mut Vec<String>,
    ) -> Result<Template, Error> {
        let profile = self.0.ok_or("missing template name")?;
        let name = profile.name.ok_or("missing template name")?;
        let device_type = profile.device_type.ok_or("missing template deviceType")?;
        let mut variables = Vec::new();
        // Root identity decides whether a catalog file is a profile. A registry
        // override is also enumerated by legacy discovery and has no identity.
        // Decode all selected variable records before resolution emits warnings.
        let inputs = profile
            .variables
            .into_iter()
            .flatten()
            .map(|raw| VariableInput::parse(&raw))
            .collect::<Result<Vec<_>, _>>()?;
        for input in inputs {
            let variable = match input {
                VariableInput::Inline(v) => {
                    let name = v.name.unwrap_or_default();
                    Variable {
                        index: v.index.0,
                        legacy_name: v.legacy_name.unwrap_or_else(|| name.clone()),
                        name,
                        unit: v.unit.unwrap_or_default(),
                        base: v.base,
                        amplitude: v.amplitude,
                        frequency: v.frequency,
                        format: v.format.unwrap_or_default(),
                        toggle_period: v.toggle_period,
                        exclude: v.exclude,
                    }
                }
                VariableInput::Reference { query, index } => {
                    let entry = registry
                        .0
                        .as_ref()
                        .and_then(|r| r.variables.as_ref())
                        .into_iter()
                        .flatten()
                        .find(|v| {
                            [&v.name, &v.display]
                                .into_iter()
                                .any(|s| s.as_ref().is_some_and(|s| eq_ignore_case(s, &query)))
                        });
                    let Some(entry) = entry else {
                        warnings.push(format!("registry variable '{query}' not found"));
                        continue;
                    };
                    let family = entry.families.as_ref().and_then(|f| {
                        match device_type.to_ascii_lowercase().as_str() {
                            "flecs" => f.flecs.as_ref(),
                            "plc" => f.plc.as_ref(),
                            "lan" => f.lan.as_ref(),
                            _ => f.flecs.as_ref().or(f.plc.as_ref()).or(f.lan.as_ref()),
                        }
                    });
                    let alias = family
                        .and_then(|f| f.alias.as_ref())
                        .or(entry.display.as_ref())
                        .cloned()
                        .unwrap_or_default();
                    let sim = entry.simulation.as_ref();
                    Variable {
                        index: family.map_or(index, |f| f.index.0),
                        name: alias.clone(),
                        legacy_name: alias,
                        unit: entry.unit.clone().unwrap_or_default(),
                        base: sim.map_or(0.0, |s| s.base),
                        amplitude: sim.map_or(0.0, |s| s.amplitude),
                        frequency: sim.map_or(0.0, |s| s.frequency),
                        format: sim.and_then(|s| s.format.clone()).unwrap_or_default(),
                        toggle_period: sim.map_or(0.0, |s| s.toggle_period),
                        exclude: entry.exclude,
                    }
                }
            };
            variables.push(variable);
        }
        variables.sort_by_key(|v| v.index);
        Ok(Template {
            name,
            device_type,
            auto_select: profile
                .auto_select
                .unwrap_or_default()
                .into_iter()
                .map(|v| v.0)
                .collect(),
            variables,
        })
    }
}
// DOMs have lost source order: reject recognized DTO collisions before decoding.
fn unambiguous(value: &Value, fields: &[&str]) -> Result<(), Error> {
    if let Some(object) = value.as_object() {
        for field in fields {
            if object
                .keys()
                .filter(|key| key.eq_ignore_ascii_case(field))
                .count()
                > 1
            {
                return Err(format!("ambiguous COM DTO field '{field}' in Value; use template_from_str or a file loader to preserve source order").into());
            }
        }
    }
    Ok(())
}
fn child<'a>(value: &'a Value, field: &str) -> Option<&'a Value> {
    value
        .as_object()?
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(field))
        .map(|(_, value)| value)
}
pub(crate) fn check_dom(template: &Value, registry: &Value) -> Result<(), Error> {
    unambiguous(template, Profile::FIELDS)?;
    unambiguous(registry, Registry::FIELDS)?;
    for value in child(template, "variables")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        if value.get("registry").is_none() {
            unambiguous(value, Inline::FIELDS)?;
        }
    }
    for value in child(registry, "variables")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        unambiguous(value, RegistryVariable::FIELDS)?;
        if let Some(sim) = child(value, "simulation") {
            unambiguous(sim, Simulation::FIELDS)?;
        }
        if let Some(families) = child(value, "families") {
            unambiguous(families, Families::FIELDS)?;
            for key in Families::FIELDS {
                if let Some(family) = child(families, key) {
                    unambiguous(family, Family::FIELDS)?;
                }
            }
        }
    }
    Ok(())
}
