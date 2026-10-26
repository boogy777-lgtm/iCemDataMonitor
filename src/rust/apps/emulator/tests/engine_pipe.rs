#![cfg(windows)]
#[path = "support/selftest.rs"]
mod selftest_support;
use icem_transport::emulator_control::request;
use std::{
    path::PathBuf,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};
struct EngineProcess {
    child: Child,
    directory: PathBuf,
}
impl Drop for EngineProcess {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.directory);
    }
}
type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
#[test]
fn headless_engine_cli_shared_state_quit_ack_and_clean_rebind() -> Result {
    let exe = env!("CARGO_BIN_EXE_icem-emulator-engine-dev");
    let directory = std::env::temp_dir().join(format!("icem-engine-{}", std::process::id()));
    std::fs::create_dir_all(&directory)?;
    for args in [
        vec!["--desktop"],
        vec!["--config", "unused.xml"],
        vec!["--templates", "unused"],
    ] {
        let output = Command::new(exe).args(args).output()?;
        assert_eq!(output.status.code(), Some(2));
        assert!(
            String::from_utf8_lossy(&output.stderr)
                .contains("--config and --templates are required")
        );
    }
    let config = directory.join("settings.xml");
    let pipe = format!("icem-engine-test-{}", std::process::id());
    let templates =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../net/iCemEmulator/templates");
    let spawn = || {
        Command::new(exe)
            .env("LOCALAPPDATA", directory.join("local"))
            .arg("--config")
            .arg(&config)
            .arg("--templates")
            .arg(&templates)
            .args(["--pipe", &pipe, "--duration-ms", "30000"])
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()
    };
    let mut process = EngineProcess {
        child: spawn()?,
        directory: directory.clone(),
    };
    let start = Instant::now();
    loop {
        if request(&pipe, "status", Duration::from_millis(250)).is_ok() {
            break;
        }
        if let Some(code) = process.child.try_wait()? {
            return Err(format!("engine exited before READY: {code}").into());
        }
        if start.elapsed() > Duration::from_secs(10) {
            return Err("engine readiness timeout".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    let status: serde_json::Value =
        serde_json::from_str(&request(&pipe, "status", Duration::from_secs(3))?)?;
    assert_eq!(status["data"]["proto"], 1);
    assert_eq!(status["data"]["com"]["streaming"], false);
    assert_eq!(status["data"]["lan"]["streaming"], false);
    let cli = Command::new(exe)
        .args(["--pipe", &pipe, "--cli", "setequipment", "0x12345678"])
        .output()?;
    assert!(
        cli.status.success(),
        "{}",
        String::from_utf8_lossy(&cli.stderr)
    );
    let response: serde_json::Value = serde_json::from_slice(&cli.stdout)?;
    assert_eq!(response["ok"], true);
    let updated: serde_json::Value =
        serde_json::from_str(&request(&pipe, "status", Duration::from_secs(3))?)?;
    assert_eq!(updated["data"]["lan"]["equipment"], "0x12345678");
    assert!(std::fs::read_to_string(&config)?.contains("0x12345678"));
    // Redirected stdin sends each line through the same shared client.
    let stdin_cli = |bytes: &[u8]| -> Result<std::process::Output> {
        let input = directory.join("cli-input.txt");
        std::fs::write(&input, bytes)?;
        let mut command = Command::new(exe);
        command
            .args(["--pipe", &pipe, "--cli"])
            .stdin(Stdio::from(std::fs::File::open(input)?));
        selftest_support::bounded(command)
    };
    let batch = stdin_cli(b"status\r\nsetpreset 4\rsetequipment 0x12345678\nstatus")?;
    assert_eq!(batch.status.code(), Some(1));
    let replies = std::str::from_utf8(&batch.stdout)?
        .lines()
        .map(serde_json::from_str::<serde_json::Value>)
        .collect::<std::result::Result<Vec<_>, _>>()?;
    assert_eq!(replies.len(), 4);
    assert_eq!(
        replies
            .iter()
            .map(|v| v["ok"].as_bool())
            .collect::<Vec<_>>(),
        [Some(true), Some(false), Some(true), Some(true)]
    );
    assert_eq!(replies[3]["data"]["lan"]["equipment"], "0x12345678");
    let mut oversized = vec![b'x'; icem_transport::emulator_control::MAX_LINE_BYTES + 1];
    oversized.extend_from_slice(b"\nsetequipment 99\n");
    let rejected = stdin_cli(&oversized)?;
    assert_eq!(rejected.status.code(), Some(1));
    assert_eq!(std::str::from_utf8(&rejected.stdout)?.lines().count(), 1);
    let after: serde_json::Value =
        serde_json::from_str(&request(&pipe, "status", Duration::from_secs(3))?)?;
    assert_eq!(after["data"]["lan"]["equipment"], "0x12345678");
    // Selftest is standalone even while the live engine owns its singleton and pipe.
    let mutex = icem_platform_windows::emulator_mutex()?;
    assert!(mutex.lock(Duration::ZERO)?.is_none());
    let staged = selftest_support::Stage::new("live")?;
    staged.readonly(true)?;
    let config_before = std::fs::read(&config)?;
    let live_files_before = selftest_support::snapshot(&directory)?;
    let staged_before = selftest_support::snapshot(&staged.root)?;
    let output = staged.run(&[
        "--selftest",
        "--pipe",
        &pipe,
        "--config",
        config.to_str().ok_or("config path")?,
    ])?;
    selftest_support::report(&output, true)?;
    assert!(mutex.lock(Duration::ZERO)?.is_none());
    assert_eq!(std::fs::read(&config)?, config_before);
    assert_eq!(selftest_support::snapshot(&directory)?, live_files_before);
    assert_eq!(selftest_support::snapshot(&staged.root)?, staged_before);
    let live_after: serde_json::Value =
        serde_json::from_str(&request(&pipe, "status", Duration::from_secs(3))?)?;
    for channel in ["com", "lan"] {
        for key in [
            "streaming",
            "portOpen",
            "port",
            "linesSent",
            "adapterOpen",
            "adapter",
            "framesSent",
            "equipment",
            "svCount",
            "targets",
        ] {
            assert_eq!(
                updated["data"][channel].get(key),
                live_after["data"][channel].get(key),
                "live {channel}.{key} changed"
            );
        }
    }
    // CLI uses the same subscription consumer for text and JSON, without a second engine.
    for command in ["watch", r#"{"cmd":"watch"}"#] {
        use std::io::BufRead;
        let mut cli = Command::new(exe)
            .args(["--pipe", &pipe, "--cli", command])
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?;
        let output = cli.stdout.take().ok_or("CLI stdout missing")?;
        let (sent, received) = std::sync::mpsc::sync_channel(1);
        let reader = std::thread::spawn(move || {
            let lines = std::io::BufReader::new(output)
                .lines()
                .take(3)
                .collect::<std::io::Result<Vec<_>>>();
            let _ = sent.send(lines);
        });
        let lines = received.recv_timeout(Duration::from_secs(7));
        let _ = cli.kill();
        let _ = cli.wait();
        reader.join().map_err(|_| "CLI reader panicked")?;
        let lines = lines??;
        assert_eq!(lines.len(), 3);
        for (line, expected) in lines.iter().zip(["watch", "status", "status"]) {
            let value: serde_json::Value = serde_json::from_str(line)?;
            assert_eq!(value["ok"], true);
            assert_eq!(value["cmd"], expected);
        }
        assert!(request(&pipe, "status", Duration::from_secs(3)).is_ok());
    }
    let rejected = Command::new(exe)
        .args(["--pipe", &pipe, "--cli", "setpreset", "4"])
        .output()?;
    assert_eq!(rejected.status.code(), Some(1));
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&rejected.stdout)?["ok"],
        false
    );
    let absent_pipe = format!("{pipe}-absent");
    let absent = Command::new(exe)
        .args(["--pipe", &absent_pipe, "--cli", "status"])
        .output()?;
    assert_eq!(absent.status.code(), Some(1));
    let absent_json: serde_json::Value = serde_json::from_slice(&absent.stdout)?;
    assert_eq!(absent_json["ok"], false);
    assert!(
        absent_json["error"]
            .as_str()
            .is_some_and(|e| e.contains("unavailable"))
    );
    let usage = Command::new(exe)
        .args(["--pipe", &absent_pipe, "--cli"])
        .output()?;
    assert_eq!(usage.status.code(), Some(0));
    assert!(usage.stdout.is_empty());
    // Persistence fails after applying; v1 stays compatible and status reports actual memory.
    std::fs::remove_file(&config)?;
    std::fs::create_dir(&config)?;
    let partial: serde_json::Value =
        serde_json::from_str(&request(&pipe, "setequipment 42", Duration::from_secs(3))?)?;
    assert_eq!(partial["ok"], true);
    let changed: serde_json::Value =
        serde_json::from_str(&request(&pipe, "status", Duration::from_secs(3))?)?;
    assert_eq!(changed["data"]["lan"]["equipment"], "0x0000002A");
    std::fs::remove_dir(&config)?;
    request(&pipe, "setequipment 42", Duration::from_secs(3))?;
    let second = spawn()?.wait()?;
    assert!(!second.success(), "singleton must reject a second engine");
    let quit_output = stdin_cli(b" QuIt \nsetequipment 99\n")?;
    assert!(quit_output.status.success());
    let quit: serde_json::Value = serde_json::from_slice(&quit_output.stdout)?;
    assert_eq!(quit["ok"], true);
    assert_eq!(quit["data"]["quitting"], true);
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(status) = process.child.try_wait()? {
            assert!(status.success());
            break;
        }
        if Instant::now() > deadline {
            return Err("quit did not stop engine".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    assert!(
        std::fs::read_to_string(&config)?.contains("0x0000002A"),
        "CLI consumed mutation after textual quit"
    );
    // The same singleton and pipe can be acquired by a successor.
    process.child = spawn()?;
    let start = Instant::now();
    loop {
        if request(&pipe, "status", Duration::from_millis(250)).is_ok() {
            break;
        }
        if start.elapsed() > Duration::from_secs(5) {
            return Err("successor did not acquire pipe".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    // Even a client that closes without reading its quit ACK must stop the
    // successor after the bounded ACK drain, rather than leave it running.
    {
        use std::io::Write;
        let mut client = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(format!(r"\\.\pipe\{pipe}"))?;
        client.write_all(b"quit\n")?;
    }
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(status) = process.child.try_wait()? {
            assert!(status.success());
            break;
        }
        if Instant::now() > deadline {
            return Err("disconnected quit left engine running".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    // No args and unknown args discover station settings and EXE-relative catalogs.
    // The intentionally absent icon stops before GPUI; both roots are isolated.
    for args in [vec![], vec!["unknown", "--cli", "status"]] {
        let staged = selftest_support::Stage::new("default-ui")?;
        let program_data = staged.root.join("program data");
        let paths = icem_config::emulator::SettingsPaths::station(&program_data, &staged.local);
        let legacy = paths.legacy.as_ref().ok_or("legacy path")?;
        std::fs::create_dir_all(legacy.parent().ok_or("legacy parent")?)?;
        let settings = icem_config::emulator::EmuSettings {
            lan_equipment: 0x76543210,
            ..Default::default()
        };
        let original = settings.to_xml();
        std::fs::write(legacy, &original)?;
        std::fs::create_dir_all(staged.cwd.join("emulator/templates"))?;
        for name in [
            "lantable_registry.json",
            "firmware_registry.json",
            "variable_registry.json",
        ] {
            std::fs::write(
                staged.cwd.join("emulator/templates").join(name),
                "corrupt cwd catalog",
            )?;
        }
        let mut command = staged.command();
        command.env("PROGRAMDATA", &program_data).args(args);
        let output = selftest_support::bounded(command)?;
        assert_eq!(output.status.code(), Some(1));
        assert!(
            std::str::from_utf8(&output.stderr)?.contains("application asset missing"),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(std::fs::read_to_string(legacy)?, original);
        let loaded = icem_config::emulator::EmuSettings::from_xml(&std::fs::read_to_string(
            &paths.canonical,
        )?)?;
        assert_eq!(loaded.lan_equipment, 0x76543210);
        assert!(!loaded.lan_enabled && !loaded.com_enabled);
        assert!(
            mutex.lock(Duration::ZERO)?.is_some(),
            "default UI did not release singleton"
        );
    }
    Ok(())
}
