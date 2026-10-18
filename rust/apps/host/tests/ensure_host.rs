#![cfg(windows)]
use icem_config::IniOptions;
use icem_web::{
    client,
    host_client::{self, Launch},
    proto::*,
};
use std::{
    fs,
    path::PathBuf,
    process::{Command, Stdio},
    time::{Duration, Instant},
};
type TestResult = Result<(), Box<dyn std::error::Error + Send + Sync>>;
struct Station(PathBuf);
impl Drop for Station {
    fn drop(&mut self) {
        let _ = icem_windows_native::signal_existing_event(icem_platform_windows::HOST_SHUTDOWN);
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn launch(config: &std::path::Path, pipe: &str) -> Launch {
    Launch {
        executable: env!("CARGO_BIN_EXE_icem-host-dev").into(),
        arguments: vec![
            "--station-config".into(),
            config.to_string_lossy().into_owned(),
            "--rpc-pipe".into(),
            pipe.into(),
            "--duration-ms".into(),
            "0".into(),
            "--bind".into(),
            "127.0.0.1:0".into(),
            "--web-bind".into(),
            "127.0.0.1:0".into(),
        ],
    }
}
#[test]
fn actual_host_missing_concurrent_existing_failed_config_and_read_only_cli() -> TestResult {
    let root = std::env::temp_dir().join(format!("icem-first-run-{}", std::process::id()));
    fs::create_dir(&root)?;
    let station = Station(root);
    let config = station.0.join("config.ini");
    let pipe = format!("icem-ensure-{}", std::process::id());
    let candidate = launch(&config, &pipe);
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        // Both clients can observe absence. The losing Host exits before config I/O;
        // both clients join the winner, without a second singleton/launch retry loop.
        let (a, b) = tokio::join!(
            host_client::ensure(&pipe, Some(&candidate), Duration::from_secs(10)),
            host_client::ensure(&pipe, Some(&candidate), Duration::from_secs(10))
        );
        a?;
        b?;
        let original = fs::read(&config)?;
        assert_eq!(
            IniOptions::read_existing(&config).map_err(|e| e.to_string())?,
            IniOptions::default()
        );
        let missing = Launch {
            executable: station.0.join("absent.exe"),
            arguments: vec![],
        };
        host_client::ensure(&pipe, Some(&missing), Duration::from_secs(2)).await?; // serving owner: no spawn
        let mut rpc = client::connect(&pipe).await?;
        assert!(
            !rpc.get_status(HostStatusRequest {})
                .await?
                .into_inner()
                .bus_open
        );
        drop(rpc);
        // A direct duplicate cannot parse/repair even a deliberately corrupt input.
        let corrupt = station.0.join("corrupt.ini");
        fs::write(&corrupt, b"broken")?;
        let status = Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
            .args(["--station-config"])
            .arg(&corrupt)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()?;
        assert_eq!(status.code(), Some(host_client::ALREADY_RUNNING));
        assert_eq!(fs::read(&corrupt)?, b"broken");
        assert_eq!(fs::read(&config)?, original);
        assert!(icem_windows_native::signal_existing_event(
            icem_platform_windows::HOST_SHUTDOWN
        )?);
        let due = Instant::now() + Duration::from_secs(5);
        while host_client::probe(&pipe).await != host_client::Presence::Absent {
            if Instant::now() >= due {
                return Err("Host failed to stop".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        // Owner teardown may follow pipe teardown. Wait for the old process to
        // release the mutex, using a zero-time ownership attempt only in the test.
        let mutex =
            icem_windows_native::NamedMutex::open_or_create(icem_platform_windows::HOST_MUTEX)?;
        loop {
            if let Some(guard) = mutex.lock(Duration::ZERO)? {
                drop(guard);
                break;
            }
            if Instant::now() >= due {
                return Err("Host ownership leaked after stop".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        let bad = launch(&corrupt, &pipe);
        let error = host_client::ensure(&pipe, Some(&bad), Duration::from_secs(3))
            .await
            .err()
            .ok_or("corruption was accepted")?;
        assert!(error.contains("config.ini"));
        assert_eq!(fs::read(&corrupt)?, b"broken");
        assert!(
            host_client::ensure(&pipe, Some(&missing), Duration::from_secs(1))
                .await
                .is_err()
        );
        Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    // CLI dispatch must precede config restoration and Host ownership. --start is
    // absent: read-only failure cannot create station files or listeners.
    let programdata = station.0.join("empty-programdata");
    let output = Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
        .arg("header")
        .env("PROGRAMDATA", &programdata)
        .output()?;
    assert_eq!(output.status.code(), Some(2));
    assert!(output.stdout.is_empty());
    assert!(!programdata.exists());
    let output = Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
        .arg("stop")
        .output()?;
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8(output.stdout)?.trim(),
        "{\"action\":\"stop\",\"result\":\"notRunning\"}"
    );
    Ok(())
}
