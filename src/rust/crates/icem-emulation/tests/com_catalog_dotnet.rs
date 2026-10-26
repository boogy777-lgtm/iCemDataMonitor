#![cfg(windows)]
//! Executes original C# method bodies, with only fixture-owned path bindings.
#[path = "support/com_catalog.rs"]
mod fixtures;
use fixtures::Result;
use icem_emulation::simulation::{Template, compute, format_line, load_catalog};
use serde_json::Value;
fn rows<'a>(v: &'a Value, key: &str) -> Result<&'a Vec<Value>> {
    v[key]
        .as_array()
        .ok_or_else(|| format!("missing array {key}").into())
}
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
#[test]
fn original_catalog_methods_agree_on_healthy_catalog_results() -> Result {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let stage = fixtures::Stage::new()?;
    // Differential evidence is for healthy external configurations. Malformed
    // inputs exercise the explicit Rust file/template policy in portable tests.
    let cases = fixtures::cases()
        .into_iter()
        .filter(|case| case.warnings == [0; 5])
        .collect::<Vec<_>>();
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
        let actual = load_catalog(&stage.input.join(&case.label));
        assert_eq!(
            serde_json::to_value(fixtures::warning_counts(&actual.warnings))?,
            expected["warnings"],
            "{} {:?}",
            case.label,
            actual.warnings
        );
        assert_eq!(
            serde_json::to_value(case.warnings)?,
            expected["warnings"],
            "{} source warning fixture",
            case.label
        );
        let templates = rows(expected, "templates")?;
        assert_eq!(actual.templates.len(), templates.len(), "{}", case.label);
        assert_eq!(
            actual.templates.len(),
            case.expected.len(),
            "{}",
            case.label
        );
        for (a, b) in actual.templates.iter().zip(templates) {
            compare(a, &b["template"])?;
            let values = a
                .variables
                .iter()
                .map(|v| compute(v, 1.0))
                .collect::<Vec<_>>();
            assert_eq!(
                Some(format_line(&values, a).as_str()),
                b["line"].as_str(),
                "{}",
                case.label
            );
        }
    }
    Ok(())
}
