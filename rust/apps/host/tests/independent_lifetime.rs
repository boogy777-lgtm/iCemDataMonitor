#![cfg(windows)]

use icem_web::host_client::{Presence, StartPolicy, ensure_with};
use std::{
    fs,
    os::windows::fs::OpenOptionsExt,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};

type TestResult = Result<(), Box<dyn std::error::Error + Send + Sync>>;
const ROOT_ENV: &str = "ICEM_LIFETIME_FIXTURE_ROOT";

fn runtime() -> std::io::Result<tokio::runtime::Runtime> {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
}

fn helper(name: &str, root: &Path) -> std::io::Result<Child> {
    Command::new(std::env::current_exe()?)
        .args(["--exact", name, "--nocapture"])
        .env(ROOT_ENV, root)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::inherit())
        .spawn()
}

#[test]
fn fixture_process() -> TestResult {
    let Some(root) = std::env::var_os(ROOT_ENV) else {
        return Ok(());
    };
    let root = PathBuf::from(root);
    fs::write(root.join("ready"), std::process::id().to_string())?;
    let deadline = Instant::now() + Duration::from_secs(20);
    while !root.join("stop").exists() {
        if root.join("probe").exists() && !root.join("alive").exists() {
            fs::write(root.join("alive"), b"alive")?;
        }
        if Instant::now() >= deadline {
            return Err("fixture exceeded safety deadline".into());
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    fs::write(root.join("exited"), b"stopped")?;
    Ok(())
}

#[test]
fn fixture_launcher() -> TestResult {
    let Some(root) = std::env::var_os(ROOT_ENV) else {
        return Ok(());
    };
    let root = PathBuf::from(root);
    runtime()?
        .block_on(ensure_with(
            || {
                std::future::ready(if root.join("ready").exists() {
                    Presence::Ready
                } else {
                    Presence::Absent
                })
            },
            || {
                helper("fixture_process", &root)
                    .map(Some)
                    .map_err(|error| error.to_string())
            },
            StartPolicy::IfAbsent,
            Duration::from_secs(5),
        ))
        .map_err(|error| -> Box<dyn std::error::Error + Send + Sync> { error.into() })?;
    Ok(())
}

struct Fixture(PathBuf);
impl Drop for Fixture {
    fn drop(&mut self) {
        if let Err(error) = fs::write(self.0.join("stop"), b"stop") {
            eprintln!("fixture stop: {error}");
        }
    }
}

#[test]
fn spawned_process_outlives_ensure_and_launcher() -> TestResult {
    let root = std::env::temp_dir().join(format!("icem-ensure-lifetime-{}", std::process::id()));
    fs::create_dir(&root)?;
    let fixture = Fixture(root);
    let mut launcher = helper("fixture_launcher", &fixture.0)?;
    let deadline = Instant::now() + Duration::from_secs(8);
    loop {
        if let Some(status) = launcher.try_wait()? {
            if !status.success() {
                return Err(format!("launcher exited {status}").into());
            }
            break;
        }
        if Instant::now() >= deadline {
            launcher.kill()?;
            launcher.wait()?;
            return Err("launcher timed out".into());
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    let pid = fs::read_to_string(fixture.0.join("ready"))?.parse::<u32>()?;
    assert_ne!(pid, launcher.id());
    fs::write(fixture.0.join("probe"), b"probe")?;
    let deadline = Instant::now() + Duration::from_secs(3);
    while !fixture.0.join("alive").exists() {
        if Instant::now() >= deadline {
            return Err("fixture did not answer after launcher exit".into());
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    fs::write(fixture.0.join("stop"), b"stop")?;
    let deadline = Instant::now() + Duration::from_secs(3);
    while !fixture.0.join("exited").exists() {
        if Instant::now() >= deadline {
            return Err("fixture did not acknowledge stop".into());
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    fs::remove_dir_all(&fixture.0)?;
    std::mem::forget(fixture);
    Ok(())
}

#[test]
fn identity_matches_windows_pipe_case_equivalence() -> TestResult {
    use icem_platform_windows::HostIpcIdentity;
    use tokio::net::windows::named_pipe::{ClientOptions, ServerOptions};
    let rt = runtime()?;
    rt.block_on(async {
        for (left, right, equivalent) in [
            ("abC", "ABC", true),
            ("тест", "ТЕСТ", true),
            ("σ", "ς", false),
            ("ß", "SS", false),
            ("one", "two", false),
        ] {
            let prefix = format!("icem-case-{}-", std::process::id());
            let a = format!("{prefix}{left}");
            let b = format!("{prefix}{right}");
            let path_a = format!(r"\\.\pipe\{a}");
            let path_b = format!(r"\\.\pipe\{b}");
            let server = ServerOptions::new()
                .first_pipe_instance(true)
                .create(path_a)?;
            assert_eq!(
                ClientOptions::new().open(path_b).is_ok(),
                equivalent,
                "{left:?}/{right:?}"
            );
            assert_eq!(
                HostIpcIdentity::for_pipe(&a)? == HostIpcIdentity::for_pipe(&b)?,
                equivalent,
                "{left:?}/{right:?}"
            );
            drop(server);
        }
        // The Windows pipe path has its own length limit; every accepted name
        // here must also fit all three derived kernel-object names.
        let prefix = format!("icem-limit-{}-", std::process::id());
        let mut accepted = 0;
        for length in [240, 247, 248, 256] {
            let name = format!("{prefix}{}", "x".repeat(length - prefix.len()));
            if let Ok(server) = ServerOptions::new()
                .first_pipe_instance(true)
                .create(format!(r"\\.\pipe\{name}"))
            {
                let identity = HostIpcIdentity::for_pipe(&name)?;
                let _mutex = icem_windows_native::NamedMutex::open_or_create(&identity.mutex)?;
                let _restart = icem_windows_native::OwnedEvent::create_reset(&identity.restart)?;
                let _shutdown = icem_windows_native::OwnedEvent::create_reset(&identity.shutdown)?;
                accepted += 1;
                drop(server);
            }
        }
        assert!(accepted > 0, "no long named-pipe case was accepted");
        Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    let default = HostIpcIdentity::for_pipe(icem_web::HOST_PIPE)?;
    assert_eq!(default.mutex, icem_platform_windows::HOST_MUTEX);
    assert_eq!(default.restart, icem_platform_windows::HOST_RESTART);
    assert_eq!(default.shutdown, icem_platform_windows::HOST_SHUTDOWN);
    assert!(default.owns_shell);
    assert!(!HostIpcIdentity::for_pipe("custom")?.owns_shell);
    Ok(())
}

#[test]
fn real_host_launcher() -> TestResult {
    let Some(root) = std::env::var_os("ICEM_REAL_HOST_ROOT") else {
        return Ok(());
    };
    let root = PathBuf::from(root);
    let pipe = fs::read_to_string(root.join("pipe"))?;
    let launch = icem_web::host_client::Launch {
        executable: root.join("host.exe"),
        arguments: vec![
            "--station-config".into(),
            root.join("config.ini").to_string_lossy().into_owned(),
            "--rpc-pipe".into(),
            pipe.clone(),
            "--bind".into(),
            "127.0.0.1:0".into(),
            "--web-bind".into(),
            "127.0.0.1:0".into(),
            "--duration-ms".into(),
            "20000".into(),
            "--bus-mutex".into(),
            format!("Local\\{pipe}-bus"),
        ],
    };
    runtime()?.block_on(icem_web::host_client::ensure(
        &pipe,
        Some(&launch),
        Duration::from_secs(10),
    ))?;
    Ok(())
}

struct HostCleanup(Vec<String>);
impl Drop for HostCleanup {
    fn drop(&mut self) {
        for event in &self.0 {
            if let Err(error) = icem_windows_native::signal_existing_event(event) {
                eprintln!("fixture Host cleanup: {error}");
            }
        }
    }
}

#[test]
fn two_real_hosts_survive_launcher_exit_and_are_independently_stopped() -> TestResult {
    let root = std::env::temp_dir().join(format!("icem-real-lifetime-{}", std::process::id()));
    fs::create_dir(&root)?;
    let mut cleanup = HostCleanup(Vec::new());
    let mut pipes = Vec::new();
    for index in 0..2 {
        let dir = root.join(index.to_string());
        fs::create_dir(&dir)?;
        fs::copy(env!("CARGO_BIN_EXE_icem-host-dev"), dir.join("host.exe"))?;
        let pipe = format!("icem-real-lifetime-{}-{index}", std::process::id());
        let identity = icem_platform_windows::HostIpcIdentity::for_pipe(&pipe)?;
        let mut config = icem_config::IniOptions::default();
        config.mmf_path = dir.join("absent.dat").to_string_lossy().into_owned();
        config.mmf_name = format!("Local\\{pipe}-map");
        config.lansniffer_on = 0;
        fs::write(dir.join("config.ini"), config.render())?;
        fs::write(dir.join("pipe"), &pipe)?;
        cleanup.0.push(identity.shutdown);
        let mut launcher = Command::new(std::env::current_exe()?)
            .args(["--exact", "real_host_launcher", "--nocapture"])
            .env("ICEM_REAL_HOST_ROOT", &dir)
            .env("PROGRAMDATA", &dir)
            .env("LOCALAPPDATA", &dir)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()?;
        let deadline = Instant::now() + Duration::from_secs(12);
        loop {
            if let Some(status) = launcher.try_wait()? {
                if !status.success() {
                    return Err(format!("launcher {index}: {status}").into());
                }
                break;
            }
            if Instant::now() >= deadline {
                launcher.kill()?;
                launcher.wait()?;
                return Err("Host launcher timed out".into());
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        pipes.push(pipe);
    }
    let rt = runtime()?;
    rt.block_on(async {
        for pipe in &pipes {
            assert_eq!(icem_web::host_client::probe(pipe).await, Presence::Ready);
        }
        assert!(icem_windows_native::signal_existing_event(&cleanup.0[0])?);
        let deadline = Instant::now() + Duration::from_secs(5);
        while icem_web::host_client::probe(&pipes[0]).await != Presence::Absent {
            if Instant::now() >= deadline {
                return Err("first Host did not stop".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        assert_eq!(
            icem_web::host_client::probe(&pipes[1]).await,
            Presence::Ready
        );
        assert!(icem_windows_native::signal_existing_event(&cleanup.0[1])?);
        while icem_web::host_client::probe(&pipes[1]).await != Presence::Absent {
            if Instant::now() >= deadline {
                return Err("second Host did not stop".into());
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
    })?;
    cleanup.0.clear();
    // Pipe teardown precedes process exit. Observe release of our copied EXEs
    // before removing the fixture; never retry the shutdown mutation.
    let deadline = Instant::now() + Duration::from_secs(5);
    for index in 0..2 {
        let exe = root.join(index.to_string()).join("host.exe");
        loop {
            match fs::OpenOptions::new().write(true).share_mode(0).open(&exe) {
                Ok(file) => {
                    drop(file);
                    break;
                }
                Err(error) if error.raw_os_error() == Some(32) && Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(10));
                }
                Err(error) => return Err(error.into()),
            }
        }
    }
    fs::remove_dir_all(root)?;
    Ok(())
}
