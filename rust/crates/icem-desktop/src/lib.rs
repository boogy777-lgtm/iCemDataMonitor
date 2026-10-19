//! GPUI desktop presentation. Domain state stays behind the original control seam.
#![forbid(unsafe_code)]
pub mod dashboard;
pub mod logs;
pub mod navigation;
pub mod window_visibility;
use icem_emulation::{
    Channel,
    control::{
        Command, Execution,
        application::{Equipment, ErrorCategory, NativeValue, Scope, Variable},
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
#[derive(Clone, Debug, Default, PartialEq)]
pub struct VariableRow {
    pub sv: String,
    pub name: String,
    pub value: String,
    pub uom: String,
    pub overridden: bool,
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
    pub snapshot: Value,
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
    pub fn flag(&self, channel: &str, field: &str) -> bool {
        self.snapshot[channel][field].as_bool() == Some(true)
    }
    pub fn field(&self, channel: &str, field: &str) -> String {
        text(&self.snapshot[channel][field])
    }
    pub fn apply(&mut self, cmd: &str, response: Result<Value, String>) {
        if cmd == "navigate" {
            self.navigation = None;
            match response {
                Ok(value) if value["ok"] == true => {
                    self.navigation = match value["data"]["pending"].as_i64() {
                        Some(1) => Some(true),
                        Some(2) => Some(false),
                        _ => None,
                    }
                }
                Ok(value) => self.note = format!("Navigation: {}", text(&value["error"])),
                Err(error) => {
                    self.note = format!(
                        "Navigation: {error}; результат неизвестен, автоматический повтор отключён"
                    );
                }
            }
            return;
        }
        if cmd == "boot" {
            let result = response.and_then(|v| {
                if v["ok"] == true {
                    Ok(v["data"].clone())
                } else {
                    Err(text(&v["error"]))
                }
            });
            match result {
                Err(error) => {
                    self.boot_stages.clear();
                    self.boot_ready = false;
                    self.boot_error = Some(error);
                }
                Ok(data) => {
                    if data["reset"] == true {
                        self.boot_stages.clear();
                    }
                    for stage in data["stages"].as_array().into_iter().flatten() {
                        if let Some(ordinal) = stage["ordinal"]
                            .as_u64()
                            .and_then(|v| u32::try_from(v).ok())
                            .filter(|&v| (1..=64).contains(&v))
                        {
                            self.boot_stages.insert(
                                ordinal,
                                BootStageRow {
                                    id: text(&stage["id"]),
                                    title: text(&stage["title"]),
                                    completed: stage["completed"] == true,
                                    detail: stage["detail"].as_str().unwrap_or("").into(),
                                },
                            );
                        }
                    }
                    self.boot_ready = self
                        .boot_stages
                        .values()
                        .any(|s| s.id == "ready" && s.completed);
                    self.boot_error = None;
                }
            }
            return;
        }
        if cmd == "metrics" {
            self.dashboard.apply_metrics(response);
            return;
        }
        if cmd == "dashboard" {
            self.dashboard.apply(response);
            return;
        }
        let result = match response {
            Ok(result) => result,
            Err(error) => {
                if cmd == "status" {
                    self.host_logs.fresh = false;
                    self.boot_stages.clear();
                    self.boot_ready = false;
                }
                self.connected = false;
                self.note = format!("⚠ {error}");
                return;
            }
        };
        if result["ok"] != true {
            let effect = if result["application"]["outcome"] == "AppliedWithError" {
                "Состояние частично изменено; "
            } else {
                ""
            };
            self.note = format!("⚠ {effect}{}", text(&result["error"]));
            return;
        }
        let data = &result["data"];
        match cmd {
            "layer" => {
                self.snapshot["layers"] = data.clone();
                self.note = data["lastError"].as_str().unwrap_or("").to_owned();
            }
            "status" => {
                if !data.is_object() {
                    self.connected = false;
                    self.host_logs.fresh = false;
                    self.boot_stages.clear();
                    self.boot_ready = false;
                    self.note = "⚠ invalid status data".into();
                    return;
                }
                self.snapshot = data.clone();
                self.connected = true;
            }
            "prefs" | "theme" => {
                self.light = data["themeLight"].as_bool().unwrap_or(false);
            }
            "profiles" => {
                self.profiles = data["profiles"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|v| {
                        Some(ProfileChoice {
                            name: v["name"].as_str()?.into(),
                            device_type: v["deviceType"].as_str()?.into(),
                        })
                    })
                    .collect()
            }
            "ports" => self.ports = names(&data["ports"]),
            "adapters" => self.adapters = names(&data["adapters"]),
            "vars" => {
                let com = self.selected(false).map(|r| (r.sv.clone(), r.name.clone()));
                let lan = self.selected(true).map(|r| (r.sv.clone(), r.name.clone()));
                update_rows(&mut self.com_variables, &data["com"]["variables"]);
                update_rows(&mut self.lan_variables, &data["lan"]["variables"]);
                self.selected_com = com.and_then(|(sv, name)| {
                    self.com_variables
                        .iter()
                        .position(|r| r.sv == sv && r.name == name)
                });
                self.selected_lan = lan.and_then(|(sv, name)| {
                    self.lan_variables
                        .iter()
                        .position(|r| r.sv == sv && r.name == name)
                });
            }
            _ => self.note = format!("✓ {cmd}"),
        }
        if result["application"]["outcome"] == "AppliedNotPersisted" {
            self.note = format!(
                "⚠ {cmd}: применено в памяти, не сохранено: {}",
                text(&result["application"]["message"])
            );
        }
    }
    pub fn selected(&self, lan: bool) -> Option<&VariableRow> {
        if lan {
            self.selected_lan.and_then(|i| self.lan_variables.get(i))
        } else {
            self.selected_com.and_then(|i| self.com_variables.get(i))
        }
    }
    pub fn select_variable(&mut self, lan: bool, sv: &str, name: &str) -> Option<String> {
        let rows = if lan {
            &self.lan_variables
        } else {
            &self.com_variables
        };
        let index = rows.iter().position(|row| row.sv == sv && row.name == name);
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
        let variable = Variable {
            scope: Scope::channel(if lan { Channel::Lan } else { Channel::Com }),
            name: row.name.clone(),
            sv: if lan {
                Some(
                    u32::from_str_radix(
                        row.sv.trim_start_matches("0x").trim_start_matches("0X"),
                        16,
                    )
                    .map_err(|_| "неверный SV")?,
                )
            } else {
                None
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
        if self.snapshot["lan"]["npcapAvailable"] == false {
            "LAN: ⚠ Npcap не установлен".into()
        } else if self.flag("lan", "streaming") {
            format!("LAN: стрим → {}", self.field("lan", "adapter"))
        } else if matches!(self.field("lan", "adapter").as_str(), "" | "—") {
            "LAN: ⚠ адаптер не выбран".into()
        } else {
            "LAN: выкл".into()
        }
    }
}
fn names(value: &Value) -> Vec<String> {
    value
        .as_array()
        .into_iter()
        .flatten()
        .map(|v| text(v.get("name").unwrap_or(v)))
        .collect()
}
fn update_rows(rows: &mut Vec<VariableRow>, value: &Value) {
    let Some(values) = value.as_array() else {
        return;
    };
    rows.resize_with(values.len(), VariableRow::default);
    for (row, v) in rows.iter_mut().zip(values) {
        row.sv = v["sv"].as_str().unwrap_or("").to_owned();
        row.name = text(&v["name"]);
        row.value = text(&v["value"]);
        row.uom = text(&v["uom"]);
        row.overridden = v["overridden"] == true;
    }
}
pub enum Reply {
    Logs(
        logs::Request,
        Result<icem_logging::Reply, icem_logging::ControlError>,
    ),
    Emulator {
        command: String,
        result: Result<Value, String>,
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
                result: Ok(port.emulator(command).ui_value()),
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
                Reply::Emulator { command, result } => model.apply(&command, result),
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
