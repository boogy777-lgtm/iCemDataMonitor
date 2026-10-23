//! Text-file decoding is shared; stream JSON deliberately has a separate policy.
#[path = "support/catalog_encoding.rs"]
mod fixtures;
use fixtures::{A, B, Encoding, LABEL, Result, Stage, profile, variables, vectors};
use icem_emulation::{
    lan::{BaseProvider, Registry},
    simulation::{compute, format_line, load_catalog, load_template},
};
use std::path::Path;

fn assert_com(path: &Path, name: &str, variable: &str, base: f64) -> Result {
    let catalog = load_catalog(path);
    assert!(
        catalog.warnings.is_empty(),
        "{path:?}: {:?}",
        catalog.warnings
    );
    assert_eq!(catalog.templates.len(), 1, "{path:?}");
    let template = &catalog.templates[0];
    assert_eq!(template.name, name);
    assert_eq!(template.device_type, "FLECS");
    assert_eq!(template.auto_select, [2]);
    assert_eq!(template.variables.len(), 2);
    let v = &template.variables[0];
    assert_eq!(
        (v.index, v.name.as_str(), v.unit.as_str(), v.base),
        (2, variable, "psi", base)
    );
    assert_eq!(template.variables[1].name, "Inline");
    let values = template
        .variables
        .iter()
        .map(|v| compute(v, 1.0))
        .collect::<Vec<_>>();
    assert_eq!(format_line(&values, template), format!("{base:.2},11"));
    assert_eq!(*template, load_template(&path.join("profile.json"), None)?);
    Ok(())
}
fn payload(path: &Path, primary: &str, firmware: &str, a: f32, b: f32) -> Result {
    let registry = Registry::from_json(&vectors(A, primary))?;
    let mut defs = registry.all().values().cloned().collect::<Vec<_>>();
    defs.extend(
        Registry::from_json(&vectors(B, firmware))?
            .all()
            .values()
            .cloned(),
    );
    let provider = BaseProvider::load(
        Registry::from_vectors(defs),
        &path.join("variable_registry.json"),
        None,
    )?;
    let frames = provider.superframe(1, 1.0);
    for (sv, value) in [(A, a), (B, b)] {
        let frame = frames
            .iter()
            .find(|v| v.sv_number == sv)
            .ok_or("missing frame")?;
        let mut expected = [0; 124];
        expected[..4].copy_from_slice(&value.to_be_bytes());
        assert_eq!(frame.payload, expected, "{path:?} {sv}");
    }
    Ok(())
}
#[test]
fn every_text_file_callsite_accepts_bom_detected_encodings_and_preserves_outputs() -> Result {
    let stage = Stage::new()?;
    for encoding in Encoding::ALL {
        // Vary the profile and each registry source independently: a missed read
        // callsite cannot hide behind another already-decoded source.
        for source in [
            "profile.json",
            "variable_registry.json",
            "variable_registry.emulator.json",
        ] {
            let path = stage.directory(format!("com-{encoding:?}-{source}"))?;
            std::fs::write(path.join("profile.json"), profile(LABEL))?;
            std::fs::write(path.join("variable_registry.json"), variables(LABEL, 7.0))?;
            let text = if source == "profile.json" {
                profile(LABEL)
            } else {
                variables(LABEL, 7.0)
            };
            std::fs::write(path.join(source), encoding.encode(&text))?;
            if source == "variable_registry.emulator.json" {
                std::fs::write(path.join("variable_registry.json"), "{must-not-be-read")?;
            }
            assert_com(&path, LABEL, LABEL, 7.0)?;
        }
        for source in [
            "variable_registry.json",
            "variable_registry.emulator.json",
            "firmware_variables.json",
        ] {
            let path = stage.directory(format!("lan-{encoding:?}-{source}"))?;
            std::fs::write(path.join("variable_registry.json"), variables(LABEL, 7.0))?;
            std::fs::write(
                path.join("firmware_variables.json"),
                variables("Firmware ° 😀", 90.0),
            )?;
            let text = if source == "firmware_variables.json" {
                variables("Firmware ° 😀", 90.0)
            } else {
                variables(LABEL, 7.0)
            };
            std::fs::write(path.join(source), encoding.encode(&text))?;
            if source == "variable_registry.emulator.json" {
                std::fs::write(path.join("variable_registry.json"), "{must-not-be-read")?;
            }
            payload(&path, LABEL, "Firmware ° 😀", 7.0, 90.0)?;
        }
    }
    Ok(())
}
#[test]
fn stream_json_accepts_only_utf8_and_preserves_next_file_recovery() -> Result {
    let stage = Stage::new()?;
    for encoding in Encoding::ALL {
        for bad_source in ["lantable_registry.json", "firmware_registry.json"] {
            let path = stage.directory(format!("{encoding:?}-{bad_source}"))?;
            std::fs::write(path.join("lantable_registry.json"), vectors(A, LABEL))?;
            std::fs::write(path.join("firmware_registry.json"), vectors(B, "Firmware"))?;
            let (sv, name) = if bad_source == "lantable_registry.json" {
                (A, LABEL)
            } else {
                (B, "Firmware")
            };
            std::fs::write(path.join(bad_source), encoding.encode(&vectors(sv, name)))?;
            let registry = Registry::load(
                &path.join("lantable_registry.json"),
                Some(&path.join("firmware_registry.json")),
            )?;
            let accepted = matches!(encoding, Encoding::Utf8 | Encoding::Utf8Bom);
            assert_eq!(registry.diagnostics().len(), usize::from(!accepted));
            assert_eq!(registry.all().len(), if accepted { 2 } else { 1 });
            assert_eq!(
                registry.get(sv).map(|v| v.name.as_str()),
                accepted.then_some(name)
            );
            let survivor = if sv == A { B } else { A };
            assert!(registry.get(survivor).is_some());
            if accepted {
                let definition = registry.get(sv).ok_or("missing vector")?;
                assert_eq!(definition.signals[0].name, name);
                assert_eq!(definition.signals[0].uom, "psi");
            }
        }
    }
    Ok(())
}
#[test]
fn malformed_encoded_scalars_use_text_replacement_but_stream_rejects() -> Result {
    let stage = Stage::new()?;
    for encoding in Encoding::ALL {
        let path = stage.directory(format!("replace-{encoding:?}"))?;
        std::fs::write(
            path.join("profile.json"),
            encoding.malformed_string(&profile("@")),
        )?;
        std::fs::write(
            path.join("variable_registry.json"),
            encoding.malformed_string(&variables("@", 7.0)),
        )?;
        assert_com(&path, "�", "�", 7.0)?;
        std::fs::write(
            path.join("firmware_variables.json"),
            encoding.malformed_string(&variables("firmware@", 90.0)),
        )?;
        payload(&path, "�", "firmware�", 7.0, 90.0)?;
        std::fs::write(
            path.join("lantable_registry.json"),
            encoding.malformed_string(&vectors(A, "@")),
        )?;
        std::fs::write(
            path.join("firmware_registry.json"),
            vectors(B, "valid-next"),
        )?;
        let registry = Registry::load(
            &path.join("lantable_registry.json"),
            Some(&path.join("firmware_registry.json")),
        )?;
        assert_eq!(registry.diagnostics().len(), 1);
        assert!(registry.get(A).is_none());
        assert_eq!(registry.get(B).map(|v| v.name.as_str()), Some("valid-next"));
    }
    Ok(())
}
#[test]
fn only_one_bom_is_removed_and_utf16_without_bom_is_rejected() -> Result {
    let stage = Stage::new()?;
    for encoding in Encoding::ALL
        .into_iter()
        .filter(|e| !matches!(e, Encoding::Utf8))
    {
        for repeated in [false, true] {
            let path = stage.directory(format!("bom-{encoding:?}-{repeated}"))?;
            let mut bytes = encoding.encode(&profile(LABEL));
            if repeated {
                bytes.splice(..0, encoding.bom().iter().copied());
            } else {
                bytes.drain(..encoding.bom().len());
            }
            if !repeated && matches!(encoding, Encoding::Utf8Bom) {
                continue;
            }
            std::fs::write(path.join("profile.json"), &bytes)?;
            std::fs::write(path.join("variable_registry.json"), variables(LABEL, 7.0))?;
            assert!(load_template(&path.join("profile.json"), None).is_err());
            let catalog = load_catalog(&path);
            assert_eq!(catalog.templates[0].name, "flecs 4.53");
            assert_eq!(catalog.warnings.len(), 2);
            let mut stream = encoding.encode(&vectors(A, LABEL));
            if repeated {
                stream.splice(..0, encoding.bom().iter().copied());
            } else {
                stream.drain(..encoding.bom().len());
            }
            std::fs::write(path.join("lantable_registry.json"), stream)?;
            std::fs::write(
                path.join("firmware_registry.json"),
                vectors(B, "valid-next"),
            )?;
            let registry = Registry::load(
                &path.join("lantable_registry.json"),
                Some(&path.join("firmware_registry.json")),
            )?;
            assert_eq!(registry.diagnostics().len(), 1);
            assert_eq!(registry.all().len(), 1);
            assert!(registry.get(B).is_some());
        }
    }
    Ok(())
}

#[cfg(windows)]
fn oracle(script: &str, args: &[(&str, &Path)]) -> Result<serde_json::Value> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let mut command = std::process::Command::new("pwsh");
    command
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support").join(script));
    for (name, value) in args {
        command.arg(name).arg(value);
    }
    let output = command.output()?;
    assert!(
        output.status.success(),
        "{script}: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    Ok(serde_json::from_slice(&output.stdout)?)
}
#[test]
#[cfg(windows)]
fn original_bcl_and_catalog_methods_agree_for_healthy_encoded_files() -> Result {
    let stage = Stage::new()?;
    let files = stage.directory("files")?;
    let catalogs = stage.directory("catalogs")?;
    let text = profile(LABEL);
    for encoding in Encoding::ALL {
        std::fs::write(
            files.join(format!("{encoding:?}.json")),
            encoding.encode(&text),
        )?;
        for overridden in [false, true] {
            let path = catalogs.join(format!("{encoding:?}-{overridden}"));
            std::fs::create_dir_all(&path)?;
            std::fs::write(path.join("profile.json"), encoding.encode(&text))?;
            std::fs::write(
                path.join("variable_registry.json"),
                variables("unused", 99.0),
            )?;
            let source = if overridden {
                "variable_registry.emulator.json"
            } else {
                "variable_registry.json"
            };
            std::fs::write(path.join(source), encoding.encode(&variables(LABEL, 7.0)))?;
        }
    }
    let byte_cases = fixtures::byte_cases();
    for case in &byte_cases {
        std::fs::write(files.join(format!("{}.json", case.label)), &case.bytes)?;
    }
    let bcl = oracle("FileEncodingOracle.ps1", &[("-InputDir", &files)])?;
    let rows = bcl["files"].as_array().ok_or("missing BCL files")?;
    assert_eq!(rows.len(), byte_cases.len() + Encoding::ALL.len());
    for case in &byte_cases {
        let row = rows
            .iter()
            .find(|r| r["label"] == case.label)
            .ok_or("missing BCL byte case")?;
        let original = row["text"]
            .as_str()
            .ok_or("BCL text must be lossless JSON string")?;
        assert_eq!(
            original, case.expected,
            "{} BCL fixture expectation",
            case.label
        );
        assert_eq!(
            icem_config::text_file::decode_read_all_text(&case.bytes),
            original,
            "{} Rust/BCL decoding",
            case.label
        );
    }
    for encoding in Encoding::ALL {
        let label = format!("{encoding:?}");
        let row = rows
            .iter()
            .find(|r| r["label"] == label)
            .ok_or("missing BCL encoding")?;
        assert_eq!(row["text"].as_str(), Some(text.as_str()));
        assert_eq!(
            row["streamAccepted"].as_bool(),
            Some(matches!(encoding, Encoding::Utf8 | Encoding::Utf8Bom))
        );
    }
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let generated = stage.root.join("oracle-code");
    let original = oracle(
        "ComCatalogOracle.ps1",
        &[
            ("-RepoRoot", &root),
            ("-InputDir", &catalogs),
            ("-OutputDir", &generated),
        ],
    )?;
    let cases = original["cases"].as_array().ok_or("missing COM cases")?;
    assert_eq!(cases.len(), Encoding::ALL.len() * 2);
    for encoding in Encoding::ALL {
        for overridden in [false, true] {
            let label = format!("{encoding:?}-{overridden}");
            let row = cases
                .iter()
                .find(|r| r["label"] == label)
                .ok_or("missing COM encoding")?;
            assert_eq!(row["warnings"], serde_json::json!([0, 0, 0, 0, 0]));
            let templates = row["templates"].as_array().ok_or("missing templates")?;
            assert_eq!(templates.len(), 1);
            assert_com(&catalogs.join(&label), LABEL, LABEL, 7.0)?;
            let template = &templates[0]["template"];
            assert_eq!(template["Name"], LABEL);
            assert_eq!(template["Variables"][0]["Name"], LABEL);
            assert_eq!(template["Variables"][0]["UoM"], "psi");
            assert_eq!(template["Variables"][0]["BaseValue"], 7.0);
            assert_eq!(templates[0]["line"], "7.00,11");
        }
    }
    Ok(())
}

#[test]
fn byte_corpus_has_explicit_replacement_and_buffer_boundary_expectations() -> Result {
    let stage = Stage::new()?;
    let cases = fixtures::byte_cases();
    assert_eq!(cases.len(), 60);
    for case in cases {
        let path = stage.root.join(format!("{}.json", case.label));
        std::fs::write(&path, &case.bytes)?;
        assert_eq!(
            icem_config::text_file::read_all_text(&path)?,
            case.expected,
            "{} file read",
            case.label
        );
        assert_eq!(
            icem_config::text_file::decode_read_all_text(&case.bytes),
            case.expected,
            "{}",
            case.label
        );
    }
    Ok(())
}
