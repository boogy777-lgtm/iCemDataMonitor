//! Original unit constants/order, including identity for unknown/cross-group units.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Group {
    None,
    Flow,
    Temperature,
    Density,
    Volume,
    Level,
    Pressure,
}
impl Group {
    pub const ALL: [Self; 6] = [
        Self::Flow,
        Self::Temperature,
        Self::Density,
        Self::Volume,
        Self::Level,
        Self::Pressure,
    ];
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Preset {
    FirmwareDefault,
    #[default]
    LanNative,
    Metric,
    Imperial,
}
impl TryFrom<u8> for Preset {
    type Error = &'static str;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::FirmwareDefault),
            1 => Ok(Self::LanNative),
            2 => Ok(Self::Metric),
            3 => Ok(Self::Imperial),
            _ => Err("preset must be 0..3"),
        }
    }
}
pub fn canonical(unit: &str) -> String {
    let unit = unit.trim().to_lowercase();
    match unit.as_str() {
        "gpm" => "gal/min",
        "m³/min" => "m3/min",
        "ppg" => "lb/gal",
        "g/cm3" => "g/cc",
        "kg/m³" => "kg/m3",
        "m³" => "m3",
        "degf" | "f" => "°f",
        "degc" | "c" => "°c",
        "psia" => "psi",
        _ => &unit,
    }
    .into()
}
pub fn group_of(unit: &str) -> Group {
    match canonical(unit).as_str() {
        "bbl/min" | "gal/min" | "m3/min" => Group::Flow,
        "°f" | "°c" => Group::Temperature,
        "lb/gal" | "g/cc" | "kg/m3" => Group::Density,
        "bbl" | "gal" | "m3" => Group::Volume,
        "ft" | "m" => Group::Level,
        "psi" | "bar" | "kpa" | "mpa" => Group::Pressure,
        _ => Group::None,
    }
}
fn factor(unit: &str) -> f64 {
    match unit {
        "gal/min" | "gal" => 42.0,
        "m3/min" | "m3" => 0.158987,
        "g/cc" => 0.119826,
        "kg/m3" => 119.826,
        "m" => 0.3048,
        "bar" => 0.0689476,
        "kpa" => 6.89476,
        "mpa" => 0.00689476,
        _ => 1.0,
    }
}
pub fn convert(value: f64, from: &str, to: &str) -> f64 {
    let from = canonical(from);
    let to = canonical(to);
    let group = group_of(&from);
    if group == Group::None || group_of(&to) != group || from == to {
        return value;
    }
    if group == Group::Temperature {
        if from == "°f" {
            (value - 32.0) * 5.0 / 9.0
        } else {
            value * 9.0 / 5.0 + 32.0
        }
    } else {
        value * factor(&to) / factor(&from)
    }
}
pub fn display_unit(group: Group, preset: Preset) -> &'static str {
    match group {
        Group::None => "",
        Group::Flow => match preset {
            Preset::FirmwareDefault => "gpm",
            Preset::Metric => "m³/min",
            _ => "bbl/min",
        },
        Group::Temperature => match preset {
            Preset::LanNative | Preset::Imperial => "°F",
            _ => "°C",
        },
        Group::Density => match preset {
            Preset::LanNative => "lb/gal",
            Preset::Metric => "g/cc",
            _ => "ppg",
        },
        Group::Volume => {
            if preset == Preset::Metric {
                "m³"
            } else {
                "bbl"
            }
        }
        Group::Level => {
            if preset == Preset::Metric {
                "m"
            } else {
                "ft"
            }
        }
        Group::Pressure => {
            if preset == Preset::Metric {
                "bar"
            } else {
                "psi"
            }
        }
    }
}
pub fn units_of(group: Group) -> &'static [&'static str] {
    match group {
        Group::None => &[],
        Group::Flow => &["bbl/min", "gpm", "gal/min", "m³/min"],
        Group::Temperature => &["°F", "°C"],
        Group::Density => &["ppg", "lb/gal", "g/cc", "kg/m³"],
        Group::Volume => &["bbl", "gal", "m³"],
        Group::Level => &["ft", "m"],
        Group::Pressure => &["psi", "bar", "kPa"],
    }
}
#[derive(Debug, Default, Clone)]
pub struct Settings {
    pub preset: Preset,
    pub overrides: std::collections::BTreeMap<Group, String>,
}
impl Settings {
    pub fn target_unit(&self, group: Group) -> &str {
        if group == Group::None {
            return "";
        }
        self.overrides
            .get(&group)
            .filter(|x| !x.trim().is_empty())
            .map(String::as_str)
            .unwrap_or_else(|| display_unit(group, self.preset))
    }
    pub fn to_target(&self, value: f64, native: &str) -> f64 {
        convert(value, native, self.target_unit(group_of(native)))
    }
    pub fn to_wire(&self, value: f64, native: &str) -> f64 {
        convert(value, self.target_unit(group_of(native)), native)
    }
    pub fn needs_conversion(&self, native: &str) -> bool {
        let group = group_of(native);
        let unit = canonical(native);
        if group == Group::None || unit == canonical(self.target_unit(group)) {
            return false;
        }
        self.preset != Preset::FirmwareDefault
            || !matches!(
                unit.as_str(),
                "gal/min" | "°c" | "lb/gal" | "g/cc" | "bbl" | "ft" | "psi"
            )
    }
}
