#![forbid(unsafe_code)]
//! Explicit headless integration executable, independent of the monitor host.
use std::process::ExitCode;
#[cfg(windows)]
mod windows {
    use icem_config::emulator::{EmuSettings, SettingsPaths};
    use icem_emulation::{
        control::{ControlReply, process_line, to_json},
        lan::{BaseProvider, Registry},
        runtime::Engine,
        simulation::load_catalog,
    };
    use icem_platform_windows::{ComSerialEmitter, QueuedAceLanEmitter};
    use icem_transport::emulator_control::{ACK_DRAIN, ControlServer, Executor, request};
    use std::{
        path::PathBuf,
        sync::{
            Arc, Mutex,
            atomic::{AtomicBool, Ordering},
        },
        time::{Duration, Instant},
    };
    type Runtime = Engine<ComSerialEmitter, QueuedAceLanEmitter>;
    struct Service {
        engine: Mutex<Runtime>,
        paths: SettingsPaths,
        started: Instant,
        quit_ack: AtomicBool,
        shutting_down: AtomicBool,
        quit_requested_at: Mutex<Option<Instant>>,
    }
    impl icem_desktop::UiPort for Service {
        fn request(&self, command: serde_json::Value) -> Result<serde_json::Value, String> {
            let reply = self.execute(&command.to_string());
            let value = serde_json::from_str(&reply.json).map_err(|e| e.to_string())?;
            self.reply_flushed(&reply);
            Ok(value)
        }
    }
    impl Executor for Service {
        fn execute(&self, line: &str) -> ControlReply {
            let mut engine = match self.engine.lock() {
                Ok(engine) => engine,
                Err(_) => {
                    return ControlReply {
                        cmd: String::new(),
                        json: to_json(false, "", None, Some("engine lock poisoned")),
                        start_watch: false,
                        quit: false,
                    };
                }
            };
            if self.shutting_down.load(Ordering::Acquire) {
                return ControlReply {
                    cmd: String::new(),
                    json: to_json(false, "", None, Some("engine is shutting down")),
                    start_watch: false,
                    quit: false,
                };
            }
            engine.set_time(self.started.elapsed().as_secs_f64());
            let revision = engine.settings_revision();
            let reply = process_line(line, &mut *engine);
            if reply.quit {
                self.quit_requested_at
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .get_or_insert_with(Instant::now);
            }
            if engine.settings_revision() != revision
                && let Err(error) = engine.settings().save(&self.paths)
            {
                eprintln!("settings save failed: {error}");
            }
            reply
        }
        fn watch_tick(&self) -> String {
            self.execute("status").json
        }
        fn reply_flushed(&self, reply: &ControlReply) {
            if reply.quit {
                self.quit_ack.store(true, Ordering::Release);
            }
        }
    }
    impl Service {
        fn should_stop(&self) -> bool {
            self.quit_ack.load(Ordering::Acquire)
                || self
                    .quit_requested_at
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .is_some_and(|request| request.elapsed() >= ACK_DRAIN)
        }
    }
    pub fn main() -> u8 {
        match run() {
            Ok(code) => code,
            Err(error) => {
                eprintln!("{error}");
                1
            }
        }
    }
    fn run() -> Result<u8, Box<dyn std::error::Error>> {
        let mut args = std::env::args().skip(1);
        let mut config = None;
        let mut templates = None;
        let mut pipe = icem_emulation::CONTROL_PIPE.to_owned();
        let mut duration = None;
        let mut cli = None;
        let mut desktop = false;
        while let Some(key) = args.next() {
            if key == "--desktop" {
                desktop = true;
                continue;
            }
            if key == "--help" {
                eprintln!(
                    "Usage: icem-emulator-engine-dev --config PATH --templates DIR [--pipe NAME] [--duration-ms N] [--desktop]\n       icem-emulator-engine-dev [--pipe NAME] --cli <command...>\nDevelopment headless engine; GPUI/tray acceptance pending."
                );
                return Ok(0);
            }
            if key == "--cli" {
                cli = Some(args.collect::<Vec<_>>().join(" "));
                break;
            }
            let Some(value) = args.next() else {
                eprintln!("{key} requires a value");
                return Ok(2);
            };
            match key.as_str() {
                "--config" => config = Some(PathBuf::from(value)),
                "--templates" => templates = Some(PathBuf::from(value)),
                "--pipe" => pipe = value,
                "--duration-ms" => match value.parse::<u64>() {
                    Ok(n) if (1..=3_600_000).contains(&n) => {
                        duration = Some(Duration::from_millis(n))
                    }
                    _ => {
                        eprintln!("duration must be 1..3600000 ms");
                        return Ok(2);
                    }
                },
                _ => {
                    eprintln!("unknown option {key}");
                    return Ok(2);
                }
            }
        }
        // CLI dispatch never creates a singleton, settings file or emitter.
        if let Some(command) = cli {
            if command.is_empty()
                || config.is_some()
                || templates.is_some()
                || duration.is_some()
                || desktop
            {
                eprintln!("invalid CLI arguments");
                return Ok(2);
            }
            let response = match request(&pipe, &command, Duration::from_secs(3)) {
                Ok(response) => response,
                Err(error) => {
                    eprintln!("{error}");
                    return Ok(2);
                }
            };
            let json: serde_json::Value = serde_json::from_str(&response)?;
            println!("{response}");
            return Ok(u8::from(json["ok"] != true));
        }
        let (Some(config), Some(templates)) = (config, templates) else {
            eprintln!("--config and --templates are required; see --help");
            return Ok(2);
        };
        let singleton = icem_platform_windows::emulator_mutex()?;
        let Some(_singleton) = singleton.lock(Duration::ZERO)? else {
            eprintln!("emulator is already running");
            return Ok(1);
        };
        let paths = SettingsPaths::explicit(config);
        let loaded = EmuSettings::load(&paths);
        for diagnostic in loaded.diagnostics {
            eprintln!("{diagnostic}");
        }
        let catalog = load_catalog(&templates);
        for warning in catalog.warnings {
            eprintln!("{warning}");
        }
        let registry = Registry::load(
            &templates.join("lantable_registry.json"),
            Some(&templates.join("firmware_registry.json")),
        )?;
        let base = BaseProvider::load(registry, &templates.join("variable_registry.json"), None)?;
        let service = Arc::new(Service {
            engine: Mutex::new(Engine::new(
                loaded.settings,
                catalog.templates,
                base,
                ComSerialEmitter::default(),
                QueuedAceLanEmitter::system()?,
            )),
            paths,
            started: Instant::now(),
            quit_ack: AtomicBool::new(false),
            shutting_down: AtomicBool::new(false),
            quit_requested_at: Mutex::new(None),
        });
        // Bind before starting hardware; a foreign pipe must never split commands.
        let server = ControlServer::bind(&pipe, service.clone())?;
        service
            .engine
            .lock()
            .map_err(|_| "engine lock poisoned")?
            .start();
        eprintln!(
            "DEVELOPMENT EMULATOR ENGINE READY: {pipe}; GPUI/tray/hardware acceptance pending"
        );
        let desktop_result = if desktop {
            let stopping = Arc::new(AtomicBool::new(false));
            let worker_stop = stopping.clone();
            let worker_service = service.clone();
            let worker = std::thread::Builder::new()
                .name("emulator-tick".into())
                .spawn(move || -> Result<(), String> {
                    let mut due = Instant::now();
                    while !worker_stop.load(Ordering::Acquire) && !worker_service.should_stop() {
                        if Instant::now() >= due {
                            let mut engine = worker_service
                                .engine
                                .lock()
                                .map_err(|_| "engine lock poisoned")?;
                            engine.set_time(worker_service.started.elapsed().as_secs_f64());
                            engine.tick();
                            due = Instant::now() + Duration::from_secs(1);
                        }
                        std::thread::sleep(Duration::from_millis(10));
                    }
                    Ok(())
                })?;
            let checking = service.clone();
            let stop = Arc::new(move || {
                checking.should_stop()
                    || duration.is_some_and(|limit| checking.started.elapsed() >= limit)
            });
            let icon = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../assets/iCemEmule.ico");
            let result = icem_desktop::run_emulator(service.clone(), stop, icon);
            stopping.store(true, Ordering::Release);
            let tick_result = worker.join().map_err(|_| "emulator tick panicked")?;
            tick_result?;
            result
        } else {
            Ok(())
        };
        let mut due = Instant::now();
        while !desktop
            && !service.should_stop()
            && duration.is_none_or(|limit| service.started.elapsed() < limit)
        {
            if Instant::now() >= due {
                let mut engine = service.engine.lock().map_err(|_| "engine lock poisoned")?;
                engine.set_time(service.started.elapsed().as_secs_f64());
                engine.tick();
                due += Duration::from_secs(1);
                if due < Instant::now() {
                    due = Instant::now();
                }
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        service.shutting_down.store(true, Ordering::Release);
        server.stop()?;
        let mut engine = service.engine.lock().map_err(|_| "engine lock poisoned")?;
        engine.shutdown();
        engine.settings().save(&service.paths)?;
        desktop_result?;
        Ok(0)
    }
}
fn main() -> ExitCode {
    #[cfg(windows)]
    {
        ExitCode::from(windows::main())
    }
    #[cfg(not(windows))]
    {
        eprintln!("The headless integration engine requires Windows COM/Npcap/named pipes.");
        ExitCode::from(78)
    }
}
