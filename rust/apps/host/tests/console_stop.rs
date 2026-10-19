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
    stage: &str,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let deadline = Instant::now() + limit;
    loop {
        if let Some(status) = child.try_wait()? {
            if !status.success() {
                return Err(format!("{stage}: child PID {} exited {status}", child.id()).into());
            }
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err(format!(
                "{stage}: child PID {} deadline exceeded after {limit:?}",
                child.id()
            )
            .into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}
#[test]
fn headless_console_rpc_reconnect_ctrl_c_tears_down_host()
-> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let helper = WindowHelper::compile()?;
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
                "120000",
            ])
            .creation_flags(0x0800_0000) // CREATE_NO_WINDOW: exercise demand-attach, not inherited console.
            .stdin(Stdio::null())
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
            if client::connect(&pipe).await.is_ok() {
                break;
            }
            if Instant::now() >= deadline {
                return Err("Host pipe not ready".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        assert!(
            !client::connect(&pipe)
                .await?
                .get_status(HostStatusRequest {})
                .await?
                .into_inner()
                .console_visible
        );
        // New client on each operation simulates desktop disconnect/reconnect.
        for visible in [false, true, false, true] {
            let mut client = client::connect(&pipe).await?;
            let status = client
                .set_console_visible(SetConsoleVisibleRequest { visible })
                .await?
                .into_inner();
            assert_eq!(status.console_visible, visible);
        }
        let initial = helper.action(host.0.id(), "Probe", 0)?;
        let handle = initial["hwnd"].as_i64().ok_or("missing window handle")?;
        for _ in 0..3 {
            helper.action(host.0.id(), "Close", handle)?;
            assert!(host.0.try_wait()?.is_none(), "X killed Host");
            let status = client::connect(&pipe)
                .await?
                .get_status(HostStatusRequest {})
                .await?
                .into_inner();
            assert!(!status.console_visible);
            assert!(std::net::TcpStream::connect_timeout(&port, Duration::from_secs(1)).is_ok());
            let status = client::connect(&pipe)
                .await?
                .set_console_visible(SetConsoleVisibleRequest { visible: true })
                .await?
                .into_inner();
            assert!(status.console_visible);
            helper.action(host.0.id(), "Minimize", handle)?;
            // visible=true must also restore an already-visible minimized window.
            client::connect(&pipe)
                .await?
                .set_console_visible(SetConsoleVisibleRequest { visible: true })
                .await?;
            assert_eq!(
                helper.action(host.0.id(), "Probe", handle)?["minimized"],
                false
            );
        }
        Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    // Real message-level Ctrl+C input to the Host-owned diagnostic window.
    helper.action(host.0.id(), "CtrlC", 0)?;
    wait(
        &mut host.0,
        Duration::from_secs(5),
        "Host teardown after CtrlC",
    )?;
    assert!(std::net::TcpStream::connect_timeout(&port, Duration::from_secs(1)).is_err());
    runtime.block_on(async {
        assert!(client::connect(&pipe).await.is_err());
    });
    manual_restart_and_autostart_quit(&runtime)?;
    Ok(())
}

fn manual_restart_and_autostart_quit(
    runtime: &tokio::runtime::Runtime,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/bus-v5-minimal.bin");
    let icon =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../assets/iCemDataMonitor.ico");
    let root = std::env::temp_dir().join(format!("icem-console-autostart-{}", std::process::id()));
    std::fs::create_dir_all(&root)?;
    for autostart in [false, true] {
        let pipe = format!("icem-console-start-{}-{autostart}", std::process::id());
        let mut command = Command::new(env!("CARGO_BIN_EXE_icem-host-dev"));
        if autostart {
            command
                .arg("--autostart")
                .arg("--station-config")
                .arg(root.join("config.ini"))
                .arg("--tray-icon")
                .arg(&icon)
                .args(["--web-bind", "127.0.0.1:0"])
                .creation_flags(0x0800_0000);
        } else {
            command
                .arg("--modbus-fixture")
                .arg(&fixture)
                .creation_flags(0x0000_0010);
        }
        let mut child = Owned(
            command
                .args([
                    "--bind",
                    "127.0.0.1:0",
                    "--rpc-pipe",
                    &pipe,
                    "--duration-ms",
                    "30000",
                ])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::inherit())
                .spawn()?,
        );
        runtime.block_on(async {
            ready(&pipe).await?;
            let status = client::connect(&pipe)
                .await?
                .get_status(HostStatusRequest {})
                .await?
                .into_inner();
            assert_eq!(status.console_visible, !autostart);
            if !autostart {
                // One explicit mutation. Reconnect only observes this unique pipe;
                // the original process exits and its own restart path launches the successor.
                assert!(icem_windows_native::signal_existing_event(
                    icem_platform_windows::HOST_RESTART
                )?);
                wait(
                    &mut child.0,
                    Duration::from_secs(5),
                    "Host exit after explicit restart/quit",
                )?;
                ready(&pipe).await?;
                assert!(
                    client::connect(&pipe)
                        .await?
                        .get_status(HostStatusRequest {})
                        .await?
                        .into_inner()
                        .console_visible
                );
            }
            // Explicit Quit with a hidden window (manual successor), then visible
            // (actual autostart with its existing independent tray).
            client::connect(&pipe)
                .await?
                .set_console_visible(SetConsoleVisibleRequest { visible: autostart })
                .await?;
            assert!(icem_windows_native::signal_existing_event(
                icem_platform_windows::HOST_SHUTDOWN
            )?);
            let deadline = Instant::now() + Duration::from_secs(5);
            let mutex =
                icem_windows_native::NamedMutex::open_or_create(icem_platform_windows::HOST_MUTEX)?;
            loop {
                if let Some(guard) = mutex.lock(Duration::ZERO)? {
                    drop(guard);
                    break;
                }
                if Instant::now() >= deadline {
                    return Err("explicit Quit retained Host ownership".into());
                }
                tokio::time::sleep(Duration::from_millis(25)).await;
            }
            assert!(client::connect(&pipe).await.is_err());
            Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
        })?;
        if autostart {
            wait(
                &mut child.0,
                Duration::from_secs(5),
                "Host exit after explicit restart/quit",
            )?;
        }
    }
    std::fs::remove_dir_all(root)?;
    Ok(())
}
async fn ready(pipe: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let deadline = Instant::now() + Duration::from_secs(12);
    loop {
        if icem_web::host_client::probe(pipe).await == icem_web::host_client::Presence::Ready {
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err("Host pipe not ready".into());
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

// Compile once before starting Host; each action retains its own 10-second child bound.
struct WindowHelper(std::path::PathBuf);
impl Drop for WindowHelper {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(self.0.join("OwnedConsoleProbe.exe"));
        let _ = std::fs::remove_dir(&self.0);
    }
}
impl WindowHelper {
    fn compile() -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
        let root = std::env::temp_dir().join(format!("icem-console-helper-{}", std::process::id()));
        std::fs::create_dir(&root)?;
        let helper = Self(root);
        let compiler = std::path::PathBuf::from(
            std::env::var_os("WINDIR").ok_or("missing WINDIR for Framework fixture compiler")?,
        )
        .join("Microsoft.NET/Framework64/v4.0.30319/csc.exe");
        let source = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../tests/windows/OwnedConsoleProbe.cs");
        let mut output = std::ffi::OsString::from("/out:");
        output.push(helper.0.join("OwnedConsoleProbe.exe"));
        let mut compiler = Owned(
            Command::new(compiler)
                .args(["/nologo", "/target:exe", "/platform:x64"])
                .arg(output)
                .arg(source)
                .creation_flags(0x0800_0000)
                .stdin(Stdio::null())
                .spawn()?,
        );
        wait(
            &mut compiler.0,
            Duration::from_secs(10),
            "console fixture compilation before Host startup",
        )?;
        Ok(helper)
    }
    fn action(
        &self,
        pid: u32,
        action: &str,
        handle: i64,
    ) -> Result<serde_json::Value, Box<dyn std::error::Error + Send + Sync>> {
        use std::io::Read;
        let mut helper = Owned(
            Command::new(self.0.join("OwnedConsoleProbe.exe"))
                .arg(pid.to_string())
                .arg(action)
                .arg(handle.to_string())
                .creation_flags(0x0800_0000)
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .spawn()?,
        );
        eprintln!(
            "window helper PID {}: target PID {pid}, action {action}, expected HWND {handle}",
            helper.0.id()
        );
        wait(
            &mut helper.0,
            Duration::from_secs(10),
            &format!("window helper target PID {pid}, action {action}, expected HWND {handle}"),
        )?;
        let mut bytes = Vec::new();
        helper
            .0
            .stdout
            .take()
            .ok_or("missing helper stdout")?
            .take(8192)
            .read_to_end(&mut bytes)?;
        Ok(serde_json::from_slice(&bytes)?)
    }
}
