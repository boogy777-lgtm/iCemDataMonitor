#![cfg(windows)]
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
    let config = directory.join("settings.xml");
    let pipe = format!("icem-engine-test-{}", std::process::id());
    let templates =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../iCemEmulator/templates");
    let spawn = || {
        Command::new(exe)
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
    assert_eq!(usage.status.code(), Some(2));
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
    let quit: serde_json::Value =
        serde_json::from_str(&request(&pipe, "quit", Duration::from_secs(3))?)?;
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
    Ok(())
}
