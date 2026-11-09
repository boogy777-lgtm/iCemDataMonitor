#![cfg(windows)]
use icem_web::protocol_control::{self, Command, Protocol};
use std::{
    net::{TcpListener, TcpStream},
    process::{Child, Command as ProcessCommand, Stdio},
    time::{Duration, Instant},
};

struct OwnedHost(Child);
impl Drop for OwnedHost {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
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
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            match protocol_control::request(&pipe, Command::Get).await {
                Ok(reply) => {
                    assert!(reply.opcua.running);
                    break;
                }
                Err(error) if Instant::now() >= deadline => return Err(error),
                Err(_) => tokio::time::sleep(Duration::from_millis(25)).await,
            }
        }
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
