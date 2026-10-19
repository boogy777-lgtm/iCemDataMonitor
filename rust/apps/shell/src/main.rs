#![forbid(unsafe_code)]
use std::process::ExitCode;
#[cfg(windows)]
mod windows {
    use icem_web::shell::{BootBatch, Request, Response};
    use icem_web::{client, proto::*};
    use std::sync::Arc;
    struct Session {
        runtime: tokio::runtime::Runtime,
        metrics: Option<tonic::Streaming<MetricsSample>>,
        boot: Option<tonic::Streaming<BootStage>>,
    }
    struct HostPort {
        pipe: String,
        endpoint: Option<std::net::SocketAddr>,
        station_endpoint: std::sync::OnceLock<Result<std::net::SocketAddr, String>>,
        launch: Option<icem_web::host_client::Launch>,
        ensured: std::sync::OnceLock<Result<(), String>>,
        session: std::sync::Mutex<Option<Session>>,
    }
    impl icem_desktop::host::Port for HostPort {
        fn request(&self, command: Request) -> Result<Response, String> {
            let mut session = self
                .session
                .lock()
                .map_err(|_| "Host UI session poisoned")?;
            if session.is_none() {
                *session = Some(Session {
                    runtime: tokio::runtime::Builder::new_current_thread()
                        .enable_all()
                        .build()
                        .map_err(|e| e.to_string())?,
                    metrics: None,
                    boot: None,
                });
            }
            let Session {
                runtime,
                metrics,
                boot,
            } = session.as_mut().ok_or("Host UI session unavailable")?;
            if let Some(launch) = &self.launch {
                let startup = self.ensured.get_or_init(|| {
                    runtime.block_on(icem_web::host_client::ensure(
                        &self.pipe,
                        Some(launch),
                        icem_web::host_client::READY_TIMEOUT,
                    ))
                });
                if let Err(error) = startup {
                    // Reconnection is read-only. A failed ensure is never another spawn demand.
                    let serving = runtime.block_on(async {
                        tokio::time::timeout(std::time::Duration::from_secs(1), async {
                            let mut rpc = client::connect(&self.pipe)
                                .await
                                .map_err(|e| e.to_string())?;
                            rpc.get_status(HostStatusRequest {})
                                .await
                                .map_err(|e| e.to_string())?;
                            Ok::<_, String>(())
                        })
                        .await
                    });
                    if !matches!(serving, Ok(Ok(()))) {
                        let detail = icem_config::StationPaths::installed()
                            .ok()
                            .and_then(|paths| {
                                let path = paths.selected().ok()?;
                                icem_config::IniOptions::read_existing(path)
                                    .err()
                                    .map(|e| format!("{}: {e}", path.display()))
                            })
                            .unwrap_or_default();
                        return Err(format!("{error} {detail}"));
                    }
                }
            }
            let operation = command.operation();
            let result = runtime.block_on(async {
                if let Request::Logs(action) = &command {
                    let (action, enabled) = action.v1();
                    return icem_web::log_control::request(&self.pipe, action, enabled)
                        .await
                        .map(Response::Logs);
                }
                if matches!(command, Request::Boot) {
                    let reset = boot.is_none();
                    let result = async {
                        if boot.is_none() {
                            let mut client = client::connect(&self.pipe)
                                .await
                                .map_err(|e| e.to_string())?;
                            *boot = Some(
                                client
                                    .subscribe_boot_stages(BootStreamRequest {})
                                    .await
                                    .map_err(|e| e.to_string())?
                                    .into_inner(),
                            );
                        }
                        let mut stages = Vec::new();
                        let due = std::time::Instant::now() + std::time::Duration::from_millis(100);
                        while stages.len() < 128 {
                            let remaining =
                                due.saturating_duration_since(std::time::Instant::now());
                            if remaining.is_zero() {
                                break;
                            }
                            match tokio::time::timeout(
                                remaining,
                                boot.as_mut().ok_or("boot stream unavailable")?.message(),
                            )
                            .await
                            {
                                Ok(Ok(Some(stage))) => stages.push(stage),
                                Ok(Ok(None)) => return Err("boot stream ended".to_owned()),
                                Ok(Err(error)) => return Err(error.to_string()),
                                Err(_) => break,
                            }
                        }
                        Ok(Response::Boot(BootBatch { stages, reset }))
                    }
                    .await;
                    if result.is_err() {
                        *boot = None;
                    }
                    return result;
                }
                if matches!(command, Request::Metrics) {
                    let result = async {
                        if metrics.is_none() {
                            let mut client = client::connect(&self.pipe)
                                .await
                                .map_err(|e| e.to_string())?;
                            *metrics = Some(
                                client
                                    .subscribe_metrics(SubscribeRequest { interval_ms: 1000 })
                                    .await
                                    .map_err(|e| e.to_string())?
                                    .into_inner(),
                            );
                        }
                        let message = tokio::time::timeout(
                            std::time::Duration::from_millis(1500),
                            metrics
                                .as_mut()
                                .ok_or("metrics stream unavailable")?
                                .message(),
                        )
                        .await
                        .map_err(|_| "metrics stream timeout".to_owned())?
                        .map_err(|e| e.to_string())?
                        .ok_or("metrics stream ended")?;
                        Ok::<_, String>(Response::Metrics(message))
                    }
                    .await;
                    if result.is_err() {
                        *metrics = None;
                    }
                    return result;
                }
                if let Request::Dashboard(selected) = &command {
                    let endpoint = match self.endpoint {
                        Some(endpoint) => endpoint,
                        None => *self
                            .station_endpoint
                            .get_or_init(|| {
                                let paths = icem_config::StationPaths::installed()
                                    .map_err(|e| e.to_string())?;
                                let settings = icem_config::IniOptions::read_existing(
                                    paths.selected().map_err(|e| e.to_string())?,
                                )
                                .map_err(|e| e.to_string())?;
                                Ok((
                                    [127, 0, 0, 1],
                                    u16::try_from(settings.web_port).map_err(|e| e.to_string())?,
                                )
                                    .into())
                            })
                            .as_ref()
                            .map_err(Clone::clone)?,
                    };
                    let data =
                        icem_web::dashboard_client::read(endpoint, selected.addresses()).await?;
                    return Ok(Response::Dashboard(data));
                }
                let mut client = client::connect(&self.pipe)
                    .await
                    .map_err(|e| e.to_string())?;
                match command {
                    Request::Navigate => client
                        .navigate_ui(NavigateUiRequest { section: 0 })
                        .await
                        .map(|r| Response::Navigate(r.into_inner()))
                        .map_err(|e| e.to_string()),
                    Request::Status => Response::status(
                        client
                            .get_status(HostStatusRequest {})
                            .await
                            .map_err(|e| e.to_string())?
                            .into_inner(),
                    ),
                    Request::Prefs => client
                        .get_ui_prefs(UiPrefsRequest {})
                        .await
                        .map(|r| Response::Prefs(r.into_inner()))
                        .map_err(|e| e.to_string()),
                    Request::Theme(theme_light) => client
                        .set_ui_theme(SetUiThemeRequest { theme_light })
                        .await
                        .map(|r| Response::Theme(r.into_inner()))
                        .map_err(|e| e.to_string()),
                    Request::Layer(layer) => client
                        .set_layer(LayerControlRequest {
                            layer: icem_web::proto::LayerKind::from(layer) as i32,
                            desired_state: false,
                        })
                        .await
                        .map(|r| Response::Layer(r.into_inner()))
                        .map_err(|e| e.to_string()),
                    Request::Console(visible) => client
                        .set_console_visible(SetConsoleVisibleRequest { visible })
                        .await
                        .map_err(|e| e.to_string())
                        .and_then(|r| r.into_inner().try_into())
                        .map(Response::Console),
                    Request::Autostart(enabled) => client
                        .set_autostart(SetAutostartRequest { enabled })
                        .await
                        .map_err(|e| e.to_string())
                        .and_then(|r| r.into_inner().try_into())
                        .map(Response::Autostart),
                    Request::Boot | Request::Metrics | Request::Dashboard(_) | Request::Logs(_) => {
                        Err("adapter request must use its existing owner".into())
                    }
                    Request::LocalLogs(_) => Err("Shell logs belong to the local worker".into()),
                }
            });
            if result.is_err()
                && matches!(
                    operation,
                    icem_web::shell::Operation::Status | icem_web::shell::Operation::Boot
                )
            {
                // Drop the runtime as well as streams: dead transport tasks can keep
                // old pipe handles alive and prevent the next Host's exclusive bind.
                // Both subscriptions must replay on a fresh session after disconnect.
                *session = None;
            }
            result
        }
    }
    pub fn run() -> Result<(), String> {
        let mut pipe = icem_web::HOST_PIPE.to_owned();
        let mut url = None;
        let mut args = std::env::args().skip(1);
        while let Some(key) = args.next() {
            let value = args.next().ok_or("option requires a value")?;
            match key.as_str() {
                "--pipe" => pipe = value,
                "--web-url" => url = Some(value),
                _ => return Err(format!("unknown option: {key}")),
            }
        }
        let endpoint = url
            .map(|url| {
                let port = url
                    .strip_prefix("http://127.0.0.1:")
                    .ok_or("--web-url requires http://127.0.0.1:PORT")?
                    .parse::<u16>()
                    .map_err(|_| "invalid web port")?;
                Ok::<std::net::SocketAddr, String>(([127, 0, 0, 1], port).into())
            })
            .transpose()?;
        // Only the installed default pipe has an automatic launch target. Custom
        // fixture endpoints stay explicit and cannot accidentally start a station Host.
        let launch = (pipe == icem_web::HOST_PIPE)
            .then(icem_web::host_client::Launch::installed)
            .transpose()?;
        icem_desktop::run_monitor(Arc::new(HostPort {
            pipe,
            endpoint,
            station_endpoint: std::sync::OnceLock::new(),
            launch,
            ensured: std::sync::OnceLock::new(),
            session: std::sync::Mutex::new(None),
        }))
        .map_err(|e| e.to_string())
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        use icem_desktop::host::Port;
        use icem_web::{BootStages, HostLayers, LocalRpcServer, WebState};
        type TestResult = Result<(), Box<dyn std::error::Error>>;

        fn state(ready: bool) -> Result<WebState, String> {
            let boot = BootStages::default();
            boot.begin("ini", "new Host")?;
            if ready {
                boot.complete("ini", None)?;
                boot.begin("ready", "ready")?;
                boot.complete("ready", None)?;
            }
            let (mut state, _) = WebState::new(
                HostLayers::default(),
                std::path::PathBuf::new(),
                Default::default(),
            );
            state.attach_boot(boot);
            Ok(state)
        }

        #[test]
        fn boot_and_status_faults_drop_session_allow_rebind_and_replay_without_old_ready()
        -> TestResult {
            let name = format!("icem-shell-reconnect-{}", std::process::id());
            let port = HostPort {
                pipe: name.clone(),
                endpoint: Some(([127, 0, 0, 1], 8090).into()),
                station_endpoint: std::sync::OnceLock::new(),
                launch: Some(icem_web::host_client::Launch {
                    executable: "must-never-spawn-on-reconnect.exe".into(),
                    arguments: vec![],
                }),
                ensured: std::sync::OnceLock::new(),
                session: std::sync::Mutex::new(None),
            };
            let mut model = icem_desktop::Model::default();
            for fault in [
                Request::Boot,
                Request::Status,
                Request::Boot,
                Request::Status,
            ] {
                let server = LocalRpcServer::bind(&name, state(true)?)?;
                let initial = port.request(Request::Boot)?;
                assert!(matches!(&initial, Response::Boot(batch) if batch.reset));
                model.apply_host(icem_web::shell::Operation::Boot, Ok(initial));
                assert!(model.boot_ready);
                server.stop()?;
                // Fault injection happens after READY while the old Shell keeps running.
                let operation = fault.operation();
                let failure = port.request(fault);
                assert!(failure.is_err());
                model.apply_host(operation, failure);
                assert!(!model.boot_ready);
                assert!(
                    port.session
                        .lock()
                        .map_err(|_| "session poisoned")?
                        .is_none()
                );
                let server = LocalRpcServer::bind(&name, state(false)?)?;
                let replay = port.request(Request::Boot)?;
                assert!(matches!(&replay, Response::Boot(batch) if batch.reset));
                model.apply_host(icem_web::shell::Operation::Boot, Ok(replay));
                assert!(!model.boot_ready);
                assert_eq!(model.boot_stages.len(), 1);
                assert_eq!(model.boot_stages[&1].id, "ini");
                server.stop()?;
                assert!(port.request(Request::Boot).is_err());
            }
            Ok(())
        }

        #[test]
        fn lost_navigation_response_after_real_host_consume_is_not_retried() -> TestResult {
            use icem_desktop::navigation::NavigationPoll;
            use icem_web::navigation::Navigation;
            let host = Navigation::default();
            host.navigate(UiSection::Web)?;
            let mut shell = NavigationPoll::default();
            let mut response = None;
            shell.submit(true, || {
                response = Some(host.navigate(UiSection::Unspecified)?);
                Ok(())
            })?;
            assert_eq!(
                response.take().ok_or("no response")?.pending,
                UiSection::Web as i32
            );
            // Discard the consumed reply before Shell sees it, then observe a new
            // pending command. Reconnect must not silently consume that command.
            shell.complete();
            host.navigate(UiSection::Status)?;
            for connected in [false, true, true] {
                shell.submit(connected, || Err("unexpected consume retry".into()))?;
            }
            shell.show();
            shell.submit(true, || {
                response = Some(host.navigate(UiSection::Unspecified)?);
                Ok(())
            })?;
            assert_eq!(
                response.ok_or("missing explicit consume")?.pending,
                UiSection::Status as i32
            );
            Ok(())
        }
    }
}
fn main() -> ExitCode {
    #[cfg(windows)]
    {
        match windows::run() {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("{error}");
                ExitCode::from(1)
            }
        }
    }
    #[cfg(not(windows))]
    {
        eprintln!("GPUI monitor requires Windows.");
        ExitCode::from(78)
    }
}
