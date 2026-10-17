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
        if let Some((_, current)) = self
            .overrides
            .iter_mut()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
        {
            *current = value;
        } else {
            self.overrides.insert(name.into(), value);
        }
    }
    pub fn clear_override(&mut self, name: &str) -> bool {
        let key = self
            .overrides
            .keys()
            .find(|key| key.eq_ignore_ascii_case(name))
            .cloned();
        key.is_some_and(|key| self.overrides.remove(&key).is_some())
    }
    pub fn snapshot_overrides(&self) -> BTreeMap<String, f64> {
        self.overrides.clone()
    }
    /// The original host mutates Tick's shared array while projecting units.
    /// Explicit copy-back preserves that observable cache without Rust aliasing.
    pub fn replace_last_values(&mut self, values: &[f64]) -> Result<(), &'static str> {
        if values.len() != self.last.len() {
            return Err("COM cache length changed");
        }
        self.last.copy_from_slice(values);
        Ok(())
    }
    /// Original FrozenComValues: seed once, overlay an owned copy; never replace
    /// LastValues for a subsequent frozen read, even after overrides/units.
    pub fn frozen_values(&mut self) -> Vec<f64> {
        if self.current().is_none() || self.last.is_empty() {
            return self.tick();
        }
        let mut values = self.last.clone();
        if let Some(template) = self.current() {
            for (value, var) in values.iter_mut().zip(&template.variables) {
                if let Some((_, ov)) = self
                    .overrides
                    .iter()
                    .find(|(key, _)| key.eq_ignore_ascii_case(&var.name))
                {
                    *value = *ov;
                }
            }
        }
        values
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
                            .iter()
                            .find(|(key, _)| key.eq_ignore_ascii_case(&v.name))
                            .map(|(_, value)| *value)
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
/// Original startup discovery; caller owns/caches the result.
#[derive(Debug)]
pub struct Catalog {
    pub templates: Vec<Template>,
    pub warnings: Vec<String>,
}
pub fn resolve_sim_file(directory: &Path, filename: &str) -> std::path::PathBuf {
    let overridden = directory.join("variable_registry.emulator.json");
    if filename.eq_ignore_ascii_case("variable_registry.json") && overridden.is_file() {
        overridden
    } else {
        directory.join(filename)
    }
}
pub fn load_catalog(directory: &Path) -> Catalog {
    let mut out = Catalog {
        templates: Vec::new(),
        warnings: Vec::new(),
    };
    let registry_path = resolve_sim_file(directory, "variable_registry.json");
    let registry = if registry_path.is_file() {
        match read_json(&registry_path) {
            Ok(r) => r,
            Err(e) => {
                out.warnings.push(format!("registry: {e}"));
                Value::Null
            }
        }
    } else {
        out.warnings.push("variable_registry.json not found".into());
        Value::Null
    };
    // Preserve Directory.GetFiles order: default profile is the first valid file.
    match std::fs::read_dir(directory) {
        Ok(files) => {
            for file in files {
                let file = match file {
                    Ok(f) => f,
                    Err(e) => {
                        out.warnings.push(e.to_string());
                        continue;
                    }
                };
                let path = file.path();
                let name = file.file_name();
                let name = name.to_string_lossy();
                if !name.to_ascii_lowercase().ends_with(".json")
                    || ["variable_registry.json", "lantable_registry.json"]
                        .iter()
                        .any(|n| name.eq_ignore_ascii_case(n))
                {
                    continue;
                }
                match read_json(&path).and_then(|v| {
                    if property(&v, "name").is_none_or(Value::is_null)
                        || property(&v, "deviceType").is_none_or(Value::is_null)
                    {
                        Ok(None)
                    } else {
                        template_from_json(&v, &registry).map(Some)
                    }
                }) {
                    Ok(Some(t)) => out.templates.push(t),
                    Ok(None) => {}
                    Err(e) => out
                        .warnings
                        .push(format!("template {}: {e}", path.display())),
                }
            }
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => out.warnings.push(e.to_string()),
    }
    if out.templates.is_empty() {
        out.warnings
            .push("zero templates: built-in flecs 4.53".into());
        out.templates.push(fallback_flecs453());
    }
    out
}

fn fallback_flecs453() -> Template {
    Template {
        name: "flecs 4.53".into(),
        device_type: "FLECS".into(),
        auto_select: vec![6, 5, 2, 3, 18, 11, 13, 22, 12, 33],
        variables: vec![
            Variable {
                index: 0,
                name: "Time & Date".into(),
                legacy_name: "Time & Date".into(),
                unit: "".into(),
                base: 0.0,
                amplitude: 0.0,
                frequency: 0.0,
                format: "elapsed".into(),
                exclude: true,
                ..Variable::default()
            },
            Variable {
                index: 1,
                name: "Unit Num".into(),
                legacy_name: "Unit Num".into(),
                unit: "".into(),
                base: 1.0,
                amplitude: 0.0,
                frequency: 0.0,
                format: "int".into(),
                exclude: true,
                ..Variable::default()
            },
            Variable {
                index: 2,
                name: "SlurryDensity".into(),
                legacy_name: "SlurryDensity".into(),
                unit: "ppg".into(),
                base: 15.80,
                amplitude: 0.40,
                frequency: 0.10,
                ..Variable::default()
            },
            Variable {
                index: 3,
                name: "DH_Density".into(),
                legacy_name: "DH_Density".into(),
                unit: "ppg".into(),
                base: 15.90,
                amplitude: 0.30,
                frequency: 0.08,
                ..Variable::default()
            },
            Variable {
                index: 4,
                name: "Mix Water Rate".into(),
                legacy_name: "Mix Water Rate".into(),
                unit: "bpm".into(),
                base: 2.10,
                amplitude: 1.00,
                frequency: 0.15,
                ..Variable::default()
            },
            Variable {
                index: 5,
                name: "PumpRate".into(),
                legacy_name: "PumpRate".into(),
                unit: "bpm".into(),
                base: 4.20,
                amplitude: 0.80,
                frequency: 0.12,
                ..Variable::default()
            },
            Variable {
                index: 6,
                name: "PumpPressure".into(),
                legacy_name: "PumpPressure".into(),
                unit: "psi".into(),
                base: 2500.0,
                amplitude: 200.0,
                frequency: 0.05,
                ..Variable::default()
            },
            Variable {
                index: 7,
                name: "DS Pump Press".into(),
                legacy_name: "DS Pump Press".into(),
                unit: "psi".into(),
                base: 1800.0,
                amplitude: 150.0,
                frequency: 0.06,
                ..Variable::default()
            },
            Variable {
                index: 8,
                name: "Water Stg Tot".into(),
                legacy_name: "Water Stg Tot".into(),
                unit: "bbl".into(),
                base: 120.0,
                amplitude: 2.0,
                frequency: 0.020,
                ..Variable::default()
            },
            Variable {
                index: 9,
                name: "Mix Water Total".into(),
                legacy_name: "Mix Water Total".into(),
                unit: "bbl".into(),
                base: 80.0,
                amplitude: 1.5,
                frequency: 0.025,
                ..Variable::default()
            },
            Variable {
                index: 10,
                name: "Pump Stg Tot".into(),
                legacy_name: "Pump Stg Tot".into(),
                unit: "bbl".into(),
                base: 200.0,
                amplitude: 3.0,
                frequency: 0.018,
                ..Variable::default()
            },
            Variable {
                index: 11,
                name: "CombPumpTotal".into(),
                legacy_name: "CombPumpTotal".into(),
                unit: "bbl".into(),
                base: 100.0,
                amplitude: 2.5,
                frequency: 0.020,
                ..Variable::default()
            },
            Variable {
                index: 12,
                name: "CementValvePsn".into(),
                legacy_name: "CementValvePsn".into(),
                unit: "%".into(),
                base: 85.0,
                amplitude: 5.0,
                frequency: 0.05,
                ..Variable::default()
            },
            Variable {
                index: 13,
                name: "WaterValvePsn".into(),
                legacy_name: "WaterValvePsn".into(),
                unit: "%".into(),
                base: 75.0,
                amplitude: 5.0,
                frequency: 0.04,
                ..Variable::default()
            },
            Variable {
                index: 14,
                name: "PS Pump Rate".into(),
                legacy_name: "PS Pump Rate".into(),
                unit: "bpm".into(),
                base: 3.50,
                amplitude: 0.50,
                frequency: 0.14,
                ..Variable::default()
            },
            Variable {
                index: 15,
                name: "DS Pump Rate".into(),
                legacy_name: "DS Pump Rate".into(),
                unit: "bpm".into(),
                base: 3.00,
                amplitude: 0.40,
                frequency: 0.13,
                ..Variable::default()
            },
            Variable {
                index: 16,
                name: "Dig out".into(),
                legacy_name: "Dig out".into(),
                unit: "".into(),
                base: 0.0,
                amplitude: 0.0,
                frequency: 0.0,
                format: "int".into(),
                toggle_period: 2.5,
                ..Variable::default()
            },
            Variable {
                index: 17,
                name: "HalWin Event".into(),
                legacy_name: "HalWin Event".into(),
                unit: "".into(),
                base: 0.0,
                amplitude: 0.0,
                frequency: 0.0,
                format: "int".into(),
                toggle_period: 10.0,
                ..Variable::default()
            },
            Variable {
                index: 18,
                name: "Temperature".into(),
                legacy_name: "Temperature".into(),
                unit: "degF".into(),
                base: 130.0,
                amplitude: 5.0,
                frequency: 0.02,
                ..Variable::default()
            },
            Variable {
                index: 19,
                name: "rho12_hat".into(),
                legacy_name: "rho12_hat".into(),
                unit: "ppg".into(),
                base: 12.80,
                amplitude: 0.30,
                frequency: 0.07,
                ..Variable::default()
            },
            Variable {
                index: 20,
                name: "h2_hat".into(),
                legacy_name: "h2_hat".into(),
                unit: "ft".into(),
                base: 5000.0,
                amplitude: 100.0,
                frequency: 0.010,
                ..Variable::default()
            },
            Variable {
                index: 21,
                name: "vhp_density_HP".into(),
                legacy_name: "vhp_density_HP".into(),
                unit: "ppg".into(),
                base: 14.50,
                amplitude: 0.40,
                frequency: 0.09,
                ..Variable::default()
            },
            Variable {
                index: 22,
                name: "TubHeight".into(),
                legacy_name: "TubHeight".into(),
                unit: "ft".into(),
                base: 6.50,
                amplitude: 0.80,
                frequency: 0.02,
                ..Variable::default()
            },
            Variable {
                index: 23,
                name: "Extra Press 1".into(),
                legacy_name: "Extra Press 1".into(),
                unit: "psi".into(),
                base: 1500.0,
                amplitude: 100.0,
                frequency: 0.04,
                ..Variable::default()
            },
            Variable {
                index: 24,
                name: "Extra Press 2".into(),
                legacy_name: "Extra Press 2".into(),
                unit: "psi".into(),
                base: 1200.0,
                amplitude: 80.0,
                frequency: 0.045,
                ..Variable::default()
            },
            Variable {
                index: 25,
                name: "Extra Rate 1".into(),
                legacy_name: "Extra Rate 1".into(),
                unit: "bpm".into(),
                base: 2.00,
                amplitude: 0.30,
                frequency: 0.11,
                ..Variable::default()
            },
            Variable {
                index: 26,
                name: "Extra Total 1".into(),
                legacy_name: "Extra Total 1".into(),
                unit: "bbl".into(),
                base: 50.0,
                amplitude: 1.0,
                frequency: 0.020,
                ..Variable::default()
            },
            Variable {
                index: 27,
                name: "Extra Rate 2".into(),
                legacy_name: "Extra Rate 2".into(),
                unit: "bpm".into(),
                base: 1.50,
                amplitude: 0.25,
                frequency: 0.10,
                ..Variable::default()
            },
            Variable {
                index: 28,
                name: "Extra Total 2".into(),
                legacy_name: "Extra Total 2".into(),
                unit: "bbl".into(),
                base: 40.0,
                amplitude: 0.8,
                frequency: 0.019,
                ..Variable::default()
            },
            Variable {
                index: 29,
                name: "Wtr/slry Ratio".into(),
                legacy_name: "Wtr/slry Ratio".into(),
                unit: "".into(),
                base: 0.85,
                amplitude: 0.05,
                frequency: 0.06,
                ..Variable::default()
            },
            Variable {
                index: 30,
                name: "MudCup Density".into(),
                legacy_name: "MudCup Density".into(),
                unit: "ppg".into(),
                base: 13.00,
                amplitude: 0.40,
                frequency: 0.08,
                ..Variable::default()
            },
            Variable {
                index: 31,
                name: "PS Pmp Stg Tot".into(),
                legacy_name: "PS Pmp Stg Tot".into(),
                unit: "bbl".into(),
                base: 100.0,
                amplitude: 2.0,
                frequency: 0.017,
                ..Variable::default()
            },
            Variable {
                index: 32,
                name: "DS Pmp Stg Tot".into(),
                legacy_name: "DS Pmp Stg Tot".into(),
                unit: "bbl".into(),
                base: 90.0,
                amplitude: 1.8,
                frequency: 0.016,
                ..Variable::default()
            },
            Variable {
                index: 33,
                name: "VolumetricMeter".into(),
                legacy_name: "VolumetricMeter".into(),
                unit: "bpm".into(),
                base: 4.20,
                amplitude: 0.50,
                frequency: 0.08,
                ..Variable::default()
            },
        ],
    }
}
