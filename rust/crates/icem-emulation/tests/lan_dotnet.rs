#![cfg(windows)]
//! Payload parity is byte-exact; expectations come from untouched C# providers.
use icem_emulation::{
    cement::Context,
    lan::{BaseProvider, Registry, SignalDef, VectorData, VectorDef, encode_ascii, encode_signal},
    lan_pipeline::{LanPipeline, convert_payload},
    units::{Group, Preset, Settings},
};
use serde_json::{Value, json};
use std::{collections::BTreeMap, path::PathBuf};
type TestResult = Result<(), Box<dyn std::error::Error>>;
fn string<'a>(v: &'a Value, key: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    v[key]
        .as_str()
        .ok_or_else(|| format!("missing string {key}").into())
}
fn rows<'a>(v: &'a Value, key: &str) -> Result<&'a Vec<Value>, Box<dyn std::error::Error>> {
    v[key]
        .as_array()
        .ok_or_else(|| format!("missing array {key}").into())
}
fn number(v: &Value, key: &str) -> Result<f64, Box<dyn std::error::Error>> {
    v[key]
        .as_f64()
        .ok_or_else(|| format!("missing number {key}").into())
}
fn uint(v: &Value, key: &str) -> Result<u32, Box<dyn std::error::Error>> {
    Ok(u32::try_from(v[key].as_u64().ok_or("missing uint")?)?)
}
fn bits(v: &Value, key: &str) -> Result<f64, Box<dyn std::error::Error>> {
    Ok(f64::from_bits(u64::from_str_radix(string(v, key)?, 16)?))
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn unhex(s: &str) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let (pairs, remainder) = s.as_bytes().as_chunks::<2>();
    if !remainder.is_empty() {
        return Err("odd-length hex from C# oracle".into());
    }
    pairs
        .iter()
        .map(|b| Ok(u8::from_str_radix(std::str::from_utf8(b)?, 16)?))
        .collect()
}
fn settings(preset: u8, custom: bool) -> Result<Settings, Box<dyn std::error::Error>> {
    Ok(Settings {
        preset: Preset::try_from(preset)?,
        overrides: if custom {
            BTreeMap::from([
                (Group::Flow, "gpm".into()),
                (Group::Temperature, "°F".into()),
                (Group::Density, "kg/m3".into()),
                (Group::Volume, "gal".into()),
                (Group::Level, "m".into()),
                (Group::Pressure, "kPa".into()),
            ])
        } else {
            BTreeMap::new()
        },
    })
}
fn signal(v: &Value) -> Result<SignalDef, Box<dyn std::error::Error>> {
    Ok(SignalDef {
        byte_offset: i32::try_from(v["byte_offset"].as_i64().ok_or("offset")?)?,
        size: i32::try_from(v["size"].as_i64().ok_or("size")?)?,
        value_type: string(v, "value_type")?.into(),
        das_factor: number(v, "das_factor")?,
        name: string(v, "name")?.into(),
        uom: string(v, "uom")?.into(),
    })
}
fn frames(actual: &[VectorData], expected: &Value) -> TestResult {
    let expected = rows(expected, "frames")?;
    assert_eq!(actual.len(), expected.len());
    for (actual, expected) in actual.iter().zip(expected) {
        assert_eq!(actual.sv_number, uint(expected, "sv")?);
        assert_eq!(actual.name, string(expected, "name")?);
        assert_eq!(
            hex(&actual.payload),
            string(expected, "payload")?,
            "SV {:08x}",
            actual.sv_number
        );
    }
    Ok(())
}
#[test]
fn original_lan_providers_byte_for_byte() -> TestResult {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let output = std::process::Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/LanPayloadOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .output()?;
    assert!(
        output.status.success(),
        "C# oracle failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let oracle: Value = serde_json::from_slice(&output.stdout)?;
    let major: u32 = string(&oracle, "runtime")?
        .split('.')
        .next()
        .ok_or("runtime")?
        .parse()?;
    assert!(
        major >= 9,
        "source targets .NET 9+ saturating cast semantics"
    );
    let templates = root.join("iCemEmulator/templates");
    let registry = Registry::load(
        &templates.join("lantable_registry.json"),
        Some(&templates.join("firmware_registry.json")),
    )?;
    let source_inventory = rows(&oracle, "inventory")?;
    assert_eq!(registry.all().len(), source_inventory.len());
    let mut signal_count = 0;
    for source in source_inventory {
        let actual = registry
            .get(uint(source, "sv")?)
            .ok_or("source SV missing")?;
        assert_eq!(actual.name, string(source, "name")?);
        assert_eq!(actual.equipment_type, string(source, "equipment_type")?);
        let expected: Result<Vec<_>, _> = rows(source, "signals")?.iter().map(signal).collect();
        assert_eq!(actual.signals, expected?);
        signal_count += actual.signals.len();
    }
    let make_base = || {
        BaseProvider::load(
            registry.clone(),
            &templates.join("variable_registry.json"),
            None,
        )
    };
    let mut base = make_base()?;
    for case in rows(&oracle, "bases")? {
        base.include_firmware_tables = case["firmware"].as_bool().ok_or("firmware")?;
        frames(&base.superframe(1, number(case, "elapsed")?), case)?;
    }
    let name = string(&oracle, "override_name")?;
    let lower = name.to_lowercase();
    let mut pipeline = LanPipeline::new(make_base()?);
    for case in rows(&oracle, "transitions")? {
        let step = uint(case, "step")?;
        match step {
            0 => pipeline.frozen = true,
            1 => pipeline.base.include_firmware_tables = true,
            2 => pipeline.set_override(0x13120203, &lower, 1234.5),
            3 => pipeline.set_override(0x13120203, name, 87.125),
            4 => pipeline.settings = settings(2, false)?,
            5 => pipeline.clear_override(0x13120203, name),
            6 => pipeline.frozen = false,
            7 => {
                pipeline.frozen = true;
                pipeline.base.include_firmware_tables = false;
            }
            8 => pipeline.settings = settings(0, false)?,
            9 => pipeline.settings = settings(1, false)?,
            10 => pipeline.settings = settings(3, false)?,
            11 => pipeline.settings = settings(2, true)?,
            12 => pipeline.frozen = false,
            13 => pipeline.base.include_firmware_tables = true,
            14 => pipeline.set_override(0x13120203, name, -50.5),
            15 => pipeline.clear_override(0x13120203, &lower),
            _ => return Err("unhandled source transition".into()),
        }
        pipeline
            .network
            .as_mut()
            .ok_or("network")?
            .execute(&mut Context::new(
                f64::from(step) * 0.25,
                0.25,
                i64::from(step),
                7,
            ));
        let mut actual = pipeline.superframe(u32::MAX, f64::from(step) * 0.25);
        frames(&actual, case)?;
        assert_eq!(
            serde_json::to_value(pipeline.active_sv_numbers())?,
            case["active"]
        );
        let expected: Result<BTreeMap<_, _>, Box<dyn std::error::Error>> = rows(case, "overrides")?
            .iter()
            .map(|v| {
                Ok((
                    (uint(v, "sv")?, string(v, "name")?.to_owned()),
                    bits(v, "value")?,
                ))
            })
            .collect();
        assert_eq!(pipeline.snapshot_overrides(), expected?);
        actual[0].payload[0] ^= 0xff;
    }
    for case in rows(&oracle, "can")? {
        let result = pipeline.can_transmit(uint(case, "sv")?, string(case, "name")?);
        assert_eq!(Some(result.is_ok()), case["ok"].as_bool(), "{case}");
        assert_eq!(
            result.err().unwrap_or_default(),
            string(case, "reason")?,
            "{case}"
        );
    }
    for case in rows(&oracle, "codec")? {
        let size = i32::try_from(case["size"].as_i64().ok_or("codec size")?)?;
        let actual = encode_signal(
            bits(case, "value")?,
            string(case, "type")?,
            number(case, "factor")?,
            size,
        )
        .ok_or("valid encoding rejected")?;
        assert_eq!(hex(&actual), string(case, "bytes")?, "codec {case}");
    }
    for case in rows(&oracle, "ascii")? {
        let size = i32::try_from(case["size"].as_i64().ok_or("ASCII size")?)?;
        let actual = encode_ascii(string(case, "text")?, size).ok_or("valid ASCII rejected")?;
        assert_eq!(hex(&actual), string(case, "bytes")?, "ASCII {case}");
    }
    for case in rows(&oracle, "conversions")? {
        let definition = VectorDef {
            sv_number: 0x13120203,
            name: "fixture".into(),
            equipment_type: String::new(),
            signals: vec![signal(&case["signal"])?],
        };
        let mut payload = unhex(string(case, "input")?)?;
        convert_payload(
            &mut payload,
            &definition,
            &settings(
                u8::try_from(uint(case, "preset")?)?,
                case["custom"].as_bool().ok_or("custom")?,
            )?,
        );
        assert_eq!(hex(&payload), string(case, "output")?, "conversion {case}");
    }
    let report = json!({"status":"PASS","oracle_runtime":oracle["runtime"],"source_vectors":source_inventory.len(),"source_signals":signal_count,
        "base_superframes":rows(&oracle,"bases")?.len(),"pipeline_transitions":rows(&oracle,"transitions")?.len(),
        "can_transmit_cases":rows(&oracle,"can")?.len(),"codec_cases":rows(&oracle,"codec")?.len(),
        "ascii_cases":rows(&oracle,"ascii")?.len(),"unit_payload_cases":rows(&oracle,"conversions")?.len(),"comparison":"exact full payload bytes"});
    let target = root.join("rust/target");
    std::fs::create_dir_all(&target)?;
    std::fs::write(
        target.join("lan-differential-report.json"),
        serde_json::to_vec_pretty(&report)?,
    )?;
    Ok(())
}

#[path = "support/lan_loading.rs"]
mod loading_fixtures;

#[test]
fn original_lan_file_methods_preserve_the_same_partial_results() -> TestResult {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let stage = loading_fixtures::Stage::new()?;
    let cases = loading_fixtures::cases();
    for case in &cases {
        stage.write(case)?;
    }
    let output = std::process::Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(root.join("rust/tests/support/LanPayloadOracle.ps1"))
        .arg("-RepoRoot")
        .arg(&root)
        .arg("-LoadingFixtures")
        .arg(&stage.root)
        .output()?;
    assert!(
        output.status.success(),
        "original file-method oracle failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let oracle: Value = serde_json::from_slice(&output.stdout)?;
    assert_eq!(rows(&oracle, "cases")?.len(), cases.len());
    for case in cases {
        let source = rows(&oracle, "cases")?
            .iter()
            .find(|v| v["label"] == case.label)
            .ok_or("missing oracle case")?;
        let path = stage.root.join(&case.label);
        let registry = Registry::load(
            &path.join("lantable_registry.json"),
            Some(&path.join("firmware_registry.json")),
        )?;
        assert_eq!(
            registry.all().len(),
            rows(source, "inventory")?.len(),
            "{}",
            case.label
        );
        for expected in rows(source, "inventory")? {
            let actual = registry
                .get(uint(expected, "sv")?)
                .ok_or("source retained SV missing")?;
            assert_eq!(actual.name, string(expected, "name")?, "{}", case.label);
            assert_eq!(
                actual.equipment_type,
                string(expected, "equipment_type")?,
                "{}",
                case.label
            );
            let signals = rows(expected, "signals")?
                .iter()
                .map(signal)
                .collect::<Result<Vec<_>, _>>()?;
            assert_eq!(actual.signals, signals, "{}", case.label);
        }
        assert_eq!(registry.all().len(), case.expected_vectors.len());
        let provider = BaseProvider::load(registry, &path.join("variable_registry.json"), None)?;
        let actual = provider.superframe(1, 0.0);
        frames(&actual, source)?;
        let a = actual
            .iter()
            .find(|f| f.sv_number == loading_fixtures::A)
            .ok_or("active A")?;
        let b = actual
            .iter()
            .find(|f| f.sv_number == loading_fixtures::B)
            .ok_or("active B")?;
        assert_eq!(
            &b.payload[..4],
            &case.expected_b.to_be_bytes(),
            "{}",
            case.label
        );
        for (chunk, expected) in a.payload[..12]
            .as_chunks::<4>()
            .0
            .iter()
            .zip(case.expected_a)
        {
            assert_eq!(*chunk, expected.to_be_bytes(), "{}", case.label);
        }
    }
    Ok(())
}
