#[path = "support/com_json_fields.rs"]
mod fixtures;
use fixtures::Result;
use icem_emulation::simulation::{
    Template, compute, format_line, load_catalog, load_template, template_from_json,
};
fn line(template: &Template) -> String {
    format_line(
        &template
            .variables
            .iter()
            .map(|v| compute(v, 1.0))
            .collect::<Vec<_>>(),
        template,
    )
}
#[test]
fn raw_fields_preserve_assignment_order_and_full_snapshots() -> Result {
    let stage = fixtures::Stage::new()?;
    for case in fixtures::cases()? {
        let directory = stage.write(&case)?;
        let explicit = load_template(&directory.join("profile.json"), None)?;
        assert_eq!(explicit, case.expected, "{} strict", case.label);
        assert_eq!(line(&explicit), line(&case.expected), "{} wire", case.label);
        let catalog = load_catalog(&directory);
        assert!(
            catalog.warnings.is_empty(),
            "{}: {:?}",
            case.label,
            catalog.warnings
        );
        assert_eq!(
            catalog.templates,
            vec![case.expected],
            "{} catalog",
            case.label
        );
    }
    Ok(())
}
#[test]
fn raw_order_survives_bom_and_utf16_file_decoding() -> Result {
    let stage = fixtures::Stage::new()?;
    let case = fixtures::cases()?
        .into_iter()
        .find(|c| c.label == "ROOT-name-case-ba-inline")
        .ok_or("missing case")?;
    let directory = stage.write(&case)?;
    for big_endian in [false, true] {
        for (name, text) in [
            ("profile.json", &case.template),
            ("variable_registry.json", &case.registry),
        ] {
            let mut bytes = if big_endian {
                vec![0xfe, 0xff]
            } else {
                vec![0xff, 0xfe]
            };
            for unit in text.encode_utf16() {
                bytes.extend_from_slice(&if big_endian {
                    unit.to_be_bytes()
                } else {
                    unit.to_le_bytes()
                });
            }
            std::fs::write(directory.join(name), bytes)?;
        }
        assert_eq!(
            load_template(&directory.join("profile.json"), None)?,
            case.expected
        );
    }
    let mut bom = vec![0xef, 0xbb, 0xbf];
    bom.extend_from_slice(case.template.as_bytes());
    std::fs::write(directory.join("profile.json"), bom)?;
    assert_eq!(
        load_template(&directory.join("profile.json"), None)?,
        case.expected
    );
    Ok(())
}
#[test]
fn malformed_known_occurrences_are_rejected_even_when_shadowed() -> Result {
    let stage = fixtures::Stage::new()?;
    let case = fixtures::cases()?.remove(0);
    let path = stage.write(&case)?;
    for malformed in [
        r#"{"name":7,"NAME":"valid","deviceType":"FLECS"}"#,
        r#"{"name":"bad","deviceType":"FLECS","autoSelectIndices":[null],"AUTOSELECTINDICES":[]}"#,
        r#"{"name":"bad","deviceType":"FLECS","variables":[{"name":"v","base":"bad","BASE":7}]}"#,
        r#"{"name":"bad","deviceType":"FLECS","variables":[{"name":"v","index":[],"INDEX":7}]}"#,
        r#"{"name":"bad","deviceType":"FLECS","variables":[{"name":"v","excludeFromTelemetry":null,"EXCLUDEFROMTELEMETRY":false}]}"#,
    ] {
        fixtures::write(&path, "bad.json", malformed)?;
        assert!(
            load_template(&path.join("bad.json"), None).is_err(),
            "{malformed}"
        );
        let catalog = load_catalog(&path);
        assert_eq!(catalog.templates, vec![case.expected.clone()]);
        assert_eq!(catalog.warnings.len(), 1, "{:?}", catalog.warnings);
        assert!(catalog.warnings[0].starts_with("template "));
    }
    Ok(())
}
#[test]
fn malformed_registry_is_atomic_and_override_does_not_fall_back_to_main() -> Result {
    let stage = fixtures::Stage::new()?;
    let case = fixtures::cases()?
        .into_iter()
        .find(|c| c.label == "exact-reference-upper-unknowns")
        .ok_or("missing case")?;
    let path = stage.write(&case)?;
    fixtures::write(
        &path,
        "inline.json",
        r#"{"name":"Inline","deviceType":"FLECS","variables":[{"name":"I","base":11}]}"#,
    )?;
    for malformed in [
        r#"{"variables":[null]}"#,
        r#"{"variables":[{"name":"p","simulation":{"base":false,"BASE":7}}]}"#,
        r#"{"variables":[{"families":{"flecs":{"index":"bad","INDEX":2}}}]}"#,
        r#"{"variables":7,"VARIABLES":[]}"#,
    ] {
        fixtures::write(&path, "variable_registry.emulator.json", malformed)?;
        assert!(load_template(&path.join("profile.json"), None).is_err());
        assert!(load_template(&path.join("inline.json"), None).is_err());
        let catalog = load_catalog(&path);
        assert_eq!(catalog.templates.len(), 2);
        assert_eq!(
            catalog
                .warnings
                .iter()
                .filter(|w| w.starts_with("registry:"))
                .count(),
            1
        );
        assert_eq!(
            catalog
                .warnings
                .iter()
                .filter(|w| w.starts_with("registry variable '"))
                .count(),
            1
        );
        assert!(catalog.warnings.iter().all(|w| w.starts_with("registry:")
            || w.starts_with("registry variable '")
            || w.starts_with("template ")));
        assert!(catalog.warnings.iter().any(|w| w.starts_with("registry:")));
        assert!(
            catalog
                .warnings
                .iter()
                .any(|w| w.starts_with("registry variable '"))
        );
        assert!(
            catalog
                .templates
                .iter()
                .find(|t| t.name == case.expected.name)
                .ok_or("missing ref")?
                .variables
                .is_empty()
        );
        assert_eq!(
            catalog
                .templates
                .iter()
                .find(|t| t.name == "Inline")
                .ok_or("missing inline")?
                .variables[0]
                .base,
            11.0
        );
    }
    Ok(())
}
#[test]
fn value_api_rejects_only_ambiguous_recognized_fields() -> Result {
    for (template, registry) in [
        (r#"{"name":"A","NAME":"B","deviceType":"FLECS"}"#, "null"),
        (
            r#"{"name":"A","deviceType":"FLECS","variables":[{"base":1,"BASE":2}]}"#,
            "null",
        ),
        (
            r#"{"name":"A","deviceType":"FLECS"}"#,
            r#"{"variables":[{"simulation":{"base":1,"BASE":2}}]}"#,
        ),
    ] {
        assert!(
            template_from_json(
                &serde_json::from_str(template)?,
                &serde_json::from_str(registry)?
            )
            .is_err()
        );
    }
    for template in [
        r#"{"name":"A","deviceType":"FLECS","unknown":1,"UNKNOWN":2,"variables":[{"name":"V","base":11}]}"#,
        r#"{"name":"A","deviceType":"FLECS","variables":[{"registry":"p","base":"ignored","BASE":false,"Index":[]}]}"#,
    ] {
        assert!(
            template_from_json(
                &serde_json::from_str(template)?,
                &serde_json::from_str(r#"{"variables":[{"name":"p","display":"P"}]}"#)?
            )
            .is_ok()
        );
    }
    Ok(())
}

#[cfg(windows)]
use serde_json::Value;
#[cfg(windows)]
fn rows<'a>(v: &'a Value, key: &str) -> Result<&'a Vec<Value>> {
    v[key]
        .as_array()
        .ok_or_else(|| format!("missing array {key}").into())
}
#[cfg(windows)]
fn compare(actual: &Template, expected: &Value) -> Result {
    assert_eq!(Some(actual.name.as_str()), expected["Name"].as_str());
    assert_eq!(
        Some(actual.device_type.as_str()),
        expected["DeviceType"].as_str()
    );
    assert_eq!(
        serde_json::to_value(&actual.auto_select)?,
        expected["AutoSelectIndices"]
    );
    let vars = rows(expected, "Variables")?;
    assert_eq!(actual.variables.len(), vars.len());
    for (a, b) in actual.variables.iter().zip(vars) {
        assert_eq!(Some(i64::from(a.index)), b["Index"].as_i64());
        for (text, key) in [
            (&a.name, "Name"),
            (&a.legacy_name, "LegacyName"),
            (&a.unit, "UoM"),
            (&a.format, "Format"),
        ] {
            assert_eq!(Some(text.as_str()), b[key].as_str());
        }
        for (number, key) in [
            (a.base, "BaseValue"),
            (a.amplitude, "Amplitude"),
            (a.frequency, "Frequency"),
            (a.toggle_period, "TogglePeriod"),
        ] {
            assert_eq!(Some(number), b[key].as_f64());
        }
        assert_eq!(Some(a.exclude), b["ExcludeFromTelemetry"].as_bool());
    }
    Ok(())
}

#[cfg(windows)]
#[test]
fn original_catalog_methods_agree_on_raw_healthy_fields() -> Result {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let stage = fixtures::Stage::new()?;
    let cases = fixtures::cases()?;
    for case in &cases {
        stage.write(case)?;
    }
    let output = std::process::Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/ComCatalogOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .arg("-InputDir")
        .arg(&stage.input)
        .arg("-OutputDir")
        .arg(stage.root.join("oracle-code"))
        .output()?;
    assert!(
        output.status.success(),
        "original catalog oracle failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let oracle: Value = serde_json::from_slice(&output.stdout)?;
    assert_eq!(rows(&oracle, "cases")?.len(), cases.len());
    for case in cases {
        let expected = rows(&oracle, "cases")?
            .iter()
            .find(|v| v["label"] == case.label)
            .ok_or("missing oracle case")?;
        assert_eq!(
            expected["warnings"],
            serde_json::json!([0, 0, 0, 0, 0]),
            "{}",
            case.label
        );
        let templates = rows(expected, "templates")?;
        assert_eq!(templates.len(), 1, "{}", case.label);
        let original = &templates[0];
        compare(&case.expected, &original["template"])?;
        let actual = load_catalog(&stage.input.join(&case.label));
        assert!(
            actual.warnings.is_empty(),
            "{}: {:?}",
            case.label,
            actual.warnings
        );
        assert_eq!(actual.templates.len(), 1, "{}", case.label);
        compare(&actual.templates[0], &original["template"])?;
        assert_eq!(
            Some(line(&actual.templates[0]).as_str()),
            original["line"].as_str(),
            "{}",
            case.label
        );
    }
    Ok(())
}

#[test]
fn selected_raw_array_and_header_determine_record_validation() -> Result {
    let stage = fixtures::Stage::new()?;
    let case = fixtures::cases()?.remove(0);
    let path = stage.write(&case)?;
    fixtures::write(
        &path,
        "profile.json",
        r#"{"name":"Selected","deviceType":"FLECS","variables":[{"index":[],"base":"bad"}],"VARIABLES":[{"name":"Good","base":11,"format":"int"}]}"#,
    )?;
    let template = load_template(&path.join("profile.json"), None)?;
    assert_eq!(template.name, "Selected");
    assert_eq!(template.variables.len(), 1);
    assert_eq!(template.variables[0].name, "Good");
    assert_eq!(template.variables[0].base, 11.0);
    assert_eq!(line(&template), "11");
    fixtures::write(
        &path,
        "nameless.json",
        r#"{"variables":[{"index":[],"base":"bad"}]}"#,
    )?;
    let catalog = load_catalog(&path);
    assert!(catalog.warnings.is_empty(), "{:?}", catalog.warnings);
    assert_eq!(catalog.templates, vec![template]);
    assert!(load_template(&path.join("nameless.json"), None).is_err());
    Ok(())
}

#[test]
fn int32_fields_reject_fraction_exponent_and_out_of_range_tokens() -> Result {
    let stage = fixtures::Stage::new()?;
    let case = fixtures::cases()?.remove(0);
    let path = stage.write(&case)?;
    for token in ["1.0", "1e0", "-0.0", "2147483648", "-2147483649"] {
        for template in [
            format!(
                r#"{{"name":"Bad","deviceType":"FLECS","variables":[{{"name":"V","index":{token}}}]}}"#
            ),
            format!(r#"{{"name":"Bad","deviceType":"FLECS","autoSelectIndices":[{token}]}}"#),
            format!(
                r#"{{"name":"Bad","deviceType":"FLECS","variables":[{{"registry":"p","index":{token}}}]}}"#
            ),
        ] {
            fixtures::write(&path, "bad.json", &template)?;
            assert!(
                load_template(&path.join("bad.json"), None).is_err(),
                "{template}"
            );
            let catalog = load_catalog(&path);
            assert_eq!(catalog.templates, vec![case.expected.clone()]);
            assert_eq!(catalog.warnings.len(), 1, "{:?}", catalog.warnings);
        }
        fixtures::write(
            &path,
            "variable_registry.json",
            &format!(
                r#"{{"variables":[{{"name":"p","families":{{"flecs":{{"index":{token}}}}}}}]}}"#
            ),
        )?;
        assert!(
            load_template(&path.join("profile.json"), None).is_err(),
            "family {token}"
        );
        fixtures::write(&path, "variable_registry.json", &case.registry)?;
    }
    Ok(())
}
