//! Source text is the fixture: never round-trip these documents through Value.
use icem_emulation::simulation::{Template, Variable};
use std::path::{Path, PathBuf};
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
const PROFILE: &str = r#"{"name":"Профиль 😀","deviceType":"FLECS","autoSelectIndices":[2],"variables":[{"index":2,"name":"Давление","legacyName":"Alias","uom":"psi","base":7,"amplitude":0,"frequency":0,"format":"int","togglePeriod":0,"excludeFromTelemetry":false%INLINE%}]%ROOT%}"#;
const REGISTRY: &str = r#"{"variables":[{"name":"p","display":"Давление","uom":"psi","excludeFromTelemetry":false,"simulation":{"base":7,"amplitude":0,"frequency":0,"format":"int","togglePeriod":0%SIM%},"families":{"flecs":{"alias":"Alias","index":2,"wireName":"Wire"%FAMILY%}%FAMILIES%}%VAR%}]%REGISTRY%}"#;
const REFERENCE: &str = r#"{"name":"Профиль 😀","deviceType":"FLECS","autoSelectIndices":[2],"variables":[{"registry":"p","index":9}]%ROOT%}"#;
fn baseline(reference: bool) -> Template {
    Template {
        name: "Профиль 😀".into(),
        device_type: "FLECS".into(),
        auto_select: vec![2],
        variables: vec![Variable {
            index: 2,
            name: if reference {
                "Alias"
            } else {
                "Давление"
            }
            .into(),
            legacy_name: "Alias".into(),
            unit: "psi".into(),
            base: 7.0,
            format: "int".into(),
            ..Variable::default()
        }],
    }
}
fn finish(raw: &str) -> String {
    [
        "%INLINE%",
        "%ROOT%",
        "%SIM%",
        "%FAMILY%",
        "%FAMILIES%",
        "%VAR%",
        "%REGISTRY%",
    ]
    .iter()
    .fold(raw.to_string(), |s, marker| s.replace(marker, ""))
}
pub struct Case {
    pub label: String,
    pub template: String,
    pub registry: String,
    pub expected: Template,
}
fn pairs(
    cases: &mut Vec<Case>,
    scope: &str,
    field: &str,
    values: (&str, &str),
    expected: (Template, Template),
    reference: bool,
) {
    let (a, b) = values;
    let (expected_a, expected_b) = expected;
    for collision in [false, true] {
        for reverse in [false, true] {
            let second = if collision {
                field.to_ascii_uppercase()
            } else {
                field.to_string()
            };
            let (first_value, last_value, expected) = if reverse {
                (b, a, expected_a.clone())
            } else {
                (a, b, expected_b.clone())
            };
            let pair = format!(",\"{field}\":{first_value},\"{second}\":{last_value}");
            let profile = if reference { REFERENCE } else { PROFILE };
            cases.push(Case {
                label: format!(
                    "{}-{field}-{}-{}",
                    scope.trim_matches('%'),
                    if collision { "case" } else { "exact" },
                    if reverse { "ba" } else { "ab" }
                ),
                template: finish(&profile.replace(scope, &pair)),
                registry: finish(&REGISTRY.replace(scope, &pair)),
                expected,
            });
        }
    }
}
pub fn cases() -> Result<Vec<Case>> {
    let mut cases = Vec::new();
    for reference in [false, true] {
        let base = baseline(reference);
        for (field, a, b, left, right) in [
            (
                "name",
                r#""A""#,
                r#""Б 😀""#,
                {
                    let mut t = base.clone();
                    t.name = "A".into();
                    t
                },
                {
                    let mut t = base.clone();
                    t.name = "Б 😀".into();
                    t
                },
            ),
            ("deviceType", r#""FLECS""#, r#""PLC""#, base.clone(), {
                let mut t = base.clone();
                t.device_type = "PLC".into();
                if reference {
                    t.variables[0].name = "Давление".into();
                    t.variables[0].legacy_name = "Давление".into();
                    t.variables[0].index = 9;
                }
                t
            }),
            (
                "autoSelectIndices",
                "[1,3]",
                "[8]",
                {
                    let mut t = base.clone();
                    t.auto_select = vec![1, 3];
                    t
                },
                {
                    let mut t = base.clone();
                    t.auto_select = vec![8];
                    t
                },
            ),
            (
                "variables",
                "[]",
                r#"[{"index":4,"name":"Новая","base":11,"format":"int"}]"#,
                {
                    let mut t = base.clone();
                    t.variables.clear();
                    t
                },
                {
                    let mut t = base.clone();
                    t.variables = vec![Variable {
                        index: 4,
                        name: "Новая".into(),
                        legacy_name: "Новая".into(),
                        base: 11.0,
                        format: "int".into(),
                        ..Variable::default()
                    }];
                    t
                },
            ),
        ] {
            let before = cases.len();
            pairs(
                &mut cases,
                "%ROOT%",
                field,
                (a, b),
                (left, right),
                reference,
            );
            for row in &mut cases[before..] {
                row.label
                    .push_str(if reference { "-ref" } else { "-inline" });
            }
        }
    }
    for reference in [false, true] {
        let base = baseline(reference);
        let scope = if reference { "%SIM%" } else { "%INLINE%" };
        for (field, a, b) in [
            ("base", "3", "11"),
            ("amplitude", "0.5", "2"),
            ("frequency", "0.1", "0.25"),
            ("togglePeriod", "2", "4"),
            ("format", r#""0.0""#, r#""int""#),
        ] {
            let mut left = base.clone();
            let mut right = base.clone();
            for (template, value) in [(&mut left, a), (&mut right, b)] {
                let v = &mut template.variables[0];
                match field {
                    "base" => v.base = value.parse()?,
                    "amplitude" => v.amplitude = value.parse()?,
                    "frequency" => v.frequency = value.parse()?,
                    "togglePeriod" => v.toggle_period = value.parse()?,
                    "format" => v.format = value.trim_matches('"').into(),
                    _ => unreachable!(),
                }
            }
            pairs(&mut cases, scope, field, (a, b), (left, right), reference);
        }
        for (field, a, b) in [
            ("name", r#""A""#, r#""Б 😀""#),
            ("legacyName", r#""Old""#, r#""Новая""#),
            ("uom", r#""psi""#, r#""бар""#),
            ("index", "3", "5"),
            ("excludeFromTelemetry", "true", "false"),
        ] {
            if reference && matches!(field, "legacyName" | "index") {
                continue;
            }
            let mut left = base.clone();
            let mut right = base.clone();
            for (template, value) in [(&mut left, a), (&mut right, b)] {
                let v = &mut template.variables[0];
                match field {
                    "name" if reference => {}
                    "name" => v.name = value.trim_matches('"').into(),
                    "legacyName" => v.legacy_name = value.trim_matches('"').into(),
                    "uom" => v.unit = value.trim_matches('"').into(),
                    "index" => v.index = value.parse()?,
                    "excludeFromTelemetry" => v.exclude = value == "true",
                    _ => unreachable!(),
                }
            }
            // Registry name controls lookup: preserve a separate display reference.
            let before = cases.len();
            pairs(
                &mut cases,
                if reference { "%VAR%" } else { "%INLINE%" },
                field,
                (a, b),
                (left, right),
                reference,
            );
            if reference && field == "name" {
                for c in &mut cases[before..] {
                    c.template = c
                        .template
                        .replace(r#""registry":"p""#, r#""registry":"Давление""#);
                }
            }
        }
    }
    // Registry root arrays and nested objects replace complete prior objects.
    for (scope, field, a, b, left, right) in [
        (
            "%REGISTRY%",
            "variables",
            r#"[{"name":"p","display":"Other","simulation":{"base":3}}]"#,
            r#"[{"name":"p","display":"Давление","uom":"psi","simulation":{"base":7,"format":"int"},"families":{"flecs":{"alias":"Alias","index":2}}}]"#,
            {
                let mut t = baseline(true);
                t.variables[0].name = "Other".into();
                t.variables[0].legacy_name = "Other".into();
                t.variables[0].index = 9;
                t.variables[0].base = 3.;
                t.variables[0].unit.clear();
                t.variables[0].format.clear();
                t
            },
            baseline(true),
        ),
        (
            "%VAR%",
            "simulation",
            r#"{"base":3,"amplitude":9,"format":"int"}"#,
            r#"{"base":11}"#,
            {
                let mut t = baseline(true);
                t.variables[0].base = 3.;
                t.variables[0].amplitude = 9.;
                t
            },
            {
                let mut t = baseline(true);
                t.variables[0].base = 11.;
                t.variables[0].format.clear();
                t
            },
        ),
        (
            "%VAR%",
            "families",
            r#"{"flecs":{"alias":"A","index":3}}"#,
            r#"{"plc":{"alias":"B","index":5}}"#,
            {
                let mut t = baseline(true);
                t.variables[0].name = "A".into();
                t.variables[0].legacy_name = "A".into();
                t.variables[0].index = 3;
                t
            },
            {
                let mut t = baseline(true);
                t.variables[0].name = "Давление".into();
                t.variables[0].legacy_name = "Давление".into();
                t.variables[0].index = 9;
                t
            },
        ),
        (
            "%FAMILIES%",
            "flecs",
            r#"{"alias":"A","index":3}"#,
            r#"{"index":5}"#,
            {
                let mut t = baseline(true);
                t.variables[0].name = "A".into();
                t.variables[0].legacy_name = "A".into();
                t.variables[0].index = 3;
                t
            },
            {
                let mut t = baseline(true);
                t.variables[0].name = "Давление".into();
                t.variables[0].legacy_name = "Давление".into();
                t.variables[0].index = 5;
                t
            },
        ),
    ] {
        pairs(&mut cases, scope, field, (a, b), (left, right), true);
    }
    for field in ["alias", "index", "wireName", "display"] {
        let (scope, a, b) = match field {
            "index" => ("%FAMILY%", "3", "5"),
            "display" => ("%VAR%", r#""A""#, r#""Б 😀""#),
            _ => ("%FAMILY%", r#""A""#, r#""Б 😀""#),
        };
        let mut left = baseline(true);
        let mut right = baseline(true);
        for (t, value) in [(&mut left, a), (&mut right, b)] {
            if field == "index" {
                t.variables[0].index = value.parse()?;
            } else if field == "alias" {
                t.variables[0].name = value.trim_matches('"').into();
                t.variables[0].legacy_name = value.trim_matches('"').into();
            }
        }
        pairs(&mut cases, scope, field, (a, b), (left, right), true);
    }
    for (label,template,registry,expected) in [
        ("escaped-property-names", PROFILE.replace("%ROOT%", r#", "na\u006de":"Escaped", "DEVICE\u0054YPE":"FLECS""#), finish(REGISTRY), {let mut t=baseline(false);t.name="Escaped".into();t}),
        ("unknown-nested-and-overflow", PROFILE.replace("%ROOT%", r#", "unknown":{"name":null,"variables":[false,{"simulation":1e400}]},"NAME_":1e400"#).replace("%INLINE%",r#", "unknown":{"index":"bad","base":1e400},"exclude_from_telemetry":"ignored""#), REGISTRY.replace("%FAMILY%",r#", "unknown":{"index":"bad","base":1e400}"#).replace("%SIM%",r#", "unknown":{"base":"bad"}"#),baseline(false)),
        ("exact-reference-upper-unknowns", REFERENCE.replace(r#""registry":"p","index":9"#,r#""Registry":7,"registry":"missing","registry":"p","Index":"ignored","index":3,"index":9,"base":"ignored","NAME":false"#),finish(REGISTRY),baseline(true)),
        ("upper-registry-is-inline", PROFILE.replace("%INLINE%",r#", "Registry":{"nested":1e400}"#),finish(REGISTRY),baseline(false)),
        ("nullable-inline-defaults", PROFILE.replace("%INLINE%",r#", "legacyName":null,"uom":null,"format":null"#),finish(REGISTRY),{let mut t=baseline(false);t.variables[0].legacy_name="Давление".into();t.variables[0].unit.clear();t.variables[0].format.clear();t}),
        ("nullable-registry-objects", finish(REFERENCE), REGISTRY.replace("%VAR%",r#", "simulation":null,"families":null,"uom":null"#),{let mut t=baseline(true);t.variables[0]=Variable {index:9,name:"Давление".into(),legacy_name:"Давление".into(),..Variable::default()};t}),
        ("nullable-template-arrays",PROFILE.replace("%ROOT%",r#", "autoSelectIndices":null,"variables":null"#),finish(REGISTRY),{let mut t=baseline(false);t.auto_select.clear();t.variables.clear();t}),
        ("nullable-family", finish(REFERENCE),REGISTRY.replace("%FAMILIES%",r#", "flecs":null"#),{let mut t=baseline(true);t.variables[0].name="Давление".into();t.variables[0].legacy_name="Давление".into();t.variables[0].index=9;t}),
    ] {cases.push(Case {label:label.into(),template:finish(&template),registry:finish(&registry),expected});}
    for family in ["plc", "lan"] {
        let before = cases.len();
        let mut left = baseline(true);
        left.variables[0].name = "A".into();
        left.variables[0].legacy_name = "A".into();
        left.variables[0].index = 3;
        let mut right = baseline(true);
        right.variables[0].name = "B".into();
        right.variables[0].legacy_name = "B".into();
        right.variables[0].index = 5;
        pairs(
            &mut cases,
            "%FAMILIES%",
            family,
            (r#"{"alias":"A","index":3}"#, r#"{"alias":"B","index":5}"#),
            (left, right),
            true,
        );
        for row in &mut cases[before..] {
            row.template = row.template.replace("FLECS", &family.to_ascii_uppercase());
            row.expected.device_type = family.to_ascii_uppercase();
        }
    }
    for scope in ["%ROOT%", "%INLINE%", "%FAMILY%", "%REFERENCE%"] {
        let reference = matches!(scope, "%FAMILY%" | "%REFERENCE%");
        let mut expected = baseline(reference);
        let mut template = if reference {
            finish(REFERENCE)
        } else {
            finish(PROFILE)
        };
        let mut registry = finish(REGISTRY);
        match scope {
            "%ROOT%" => {
                template = template.replace("[2]", "[-0]");
                expected.auto_select = vec![0];
            }
            "%INLINE%" => {
                template = template.replace(r#""index":2"#, r#""index":-0"#);
                expected.variables[0].index = 0;
            }
            "%FAMILY%" => {
                registry = registry.replace(r#""index":2"#, r#""index":-0"#);
                expected.variables[0].index = 0;
            }
            _ => {
                registry = finish(&REGISTRY.replace("%VAR%", r#", "families":null"#));
                template = template.replace(r#""index":9"#, r#""index":-0"#);
                expected.variables[0].index = 0;
                expected.variables[0].name = "Давление".into();
                expected.variables[0].legacy_name = "Давление".into();
            }
        }
        cases.push(Case {
            label: format!("lexical-negative-zero-{}", scope.trim_matches('%')),
            template,
            registry,
            expected,
        });
    }
    for reverse in [false, true] {
        let mut expected = baseline(true);
        expected.variables[0].name = "Давление".into();
        expected.variables[0].legacy_name = "Давление".into();
        expected.variables[0].index = if reverse { 3 } else { 9 };
        let pair = if reverse {
            r#""registry":"Давление","registry":"p","index":9,"index":3,"Index":99"#
        } else {
            r#""registry":"p","registry":"Давление","index":3,"index":9,"Index":99"#
        };
        cases.push(Case {
            label: format!("reference-exact-order-{reverse}"),
            template: finish(&REFERENCE.replace(r#""registry":"p","index":9"#, pair)),
            registry: finish(&REGISTRY.replace("%VAR%", r#", "families":null"#)),
            expected,
        });
    }
    let mut expected = baseline(true);
    expected.variables[0].name = "Давление".into();
    expected.variables[0].legacy_name = "Давление".into();
    expected.variables[0].index = 0;
    cases.push(Case {
        label: "reference-upper-index-unknown".into(),
        template: finish(&REFERENCE.replace(r#""index":9"#, r#""Index":{"unknown":1e400}"#)),
        registry: finish(&REGISTRY.replace("%VAR%", r#", "families":null"#)),
        expected,
    });
    assert_eq!(cases.len(), 159, "all raw scopes must reach every consumer");
    Ok(cases)
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
        let root = std::env::temp_dir().join(format!(
            "icem-com-json-fields-{}-{nonce}",
            std::process::id()
        ));
        let input = root.join("input");
        std::fs::create_dir_all(&input)?;
        Ok(Self { root, input })
    }
    pub fn write(&self, case: &Case) -> Result<PathBuf> {
        let path = self.input.join(&case.label);
        std::fs::create_dir_all(&path)?;
        std::fs::write(path.join("variable_registry.json"), &case.registry)?;
        std::fs::write(path.join("profile.json"), &case.template)?;
        Ok(path)
    }
}
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}
pub fn write(path: &Path, name: &str, text: &str) -> Result {
    std::fs::write(path.join(name), text)?;
    Ok(())
}
