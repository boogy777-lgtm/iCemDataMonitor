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
                });
            }
            let Session { runtime, metrics } =
                session.as_mut().ok_or("Host UI session unavailable")?;
            runtime.block_on(async {
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
                                register_delta::Value::FloatValue(v)=>(json!(v.to_string()),"float"),
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
            })
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
