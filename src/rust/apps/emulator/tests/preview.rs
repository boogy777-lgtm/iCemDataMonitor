use std::process::Command;
type TestResult = Result<(), Box<dyn std::error::Error>>;
#[test]
fn real_executable_emits_32_flecs_fields_with_crlf_and_override() -> TestResult {
    let template = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../iCemEmulator/templates/flecs_4.53.json");
    let output = Command::new(env!("CARGO_BIN_EXE_icem-emulator-dev"))
        .arg("--template")
        .arg(template)
        .args([
            "--ticks",
            "2",
            "--interval-ms",
            "0",
            "--set",
            "PS Pump Press=1234.5",
        ])
        .output()?;
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let text = String::from_utf8(output.stdout)?;
    assert_eq!(text.matches("\r\n").count(), 2);
    for line in text.split_terminator("\r\n") {
        let columns: Vec<_> = line.split(',').collect();
        assert_eq!(columns.len(), 32);
        assert_eq!(columns[4], "1234.50");
    }
    assert!(String::from_utf8_lossy(&output.stderr).contains("No hardware streams started"));
    Ok(())
}
#[test]
fn usage_and_input_failures_do_not_print_wire_data() -> TestResult {
    for (args, code) in [
        (vec![], 2),
        (vec!["--template", "missing"], 1),
        (vec!["--template", "missing", "--ticks", "0"], 2),
        (vec!["--template", "missing", "--set", "X=NaN"], 2),
    ] {
        let result = Command::new(env!("CARGO_BIN_EXE_icem-emulator-dev"))
            .args(args)
            .output()?;
        assert_eq!(result.status.code(), Some(code));
        assert!(result.stdout.is_empty());
    }
    Ok(())
}
