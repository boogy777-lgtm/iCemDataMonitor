//! Original template-driven COM waveform model; no serial I/O or ambient clock.
use serde_json::Value;
use std::{collections::BTreeMap, path::Path};
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Variable {
    pub index: i32,
    pub name: String,
    pub legacy_name: String,
    pub unit: String,
    pub base: f64,
    pub amplitude: f64,
    pub frequency: f64,
    pub format: String,
    pub toggle_period: f64,
    pub exclude: bool,
}
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Template {
    pub name: String,
    pub device_type: String,
    pub auto_select: Vec<i32>,
    pub variables: Vec<Variable>,
}
fn property<'a>(value: &'a Value, name: &str) -> Option<&'a Value> {
    value
        .as_object()?
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, value)| value)
}
fn text(value: &Value, name: &str) -> String {
    property(value, name)
        .and_then(Value::as_str)
        .unwrap_or("")
        .into()
}
fn number(value: &Value, name: &str) -> f64 {
    property(value, name).and_then(Value::as_f64).unwrap_or(0.0)
}
fn integer(value: &Value, name: &str) -> Result<i32, Box<dyn std::error::Error>> {
    match property(value, name) {
        None | Some(Value::Null) => Ok(0),
        Some(value) => Ok(i32::try_from(
            value.as_i64().ok_or("template index must be int32")?,
        )?),
    }
}
fn read_json(path: &Path) -> Result<Value, Box<dyn std::error::Error>> {
    let text = std::fs::read_to_string(path)?;
    Ok(serde_json::from_str(text.trim_start_matches('\u{feff}'))?)
}
/// Explicit file load, not the production directory catalog/fallback resolver.
/// Registry override naming follows the original SimFileResolver for this one file.
pub fn load_template(
    path: &Path,
    registry: Option<&Path>,
) -> Result<Template, Box<dyn std::error::Error>> {
    let template = read_json(path)?;
    let default = path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("variable_registry.json");
    let overridden = default.with_file_name("variable_registry.emulator.json");
    let registry_path = registry.unwrap_or_else(|| {
        if overridden.is_file() {
            &overridden
        } else {
            &default
        }
    });
    let registry = if registry_path.is_file() {
        read_json(registry_path)?
    } else {
        Value::Null
    };
    template_from_json(&template, &registry)
}
pub fn template_from_json(
    template: &Value,
    registry: &Value,
) -> Result<Template, Box<dyn std::error::Error>> {
    validate_fields(template, &["name", "deviceType"], &[], &[], &[])?;
    for key in ["variables", "autoSelectIndices"] {
        if let Some(v) = property(template, key)
            && !v.is_null()
            && !v.is_array()
        {
            return Err(format!("{key} must be an array").into());
        }
    }
    for v in property(template, "variables")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        validate_fields(
            v,
            &["name", "legacyName", "uom", "format", "registry"],
            &["base", "amplitude", "frequency", "togglePeriod"],
            &["index"],
            &["excludeFromTelemetry"],
        )?;
    }
    if let Some(v) = property(registry, "variables")
        && !v.is_null()
        && !v.is_array()
    {
        return Err("registry variables must be an array".into());
    }
    for v in property(registry, "variables")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        validate_fields(
            v,
            &["name", "display", "uom"],
            &[],
            &[],
            &["excludeFromTelemetry"],
        )?;
        if let Some(sim) = property(v, "simulation")
            && !sim.is_null()
        {
            validate_fields(
                sim,
                &["format"],
                &["base", "amplitude", "frequency", "togglePeriod"],
                &[],
                &[],
            )?;
        }
        if let Some(families) = property(v, "families")
            && !families.is_null()
        {
            for key in ["flecs", "plc", "lan"] {
                if let Some(f) = property(families, key)
                    && !f.is_null()
                {
                    validate_fields(f, &["alias", "wireName"], &[], &["index"], &[])?;
                }
            }
        }
    }
    let name = property(template, "name")
        .and_then(Value::as_str)
        .ok_or("missing template name")?
        .into();
    let device_type = text(template, "deviceType");
    if property(template, "deviceType")
        .and_then(Value::as_str)
        .is_none()
    {
        return Err("missing template deviceType".into());
    }
    let entries = property(registry, "variables").and_then(Value::as_array);
    let mut variables = Vec::new();
    for variable in property(template, "variables")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        let v = if let Some(reference) = variable.get("registry") {
            let query = reference
                .as_str()
                .ok_or("registry reference must be a string")?;
            let Some(entry) = entries.into_iter().flatten().find(|entry| {
                text(entry, "name").eq_ignore_ascii_case(query)
                    || text(entry, "display").eq_ignore_ascii_case(query)
            }) else {
                eprintln!("registry variable '{query}' not found");
                continue;
            };
            let families = property(entry, "families").unwrap_or(&Value::Null);
            let family = match device_type.to_ascii_lowercase().as_str() {
                "flecs" => property(families, "flecs"),
                "plc" => property(families, "plc"),
                "lan" => property(families, "lan"),
                _ => ["flecs", "plc", "lan"]
                    .into_iter()
                    .filter_map(|key| property(families, key))
                    .find(|v| !v.is_null()),
            }
            .filter(|v| !v.is_null());
            let simulation = property(entry, "simulation").unwrap_or(&Value::Null);
            let alias = family
                .and_then(|f| property(f, "alias"))
                .and_then(Value::as_str)
                .map(str::to_owned)
                .unwrap_or_else(|| text(entry, "display"));
            Variable {
                index: match family {
                    Some(f) => integer(f, "index")?,
                    None => integer(variable, "index")?,
                },
                name: alias.clone(),
                legacy_name: alias,
                unit: text(entry, "uom"),
                base: number(simulation, "base"),
                amplitude: number(simulation, "amplitude"),
                frequency: number(simulation, "frequency"),
                format: text(simulation, "format"),
                toggle_period: number(simulation, "togglePeriod"),
                exclude: property(entry, "excludeFromTelemetry")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
            }
        } else {
            let name = text(variable, "name");
            Variable {
                index: integer(variable, "index")?,
                legacy_name: property(variable, "legacyName")
                    .and_then(Value::as_str)
                    .map(str::to_owned)
                    .unwrap_or_else(|| name.clone()),
                name,
                unit: text(variable, "uom"),
                base: number(variable, "base"),
                amplitude: number(variable, "amplitude"),
                frequency: number(variable, "frequency"),
                format: text(variable, "format"),
                toggle_period: number(variable, "togglePeriod"),
                exclude: property(variable, "excludeFromTelemetry")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
            }
        };
        variables.push(v);
    }
    variables.sort_by_key(|v| v.index);
    let auto_select = property(template, "autoSelectIndices")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .map(|v| {
            v.as_i64()
                .ok_or("autoSelectIndices must be int32")
                .and_then(|v| i32::try_from(v).map_err(|_| "autoSelectIndices overflow"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    Ok(Template {
        name,
        device_type,
        auto_select,
        variables,
    })
}
pub fn compute(variable: &Variable, elapsed: f64) -> f64 {
    if variable.format.eq_ignore_ascii_case("elapsed") {
        return elapsed;
    }
    if variable.toggle_period > 0.0 {
        return f64::from(((elapsed / variable.toggle_period) as i32) % 2 != 0);
    }
    if variable.amplitude > 0.0 && variable.frequency > 0.0 {
        return variable.base
            + variable.amplitude
                * (2.0 * std::f64::consts::PI * variable.frequency * elapsed).sin();
    }
    variable.base
}
#[derive(Debug)]
pub struct Engine {
    templates: Vec<Template>,
    current: Option<usize>,
    elapsed: f64,
    overrides: BTreeMap<String, f64>,
    last: Vec<f64>,
}
impl Engine {
    pub fn new(templates: Vec<Template>) -> Self {
        Self {
            current: (!templates.is_empty()).then_some(0),
            templates,
            elapsed: 0.0,
            overrides: BTreeMap::new(),
            last: Vec::new(),
        }
    }
    pub fn templates(&self) -> &[Template] {
        &self.templates
    }
    pub fn current(&self) -> Option<&Template> {
        self.current.and_then(|i| self.templates.get(i))
    }
    pub fn elapsed(&self) -> f64 {
        self.elapsed
    }
    pub fn last_values(&self) -> &[f64] {
        &self.last
    }
    pub fn set_template(&mut self, device_type: &str, name: &str) {
        let same_device = |t: &Template| t.device_type.eq_ignore_ascii_case(device_type);
        self.current = self
            .templates
            .iter()
            .position(|t| same_device(t) && t.name.eq_ignore_ascii_case(name))
            .or_else(|| self.templates.iter().position(same_device))
            .or_else(|| (!self.templates.is_empty()).then_some(0));
    }
    pub fn find_variable(&self, name: &str) -> Option<(usize, &Variable)> {
        self.current()?.variables.iter().enumerate().find(|(_, v)| {
            v.name.eq_ignore_ascii_case(name) || v.legacy_name.eq_ignore_ascii_case(name)
        })
    }
    pub fn set_override(&mut self, name: &str, value: f64) {
        self.overrides.insert(name.to_ascii_lowercase(), value);
    }
    pub fn clear_override(&mut self, name: &str) -> bool {
        self.overrides.remove(&name.to_ascii_lowercase()).is_some()
    }
    pub fn reset_clock(&mut self) {
        self.elapsed = 0.0;
    }
    pub fn tick(&mut self) -> Vec<f64> {
        self.elapsed += 1.0;
        let values: Vec<f64> = self
            .current()
            .map(|t| {
                t.variables
                    .iter()
                    .map(|v| {
                        self.overrides
                            .get(&v.name.to_ascii_lowercase())
                            .copied()
                            .unwrap_or_else(|| compute(v, self.elapsed))
                    })
                    .collect()
            })
            .unwrap_or_default();
        // Original empty-template path returns [] without clearing the previous LastValues.
        if self.current().is_some_and(|t| !t.variables.is_empty()) {
            self.last = values.clone();
        }
        values
    }
}
/// FormatLine only, without CRLF. The serial adapter (not this pure function) appends it.
pub fn format_line(values: &[f64], template: &Template) -> String {
    template
        .variables
        .iter()
        .enumerate()
        .filter(|(_, v)| !v.exclude)
        .map(|(i, v)| {
            let value = values.get(i).copied().unwrap_or(0.0);
            if v.format.eq_ignore_ascii_case("int") {
                return (value.round_ties_even() as i32).to_string();
            }
            if value.is_nan() {
                return "NaN".into();
            }
            if value == f64::INFINITY {
                return "Infinity".into();
            }
            if value == f64::NEG_INFINITY {
                return "-Infinity".into();
            }
            if v.format.eq_ignore_ascii_case("elapsed") {
                format!("{value:.1}")
            } else {
                format!("{value:.2}")
            }
        })
        .collect::<Vec<_>>()
        .join(",")
}

fn validate_fields(
    value: &Value,
    strings: &[&str],
    numbers: &[&str],
    integers: &[&str],
    booleans: &[&str],
) -> Result<(), Box<dyn std::error::Error>> {
    if !value.is_object() {
        return Err("template/registry entry must be an object".into());
    }
    for key in strings {
        if let Some(v) = property(value, key)
            && !v.is_null()
            && !v.is_string()
        {
            return Err(format!("{key} must be a string").into());
        }
    }
    for key in numbers {
        if let Some(v) = property(value, key)
            && !v.is_number()
        {
            return Err(format!("{key} must be a number").into());
        }
    }
    for key in integers {
        if let Some(v) = property(value, key)
            && v.as_i64().and_then(|n| i32::try_from(n).ok()).is_none()
        {
            return Err(format!("{key} must be int32").into());
        }
    }
    for key in booleans {
        if let Some(v) = property(value, key)
            && !v.is_boolean()
        {
            return Err(format!("{key} must be bool").into());
        }
    }
    Ok(())
}
