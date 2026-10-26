#![cfg(windows)]
#[path = "support/selftest.rs"]
mod support;
use support::{Result, Stage, report, snapshot};
#[test]
fn relocated_readonly_assets_first_argument_dispatch_and_missing_assets() -> Result {
    let stage = Stage::new("relocated")?;
    // Actual package role is intentionally distinct from Host's templates directory.
    let manifest: serde_json::Value =
        serde_json::from_str(include_str!("../../../installer/windows-payload.json"))?;
    assert_eq!(
        manifest["binaries"]
            .as_array()
            .ok_or("binaries")?
            .iter()
            .find(|b| b["role"] == "emulator")
            .ok_or("emulator")?["templates_directory"],
        "emulator/templates"
    );
    stage.readonly(true)?;
    let before = snapshot(&stage.root)?;
    let output = stage.run(&[
        "  --SeLfTeSt  ",
        "--pipe",
        "iCemEmule.Control",
        "--config",
        "ignored.xml",
        "--garbage",
    ])?;
    report(&output, true)?;
    assert_eq!(
        snapshot(&stage.root)?,
        before,
        "selftest must read only even with ignored config/pipe suffix"
    );
    stage.readonly(false)?;
    let missing = stage.templates.join("firmware_registry.json");
    let bytes = std::fs::read(&missing)?;
    std::fs::remove_file(&missing)?;
    // Host assets cannot satisfy the emulator asset contract.
    let host = stage.root.join("package/templates");
    std::fs::create_dir_all(&host)?;
    std::fs::write(host.join("firmware_registry.json"), bytes)?;
    let before = snapshot(&stage.root)?;
    let output = stage.run(&["--selftest"])?;
    report(&output, false)?;
    assert!(String::from_utf8_lossy(&output.stdout).contains("required emulator asset missing"));
    assert_eq!(snapshot(&stage.root)?, before);
    Ok(())
}
#[test]
fn firmware_failure_count_above255_and_late_failure_are_real_process_results() -> Result {
    let stage = Stage::new("fail-count")?;
    let path = stage.templates.join("firmware_registry.json");
    let text = std::fs::read_to_string(&path)?;
    let mut root: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}'))?;
    let mut changed = 0;
    for vector in root["serviceVectors"].as_array_mut().ok_or("vectors")? {
        for signal in vector["signals"].as_array_mut().ok_or("signals")? {
            signal["uom"] = "bbl/min".into();
            changed += 1;
        }
    }
    assert_eq!(changed, 267);
    std::fs::write(&path, serde_json::to_vec(&root)?)?;
    let output = stage.run(&["--selftest"])?;
    let count = report(&output, false)?;
    assert!(count > 255, "{count}");
    assert!(String::from_utf8_lossy(&output.stdout).contains("T26 PASS"));
    // Catalog remains parseable, so the late real-template assertion is reached.
    let fresh = Stage::new("late")?;
    for file in ["flecs_4.53.json", "plc_basic.json"] {
        let path = fresh.templates.join(file);
        let text = std::fs::read_to_string(&path)?;
        let mut template: serde_json::Value =
            serde_json::from_str(text.trim_start_matches('\u{feff}'))?;
        template["variables"] = serde_json::json!([]);
        std::fs::write(path, serde_json::to_vec(&template)?)?;
    }
    let output = fresh.run(&["--selftest"])?;
    report(&output, false)?;
    let text = String::from_utf8(output.stdout)?;
    assert!(text.contains("T23 FAIL"));
    assert!(text.contains("T24 PASS") && text.contains("T26 PASS"));
    Ok(())
}

#[test]
fn closed_stdout_cannot_report_a_success_exit() -> Result {
    use std::{
        io::{Read, Write},
        process::{Command, Stdio},
        time::{Duration, Instant},
    };
    let stage = Stage::new("closed-output")?;
    // The shell cannot launch the EXE until the parent releases its stdin gate.
    // This avoids racing the first stdout write against closing the read handle.
    std::fs::write(
        stage.cwd.join("selftest-gate.cmd"),
        b"@echo off\r\nset /p ICEM_SELFTEST_GATE=\r\n\"%ICEM_SELFTEST_EXE%\" --selftest\r\nexit /b %errorlevel%\r\n",
    )?;
    let mut child = Command::new("cmd.exe")
        .args(["/d", "/v:off", "/c", "selftest-gate.cmd"])
        .current_dir(&stage.cwd)
        .env("LOCALAPPDATA", &stage.local)
        .env("ICEM_SELFTEST_EXE", &stage.exe)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    let stderr = child.stderr.take();
    let reader = std::thread::spawn(move || -> std::io::Result<Vec<u8>> {
        let mut stderr = stderr.ok_or_else(|| std::io::Error::other("missing stderr"))?;
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes)?;
        Ok(bytes)
    });
    let result = (|| -> Result {
        drop(child.stdout.take().ok_or("missing stdout")?);
        let mut gate = child.stdin.take().ok_or("missing stdin gate")?;
        gate.write_all(b"go\r\n")?;
        drop(gate);
        let deadline = Instant::now() + Duration::from_secs(45);
        loop {
            if let Some(status) = child.try_wait()? {
                if status.code() != Some(1) {
                    return Err(format!("closed-output selftest exit {status}, expected 1").into());
                }
                return Ok(());
            }
            if Instant::now() >= deadline {
                return Err("closed-output child deadline exceeded".into());
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    })();
    if result.is_err() {
        // Only this owned shell's tree: its engine must also stop on timeout/error.
        let mut cleanup = Command::new("taskkill.exe");
        cleanup.args(["/PID", &child.id().to_string(), "/T", "/F"]);
        let _ = support::bounded(cleanup);
        let _ = child.kill();
    }
    let _ = child.wait();
    let diagnostics = reader.join().map_err(|_| "stderr reader panicked")??;
    result?;
    let diagnostics = String::from_utf8(diagnostics)?;
    assert!(
        diagnostics.contains("selftest output/setup failed:"),
        "expected the actual Rust output-error diagnostic, got {diagnostics:?}"
    );
    Ok(())
}
