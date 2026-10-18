//! Closed config.ini schema, matching IniConfig/IniOptions in the pinned .NET tree.
use std::collections::BTreeMap;
use std::fmt;
use std::path::Path;
use std::str::FromStr;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConfigError {
    pub violations: Vec<String>,
}
impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "config.ini invalid — {} violation(s):\n  - {}",
            self.violations.len(),
            self.violations.join("\n  - ")
        )
    }
}
impl std::error::Error for ConfigError {}
#[derive(Clone, Copy)]
enum Rule {
    Flag,
    Port,
    Tick,
    Total,
    Base,
    Length,
    Text,
}
struct Key {
    section: &'static str,
    name: &'static str,
    optional: bool,
}

// Confirmed class: 16 closed INI fields. One row generates field/default/parse/render
// and validation; the next key has no independent dispatch/completeness edit points.
macro_rules! ini_fields {
    ($($field:ident: $ty:ty = $default:expr => ($section:literal, $rule:ident, $optional:literal)),* $(,)?) => {
        #[derive(Debug, Clone, PartialEq, Eq)]
        pub struct IniOptions { $(pub $field: $ty),* }
        impl Default for IniOptions { fn default() -> Self { Self { $($field: $default),* } } }
        const KEYS: &[Key] = &[$(Key { section: $section, name: stringify!($field), optional: $optional }),*];
        impl IniOptions {
            fn from_values(values: &BTreeMap<String, String>, errors: &mut Vec<String>) -> Self {
                let defaults = Self::default();
                Self { $($field: value::<$ty>(values, $section, stringify!($field), defaults.$field, errors)),* }
            }
            fn entries(&self) -> Vec<(&'static str, &'static str, String, Rule)> {
                vec![$(($section, stringify!($field), self.$field.to_string(), Rule::$rule)),*]
            }
        }
    }
}
ini_fields! {
    modbus_on: i32 = 1 => ("station", Flag, false),
    web_on: i32 = 1 => ("station", Flag, true),
    opcda_on: i32 = 0 => ("station", Flag, false),
    lansniffer_on: i32 = 1 => ("station", Flag, false),
    ui_set_as_default: i32 = 1 => ("station", Flag, true),
    web_remote_on: i32 = 0 => ("station", Flag, true),
    modbus_port: i32 = 502 => ("station", Port, false),
    modbus_tick_ms: i32 = 500 => ("station", Tick, false),
    web_port: i32 = 8090 => ("station", Port, false),
    mmf_name: String = r"Global\iCemDataMonitorBus_v1".into() => ("station", Text, false),
    mmf_path: String = r"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat".into() => ("station", Text, false),
    wits_base: i32 = 600 => ("zones", Base, false),
    wits_len: i32 = 0 => ("zones", Length, false),
    lan_ext_base: i32 = 650 => ("zones", Base, false),
    lan_ext_len: i32 = 0 => ("zones", Length, false),
    total: i32 = 10_001 => ("zones", Total, false),
}
const RETIRED: &[&str] = &[
    "station/emule_on",
    "station/com_port",
    "zones/com_base",
    "zones/com_len",
    "zones/lan_base",
    "zones/lan_len",
    "zones/raw_base",
    "zones/raw_len",
];

impl IniOptions {
    pub fn parse(text: &str) -> Result<Self, ConfigError> {
        let mut errors = Vec::new();
        let mut values = BTreeMap::new();
        let mut section = String::new();
        let text = text
            .trim_start_matches('\u{feff}')
            .replace("\r\n", "\n")
            .replace('\r', "\n");
        for (index, raw) in text.lines().enumerate() {
            let line_no = index + 1;
            let line = raw.trim();
            if line.is_empty() || line.starts_with([';', '#']) {
                continue;
            }
            if line.starts_with('[') {
                if let Some(close) = line.find(']') {
                    section = line[1..close].trim().to_ascii_lowercase();
                } else {
                    errors.push(format!("line {line_no}: malformed section header '{line}'"));
                    section.clear();
                }
                continue;
            }
            let Some((key, raw_value)) = line
                .split_once('=')
                .filter(|(key, _)| !key.trim().is_empty())
            else {
                errors.push(format!(
                    "line {line_no}: malformed line '{line}' (expected key=value)"
                ));
                continue;
            };
            let key = key.trim().to_ascii_lowercase();
            let raw_value = raw_value.trim();
            if section.is_empty() {
                errors.push(format!("line {line_no}: key '{key}' outside any section"));
                continue;
            }
            if !KEYS.iter().any(|entry| entry.section == section) {
                errors.push(format!("line {line_no}: unknown section [{section}]"));
                continue;
            }
            let full = format!("{section}/{key}");
            if !KEYS
                .iter()
                .any(|entry| entry.section == section && entry.name == key)
            {
                if !RETIRED.contains(&full.as_str()) {
                    errors.push(format!(
                        "line {line_no}: unknown key '{key}' in [{section}]"
                    ));
                }
                continue;
            }
            if values.insert(full, raw_value.to_owned()).is_some() {
                errors.push(format!(
                    "line {line_no}: duplicate key '{key}' in [{section}]"
                ));
            }
        }
        for key in KEYS {
            if !key.optional && !values.contains_key(&format!("{}/{}", key.section, key.name)) {
                errors.push(format!("missing key '{}' in [{}]", key.name, key.section));
            }
        }
        let options = Self::from_values(&values, &mut errors);
        // Same legacy precedence: contract validation follows only a syntax-complete parse.
        if errors.is_empty() {
            errors.extend(options.validate());
        }
        if errors.is_empty() {
            Ok(options)
        } else {
            Err(ConfigError { violations: errors })
        }
    }
    /// Explicit bounded read; invalid or unreadable files are never replaced by defaults.
    pub fn read_existing(path: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        Ok(Self::parse(&decode_text(&read_bytes(path)?))?)
    }
    pub fn render(&self) -> String {
        let mut result = String::new();
        let mut section = "";
        for (next, key, value, _) in self.entries() {
            if next != section {
                result.push_str(&format!("\n[{next}]\n"));
                section = next;
            }
            result.push_str(&format!("{key}={value}\n"));
        }
        result
    }
    pub fn validate(&self) -> Vec<String> {
        let mut errors = Vec::new();
        for (section, key, raw, rule) in self.entries() {
            if matches!(rule, Rule::Text) {
                if raw.trim().is_empty() {
                    errors.push(format!("{key} must be non-empty"));
                }
                continue;
            }
            let Ok(number) = raw.parse::<i32>() else {
                errors.push(format!("[{section}] {key}: not an integer"));
                continue;
            };
            if matches!(rule, Rule::Length) {
                if number < 0 {
                    errors.push(format!("[{section}] {key}: must be non-negative"));
                }
                if number % 2 != 0 {
                    errors.push(format!("[{section}] {key}: must be even"));
                }
                continue;
            }
            let valid = match rule {
                Rule::Flag => matches!(number, 0 | 1),
                Rule::Port => (1..=65535).contains(&number),
                Rule::Tick => (200..=500).contains(&number),
                Rule::Total => (650..=65535).contains(&number),
                Rule::Base => number >= 4,
                Rule::Length => number >= 0 && number % 2 == 0,
                Rule::Text => true,
            };
            if !valid {
                errors.push(format!("[{section}] {key}: invalid value {number}"));
            }
        }
        let zones = [
            ("wits", i64::from(self.wits_base), i64::from(self.wits_len)),
            (
                "lan_ext",
                i64::from(self.lan_ext_base),
                i64::from(self.lan_ext_len),
            ),
        ];
        for (name, start, length) in zones {
            if start >= 0
                && length >= 0
                && (start >= i64::from(self.total)
                    || (length > 0 && start + length > i64::from(self.total)))
            {
                errors.push(format!("zones.{name} out of range"));
            }
        }
        let [(_, a, a_len), (_, b, b_len)] = zones;
        // Preserve actual .NET predicate, including zero-length-base overlap edge.
        if a < b + b_len && b < a + a_len {
            errors.push("zone overlap: wits and lan_ext".into());
        }
        errors
    }
}
pub(crate) fn read_bytes(path: &Path) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    use std::io::Read;
    const LIMIT: u64 = 1_048_576;
    let mut bytes = Vec::new();
    std::fs::File::open(path)?
        .take(LIMIT + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() as u64 > LIMIT {
        return Err("config.ini exceeds 1 MiB".into());
    }
    Ok(bytes)
}
fn value<T: FromStr>(
    values: &BTreeMap<String, String>,
    section: &str,
    key: &str,
    fallback: T,
    errors: &mut Vec<String>,
) -> T {
    match values.get(&format!("{section}/{key}")) {
        None => fallback,
        Some(raw) => match raw.parse() {
            Ok(value) => value,
            Err(_) => {
                errors.push(format!("[{section}] {key}: not an integer: '{raw}'"));
                fallback
            }
        },
    }
}
fn decode_text(bytes: &[u8]) -> String {
    // File.ReadAllText BOM detection, including UTF-16/32 operator-edited files.
    let (body, width, little) = if bytes.starts_with(&[255, 254, 0, 0]) {
        (&bytes[4..], 4, true)
    } else if bytes.starts_with(&[0, 0, 254, 255]) {
        (&bytes[4..], 4, false)
    } else if bytes.starts_with(&[255, 254]) {
        (&bytes[2..], 2, true)
    } else if bytes.starts_with(&[254, 255]) {
        (&bytes[2..], 2, false)
    } else {
        return String::from_utf8_lossy(bytes.strip_prefix(&[239, 187, 191]).unwrap_or(bytes))
            .into_owned();
    };
    if width == 2 {
        let (pairs, tail) = body.as_chunks::<2>();
        let words: Vec<u16> = pairs
            .iter()
            .map(|pair| {
                if little {
                    u16::from_le_bytes(*pair)
                } else {
                    u16::from_be_bytes(*pair)
                }
            })
            .collect();
        let mut text = String::from_utf16_lossy(&words);
        if !tail.is_empty() {
            text.push(char::REPLACEMENT_CHARACTER);
        }
        text
    } else {
        let (groups, tail) = body.as_chunks::<4>();
        let mut text: String = groups
            .iter()
            .map(|group| {
                char::from_u32(if little {
                    u32::from_le_bytes(*group)
                } else {
                    u32::from_be_bytes(*group)
                })
                .unwrap_or(char::REPLACEMENT_CHARACTER)
            })
            .collect();
        if !tail.is_empty() {
            text.push(char::REPLACEMENT_CHARACTER);
        }
        text
    }
}
#[cfg(test)]
mod tests;
