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

#[cfg(windows)]
mod windows;

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
    duration: Option<Duration>,
    tick: Duration,
    total: u16,
    modbus_on: bool,
    web: Option<SocketAddr>,
    web_on: bool,
    opc_da: bool,
    set_as_default: bool,
    theme: PathBuf,
    registry: Option<PathBuf>,
    rpc_pipe: Option<String>,
    tray_icon: Option<PathBuf>,
    station: Option<(PathBuf, IniOptions)>,
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
    let mut duration_explicit = false;
    let mut autostart = false;
    let mut web_bind = None;
    let mut theme = None;
    let mut registry = None;
    let mut rpc_pipe = None;
    let mut tray_icon = None;
    while let Some(key) = args.next() {
        if key == "--autostart" {
            autostart = true;
            continue;
        }
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
            "--tray-icon" => tray_icon = Some(PathBuf::from(value)),
            "--rpc-pipe" => rpc_pipe = Some(value),
            "--registry" => registry = Some(PathBuf::from(value)),
            "--duration-ms" => {
                duration = value.parse().map_err(|_| "invalid duration")?;
                duration_explicit = true;
            }
            _ => return Err(format!("unknown option: {key}")),
        }
    }
    if autostart {
        if fixture.is_some() || live.is_some() {
            return Err("--autostart requires station config".into());
        }
        let station = PathBuf::from(
            std::env::var_os("PROGRAMDATA").unwrap_or_else(|| r"C:\ProgramData".into()),
        )
        .join("Halliburton/iCem/DataMonitoring");
        config.get_or_insert_with(|| station.join("config.ini"));
        if !duration_explicit {
            duration = 0;
        }
        if tray_icon.is_none() {
            tray_icon = Some(
                PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../assets/iCemDataMonitor.ico"),
            );
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
    if !bind.ip().is_loopback() || duration > 3_600_000 {
        return Err("development host requires loopback and duration 0..3600000 ms (0 = until lifecycle stop)".into());
    }
    Ok(Options {
        input,
        bind,
        duration: (duration != 0).then(|| Duration::from_millis(duration)),
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
        rpc_pipe: rpc_pipe.or_else(|| tray_icon.as_ref().map(|_| "iCemDataMonitor.Host".into())),
        tray_icon,
        station: config.map(|path| (path, settings)),
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
fn run(mut options: Options) -> Result<bool, Box<dyn std::error::Error>> {
    #[cfg(windows)]
    let singleton = options
        .rpc_pipe
        .as_ref()
        .map(|_| windows::singleton())
        .transpose()?;
    #[cfg(windows)]
    let _guard = match &singleton {
        Some(mutex) => Some(
            mutex
                .lock(Duration::ZERO)?
                .ok_or("Host is already running")?,
        ),
        None => None,
    };
    #[cfg(not(windows))]
    if options.tray_icon.is_some() {
        return Err("Host tray requires Windows".into());
    }
    #[cfg(windows)]
    let mut restart = false;
    #[cfg(not(windows))]
    let restart = false;
    #[cfg(windows)]
    let web_url = format!(
        "http://127.0.0.1:{}",
        options.web.map_or(8090, |a| a.port())
    );
    let mut source = source(options.input)?;
    let bank = Arc::new(Mutex::new(
        RegisterBank::new(options.total).map_err(|error| format!("{error:?}"))?,
    ));
    let mut last_error = None;
    let first = refresh(&mut *source, &bank, &mut last_error)?;
    let mut server = if options.modbus_on {
        Some(ModbusServer::bind(
            options.bind,
            Arc::clone(&bank),
            ServerOptions::default(),
        )?)
    } else {
        None
    };
    eprintln!(
        "DEVELOPMENT HOST; full Host/UI acceptance pending. Modbus {}",
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
    let (mut web, requests) = WebState::new(
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
    let layer_requests = web.enable_layer_control();
    web.observe(&first);
    let web_server = options
        .web
        .map(|address| WebServer::bind(address, web.clone()))
        .transpose()?;
    if let Some(server) = &web_server {
        eprintln!("Web http://{}", server.local_addr());
    }
    #[cfg(windows)]
    let pipe_server = options
        .rpc_pipe
        .as_deref()
        .map(|name| icem_web::LocalRpcServer::bind(name, web.clone()))
        .transpose()?;
    #[cfg(windows)]
    let mut lifecycle = options
        .rpc_pipe
        .as_ref()
        .map(|_| windows::Lifecycle::new(options.tray_icon.clone()))
        .transpose()?;
    #[cfg(not(windows))]
    if options.rpc_pipe.is_some() {
        return Err("local Host gRPC pipe requires Windows".into());
    }
    let started = Instant::now();
    let mut mirror_due = started + options.tick;
    let mut health_due = started;
    while options
        .duration
        .is_none_or(|limit| started.elapsed() < limit)
    {
        #[cfg(windows)]
        if let Some(lifecycle) = &lifecycle
            && let Some(action) = lifecycle.poll()?
        {
            use windows::Action;
            let result: Result<(), Box<dyn std::error::Error>> = match action {
                Action::Panel => windows::open_panel(
                    options.rpc_pipe.as_deref().unwrap_or(icem_web::HOST_PIPE),
                    &web_url,
                )
                .map_err(Into::into),
                Action::Web => windows::open_web(&web_url).map_err(Into::into),
                Action::Console(visible) => windows::console(visible).map_err(Into::into),
                Action::Restart => {
                    restart = true;
                    break;
                }
                Action::Close => break,
            };
            if let Err(error) = result {
                eprintln!("host action: {error}");
            }
        }
        while let Ok(request) = layer_requests.try_recv() {
            if request.response.is_closed() {
                continue;
            }
            let result = (|| -> Result<(), Box<dyn std::error::Error>> {
                if request.layer != icem_core::LayerKind::Modbus {
                    return Err("layer not implemented".into());
                }
                if let Some(listener) = server.take() {
                    listener.stop()?;
                } else {
                    server = Some(ModbusServer::bind(
                        options.bind,
                        Arc::clone(&bank),
                        ServerOptions::default(),
                    )?);
                }
                web.set_modbus_bound(server.is_some())?;
                if let Some((path, settings)) = &mut options.station
                    && settings.ui_set_as_default == 1
                {
                    settings.modbus_on = i32::from(server.is_some());
                    std::fs::write(path, settings.render())?;
                }
                Ok(())
            })()
            .map_err(|e| e.to_string());
            let _ = request.response.send(result);
        }
        let now = Instant::now();
        if now >= mirror_due {
            let reply = refresh(&mut *source, &bank, &mut last_error)?;
            web.observe(&reply);
            mirror_due = Instant::now() + options.tick;
        }
        if now >= health_due {
            let reply = BusReply {
                read: source.snapshot().ok(),
                is_open: source.is_open(),
            };
            web.probe(&reply, icem_web::utc_now_ms())?;
            health_due = Instant::now() + Duration::from_secs(1);
        }
        let mut wake = mirror_due
            .min(options.duration.map_or(mirror_due, |limit| started + limit))
            .min(Instant::now() + Duration::from_millis(50));
        wake = wake.min(health_due);
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
    #[cfg(windows)]
    if let Some(lifecycle) = &mut lifecycle {
        lifecycle.remove_tray()?;
        windows::close_shell()?;
    }
    #[cfg(windows)]
    if let Some(server) = pipe_server {
        server.stop()?;
    }
    if let Some(web) = web_server {
        web.stop()?;
    }
    if let Some(server) = server {
        eprintln!("Stopped: {:?}", server.stop()?);
    }
    Ok(restart)
}
fn main() -> ExitCode {
    let options = match options() {
        Ok(options) => options,
        Err(error) => {
            eprintln!(
                "{error}\nUsage: icem-host-dev (--modbus-fixture PATH | --bus-live PATH | --station-config PATH) [--bus-map NAME] [--bus-mutex NAME] [--bind 127.0.0.1:1502] [--duration-ms 10000] [--web-bind 127.0.0.1:8090] [--theme-file PATH] [--registry PATH] [--rpc-pipe iCemDataMonitor.Host] [--tray-icon PATH]"
            );
            return ExitCode::from(2);
        }
    };
    match run(options) {
        Ok(restart) => {
            if restart {
                let result = std::env::current_exe().and_then(|exe| {
                    std::process::Command::new(exe)
                        .args(std::env::args_os().skip(1))
                        .spawn()
                });
                if let Err(error) = result {
                    eprintln!("Host restart: {error}");
                    return ExitCode::from(1);
                }
            }
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(1)
        }
    }
}
