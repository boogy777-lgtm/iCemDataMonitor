//! GPUI desktop presentation. Domain state stays behind the original control seam.
#![forbid(unsafe_code)]
pub mod dashboard;
pub mod emulator;
pub mod logs;
pub mod navigation;
pub mod window_visibility;
use icem_emulation::{
    Channel,
    control::{
        Command, Execution,
        application::{Equipment, ErrorCategory, NativeValue, Scope, Variable},
        payload::Signal,
    },
};
use serde_json::Value;
use std::sync::Arc;
pub mod host;
pub mod worker;
pub use worker::WorkerCompletion;

/// Implementations must bound I/O time and return owned values.
pub trait UiPort: Send + Sync + 'static {
    fn logs(
        &self,
        command: icem_logging::Command,
    ) -> Result<icem_logging::Reply, icem_logging::ControlError>;
    fn emulator(&self, command: Command) -> Execution {
        Execution::rejected(
            command.name(),
            ErrorCategory::Unavailable,
            "emulator API unavailable",
        )
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Page {
    #[default]
    Com,
    Lan,
    Units,
    Status,
    Dashboard,
}
#[derive(Clone, Debug, PartialEq)]
pub struct VariableRow {
    pub identity: Signal,
    pub value: String,
    pub uom: String,
    pub overridden: bool,
}
impl Default for VariableRow {
    fn default() -> Self {
        Self {
            identity: Signal::Com {
                name: String::new(),
            },
            value: String::new(),
            uom: String::new(),
            overridden: false,
        }
    }
}
impl VariableRow {
    pub fn name(&self) -> &str {
        match &self.identity {
            Signal::Com { name } | Signal::Lan { name, .. } => name,
        }
    }
    pub fn sv(&self) -> String {
        match self.identity {
            Signal::Com { .. } => String::new(),
            Signal::Lan { sv, .. } => format!("0x{sv:08X}"),
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProfileChoice {
    pub name: String,
    pub device_type: String,
}
impl ProfileChoice {
    pub fn command(&self) -> Command {
        Command::Profile {
            name: self.name.clone(),
            device_type: Some(self.device_type.clone()),
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BootStageRow {
    pub id: String,
    pub title: String,
    pub completed: bool,
    pub detail: String,
}
#[derive(Default)]
pub struct Model {
    pub host_logs: logs::LogView,
    pub local_logs: logs::LogView,
    pub page: Page,
    pub dashboard: dashboard::Dashboard,
    pub boot_stages: std::collections::BTreeMap<u32, BootStageRow>,
    pub boot_ready: bool,
    pub boot_error: Option<String>,
    pub navigation: Option<bool>,
    pub light: bool,
    pub connected: bool,
    pub note: String,
    pub emulator: emulator::View,
    pub host: Option<icem_web::shell::Status>,
    pub com_variables: Vec<VariableRow>,
    pub lan_variables: Vec<VariableRow>,
    pub profiles: Vec<ProfileChoice>,
    pub ports: Vec<String>,
    pub adapters: Vec<String>,
    pub selected_com: Option<usize>,
    pub selected_lan: Option<usize>,
    pub group: usize,
}
pub const UNIT_GROUPS: [(&str, &str); 6] = [
    ("flow", "Расход"),
    ("temperature", "Температура"),
    ("density", "Плотность"),
    ("volume", "Объём"),
    ("level", "Уровень"),
    ("pressure", "Давление"),
];
pub const PRESETS: [&str; 4] = ["FirmwareDefault", "LanNative", "Metric", "Imperial"];
pub fn text(value: &Value) -> String {
    match value {
        Value::Null => "—".into(),
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}
impl Model {
    pub fn can_submit_log(&self, request: &logs::Request) -> bool {
        let view = if request.local {
            &self.local_logs
        } else {
            &self.host_logs
        };
        if request.command == icem_logging::Command::Get {
            !view.pending
        } else {
            view.actionable()
        }
    }

    pub fn can_toggle_sniffer(&self) -> bool {
        self.connected
            && self
                .host
                .as_ref()
                .is_some_and(|s| s.layers.sniffer_available && !s.layers.busy)
    }
    pub fn selected(&self, lan: bool) -> Option<&VariableRow> {
        if lan {
            self.selected_lan.and_then(|i| self.lan_variables.get(i))
        } else {
            self.selected_com.and_then(|i| self.com_variables.get(i))
        }
    }
    pub fn select_variable(&mut self, identity: &Signal) -> Option<String> {
        let lan = matches!(identity, Signal::Lan { .. });
        let rows = if lan {
            &self.lan_variables
        } else {
            &self.com_variables
        };
        let index = rows.iter().position(|row| &row.identity == identity);
        let value = index.map(|i| rows[i].value.clone());
        if lan {
            self.selected_lan = index;
        } else {
            self.selected_com = index;
        }
        value
    }
    pub fn edit_command(&self, lan: bool, value: Option<&str>) -> Result<Command, String> {
        let row = self
            .selected(lan)
            .ok_or_else(|| "выберите переменную в списке слева".to_owned())?;
        let variable = match &row.identity {
            Signal::Com { name } => Variable {
                scope: Scope::channel(Channel::Com),
                name: name.clone(),
                sv: None,
            },
            Signal::Lan { sv, name } => Variable {
                scope: Scope::channel(Channel::Lan),
                name: name.clone(),
                sv: Some(*sv),
            },
        };
        if let Some(value) = value {
            let number = value
                .trim()
                .replace(',', ".")
                .parse::<f64>()
                .map_err(|_| "ожидается числовое значение")?;
            Ok(Command::Set(
                variable,
                NativeValue::finite(number).map_err(str::to_owned)?,
            ))
        } else {
            Ok(Command::Clear(variable))
        }
    }
    pub fn lan_header(&self) -> String {
        self.emulator.lan_header()
    }
}
pub enum Reply {
    Logs(
        logs::Request,
        Result<icem_logging::Reply, icem_logging::ControlError>,
    ),
    Emulator {
        command: String,
        result: Execution,
    },
}
enum Request {
    Logs(logs::Request),
    Emulator(Command),
}
pub struct Bridge {
    worker: worker::Worker<Request, Reply>,
}
impl Bridge {
    pub fn start(port: Arc<dyn UiPort>) -> std::io::Result<(Self, WorkerCompletion)> {
        let (worker, completion) = worker::Worker::start(move |request| match request {
            Request::Logs(request) => Reply::Logs(request, port.logs(request.command)),
            Request::Emulator(command) => Reply::Emulator {
                command: command.name().to_owned(),
                result: port.emulator(command),
            },
        })?;
        Ok((Self { worker }, completion))
    }
    pub fn send_logs(&self, request: logs::Request) -> Result<(), String> {
        self.worker.send(Request::Logs(request))
    }
    pub fn send_emulator(&self, command: Command) -> Result<(), String> {
        command.validate_budget().map_err(str::to_owned)?;
        self.worker.send(Request::Emulator(command))
    }
    pub fn drain(&self, model: &mut Model) {
        while let Ok(reply) = self.worker.try_recv() {
            match reply {
                Reply::Logs(request, result) => {
                    if request.local {
                        model.local_logs.apply(result)
                    } else {
                        model.host_logs.apply(result)
                    }
                }
                Reply::Emulator { command, result } => model.apply_emulator(&command, result),
            }
        }
    }
}

#[cfg(windows)]
mod input;
#[cfg(windows)]
mod theme;
#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use windows::run_emulator;

pub fn equipment_command(value: &str) -> Result<Command, String> {
    let value = value.trim();
    let number = if let Some(hex) = value
        .strip_prefix("0x")
        .or_else(|| value.strip_prefix("0X"))
    {
        u32::from_str_radix(hex, 16)
    } else {
        value.parse::<u32>()
    }
    .map_err(|_| "ожидается uint32: 0x… или десятичное число".to_owned())?;
    Ok(Command::SetEquipment(Equipment(number)))
}
pub fn unit_command(group: usize, unit: Option<&str>) -> Command {
    Command::SetUnit {
        group: UNIT_GROUPS.get(group).map(|g| g.0).unwrap_or("").into(),
        unit: unit
            .filter(|u| !u.trim().is_empty())
            .map(|u| u.trim().to_owned()),
    }
}
#[cfg(windows)]
mod monitor;
#[cfg(windows)]
pub use monitor::run_monitor;

#[cfg(windows)]
mod tray;
#[cfg(windows)]
pub use tray::HostTray;
