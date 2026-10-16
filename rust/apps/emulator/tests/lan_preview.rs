use icem_protocols::ace;
use serde_json::Value;
use std::{path::PathBuf, process::Command};
type TestResult<T = ()> = Result<T, Box<dyn std::error::Error>>;
fn templates() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../iCemEmulator/templates")
}
fn bytes(v: &Value) -> TestResult<Vec<u8>> {
    let s = v.as_str().ok_or("not hex")?;
    Ok((0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16))
        .collect::<Result<_, _>>()?)
}
#[test]
fn real_exe_emits_complete_ace_frames_and_freezes_after_first_tick() -> TestResult {
    let output = Command::new(env!("CARGO_BIN_EXE_icem-lan-dev"))
        .arg("--templates")
        .arg(templates())
        .args([
            "--ticks",
            "3",
            "--interval-ms",
            "0",
            "--emulate",
            "true",
            "--freeze-at",
            "1",
            "--firmware",
            "true",
            "--equipment",
            "0x12345678",
            "--preset",
            "2",
            "--set",
            "0x13120206:pump a pressure=1000",
        ])
        .output()?;
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let text = String::from_utf8(output.stdout)?;
    let rows: Vec<Value> = text
        .lines()
        .map(serde_json::from_str)
        .collect::<Result<_, _>>()?;
    assert_eq!(rows.len(), 3);
    assert_eq!(rows[0]["frozen"], false);
    assert_eq!(rows[1]["frozen"], true);
    for row in &rows {
        let frames = row["frames"].as_array().ok_or("frames")?;
        assert_eq!(frames.len(), 20);
        for frame in frames {
            let raw = bytes(&frame["frame"])?;
            assert_eq!(raw.len(), 152);
            let parsed = ace::parse(&raw).ok_or("invalid ACE")?;
            assert_eq!(parsed.equipment, 0x12345678);
            assert_eq!(Some(u64::from(parsed.sv)), frame["sv"].as_u64());
            assert_eq!(parsed.payload.as_slice(), bytes(&frame["payload"])?);
            if parsed.sv == 0x13120206 {
                assert_eq!(
                    f32::from_be_bytes(parsed.payload[..4].try_into()?),
                    (1000.0_f64 * 0.0689476) as f32
                );
            }
        }
    }
    assert_eq!(rows[0]["frames"], rows[1]["frames"]);
    assert_eq!(rows[1]["frames"], rows[2]["frames"]);
    assert!(String::from_utf8_lossy(&output.stderr).contains("No hardware streams started"));
    Ok(())
}
#[test]
fn defaults_hold_first_preview_and_hex_output_is_exact_frame_length() -> TestResult {
    let output = Command::new(env!("CARGO_BIN_EXE_icem-lan-dev"))
        .arg("--templates")
        .arg(templates())
        .args(["--ticks", "2", "--interval-ms", "0"])
        .output()?;
    assert!(output.status.success());
    let text = String::from_utf8(output.stdout)?;
    let rows: Vec<Value> = text
        .lines()
        .map(serde_json::from_str)
        .collect::<Result<_, _>>()?;
    assert_eq!(rows[0]["frozen"], true);
    assert_eq!(rows[0]["frames"].as_array().ok_or("frames")?.len(), 6);
    assert_eq!(rows[0]["frames"], rows[1]["frames"]);
    let hex = Command::new(env!("CARGO_BIN_EXE_icem-lan-dev"))
        .arg("--templates")
        .arg(templates())
        .args(["--format", "hex"])
        .output()?;
    assert!(hex.status.success());
    let text = String::from_utf8(hex.stdout)?;
    assert_eq!(text.lines().count(), 6);
    for line in text.lines() {
        assert_eq!(line.len(), 304);
        assert!(line.bytes().all(|c| c.is_ascii_hexdigit()));
    }
    Ok(())
}
#[test]
fn invalid_options_and_unknown_signal_do_not_emit_frames() -> TestResult {
    for (args, code) in [
        (vec![], 2),
        (vec!["--templates", "missing", "--ticks", "0"], 2),
        (vec!["--templates", "missing", "--emulate", "1"], 2),
        (
            vec!["--templates", "missing", "--set", "0x100000000:X=1"],
            2,
        ),
        (vec!["--templates", "missing", "--set", "0x1:X=NaN"], 2),
        (vec!["--templates", "missing", "--preset", "4"], 2),
    ] {
        let result = Command::new(env!("CARGO_BIN_EXE_icem-lan-dev"))
            .args(args)
            .output()?;
        assert_eq!(result.status.code(), Some(code));
        assert!(result.stdout.is_empty());
    }
    let result = Command::new(env!("CARGO_BIN_EXE_icem-lan-dev"))
        .arg("--templates")
        .arg(templates())
        .args(["--set", "0x13120206:unknown=1"])
        .output()?;
    assert_eq!(result.status.code(), Some(1));
    assert!(result.stdout.is_empty());
    Ok(())
}
