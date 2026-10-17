#![forbid(unsafe_code)]
use std::process::ExitCode;
#[cfg(windows)]
mod windows {
    use icem_web::{client, proto::*};
    use serde_json::{Value, json};
    use std::sync::Arc;
    struct HostPort(String);
    impl icem_desktop::UiPort for HostPort {
        fn request(&self, command: Value) -> Result<Value, String> {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .map_err(|e| e.to_string())?;
            runtime.block_on(async{
                let mut client=client::connect(&self.0).await.map_err(|e|e.to_string())?;
                let data=match command["cmd"].as_str().unwrap_or(""){
                    "status"=>{
                        let status=client.get_status(HostStatusRequest{}).await.map_err(|e|e.to_string())?.into_inner();
                        let layers=status.layers.unwrap_or_default();
                        json!({"busOpen":status.bus_open,"addinAlive":status.addin_alive,"heartbeat":status.heartbeat,"zones":status.zones.into_iter().map(|z|json!({"active":z.active,"ageMs":z.age_ms})).collect::<Vec<_>>(),"layers":{"modbus":layers.modbus_bound,"sniffer":layers.sniffer,"opcDa":layers.opc_da},"consoleVisible":status.console_visible,"autostartEnabled":status.autostart_enabled})
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
        endpoint.parse::<u16>().map_err(|_| "invalid web port")?;
        icem_desktop::run_monitor(Arc::new(HostPort(pipe)), url).map_err(|e| e.to_string())
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
