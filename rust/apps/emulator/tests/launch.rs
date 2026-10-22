use std::process::Command;
type Result = std::result::Result<(), Box<dyn std::error::Error>>;
#[test]
fn help_aliases_and_empty_redirected_cli() -> Result {
    let exe = env!("CARGO_BIN_EXE_icem-emulator-engine-dev");
    for key in ["--help", " -H ", "/?"] {
        let output = Command::new(exe)
            .args([key, "--config", "ignored.xml"])
            .output()?;
        assert!(output.status.success());
        let text = std::str::from_utf8(&output.stdout)?;
        assert!(
            text.contains("--cli") && text.contains("--selftest") && text.contains("--landiag")
        );
    }
    for args in [
        vec!["--cli"],
        vec![" --CLI "],
        vec!["--pipe", "absent-test-pipe", "--cli"],
    ] {
        let output = Command::new(exe).args(args).output()?;
        assert!(output.status.success());
        assert!(output.stdout.is_empty());
    }
    Ok(())
}
