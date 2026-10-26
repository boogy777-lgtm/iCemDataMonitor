use serde_json::{Value, json};
use std::path::PathBuf;
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
pub const A: u32 = 0x13120203;
pub const B: u32 = 0x13120206;
pub const C: u32 = 0x13120200;
pub struct Case {
    pub label: String,
    pub legacy: String,
    pub firmware: String,
    pub variables: String,
    pub firmware_variables: String,
    pub expected_vectors: Vec<(u32, &'static str)>,
    pub expected_a: [f32; 3],
    pub expected_b: f32,
}
fn vector(sv: u32, name: &str, signals: &[&str]) -> Value {
    json!({"svNumber":sv,"name":name,"signals":signals.iter().enumerate().map(|(i,name)|json!({"byteOffset":4+i*4,"size":4,"valueType":"FF","name":name})).collect::<Vec<_>>()})
}
fn variable(name: &str, base: f64) -> Value {
    json!({"name":name,"display":name,"simulation":{"base":base}})
}
fn normal(label: &str) -> Case {
    Case {
        label: label.into(),
        legacy: json!({"serviceVectors":[vector(A,"primary", &["prefix","middle","tail"])]}).to_string(),
        firmware: json!({"serviceVectors":[vector(B,"firmware", &["firmware"])]}).to_string(),
        variables: json!({"variables":[variable("prefix",1.0),variable("middle",2.0),variable("tail",3.0)]}).to_string(),
        firmware_variables: json!({"variables":[variable("firmware",90.0)]}).to_string(),
        expected_vectors: vec![(A,"primary"),(B,"firmware")], expected_a: [1.0,2.0,3.0], expected_b: 90.0,
    }
}
pub fn cases() -> Vec<Case> {
    let mut cases = vec![normal("valid")];
    for (label, bad) in [
        ("registry-missing-name", json!({"svNumber":C})),
        (
            "registry-bad-signal",
            json!({"svNumber":C,"name":"bad","signals":[{"byteOffset":4,"size":4,"valueType":"FF","name":"prefix"},{"byteOffset":8,"size":"bad","valueType":"FF","name":"middle"}]}),
        ),
        // Even an already-present SV is fully parsed before duplicate suppression.
        (
            "registry-malformed-duplicate",
            json!({"svNumber":A,"name":"bad-duplicate","signals":null}),
        ),
    ] {
        let mut c = normal(label);
        c.legacy = json!({"serviceVectors":[vector(A,"primary", &["prefix","middle","tail"]),bad,vector(C,"ignored-tail", &[])]}).to_string();
        cases.push(c);
    }
    let mut c = normal("registry-syntax");
    c.legacy = "{broken".into();
    c.expected_vectors = vec![(B, "firmware")];
    c.expected_a = [0.0; 3];
    cases.push(c);
    let mut c = normal("registry-duplicate-first-wins");
    c.firmware = json!({"serviceVectors":[vector(A,"replacement", &[]),vector(B,"firmware", &["firmware"])]}).to_string();
    cases.push(c);
    let mut c = normal("firmware-registry-prefix");
    c.firmware = json!({"serviceVectors":[vector(B,"firmware", &["firmware"]),{"svNumber":C},vector(C,"ignored-tail", &[])]}).to_string();
    cases.push(c);
    let mut c = normal("firmware-variables-prefix");
    c.firmware_variables = json!({"variables":[{"name":"other","display":"other","simulation":{"base":9},"families":{"lan":{"wireName":"prefix"}}},{"name":"broken","display":"broken","excludeFromTelemetry":null},variable("firmware",90.0)]}).to_string();
    c.expected_a = [9.0, 2.0, 3.0];
    c.expected_b = 0.0;
    cases.push(c);
    for (label, key, value) in [
        ("uom-number", "uom", json!(7)),
        ("exclude-null", "excludeFromTelemetry", Value::Null),
        ("exclude-string", "excludeFromTelemetry", json!("false")),
        ("simulation-null", "simulation", Value::Null),
        ("simulation-array", "simulation", json!([])),
        ("simulation-bad-base", "simulation", json!({"base":"bad"})),
        ("families-null", "families", Value::Null),
        ("families-array", "families", json!([])),
        ("lan-null", "families", json!({"lan":null})),
        ("lan-array", "families", json!({"lan":[]})),
        (
            "lan-alias-number",
            "families",
            json!({"lan":{"wireName":"middle","alias":7}}),
        ),
    ] {
        let mut c = normal(label);
        let mut bad = variable("middle", 2.0);
        bad[key] = value;
        c.variables =
            json!({"variables":[variable("prefix",1.0),bad,variable("tail",3.0)]}).to_string();
        c.expected_a = [1.0, 0.0, 0.0];
        cases.push(c);
    }
    let mut c = normal("variables-syntax");
    c.variables = "{broken".into();
    c.expected_a = [0.0; 3];
    cases.push(c);
    let mut c = normal("variables-optional-absent-null-string");
    c.variables = json!({"variables":[{"name":"prefix","display":"prefix","uom":null,"excludeFromTelemetry":false,"stringValue":null},variable("middle",2.0),variable("tail",3.0)]}).to_string();
    c.expected_a = [0.0, 2.0, 3.0];
    cases.push(c);
    let mut c = normal("lookup-duplicate-priority");
    c.variables = json!({"variables":[variable("prefix",1.0),variable("middle",2.0),variable("tail",3.0),variable("prefix",8.0)]}).to_string();
    c.firmware_variables = json!({"variables":[{"name":"other","display":"other","simulation":{"base":9},"families":{"lan":{"wireName":"prefix","alias":"middle"}}},variable("firmware",90.0)]}).to_string();
    c.expected_a = [9.0, 9.0, 3.0];
    cases.push(c);
    cases
}
pub struct Stage {
    pub root: PathBuf,
}
impl Stage {
    pub fn new() -> Result<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("icem-lan-loading-{}-{nonce}", std::process::id()));
        std::fs::create_dir_all(&root)?;
        Ok(Self { root })
    }
    pub fn write(&self, case: &Case) -> Result<PathBuf> {
        let path = self.root.join(&case.label);
        std::fs::create_dir_all(&path)?;
        for (name, text) in [
            ("lantable_registry.json", &case.legacy),
            ("firmware_registry.json", &case.firmware),
            ("variable_registry.json", &case.variables),
            ("firmware_variables.json", &case.firmware_variables),
        ] {
            std::fs::write(path.join(name), text)?;
        }
        Ok(path)
    }
}
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}
