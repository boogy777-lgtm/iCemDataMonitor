//! GPUI desktop presentation. Domain state stays behind the original control seam.
#![forbid(unsafe_code)]
use serde_json::{Value, json};
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
    mpsc,
};

/// Implementations must bound I/O time and return owned values.
pub trait UiPort: Send + Sync + 'static {
    fn request(&self, command: Value) -> Result<Value, String>;
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
#[derive(Default)]
pub struct Model {
    pub page: Page,
    pub light: bool,
    pub connected: bool,
    pub note: String,
    pub snapshot: Value,
    pub com_variables: Vec<VariableRow>,
    pub lan_variables: Vec<VariableRow>,
    pub profiles: Vec<String>,
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
    pub fn flag(&self, channel: &str, field: &str) -> bool {
        self.snapshot[channel][field].as_bool() == Some(true)
    }
    pub fn field(&self, channel: &str, field: &str) -> String {
        text(&self.snapshot[channel][field])
    }
    pub fn apply(&mut self, cmd: &str, response: Result<Value, String>) {
        let result = match response {
            Ok(result) => result,
            Err(error) => {
                self.connected = false;
                self.note = format!("⚠ {error}");
                return;
            }
        };
        if result["ok"] != true {
            self.note = format!("⚠ {}", text(&result["error"]));
            return;
        }
        let data = &result["data"];
        match cmd {
            "status" => {
                self.snapshot = data.clone();
                self.connected = true;
            }
            "prefs" | "theme" => {
                self.light = data["themeLight"].as_bool().unwrap_or(false);
            }
            "profiles" => self.profiles = names(&data["profiles"]),
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
    }
    pub fn selected(&self, lan: bool) -> Option<&VariableRow> {
        if lan {
            self.selected_lan.and_then(|i| self.lan_variables.get(i))
        } else {
            self.selected_com.and_then(|i| self.com_variables.get(i))
        }
    }
    pub fn edit_command(&self, lan: bool, value: Option<&str>) -> Result<Value, String> {
        let row = self
            .selected(lan)
            .ok_or_else(|| "выберите переменную в списке слева".to_owned())?;
        let mut command = json!({"cmd":if value.is_some(){"set"}else{"clear"},"channel":if lan{"lan"}else{"com"},"name":row.name});
        if lan {
            command["sv"] = row.sv.clone().into();
        }
        if let Some(value) = value {
            let number = value
                .trim()
                .replace(',', ".")
                .parse::<f64>()
                .map_err(|_| "ожидается числовое значение".to_owned())?;
            command["value"] = serde_json::Number::from_f64(number)
                .map(Value::Number)
                .ok_or_else(|| "значение должно быть конечным".to_owned())?;
        }
        Ok(command)
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
pub struct Reply {
    pub command: String,
    pub result: Result<Value, String>,
}
/// One worker; queues bounded, UI never blocks on backend I/O.
pub struct Bridge {
    sender: mpsc::SyncSender<Value>,
    receiver: mpsc::Receiver<Reply>,
    stopping: Arc<AtomicBool>,
    worker: Option<std::thread::JoinHandle<()>>,
}
impl Bridge {
    pub fn start(port: Arc<dyn UiPort>) -> std::io::Result<Self> {
        let (sender, requests) = mpsc::sync_channel::<Value>(16);
        let (responses, receiver) = mpsc::sync_channel(16);
        let stopping = Arc::new(AtomicBool::new(false));
        let stop = stopping.clone();
        let worker = std::thread::Builder::new()
            .name("desktop-control".into())
            .spawn(move || {
                while !stop.load(Ordering::Acquire) {
                    let request = match requests.recv_timeout(std::time::Duration::from_millis(50))
                    {
                        Ok(request) => request,
                        Err(mpsc::RecvTimeoutError::Timeout) => continue,
                        Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    };
                    if stop.load(Ordering::Acquire) {
                        break;
                    }
                    let command = request["cmd"].as_str().unwrap_or("").to_owned();
                    let result = port.request(request);
                    if responses.send(Reply { command, result }).is_err() {
                        break;
                    }
                }
            })?;
        Ok(Self {
            sender,
            receiver,
            stopping,
            worker: Some(worker),
        })
    }
    pub fn send(&self, command: Value) -> Result<(), String> {
        self.sender
            .try_send(command)
            .map_err(|e| format!("очередь команд: {e}"))
    }
    pub fn drain(&self, model: &mut Model) {
        while let Ok(reply) = self.receiver.try_recv() {
            model.apply(&reply.command, reply.result);
        }
    }
}
impl Drop for Bridge {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::Release);
        let (_, empty) = mpsc::channel();
        drop(std::mem::replace(&mut self.receiver, empty));
        if let Some(worker) = self.worker.take()
            && worker.join().is_err()
        {
            eprintln!("desktop worker panicked");
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

pub fn equipment_command(value: &str) -> Result<Value, String> {
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
    Ok(json!({"cmd":"setequipment","value":number}))
}
pub fn unit_command(group: usize, unit: Option<&str>) -> Value {
    let mut command =
        json!({"cmd":"setunit","channel":UNIT_GROUPS.get(group).map(|g|g.0).unwrap_or("")});
    if let Some(unit) = unit.filter(|u| !u.trim().is_empty()) {
        command["name"] = unit.trim().into();
    }
    command
}
#[cfg(windows)]
mod monitor;
#[cfg(windows)]
pub use monitor::run_monitor;
