#![forbid(unsafe_code)]
//! Development host: explicit fixture or Windows live MMF. No production UI/lifecycle yet.
use icem_bus::Snapshot;
use icem_config::IniOptions;
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource, SourceError};
use icem_protocols::modbus::{MirrorInput, RegisterBank};
use icem_transport::modbus_tcp::{ModbusServer, ServerOptions};
use icem_web::{BusReply, HostLayers, WebServer, WebState};
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
    web: Option<SocketAddr>,
    web_on: bool,
    opc_da: bool,
    set_as_default: bool,
    theme: PathBuf,
    registry: Option<PathBuf>,
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
    let mut web_bind = None;
    let mut theme = None;
    let mut registry = None;
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
            "--web-bind" => {
                web_bind = Some(
                    value
                        .parse::<SocketAddr>()
                        .map_err(|_| "invalid web address")?,
                )
            }
            "--theme-file" => theme = Some(PathBuf::from(value)),
            "--registry" => registry = Some(PathBuf::from(value)),
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
    let web = web_bind.or_else(|| {
        has_config.then(|| {
            SocketAddr::from((
                if settings.web_remote_on == 1 {
                    [0, 0, 0, 0]
                } else {
                    [127, 0, 0, 1]
                },
                settings.web_port as u16,
            ))
        })
    });
    if web.is_some_and(|x| !x.ip().is_loopback()) && settings.web_remote_on != 1 {
        return Err("non-loopback web binding requires web_remote_on=1 in station config".into());
    }
    let theme = theme.unwrap_or_else(|| {
        PathBuf::from(std::env::var_os("PROGRAMDATA").unwrap_or_else(|| r"C:\ProgramData".into()))
            .join("Halliburton/iCem/DataMonitoring/ui-theme")
    });
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
        web,
        web_on: settings.web_on == 1,
        opc_da: settings.opcda_on == 1,
        set_as_default: settings.ui_set_as_default == 1,
        theme,
        registry,
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
) -> Result<BusReply, Box<dyn std::error::Error>> {
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
    Ok(BusReply {
        read: read.ok(),
        is_open: source.is_open(),
    })
}
fn run(options: Options) -> Result<(), Box<dyn std::error::Error>> {
    let mut source = source(options.input)?;
    let bank = Arc::new(Mutex::new(
        RegisterBank::new(options.total).map_err(|error| format!("{error:?}"))?,
    ));
    let mut last_error = None;
    let first = refresh(&mut *source, &bank, &mut last_error)?;
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
        "DEVELOPMENT HOST; tray/emulator/lifecycle pending. Modbus {}",
        server
            .as_ref()
            .map(|server| server.local_addr().to_string())
            .unwrap_or_else(|| "disabled by config".into())
    );
    let names = match options.registry {
        Some(path) => icem_web::registry::load_names(&path)?,
        None => {
            let path = std::env::current_exe()?
                .parent()
                .ok_or("missing exe directory")?
                .join("templates/variable_registry.json");
            match icem_web::registry::load_names(&path) {
                Ok(names) => names,
                Err(error) => {
                    if options.web.is_some() {
                        eprintln!("registry unavailable: {error}; sensor names remain null");
                    }
                    Default::default()
                }
            }
        }
    };
    let (web, requests) = WebState::new(
        HostLayers {
            modbus_bound: server.is_some(),
            modbus_port: options.bind.port(),
            web: options.web_on,
            opc_da: options.opc_da,
            set_as_default: options.set_as_default,
        },
        options.theme,
        names,
    );
    web.observe(&first);
    let web_server = options
        .web
        .map(|address| WebServer::bind(address, web.clone()))
        .transpose()?;
    if let Some(server) = &web_server {
        eprintln!("Web http://{}", server.local_addr());
    }
    let started = Instant::now();
    let mut mirror_due = started + options.tick;
    let mut health_due = started;
    while started.elapsed() < options.duration {
        let now = Instant::now();
        if now >= mirror_due {
            let reply = refresh(&mut *source, &bank, &mut last_error)?;
            web.observe(&reply);
            mirror_due = Instant::now() + options.tick;
        }
        if web_server.is_some() && now >= health_due {
            let reply = BusReply {
                read: source.snapshot().ok(),
                is_open: source.is_open(),
            };
            web.probe(&reply, icem_web::utc_now_ms())?;
            health_due = Instant::now() + Duration::from_secs(1);
        }
        let mut wake = mirror_due.min(started + options.duration);
        if web_server.is_some() {
            wake = wake.min(health_due);
        }
        match requests.recv_timeout(wake.saturating_duration_since(Instant::now())) {
            Ok(request) if !request.response.is_closed() => {
                let reply = BusReply {
                    read: source.snapshot().ok(),
                    is_open: source.is_open(),
                };
                web.observe(&reply);
                let _ = request.response.send(reply);
            }
            Ok(_) | Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    if let Some(web) = web_server {
        web.stop()?;
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
                "{error}\nUsage: icem-host-dev (--modbus-fixture PATH | --bus-live PATH | --station-config PATH) [--bus-map NAME] [--bus-mutex NAME] [--bind 127.0.0.1:1502] [--duration-ms 10000] [--web-bind 127.0.0.1:8090] [--theme-file PATH] [--registry PATH]"
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
