#![forbid(unsafe_code)]
use std::process::ExitCode;
#[cfg(windows)]
mod windows {
    use icem_web::{client, proto::*};
    use serde_json::{Value, json};
    use std::sync::Arc;
    struct Session {
        runtime: tokio::runtime::Runtime,
        metrics: Option<tonic::Streaming<MetricsSample>>,
        boot: Option<tonic::Streaming<BootStage>>,
    }
    struct HostPort {
        pipe: String,
        endpoint: std::net::SocketAddr,
        session: std::sync::Mutex<Option<Session>>,
    }
    impl icem_desktop::UiPort for HostPort {
        fn request(&self, command: Value) -> Result<Value, String> {
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
            let result = runtime.block_on(async {
                if command["cmd"] == "boot" {
                    let reset = boot.is_none();
                    let result = async {
                        if boot.is_none() {
                            let mut client = client::connect(&self.pipe).await.map_err(|e|e.to_string())?;
                            *boot=Some(client.subscribe_boot_stages(BootStreamRequest{}).await.map_err(|e|e.to_string())?.into_inner());
                        }
                        let mut stages=Vec::new();
                        let due=std::time::Instant::now()+std::time::Duration::from_millis(100);
                        while stages.len()<128 {
                            let remaining=due.saturating_duration_since(std::time::Instant::now());
                            if remaining.is_zero(){break;}
                            match tokio::time::timeout(remaining,boot.as_mut().ok_or("boot stream unavailable")?.message()).await {
                                Ok(Ok(Some(stage)))=>stages.push(json!({"ordinal":stage.ordinal,"id":stage.id,"title":stage.title,"completed":stage.completed,"detail":stage.detail})),
                                Ok(Ok(None))=>return Err("boot stream ended".to_owned()),
                                Ok(Err(error))=>return Err(error.to_string()),
                                Err(_)=>break,
                            }
                        }
                        Ok(json!({"ok":true,"data":{"stages":stages,"reset":reset}}))
                    }.await;
                    if result.is_err(){*boot=None;}
                    return result;
                }
                if command["cmd"] == "metrics" {
                    let result = async {
                        if metrics.is_none() {
                            let mut client = client::connect(&self.pipe).await.map_err(|e|e.to_string())?;
                            *metrics = Some(client.subscribe_metrics(SubscribeRequest { interval_ms: 1000 }).await.map_err(|e|e.to_string())?.into_inner());
                        }
                        let message = tokio::time::timeout(std::time::Duration::from_millis(1500),
                            metrics.as_mut().ok_or("metrics stream unavailable")?.message()).await
                            .map_err(|_|"metrics stream timeout".to_owned())?.map_err(|e|e.to_string())?.ok_or("metrics stream ended")?;
                        let at = message.timestamp_utc.map(|t|t.seconds.saturating_mul(1000).saturating_add(i64::from(t.nanos)/1_000_000)).unwrap_or_default();
                        let deltas: Vec<_> = message.deltas.into_iter().filter_map(|d| {
                            let (value,kind) = match d.value? {
                                register_delta::Value::Int32Value(v)=>(json!(v),"int32"),
                                register_delta::Value::FloatValue(v)=>(json!(if v.is_nan(){"NaN".to_owned()}else if v==f32::INFINITY{"Infinity".to_owned()}else if v==f32::NEG_INFINITY{"-Infinity".to_owned()}else{v.to_string()}),"float"),
                            };
                            Some(json!({"addr":d.addr,"value":value,"kind":kind}))
                        }).collect();
                        Ok::<_,String>(json!({"ok":true,"data":{"at":at,"heartbeat":message.heartbeat,"clamped":message.interval_clamped,"deltas":deltas}}))
                    }.await;
                    if result.is_err() {*metrics=None;}
                    return result;
                }
                if command["cmd"] == "dashboard" {
                    let selected = command["selected"].as_array().ok_or("missing dashboard selection")?.iter()
                        .map(|a| a.as_u64().and_then(|n|u16::try_from(n).ok()).ok_or("invalid selection"))
                        .collect::<Result<Vec<_>,_>>()?;
                    let data = icem_web::dashboard_client::read(self.endpoint, &selected).await?;
                    return Ok(json!({"ok":true,"data":data}));
                }
                let mut client=client::connect(&self.pipe).await.map_err(|e|e.to_string())?;
                let data=match command["cmd"].as_str().unwrap_or(""){
                    "navigate"=>{
                        let result=client.navigate_ui(NavigateUiRequest{section:0}).await.map_err(|e|e.to_string())?.into_inner();
                        json!({"pending":result.pending,"uiAlive":result.ui_alive})
                    }
                    "status"=>{
                        let status=client.get_status(HostStatusRequest{}).await.map_err(|e|e.to_string())?.into_inner();
                        let layers=status.layers.unwrap_or_default();
                        json!({"busOpen":status.bus_open,"addinAlive":status.addin_alive,"heartbeat":status.heartbeat,"zones":status.zones.into_iter().map(|z|json!({"kind":z.kind,"active":z.active,"ageMs":z.age_ms})).collect::<Vec<_>>(),"layers":{"modbus":layers.modbus_bound,"sniffer":layers.sniffer,"opcDa":layers.opc_da,"web":layers.web,"lastError":layers.last_error},"consoleVisible":status.console_visible,"autostartEnabled":status.autostart_enabled})
                    }
                    "prefs"=>{let result=client.get_ui_prefs(UiPrefsRequest{}).await.map_err(|e|e.to_string())?.into_inner();json!({"themeLight":result.theme_light})}
                    "theme"=>{let result=client.set_ui_theme(SetUiThemeRequest{theme_light:command["value"]==true}).await.map_err(|e|e.to_string())?.into_inner();json!({"themeLight":result.theme_light})}
                    "layer"=>{client.set_layer(LayerControlRequest{layer:command["layer"].as_i64().unwrap_or(0) as i32,desired_state:false}).await.map_err(|e|e.to_string())?;json!({})}
                    "console"=>{client.set_console_visible(SetConsoleVisibleRequest{visible:command["value"]==true}).await.map_err(|e|e.to_string())?;json!({})}
                    "autostart"=>{client.set_autostart(SetAutostartRequest{enabled:command["value"]==true}).await.map_err(|e|e.to_string())?;json!({})}
                    _=>return Err("unknown Host UI command".into()),
                };Ok(json!({"ok":true,"data":data}))
            });
            if result.is_err() && matches!(command["cmd"].as_str(), Some("status" | "boot")) {
                // Drop the runtime as well as streams: dead transport tasks can keep
                // old pipe handles alive and prevent the next Host's exclusive bind.
                // Both subscriptions must replay on a fresh session after disconnect.
                *session = None;
            }
            result
        }
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        use icem_desktop::UiPort;
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
                endpoint: ([127, 0, 0, 1], 8090).into(),
                session: std::sync::Mutex::new(None),
            };
            let mut model = icem_desktop::Model::default();
            for fault in ["boot", "status", "boot", "status"] {
                let server = LocalRpcServer::bind(&name, state(true)?)?;
                let initial = port.request(json!({"cmd":"boot"}))?;
                assert_eq!(initial["data"]["reset"], true);
                model.apply("boot", Ok(initial));
                assert!(model.boot_ready);
                server.stop()?;
                // Fault injection happens after READY while the old Shell keeps running.
                let failure = port.request(json!({"cmd":fault}));
                assert!(failure.is_err());
                model.apply(fault, failure);
                assert!(!model.boot_ready);
                assert!(
                    port.session
                        .lock()
                        .map_err(|_| "session poisoned")?
                        .is_none()
                );
                let server = LocalRpcServer::bind(&name, state(false)?)?;
                let replay = port.request(json!({"cmd":"boot"}))?;
                assert_eq!(replay["data"]["reset"], true);
                model.apply("boot", Ok(replay));
                assert!(!model.boot_ready);
                assert_eq!(model.boot_stages.len(), 1);
                assert_eq!(model.boot_stages[&1].id, "ini");
                server.stop()?;
                assert!(port.request(json!({"cmd":"boot"})).is_err());
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

    pub fn run() -> Result<(), String> {
        let mut pipe = icem_web::HOST_PIPE.to_owned();
        let mut url = "http://127.0.0.1:8090".to_owned();
        let mut args = std::env::args().skip(1);
        while let Some(key) = args.next() {
            let value = args.next().ok_or("option requires a value")?;
            match key.as_str() {
                "--pipe" => pipe = value,
                "--web-url" => url = value,
                _ => return Err(format!("unknown option: {key}")),
            }
        }
        // Development host URL is constrained to the local station endpoint.
        let endpoint = url
            .strip_prefix("http://127.0.0.1:")
            .ok_or("--web-url requires http://127.0.0.1:PORT")?;
        let port = endpoint.parse::<u16>().map_err(|_| "invalid web port")?;
        icem_desktop::run_monitor(Arc::new(HostPort {
            pipe,
            endpoint: ([127, 0, 0, 1], port).into(),
            session: std::sync::Mutex::new(None),
        }))
        .map_err(|e| e.to_string())
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
