#![cfg(windows)]
use icem_web::{client, proto::*};
use std::{
    os::windows::process::CommandExt,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};
struct Owned(Child);
impl Drop for Owned {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}
fn wait(
    child: &mut Child,
    limit: Duration,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let deadline = Instant::now() + limit;
    loop {
        if let Some(status) = child.try_wait()? {
            if !status.success() {
                return Err(format!("child exited {status}").into());
            }
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err("child deadline exceeded".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}
#[test]
fn headless_console_rpc_reconnect_ctrl_c_tears_down_host()
-> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let pipe = format!("icem-console-stop-{}", std::process::id());
    let port = std::net::TcpListener::bind("127.0.0.1:0")?.local_addr()?;
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/bus-v5-minimal.bin");
    let mut host = Owned(
        Command::new(env!("CARGO_BIN_EXE_icem-host-dev"))
            .arg("--modbus-fixture")
            .arg(fixture)
            .args([
                "--bind",
                &port.to_string(),
                "--rpc-pipe",
                &pipe,
                "--duration-ms",
                "30000",
            ])
            .creation_flags(0x0800_0000) // CREATE_NO_WINDOW: exercise demand-attach, not inherited console.
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()?,
    );
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if client::connect(&pipe).await.is_ok() {
                break;
            }
            if Instant::now() >= deadline {
                return Err("Host pipe not ready".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        // New client on each operation simulates desktop disconnect/reconnect.
        for visible in [false, true, false, true] {
            let mut client = client::connect(&pipe).await?;
            let status = client
                .set_console_visible(SetConsoleVisibleRequest { visible })
                .await?
                .into_inner();
            assert_eq!(status.console_visible, visible);
        }
        Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    // Isolated helper joins ONLY this owned Host's console. It ignores its own Ctrl+C;
    // it never sends a console signal to the test runner or an unrelated PID.
    let script = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/windows/Send-OwnedConsoleCtrlC.ps1");
    let mut helper = Owned(
        Command::new("powershell.exe")
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
            ])
            .arg(script)
            .arg("-TargetProcessId")
            .arg(host.0.id().to_string())
            .creation_flags(0x0800_0000)
            .stdin(Stdio::null())
            .spawn()?,
    );
    wait(&mut helper.0, Duration::from_secs(10))?;
    wait(&mut host.0, Duration::from_secs(5))?;
    assert!(std::net::TcpStream::connect_timeout(&port, Duration::from_secs(1)).is_err());
    runtime.block_on(async {
        assert!(client::connect(&pipe).await.is_err());
    });
    Ok(())
}
