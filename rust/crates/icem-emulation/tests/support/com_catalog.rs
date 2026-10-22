use serde_json::{Value, json};
use std::path::PathBuf;
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
pub struct Case {
    pub label: String,
    pub registry: Option<String>,
    pub override_registry: Option<String>,
    pub profiles: Vec<(String, String)>,
    pub expected: Vec<(&'static str, Vec<(&'static str, f64)>)>,
    // registry failure, missing registry, template failure, missing reference, fallback
    pub warnings: [usize; 5],
}
fn entry() -> Value {
    json!({"name":"p","display":"Pressure","uom":"psi","simulation":{"base":7},"families":{"flecs":{"alias":"Alias","index":2}}})
}
fn inline() -> Value {
    json!({"name":"Inline","index":3,"base":11,"uom":"psi","excludeFromTelemetry":false})
}
fn reference() -> Value {
    json!({"registry":"p","index":1})
}
fn profile(name: &str, variables: Vec<Value>) -> (String, String) {
    (
        format!("{name}.json"),
        json!({"name":name,"deviceType":"FLECS","autoSelectIndices":[1,3],"variables":variables})
            .to_string(),
    )
}
fn normal(label: &str) -> Case {
    Case {
        label: label.into(),
        registry: Some(json!({"variables":[entry()]}).to_string()),
        override_registry: None,
        profiles: vec![
            profile("inline", vec![inline()]),
            profile("ref", vec![reference()]),
            profile("mixed", vec![inline(), reference()]),
        ],
        expected: vec![
            ("inline", vec![("Inline", 11.0)]),
            ("ref", vec![("Alias", 7.0)]),
            ("mixed", vec![("Alias", 7.0), ("Inline", 11.0)]),
        ],
        warnings: [0; 5],
    }
}
fn empty_registry(case: &mut Case) {
    case.expected = vec![
        ("inline", vec![("Inline", 11.0)]),
        ("ref", vec![]),
        ("mixed", vec![("Inline", 11.0)]),
    ];
    case.warnings[3] = 2;
}
pub fn cases() -> Vec<Case> {
    let mut cases = vec![normal("valid")];
    let mut c = normal("registry-syntax");
    c.registry = Some("{broken".into());
    empty_registry(&mut c);
    c.warnings[0] = 1;
    cases.push(c);
    for (label, root) in [
        ("registry-scalar-root", json!(7)),
        ("registry-array-root", json!([])),
        ("registry-variables-scalar", json!({"variables":7})),
        (
            "registry-scalar-record",
            json!({"variables":[entry(),7,entry()]}),
        ),
        (
            "registry-families-scalar",
            json!({"variables":[entry(),{"families":7},entry()]}),
        ),
        (
            "registry-family-array",
            json!({"variables":[entry(),{"families":{"plc":[]}},entry()]}),
        ),
        (
            "registry-simulation-scalar",
            json!({"variables":[entry(),{"simulation":7},entry()]}),
        ),
        (
            "registry-numeric-null",
            json!({"variables":[entry(),{"simulation":{"base":null}},entry()]}),
        ),
        (
            "registry-bool-null",
            json!({"variables":[entry(),{"excludeFromTelemetry":null},entry()]}),
        ),
    ] {
        let mut c = normal(label);
        c.registry = Some(root.to_string());
        empty_registry(&mut c);
        c.warnings[0] = 1;
        cases.push(c);
    }
    for (label, root) in [
        ("registry-null-root", Value::Null),
        ("registry-variables-missing", json!({})),
        ("registry-variables-null", json!({"variables":null})),
    ] {
        let mut c = normal(label);
        c.registry = Some(root.to_string());
        empty_registry(&mut c);
        cases.push(c);
    }
    let mut c = normal("registry-missing-file");
    c.registry = None;
    empty_registry(&mut c);
    c.warnings[1] = 1;
    cases.push(c);
    let mut c = normal("null-record-before-match");
    c.registry = Some(json!({"variables":[null,entry()]}).to_string());
    empty_registry(&mut c);
    c.warnings[0] = 1;
    cases.push(c);
    let mut c = normal("null-record-after-match");
    c.registry = Some(json!({"variables":[entry(),null]}).to_string());
    empty_registry(&mut c);
    c.warnings[0] = 1;
    cases.push(c);
    let mut c = normal("null-record-unmatched-resolution");
    c.registry = Some(json!({"variables":[entry(),null]}).to_string());
    c.profiles
        .push(profile("unmatched", vec![json!({"registry":"absent"})]));
    empty_registry(&mut c);
    c.expected.push(("unmatched", vec![]));
    c.warnings[0] = 1;
    c.warnings[3] = 3;
    cases.push(c);
    let mut c = normal("malformed-template-local");
    c.profiles
        .insert(1, profile("bad", vec![inline(), json!({"index":"bad"})]));
    c.warnings[2] = 1;
    cases.push(c);
    let mut c = normal("missing-reference-only-empty-template");
    c.registry = Some(json!({"variables":[]}).to_string());
    c.profiles = vec![profile("empty", vec![reference()])];
    c.expected = vec![("empty", vec![])];
    c.warnings[3] = 1;
    cases.push(c);
    let mut c = normal("zero-templates-fallback");
    c.profiles = vec![("bad.json".into(), "{broken".into())];
    c.expected = vec![("flecs 4.53", vec![])];
    c.warnings[2] = 1;
    c.warnings[4] = 1;
    cases.push(c);
    let mut c = normal("malformed-override-no-main-fallback");
    c.override_registry = Some(json!({"variables":[{"families":7}]}).to_string());
    empty_registry(&mut c);
    c.warnings[0] = 1;
    cases.push(c);
    let mut c = normal("nullable-simulation-families");
    c.registry = Some(
        json!({"variables":[{"name":"p","display":"Pressure","simulation":null,"families":null}]})
            .to_string(),
    );
    c.expected = vec![
        ("inline", vec![("Inline", 11.0)]),
        ("ref", vec![("Pressure", 0.0)]),
        ("mixed", vec![("Pressure", 0.0), ("Inline", 11.0)]),
    ];
    cases.push(c);
    let mut c = normal("nullable-family-and-missing-simulation");
    c.registry = Some(
        json!({"variables":[{"name":"p","display":"Pressure","families":{"flecs":null}}]})
            .to_string(),
    );
    c.expected = vec![
        ("inline", vec![("Inline", 11.0)]),
        ("ref", vec![("Pressure", 0.0)]),
        ("mixed", vec![("Pressure", 0.0), ("Inline", 11.0)]),
    ];
    cases.push(c);
    for (label, variables) in [
        ("template-variables-null", Some(Value::Null)),
        ("template-variables-missing", None),
    ] {
        let mut c = normal(label);
        let mut template = json!({"name":"empty","deviceType":"FLECS"});
        if let Some(variables) = variables {
            template["variables"] = variables;
        }
        c.profiles = vec![("empty.json".into(), template.to_string())];
        c.expected = vec![("empty", vec![])];
        cases.push(c);
    }
    let mut c = normal("missing-families-and-simulation");
    c.registry = Some(json!({"variables":[{"name":"p","display":"Pressure"}]}).to_string());
    c.expected = vec![
        ("inline", vec![("Inline", 11.0)]),
        ("ref", vec![("Pressure", 0.0)]),
        ("mixed", vec![("Pressure", 0.0), ("Inline", 11.0)]),
    ];
    cases.push(c);
    let mut c = normal("dto-case-and-unknown-snake");
    c.registry=Some(json!({"Variables":[{"Name":"p","Display":"Pressure","Simulation":{"Base":7},"Families":{"FLECS":{"Alias":"Alias","Index":2}},"exclude_from_telemetry":"ignored"}]}).to_string());
    cases.push(c);
    let mut c = normal("reference-exact-property-case");
    c.profiles = vec![
        profile(
            "inline",
            vec![json!({"Registry":7,"Name":"Inline","Base":11,"index":3})],
        ),
        profile(
            "ref",
            vec![json!({"registry":"p","Index":"ignored","base":"ignored"})],
        ),
    ];
    c.expected = vec![
        ("inline", vec![("Inline", 11.0)]),
        ("ref", vec![("Alias", 7.0)]),
    ];
    cases.push(c);
    let mut c = normal("autoselect-deserialize-before-resolution");
    c.profiles.push(("bad.json".into(),json!({"name":"bad","deviceType":"FLECS","autoSelectIndices":[null],"variables":[{"registry":"missing"}]}).to_string()));
    c.warnings[2] = 1;
    cases.push(c);
    cases
}
pub struct Stage {
    pub root: PathBuf,
    pub input: PathBuf,
}
impl Stage {
    pub fn new() -> Result<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("icem-com-catalog-{}-{nonce}", std::process::id()));
        let input = root.join("input");
        std::fs::create_dir_all(&input)?;
        Ok(Self { root, input })
    }
    pub fn write(&self, case: &Case) -> Result<PathBuf> {
        let path = self.input.join(&case.label);
        std::fs::create_dir_all(&path)?;
        if let Some(registry) = &case.registry {
            std::fs::write(path.join("variable_registry.json"), registry)?;
        }
        if let Some(registry) = &case.override_registry {
            std::fs::write(path.join("variable_registry.emulator.json"), registry)?;
        }
        for (name, text) in &case.profiles {
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
pub fn warning_counts(warnings: &[String]) -> [usize; 5] {
    let mut counts = [0; 5];
    for warning in warnings {
        if warning.starts_with("registry:") {
            counts[0] += 1;
        } else if warning.starts_with("variable_registry.json not found") {
            counts[1] += 1;
        } else if warning.starts_with("template ") {
            counts[2] += 1;
        } else if warning.starts_with("registry variable '") {
            counts[3] += 1;
        } else if warning.starts_with("zero templates:") {
            counts[4] += 1;
        } else {
            panic!("unclassified catalog warning: {warning}");
        }
    }
    counts
}
