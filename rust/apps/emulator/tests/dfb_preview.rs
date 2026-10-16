use std::process::Command;
type TestResult = Result<(), Box<dyn std::error::Error>>;
#[test]
fn executable_runs_cement_network_and_preserves_independent_outputs() -> TestResult {
    let output = Command::new(env!("CARGO_BIN_EXE_icem-dfb-dev"))
        .args([
            "--ticks",
            "2",
            "--interval-ms",
            "0",
            "--dt",
            "0.5",
            "--seed",
            "-2147483648",
            "--rate-a",
            "12",
            "--opko-a",
            "1000",
            "--unit",
            "4294967295",
        ])
        .output()?;
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let text = String::from_utf8(output.stdout)?;
    let rows: Vec<serde_json::Value> = text
        .lines()
        .map(serde_json::from_str)
        .collect::<Result<_, _>>()?;
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[1]["elapsed"], 1.0);
    assert_eq!(rows[1]["wire"]["Pump A Job Total"]["value"], 12.0);
    assert_eq!(rows[1]["wire"]["Side A Kicked Out?"]["value"], 1.0);
    assert_eq!(rows[1]["wire"]["Unit Number"]["value"], 4294967295.0);
    assert_eq!(rows[1]["wire"].as_object().ok_or("wire object")?.len(), 325);
    assert!(String::from_utf8_lossy(&output.stderr).contains("No hardware streams started"));
    Ok(())
}
#[test]
fn invalid_trace_options_emit_no_json() -> TestResult {
    for args in [
        vec!["--ticks", "0"],
        vec!["--dt", "NaN"],
        vec!["--dt", "0"],
        vec!["--seed", "2147483648"],
        vec!["--rate-a", "inf"],
        vec!["--job-active", "1"],
        vec!["--unknown", "x"],
    ] {
        let result = Command::new(env!("CARGO_BIN_EXE_icem-dfb-dev"))
            .args(args)
            .output()?;
        assert_eq!(result.status.code(), Some(2));
        assert!(result.stdout.is_empty());
    }
    Ok(())
}
