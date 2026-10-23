use serde_json::{Value, json};
use std::path::{Path, PathBuf};
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
// Recorded from actual StringComparer.OrdinalIgnoreCase, not Unicode full folding.
// The Windows differential rechecks every expectation against BCL and originals.
pub const PAIRS: [(&str, &str, bool); 13] = [
    ("Давление", "ДАВЛЕНИЕ", true),
    ("débit", "DÉBIT", true),
    ("𐐨", "𐐀", true),
    ("σ", "ς", true),
    ("σ", "Σ", true),
    ("i", "I", true),
    ("ı", "I", false),
    ("İ", "i", false),
    ("ſ", "S", false),
    ("ß", "SS", false),
    ("ß", "ẞ", false),
    ("é", "e\u{301}", false),
    ("Давление", "Давлениe", false),
];
pub const SV: u32 = 0x13120203;
pub fn registry(name: &str, display: &str, base: f64) -> Value {
    json!({"variables":[{"name":name,"display":display,"uom":"psi","simulation":{"base":base},"families":{"flecs":{"index":2,"alias":name},"lan":{"wireName":name}}}]})
}
pub fn profile(name: &str, query: &str) -> Value {
    json!({"name":name,"deviceType":"FLECS","variables":[{"registry":query},{"index":3,"name":"Marker","base":7,"format":"int"}]})
}
pub fn vector(names: &[&str]) -> Value {
    json!({"serviceVectors":[{"svNumber":SV,"name":"identity-vector","signals":names.iter().enumerate().map(|(i,name)|json!({"byteOffset":4+i*4,"size":4,"valueType":"FF","name":name})).collect::<Vec<_>>()}]})
}
pub struct Stage(pub PathBuf);
impl Stage {
    pub fn new() -> Result<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos();
        let path =
            std::env::temp_dir().join(format!("icem-unicode-{}-{nonce}", std::process::id()));
        std::fs::create_dir_all(&path)?;
        Ok(Self(path))
    }
    pub fn directory(&self, name: &str) -> Result<PathBuf> {
        let path = self.0.join(name);
        std::fs::create_dir_all(&path)?;
        Ok(path)
    }
}
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
pub fn write(path: &Path, value: &Value) -> Result {
    std::fs::write(path, serde_json::to_vec(value)?)?;
    Ok(())
}
// wireName/alias overwrite. Name/display only AddIfAbsent, including firmware.
pub fn precedence_files(directory: &Path) -> Result<Vec<&'static str>> {
    let primary = json!({"variables":[
        {"name":"base-name","display":"base-display","simulation":{"base":11},"families":{"lan":{"wireName":"Давление","alias":"Débit"}}},
        {"name":"ДАВЛЕНИЕ","display":"DÉBIT","simulation":{"base":22}},
        {"name":"Имя","display":"Показ","simulation":{"base":33}},
        {"name":"ИМЯ","display":"ПОКАЗ","simulation":{"base":44}},
        {"name":"supp","display":"supp-display","simulation":{"base":55},"families":{"lan":{"wireName":"𐐨"}}}
    ]});
    let firmware = json!({"variables":[
        {"name":"ДАВЛЕНИЕ","display":"DÉBIT","simulation":{"base":66}},
        {"name":"firmware","display":"firm-display","simulation":{"base":77},"families":{"lan":{"wireName":"давление","alias":"débit"}}},
        {"name":"имя","display":"показ","simulation":{"base":88}},
        {"name":"supp-firmware","display":"supp-firmware-display","simulation":{"base":99},"families":{"lan":{"wireName":"𐐀"}}}
    ]});
    let names = vec![
        "ДАВЛЕНИЕ",
        "DÉBIT",
        "имя",
        "показ",
        "𐐨",
        "BASE-NAME",
        "BASE-DISPLAY",
    ];
    write(&directory.join("variable_registry.json"), &primary)?;
    write(&directory.join("firmware_variables.json"), &firmware)?;
    write(&directory.join("lantable_registry.json"), &vector(&names))?;
    write(
        &directory.join("firmware_registry.json"),
        &json!({"serviceVectors":[]}),
    )?;
    Ok(names)
}
