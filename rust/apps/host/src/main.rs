#![forbid(unsafe_code)]
//! Explicit fixture-driven development host; never opens production Data.dat.
use icem_bus::Snapshot;
use icem_protocols::modbus::{MirrorInput, RegisterBank};
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use std::net::SocketAddr;
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

struct Options {
    fixture: PathBuf,
    bind: SocketAddr,
    duration_ms: u64,
}
fn options() -> Result<Options, String> {
    let mut args = std::env::args().skip(1);
    let mut fixture = None;
    let mut bind = "127.0.0.1:1502"
        .parse::<SocketAddr>()
        .map_err(|e| e.to_string())?;
    let mut duration_ms = 10_000;
    while let Some(key) = args.next() {
        let value = args.next().ok_or("each option requires a value")?;
        match key.as_str() {
            "--modbus-fixture" => fixture = Some(PathBuf::from(value)),
            "--bind" => bind = value.parse().map_err(|_| "invalid socket address")?,
            "--duration-ms" => duration_ms = value.parse().map_err(|_| "invalid duration")?,
            _ => return Err(format!("unknown option: {key}")),
        }
    }
    if !bind.ip().is_loopback() || !(1..=3_600_000).contains(&duration_ms) {
        return Err("development host requires loopback and duration 1..3600000 ms".into());
    }
    Ok(Options {
        fixture: fixture.ok_or("--modbus-fixture is required")?,
        bind,
        duration_ms,
    })
}
fn run(options: Options) -> Result<(), Box<dyn std::error::Error>> {
    // Bound the input before allocation; immutable fixture, no polling/MMF compatibility claim.
    let file = std::fs::File::open(options.fixture)?;
    if file.metadata()?.len() > 64 + 2 * u64::from(u16::MAX) {
        return Err("fixture exceeds Bus v5 maximum".into());
    }
    use std::io::Read;
    let mut bytes = Vec::new();
    file.take(64 + 2 * u64::from(u16::MAX) + 1)
        .read_to_end(&mut bytes)?;
    let snapshot = Snapshot::decode(&bytes)?;
    let mut bank =
        RegisterBank::new(snapshot.register_count()).map_err(|error| format!("{error:?}"))?;
    bank.tick(MirrorInput::Fresh(&snapshot));
    let bank = Arc::new(Mutex::new(bank));
    let server = ModbusServer::bind(options.bind, Arc::clone(&bank), ServerOptions::default())?;
    eprintln!(
        "DEVELOPMENT FIXTURE ONLY; no tray/MMF/web/emulator. Modbus {}",
        server.local_addr()
    );
    let started = Instant::now();
    while started.elapsed() < Duration::from_millis(options.duration_ms) {
        std::thread::sleep(
            Duration::from_millis(500)
                .min(Duration::from_millis(options.duration_ms).saturating_sub(started.elapsed())),
        );
        bank.lock()
            .map_err(|_| "register mirror lock poisoned")?
            .tick(MirrorInput::Fresh(&snapshot));
    }
    let stats = server.stop()?;
    eprintln!("Stopped: {stats:?}");
    Ok(())
}
fn main() -> ExitCode {
    let options = match options() {
        Ok(options) => options,
        Err(error) => {
            eprintln!("{error}\nUsage: icem-host-dev --modbus-fixture PATH [--bind 127.0.0.1:1502] [--duration-ms 10000]");
            return ExitCode::from(2);
        }
    };
    match run(options) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(1)
        }
    }
}
