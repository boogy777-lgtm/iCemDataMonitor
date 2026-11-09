#![cfg(windows)]
use icem_windows_native::NamedMutex;
use std::io::{BufRead, BufReader};
use std::process::{Child, Command, Stdio};
use std::time::Duration;

struct Fixture(Child);
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn fixture(name: &str, rights: &str) -> Result<Fixture, Box<dyn std::error::Error>> {
    let mut child = Fixture(
        Command::new("pwsh")
            .args(["-NoProfile", "-File"])
            .arg(concat!(env!("CARGO_MANIFEST_DIR"), "/tests/mutex_acl.ps1"))
            .args(["-Name", name, "-Rights", rights])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?,
    );
    let stdout = child.0.stdout.take().ok_or("missing fixture stdout")?;
    let (send, recv) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let mut line = String::new();
        let result = BufReader::new(stdout).read_line(&mut line).map(|_| line);
        let _ = send.send(result);
    });
    let line = recv.recv_timeout(Duration::from_secs(20))??;
    if line.trim() != "READY" {
        return Err(format!("fixture did not start: {line:?}").into());
    }
    Ok(child)
}

#[test]
fn existing_mutex_requires_only_wait_and_release_rights() -> Result<(), Box<dyn std::error::Error>>
{
    let name = format!("Local\\icem-mutex-acl-{}", std::process::id());
    let _fixture = fixture(&name, "0x00100001")?;
    let mutex = NamedMutex::open_or_create(&name)?;
    let guard = mutex.lock(Duration::ZERO)?.ok_or("unexpected contention")?;
    guard.release()?;
    assert!(mutex.lock(Duration::ZERO)?.is_some());
    Ok(())
}

#[test]
fn missing_release_right_is_reported_as_access_denied() -> Result<(), Box<dyn std::error::Error>> {
    let name = format!("Local\\icem-mutex-denied-{}", std::process::id());
    let _fixture = fixture(&name, "0x00100000")?;
    let error = match NamedMutex::open_or_create(&name) {
        Ok(_) => return Err("opened mutex without release right".into()),
        Err(error) => error,
    };
    assert_eq!(error.code, 5);
    assert_eq!(error.operation, "create_mutex");
    Ok(())
}
