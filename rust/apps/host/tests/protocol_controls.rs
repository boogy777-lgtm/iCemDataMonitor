#![cfg(windows)]
#[path = "fixtures/test_directory.rs"]
mod test_directory;
use icem_web::protocol_control::{self, Command, Protocol};
use std::{
    net::{TcpListener, TcpStream},
    process::{Child, Command as ProcessCommand, Stdio},
    time::{Duration, Instant},
};
use test_directory::TestDirectory;

struct OwnedHost(Child);
impl Drop for OwnedHost {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

impl TestDirectory {
    fn host(&self, pipe: &str) -> ProcessCommand {
        let mut command = ProcessCommand::new(self.path().join("icem-host-dev.exe"));
        command
            .env("PROGRAMDATA", self.path().join("programdata"))
            .env("LOCALAPPDATA", self.path().join("localappdata"))
            .current_dir(self.path())
            .arg("--autostart")
            .arg("--station-config")
            .arg(self.path().join("config.ini"))
            .arg("--tray-icon")
            .arg(
                std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                    .join("../../assets/iCemDataMonitor.ico"),
            )
            .args(["--bind", "127.0.0.1:0", "--web-bind", "127.0.0.1:0"])
            .args(["--rpc-pipe", pipe, "--duration-ms", "15000"])
            .stdout(Stdio::null())
            .stderr(Stdio::inherit());
        command
    }
    fn station(&self, pipe: &str, on: bool, save: bool) -> std::io::Result<String> {
        let station = icem_config::IniOptions {
            opcda_on: i32::from(on),
            ui_set_as_default: i32::from(save),
            modbus_on: 0,
            lansniffer_on: 0,
            mmf_path: self
                .path()
                .join("absent-bus.dat")
                .to_string_lossy()
                .into_owned(),
            mmf_name: format!("Local\\{pipe}-bus"),
            ..Default::default()
        };
        let original = format!("\u{feff}; operator comment\r\n{}", station.render());
        std::fs::write(self.path().join("config.ini"), &original)?;
        Ok(original)
    }
}

async fn wait_protocol(pipe: &str) -> Result<protocol_control::Reply, String> {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match protocol_control::request(pipe, Command::Get).await {
            Ok(reply) => return Ok(reply),
            Err(error) if Instant::now() >= deadline => return Err(error),
            Err(_) => tokio::time::sleep(Duration::from_millis(25)).await,
        }
    }
}

fn assert_da_start_failure(host: &mut OwnedHost) -> Result<(), Box<dyn std::error::Error>> {
    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = host.0.try_wait()? {
            break status;
        }
        if Instant::now() >= deadline {
            return Err("DA startup did not fail before the deadline".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    };
    assert_eq!(status.code(), Some(1));
    let mut error = String::new();
    std::io::Read::read_to_string(host.0.stderr.as_mut().ok_or("missing stderr")?, &mut error)?;
    assert!(error.contains("OPC DA spawn"), "{error}");
    Ok(())
}

#[test]
fn installed_da_plan_and_failure_reach_the_real_pipe_without_touching_ini()
-> Result<(), Box<dyn std::error::Error>> {
    let root = TestDirectory::for_host(
        std::env::temp_dir().join(format!("icem-installed-da-plan-{}", std::process::id())),
    )?;
    // Deliberately omit the sidecar. No COM class or production installation is touched.
    let exe = root.path().join("icem-host-dev.exe");
    std::fs::copy(env!("CARGO_BIN_EXE_icem-host-dev"), &exe)?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;

    for save in [false, true] {
        let reserved = TcpListener::bind("127.0.0.1:0")?;
        let address = reserved.local_addr()?;
        drop(reserved);
        let ua = root.path().join("ua.json");
        std::fs::write(
            &ua,
            format!(
                r#"{{"bind":"{address}","profile":"loopback-anonymous-none","update_interval_ms":200}}"#
            ),
        )?;
        let pipe = format!("icem-installed-da-{}-{save}", std::process::id());
        let ini = root.path().join("config.ini");
        let original = root.station(&pipe, false, save)?;
        let host = OwnedHost(root.host(&pipe).arg("--opcua-config").arg(&ua).spawn()?);
        runtime.block_on(async {
            let initial = wait_protocol(&pipe).await?;
            assert!(initial.opcua.running && initial.opcda.configured);
            assert!(!initial.opcda.enabled && !initial.opcda.running);
            assert!(initial.opcda.error.is_empty());
            assert!(TcpStream::connect(address).is_ok());

            // A successful live control is session-only, even when the legacy
            // persistence checkbox is set; no unapproved UA key enters config.ini.
            let stop = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcUa,
                    enabled: false,
                },
            )
            .await?;
            assert!(stop.ok && !stop.opcua.enabled && !stop.opcua.running);
            let failed = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcDa,
                    enabled: true,
                },
            )
            .await?;
            assert!(!failed.ok && failed.opcda.error.contains("OPC DA spawn"));
            let observed = protocol_control::request(&pipe, Command::Get).await?;
            assert!(!observed.opcda.enabled && !observed.opcda.running);
            assert!(observed.opcda.error.contains("OPC DA spawn"));
            Ok::<(), String>(())
        })?;
        assert_eq!(std::fs::read(&ini)?, original.as_bytes());
        drop(host);
    }

    // ON with an absent bundled sidecar retains the existing fail-fast behavior.
    let ini = root.path().join("config.ini");
    let mut station = icem_config::IniOptions::read_existing(&ini)?;
    station.opcda_on = 1;
    std::fs::write(&ini, station.render())?;
    let original = std::fs::read(&ini)?;
    let mut host = OwnedHost(
        root.host(&format!("icem-missing-da-{}", std::process::id()))
            .stderr(Stdio::piped())
            .spawn()?,
    );
    assert_da_start_failure(&mut host)?;
    assert_eq!(std::fs::read(&ini)?, original);
    Ok(())
}

#[test]
fn installed_da_controls_own_a_test_ipc_peer_without_persisting_the_plan()
-> Result<(), Box<dyn std::error::Error>> {
    let root = TestDirectory::for_host(
        std::env::temp_dir().join(format!("icem-installed-da-ipc-{}", std::process::id())),
    )?;
    let exe = root.path().join("icem-host-dev.exe");
    std::fs::copy(env!("CARGO_BIN_EXE_icem-host-dev"), &exe)?;
    let peer = root.path().join("icem-opc-da-sidecar.exe");
    let source =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/da_ipc_peer.rs");
    let compiler = std::env::var_os("RUSTC").unwrap_or_else(|| "rustc".into());
    let status = ProcessCommand::new(compiler)
        .args([
            "--edition=2024",
            "-C",
            "opt-level=2",
            "-C",
            "debug-assertions=no",
        ])
        .arg(source)
        .arg("-o")
        .arg(&peer)
        .status()?;
    assert!(status.success(), "test IPC peer did not compile");
    let marker = root.path().join("da-ipc-starts.log");
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;

    for (on, save) in [(false, false), (true, true)] {
        let pipe = format!("icem-da-ipc-{}-{on}", std::process::id());
        let ini = root.path().join("config.ini");
        let original = root.station(&pipe, on, save)?;
        let host = OwnedHost(root.host(&pipe).spawn()?);
        runtime.block_on(async {
            let initial = wait_protocol(&pipe).await?;
            assert!(initial.opcda.configured && initial.opcda.error.is_empty());
            assert_eq!(initial.opcda.running, on);
            assert_eq!(initial.opcda.enabled, on);
            if on {
                let stop = protocol_control::request(
                    &pipe,
                    Command::Set {
                        protocol: Protocol::OpcDa,
                        enabled: false,
                    },
                )
                .await?;
                assert!(stop.ok && !stop.opcda.enabled && !stop.opcda.running);
            } else {
                assert!(!marker.exists());
            }
            let start = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcDa,
                    enabled: true,
                },
            )
            .await?;
            assert!(start.ok && start.opcda.enabled && start.opcda.running);
            let stop = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcDa,
                    enabled: false,
                },
            )
            .await?;
            assert!(stop.ok && !stop.opcda.enabled && !stop.opcda.running);
            let observed = protocol_control::request(&pipe, Command::Get).await?;
            assert!(!observed.opcda.enabled && !observed.opcda.running);
            Ok::<(), String>(())
        })?;
        assert_eq!(std::fs::read(&ini)?, original.as_bytes());
        assert_eq!(
            std::fs::read_to_string(&marker)?.lines().count(),
            if on { 2 } else { 1 }
        );
        drop(host);
        std::fs::remove_file(&marker)?;
    }

    // An explicit missing executable wins over the working bundled test peer.
    let explicit = root.path().join("explicit-da.json");
    std::fs::write(
        &explicit,
        br#"{"sidecar":"missing-explicit.exe","update_interval_ms":200,"watchdog_ms":1000}"#,
    )?;
    let ini = root.path().join("config.ini");
    let original = std::fs::read(&ini)?;
    let mut host = OwnedHost(
        root.host(&format!("icem-explicit-da-{}", std::process::id()))
            .arg("--opc-da-config")
            .arg(&explicit)
            .stderr(Stdio::piped())
            .spawn()?,
    );
    assert_da_start_failure(&mut host)?;
    assert!(!marker.exists());
    assert_eq!(std::fs::read(&ini)?, original);
    Ok(())
}

#[test]
fn private_pipe_controls_real_owned_ua_listener() -> Result<(), Box<dyn std::error::Error>> {
    let reserved = TcpListener::bind("127.0.0.1:0")?;
    let address = reserved.local_addr()?;
    drop(reserved);
    let root = std::env::temp_dir().join(format!("icem-protocol-pipe-{}", std::process::id()));
    std::fs::create_dir(&root)?;
    let program_data = root.join("programdata");
    let local_data = root.join("localappdata");
    std::fs::create_dir(&program_data)?;
    std::fs::create_dir(&local_data)?;
    let config = root.join("ua.json");
    std::fs::write(
        &config,
        format!(
            r#"{{"bind":"{address}","profile":"loopback-anonymous-none","update_interval_ms":200}}"#
        ),
    )?;
    let pipe = format!("icem-protocol-test-{}", std::process::id());
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/bus-v5-minimal.bin");
    let host = OwnedHost(
        ProcessCommand::new(env!("CARGO_BIN_EXE_icem-host-dev"))
            .env("PROGRAMDATA", &program_data)
            .env("LOCALAPPDATA", &local_data)
            .current_dir(&root)
            .arg("--modbus-fixture")
            .arg(fixture)
            .args([
                "--bind",
                "127.0.0.1:0",
                "--rpc-pipe",
                &pipe,
                "--duration-ms",
                "15000",
                "--opcua-config",
            ])
            .arg(&config)
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()?,
    );
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        assert!(wait_protocol(&pipe).await?.opcua.running);
        for _ in 0..2 {
            let stop = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcUa,
                    enabled: false,
                },
            )
            .await?;
            assert!(stop.ok && !stop.opcua.running && !stop.opcua.enabled);
            let occupied = TcpListener::bind(address).map_err(|e| e.to_string())?;
            let failed = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcUa,
                    enabled: true,
                },
            )
            .await?;
            assert!(!failed.ok && !failed.opcua.enabled && !failed.opcua.error.is_empty());
            drop(occupied);
            let observed = protocol_control::request(&pipe, Command::Get).await?;
            assert!(!observed.opcua.enabled && !observed.opcua.error.is_empty());
            let started = protocol_control::request(
                &pipe,
                Command::Set {
                    protocol: Protocol::OpcUa,
                    enabled: true,
                },
            )
            .await?;
            assert!(
                started.ok && started.opcua.enabled && started.opcua.running,
                "{started:?}"
            );
            assert!(TcpStream::connect(address).is_ok());
        }
        let da = protocol_control::request(
            &pipe,
            Command::Set {
                protocol: Protocol::OpcDa,
                enabled: true,
            },
        )
        .await?;
        assert!(!da.ok && !da.opcda.configured && da.opcda.error.contains("--opc-da-config"));
        Ok::<(), String>(())
    })?;
    drop(host);
    std::fs::remove_dir_all(root)?;
    Ok(())
}
