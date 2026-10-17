#![cfg(windows)]
use icem_web::{client, proto::*};
use std::{
    net::{TcpListener, TcpStream},
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};
struct Process(Child);
impl Drop for Process {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}
#[test]
fn grpc_host_toggle_ignores_desired_state_and_stops_listener()
-> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let port = TcpListener::bind("127.0.0.1:0")?.local_addr()?;
    let pipe = format!("icem-grpc-host-{}", std::process::id());
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/bus-v5-minimal.bin");
    let mut host = Process(
        Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
            .arg("--modbus-fixture")
            .arg(fixture)
            .args([
                "--bind",
                &port.to_string(),
                "--rpc-pipe",
                &pipe,
                "--duration-ms",
                "15000",
            ])
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()?,
    );
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut client = loop {
            match client::connect(&pipe).await {
                Ok(client) => break client,
                Err(e) if Instant::now() < deadline => {
                    let _ = e;
                    tokio::time::sleep(Duration::from_millis(25)).await;
                }
                Err(e) => return Err(e),
            }
        };
        let status = client.get_status(HostStatusRequest {}).await?.into_inner();
        assert!(status.bus_open);
        assert!(status.layers.ok_or("layers missing")?.modbus_bound);
        for expected in [false, true] {
            let layers = client
                .set_layer(LayerControlRequest {
                    layer: 1,
                    desired_state: false,
                })
                .await?
                .into_inner();
            assert_eq!(layers.modbus_bound, expected);
            assert_eq!(
                TcpStream::connect_timeout(&port, Duration::from_secs(1)).is_ok(),
                expected
            );
        }
        Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    assert!(icem_windows_native::signal_existing_event(
        icem_platform_windows::HOST_SHUTDOWN
    )?);
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(code) = host.0.try_wait()? {
            assert!(code.success());
            break;
        }
        if Instant::now() >= deadline {
            return Err("Host did not exit after shutdown event".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    assert!(TcpStream::connect_timeout(&port, Duration::from_secs(1)).is_err());
    Ok(())
}
