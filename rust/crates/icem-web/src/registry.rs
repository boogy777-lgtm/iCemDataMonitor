//! Existing v1 registry -> legacy REST display names; never invent sensor labels.
use std::{collections::BTreeMap, path::Path};
pub fn load_names(path: &Path) -> Result<BTreeMap<u16, String>, Box<dyn std::error::Error>> {
    let text = std::fs::read_to_string(path)?;
    let root: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}'))?;
    if root["version"] != 1 {
        return Err("unsupported variable registry version".into());
    }
    let mut result = BTreeMap::new();
    let variables = root["variables"]
        .as_array()
        .ok_or("missing variables array")?;
    for family in ["flecs", "lan"] {
        let mut entries = Vec::new();
        for variable in variables {
            let info = &variable["families"][family];
            if info.is_null() {
                continue;
            }
            let index = info["index"]
                .as_i64()
                .ok_or("registry index must be integer")?;
            let zone = info["zone"]
                .as_str()
                .map(|x| x.trim().to_ascii_lowercase())
                .unwrap_or_else(|| if family == "flecs" { "com" } else { "lan" }.into());
            let addr = match zone.as_str() {
                "com" => index
                    .checked_add(6)
                    .and_then(|x| x.checked_mul(2))
                    .and_then(|x| x.checked_add(4))
                    .filter(|&x| (4..200).contains(&x)),
                "lan" if (0..96).contains(&index) => {
                    index.checked_mul(2).and_then(|x| x.checked_add(200))
                }
                _ => None,
            };
            if let Some(addr) = addr {
                entries.push((
                    index,
                    addr,
                    variable["name"]
                        .as_str()
                        .ok_or("missing variable name")?
                        .to_owned(),
                ));
            }
        }
        entries.sort_by_key(|x| x.0);
        for (_, addr, name) in entries {
            if let Ok(addr) = u16::try_from(addr) {
                result.insert(addr, name);
            }
        }
    }
    Ok(result)
}
