#![forbid(unsafe_code)]
//! Development host: explicit fixture or Windows live MMF. No production UI/lifecycle yet.
use icem_bus::Snapshot;
use icem_config::IniOptions;
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource, SourceError};
use icem_protocols::modbus::{MirrorInput, RegisterBank};
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use std::io::Read;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

enum Input {
    Fixture(PathBuf),
    Live {
        path: PathBuf,
        map: String,
        mutex: String,
    },
}
struct Options {
    input: Input,
    bind: SocketAddr,
    duration: Duration,
    tick: Duration,
    total: u16,
    modbus_on: bool,
}
fn options() -> Result<Options, String> {
    let mut args = std::env::args().skip(1);
    let mut fixture = None;
    let mut live = None;
    let mut config = None;
    let mut map = None;
    let mut mutex = None;
    let mut bind = None;
    let mut duration = 10_000;
    while let Some(key) = args.next() {
        let value = args.next().ok_or("each option requires a value")?;
        match key.as_str() {
            "--modbus-fixture" => fixture = Some(PathBuf::from(value)),
            "--bus-live" => live = Some(PathBuf::from(value)),
            "--station-config" => config = Some(PathBuf::from(value)),
            "--bus-map" => map = Some(value),
            "--bus-mutex" => mutex = Some(value),
            "--bind" => {
                bind = Some(
                    value
                        .parse::<SocketAddr>()
                        .map_err(|_| "invalid socket address")?,
                )
            }
            "--duration-ms" => duration = value.parse().map_err(|_| "invalid duration")?,
            _ => return Err(format!("unknown option: {key}")),
        }
    }
    if usize::from(fixture.is_some()) + usize::from(live.is_some()) + usize::from(config.is_some())
        != 1
    {
        return Err("choose exactly one of --modbus-fixture, --bus-live, --station-config".into());
    }
    let settings = match &config {
        Some(path) => IniOptions::read_existing(path).map_err(|error| error.to_string())?,
        None => IniOptions::default(),
    };
    let has_config = config.is_some();
    let input = if let Some(path) = fixture {
        if map.is_some() || mutex.is_some() {
            return Err("bus names require a live source".into());
        }
        Input::Fixture(path)
    } else {
        Input::Live {
            path: live.unwrap_or_else(|| PathBuf::from(&settings.mmf_path)),
            map: map.unwrap_or_else(|| settings.mmf_name.clone()),
            mutex: mutex.unwrap_or_else(|| icem_platform_windows::BUS_MUTEX.into()),
        }
    };
    let port = if has_config {
        u16::try_from(settings.modbus_port).map_err(|_| "invalid config port")?
    } else {
        1502
    };
    let bind = bind.unwrap_or_else(|| SocketAddr::from(([127, 0, 0, 1], port)));
    if !bind.ip().is_loopback() || !(1..=3_600_000).contains(&duration) {
        return Err("development host requires loopback and duration 1..3600000 ms".into());
    }
    Ok(Options {
        input,
        bind,
        duration: Duration::from_millis(duration),
        tick: Duration::from_millis(
            u64::try_from(settings.modbus_tick_ms).map_err(|_| "invalid tick")?,
        ),
        total: u16::try_from(settings.total).map_err(|_| "invalid config total")?,
        modbus_on: settings.modbus_on == 1,
    })
}
struct Fixture(Snapshot);
impl SnapshotSource for Fixture {
    fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
        Ok(SnapshotRead {
            snapshot: self.0.clone(),
            freshness: Freshness::Fresh,
            origin: SnapshotOrigin::Captured,
        })
    }
}
fn source(input: Input) -> Result<Box<dyn SnapshotSource>, Box<dyn std::error::Error>> {
    match input {
        Input::Fixture(path) => {
            let file = std::fs::File::open(path)?;
            let maximum = 64 + 2 * u64::from(u16::MAX);
            if file.metadata()?.len() > maximum {
                return Err("fixture exceeds Bus v5 maximum".into());
            }
            let mut bytes = Vec::new();
            file.take(maximum + 1).read_to_end(&mut bytes)?;
            Ok(Box::new(Fixture(Snapshot::decode(&bytes)?)))
        }
        Input::Live { path, map, mutex } => live_source(path, map, mutex),
    }
}
#[cfg(windows)]
fn live_source(
    path: PathBuf,
    map: String,
    mutex: String,
) -> Result<Box<dyn SnapshotSource>, Box<dyn std::error::Error>> {
    Ok(Box::new(icem_platform_windows::WindowsBusReader::new(
        path, map, mutex,
    )))
}
#[cfg(not(windows))]
fn live_source(
    _: PathBuf,
    _: String,
    _: String,
) -> Result<Box<dyn SnapshotSource>, Box<dyn std::error::Error>> {
    Err("live station bus requires Windows named mappings/mutex".into())
}
fn refresh(
    source: &mut dyn SnapshotSource,
    bank: &Mutex<RegisterBank>,
    previous: &mut Option<SourceError>,
) -> Result<(), Box<dyn std::error::Error>> {
    let read = source.snapshot();
    let error = source.last_error().or_else(|| read.as_ref().err().copied());
    if *previous != error {
        eprintln!(
            "Bus state: {}",
            match error {
                Some(error) => format!("{error:?}"),
                None => "recovered".into(),
            }
        );
        *previous = error;
    }
    let mut bank = bank.lock().map_err(|_| "register mirror lock poisoned")?;
    bank.tick(match &read {
        Ok(read) if read.freshness == Freshness::Fresh => MirrorInput::Fresh(&read.snapshot),
        Ok(read) => MirrorInput::LastGood(&read.snapshot),
        Err(_) => MirrorInput::Unavailable,
    });
    Ok(())
}
fn run(options: Options) -> Result<(), Box<dyn std::error::Error>> {
    let mut source = source(options.input)?;
    let bank = Arc::new(Mutex::new(
        RegisterBank::new(options.total).map_err(|error| format!("{error:?}"))?,
    ));
    let mut last_error = None;
    refresh(&mut *source, &bank, &mut last_error)?;
    let server = if options.modbus_on {
        Some(ModbusServer::bind(
            options.bind,
            Arc::clone(&bank),
            ServerOptions::default(),
        )?)
    } else {
        None
    };
    eprintln!(
        "DEVELOPMENT HOST; tray/web/emulator/lifecycle pending. Modbus {}",
        server
            .as_ref()
            .map(|server| server.local_addr().to_string())
            .unwrap_or_else(|| "disabled by config".into())
    );
    let started = Instant::now();
    while started.elapsed() < options.duration {
        std::thread::sleep(
            options
                .tick
                .min(options.duration.saturating_sub(started.elapsed())),
        );
        refresh(&mut *source, &bank, &mut last_error)?;
    }
    if let Some(server) = server {
        eprintln!("Stopped: {:?}", server.stop()?);
    }
    Ok(())
}
fn main() -> ExitCode {
    let options = match options() {
        Ok(options) => options,
        Err(error) => {
            eprintln!(
                "{error}\nUsage: icem-host-dev (--modbus-fixture PATH | --bus-live PATH | --station-config PATH) [--bus-map NAME] [--bus-mutex NAME] [--bind 127.0.0.1:1502] [--duration-ms 10000]"
            );
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
