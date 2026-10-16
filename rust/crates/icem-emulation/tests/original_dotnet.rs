#![cfg(windows)]
use icem_emulation::{dfb::*, simulation::*, units::*};
use icem_protocols::ace;
use serde_json::Value;
use std::path::PathBuf;
type TestResult = Result<(), Box<dyn std::error::Error>>;
fn bits(value: &Value) -> Result<f64, Box<dyn std::error::Error>> {
    Ok(f64::from_bits(u64::from_str_radix(
        value.as_str().ok_or("not a bit string")?,
        16,
    )?))
}
fn same(actual: f64, expected: f64, case: &Value) {
    if expected.is_nan() {
        assert!(actual.is_nan(), "{case}");
    } else if expected == 0.0 {
        assert_eq!(actual.to_bits(), expected.to_bits(), "signed zero {case}");
    } else if expected.is_infinite() {
        assert_eq!(actual, expected, "{case}");
    } else {
        assert!(
            (actual - expected).abs() <= expected.abs().max(1.0) * 1e-12,
            "actual={actual:?} expected={expected:?} case={case}"
        );
    }
}
fn rows<'a>(value: &'a Value, key: &str) -> Result<&'a Vec<Value>, Box<dyn std::error::Error>> {
    value[key]
        .as_array()
        .ok_or_else(|| format!("missing array {key}").into())
}
fn string<'a>(value: &'a Value, key: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    value[key]
        .as_str()
        .ok_or_else(|| format!("missing string {key}").into())
}
#[test]
fn differential_original_dotnet_classes() -> TestResult {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let script = root.join("rust/tests/support/EmulatorOracle.ps1");
    let output = std::process::Command::new("pwsh")
        .args(["-NoProfile", "-File"])
        .arg(script)
        .arg("-RepoRoot")
        .arg(&root)
        .output()?;
    assert!(
        output.status.success(),
        "original C# oracle failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let oracle: Value = serde_json::from_slice(&output.stdout)?;
    let major = string(&oracle, "runtime")?
        .split('.')
        .next()
        .ok_or("no runtime version")?
        .parse::<u32>()?;
    assert!(
        major >= 9,
        "oracle must use the .NET 9+ cast semantics of the net10 source"
    );
    for entry in rows(&oracle, "templates")? {
        let template = load_template(
            &root
                .join("iCemEmulator/templates")
                .join(string(entry, "file")?),
            None,
        )?;
        assert_eq!(template.name, string(entry, "Name")?);
        assert_eq!(template.device_type, string(entry, "DeviceType")?);
        assert_eq!(
            serde_json::to_value(
                template
                    .variables
                    .iter()
                    .map(|v| &v.name)
                    .collect::<Vec<_>>()
            )?,
            entry["names"]
        );
        assert_eq!(
            serde_json::to_value(
                template
                    .variables
                    .iter()
                    .map(|v| v.index)
                    .collect::<Vec<_>>()
            )?,
            entry["indices"]
        );
        let name = template.variables[2].name.clone();
        let mut engine = Engine::new(vec![template]);
        for (tick, expected) in rows(entry, "ticks")?.iter().enumerate() {
            if tick == 1 {
                engine.set_override(&name, 1234.125);
            }
            if tick == 3 {
                engine.clear_override(&name);
            }
            if tick == 4 {
                engine.reset_clock();
            }
            let values = engine.tick();
            for (actual, expected) in values.iter().zip(rows(expected, "values")?) {
                same(*actual, bits(expected)?, expected);
            }
            assert_eq!(
                format_line(&values, engine.current().ok_or("missing current template")?),
                string(expected, "line")?,
                "template {} tick {tick}",
                entry["Name"]
            );
        }
    }
    for case in rows(&oracle, "units")? {
        same(
            convert(
                bits(&case["input"])?,
                string(case, "from")?,
                string(case, "to")?,
            ),
            bits(&case["value"])?,
            case,
        );
    }
    for case in rows(&oracle, "settings")? {
        let settings = Settings {
            preset: Preset::try_from(case["preset"].as_u64().ok_or("bad preset")? as u8)?,
            overrides: std::collections::BTreeMap::from([
                (Group::Pressure, "bar".into()),
                (Group::Temperature, "°F".into()),
            ]),
        };
        let native = string(case, "native")?;
        assert_eq!(
            settings.target_unit(group_of(native)),
            string(case, "target")?
        );
        assert_eq!(
            Some(settings.needs_conversion(native)),
            case["needs"].as_bool()
        );
        same(
            settings.to_target(32.0, native),
            bits(&case["value"])?,
            case,
        );
        same(settings.to_wire(32.0, native), bits(&case["wire"])?, case);
    }
    let mut lag = FirstOrderLag::default();
    let mut latch = KickoutLatch::default();
    let mut total = Totalizer::default();
    for case in rows(&oracle, "dfb")? {
        let dt = case["dt"].as_f64().ok_or("dt")?;
        let reset = case["reset"].as_bool().ok_or("reset")?;
        same(
            lag.execute(case["target"].as_f64().ok_or("target")?, dt, 2.0),
            bits(&case["lag"])?,
            case,
        );
        let (trip, captured) =
            latch.execute(case["pressure"].as_f64().ok_or("pressure")?, 500.0, reset);
        assert_eq!(Some(trip), case["trip"].as_bool());
        same(captured, bits(&case["captured"])?, case);
        let (stage, job) = total.execute(case["rate"].as_f64().ok_or("rate")?, dt, reset);
        same(stage, bits(&case["stage"])?, case);
        same(job, bits(&case["job"])?, case);
    }
    for case in rows(&oracle, "edges")? {
        same(
            FirstOrderLag::default().execute(10.0, bits(&case["dt"])?, bits(&case["tau"])?),
            bits(&case["value"])?,
            case,
        );
    }
    for case in rows(&oracle, "formats")? {
        let template = Template {
            variables: vec![Variable {
                format: string(case, "format")?.into(),
                ..Variable::default()
            }],
            ..Template::default()
        };
        assert_eq!(
            format_line(&[bits(&case["input"])?], &template),
            string(case, "line")?,
            "{case}"
        );
    }
    for case in rows(&oracle, "ace")? {
        let size = case["size"].as_u64().ok_or("size")? as usize;
        let payload: Vec<_> = (0..size).map(|i| i as u8).collect();
        let frame = ace::build([0, 1, 2, 3, 4, 5], 0x12345678, 0x13120200, &payload);
        assert_eq!(
            frame.iter().map(|b| format!("{b:02x}")).collect::<String>(),
            string(case, "frame")?
        );
    }
    let payload: Vec<_> = (0..124).map(|i| 255 - i).collect();
    for case in rows(&oracle, "decode")? {
        let actual = ace::decode_native(
            &payload,
            case["offset"].as_i64().ok_or("offset")? as i32,
            case["width"].as_i64().ok_or("width")? as i32,
            string(case, "type")?,
            0.125,
        );
        assert_eq!(Some(actual.is_some()), case["ok"].as_bool(), "{case}");
        if let Some(actual) = actual {
            same(actual, bits(&case["value"])?, case);
        }
    }
    let summary = serde_json::json!({"runtime":oracle["runtime"],"templates":rows(&oracle,"templates")?.len(),"unit_cases":rows(&oracle,"units")?.len(),"settings_cases":rows(&oracle,"settings")?.len(),"dfb_steps":rows(&oracle,"dfb")?.len(),"dfb_edge_cases":rows(&oracle,"edges")?.len(),"formatter_cases":rows(&oracle,"formats")?.len(),"ace_frames":rows(&oracle,"ace")?.len(),"signal_decodes":rows(&oracle,"decode")?.len(),"numeric_tolerance":"1e-12 relative/absolute floor1; zero sign/Infinity exact; NaN classification","wire_equality":"exact FLECS strings and ACE bytes","result":"PASS"});
    std::fs::write(
        root.join("rust/target/emu-differential-report.json"),
        serde_json::to_vec_pretty(&summary)?,
    )?;
    Ok(())
}
