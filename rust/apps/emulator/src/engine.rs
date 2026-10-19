#![forbid(unsafe_code)]
//! Explicit headless integration executable, independent of the monitor host.
use std::process::ExitCode;
#[cfg(windows)]
mod windows {
    use icem_config::emulator::{EmuSettings, SettingsPaths};
    use icem_emulation::{
        control::{Command, ControlReply, Execution, application::ErrorCategory, parse_line},
        lan::{BaseProvider, Registry},
        runtime::Engine,
        simulation::load_catalog,
    };
    use icem_platform_windows::{ComSerialEmitter, QueuedAceLanEmitter};
    use icem_transport::emulator_control::{
        ACK_DRAIN, ClientEnd, ControlServer, Executor, cli_request,
    };
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
        logs: Arc<icem_logging::SharedLogs>,
        engine: Mutex<Runtime>,
        paths: SettingsPaths,
        started: Instant,
        quit_ack: AtomicBool,
        shutting_down: AtomicBool,
        quit_requested_at: Mutex<Option<Instant>>,
    }
    impl icem_desktop::UiPort for Service {
        fn logs(
            &self,
            command: icem_logging::Command,
        ) -> Result<icem_logging::Reply, icem_logging::ControlError> {
            self.logs.request(command)
        }
        fn emulator(&self, command: Command) -> Execution {
            let reply = self.execute_application(command);
            if reply.command == "quit" && reply.result.ok {
                self.quit_ack.store(true, Ordering::Release);
            }
            reply
        }
    }
    impl Executor for Service {
        fn execute(&self, line: &str) -> ControlReply {
            match parse_line(line) {
                Ok(command) => self.execute_application(command).v1(),
                Err(reply) => reply,
            }
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
        fn execute_application(&self, command: Command) -> Execution {
            let name = command.name();
            let mut engine = match self.engine.lock() {
                Ok(engine) => engine,
                Err(_) => {
                    return Execution::rejected(
                        name,
                        ErrorCategory::Internal,
                        "engine lock poisoned",
                    );
                }
            };
            if self.shutting_down.load(Ordering::Acquire) {
                return Execution::rejected(
                    name,
                    ErrorCategory::Unavailable,
                    "engine is shutting down",
                );
            }
            engine.set_time(self.started.elapsed().as_secs_f64());
            let reply = engine.execute_application(command, |settings| {
                settings.save(&self.paths).map_err(|e| e.to_string())
            });
            if reply.command == "quit" && reply.result.ok {
                self.quit_requested_at
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .get_or_insert_with(Instant::now);
            }
            if let Some(error) = &reply.error
                && error.category == ErrorCategory::Persistence
            {
                self.logs
                    .record(&format!("settings save failed: {}", error.message));
                eprintln!("settings save failed: {}", error.message);
            }
            reply
        }

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
            use std::io::Write;
            let mut output = std::io::stdout().lock();
            let result = cli_request(&pipe, &command, |response| {
                writeln!(output, "{response}")?;
                output.flush()?;
                Ok(std::ops::ControlFlow::Continue(()))
            });
            return Ok(match result {
                Ok(ClientEnd::Reply { ok }) => u8::from(!ok),
                Ok(ClientEnd::Cancelled) => 0,
                Err(error) => {
                    // Serializer escapes operator text. The server's frozen v1 envelope is unchanged.
                    let value =
                        icem_emulation::control::to_json(false, "", None, Some(&error.to_string()));
                    let _ = writeln!(output, "{value}");
                    eprintln!("{error}");
                    1
                }
            });
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
        let logs = Arc::new(icem_logging::SharedLogs::new(icem_logging::Owner::Emulator));
        logs.record("Emulator startup");
        let paths = SettingsPaths::explicit(config);
        let loaded = EmuSettings::load(&paths);
        for diagnostic in loaded.diagnostics {
            logs.record(&diagnostic);
            eprintln!("{diagnostic}");
        }
        let catalog = load_catalog(&templates);
        for warning in catalog.warnings {
            logs.record(&warning);
            eprintln!("{warning}");
        }
        let registry = Registry::load(
            &templates.join("lantable_registry.json"),
            Some(&templates.join("firmware_registry.json")),
        )?;
        let base = BaseProvider::load(registry, &templates.join("variable_registry.json"), None)?;
        let service = Arc::new(Service {
            logs,
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
        service.logs.record("Emulator READY");
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
            let result = icem_platform_windows::application_asset("iCemEmule.ico")
                .and_then(|icon| icem_desktop::run_emulator(service.clone(), stop, icon));
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
