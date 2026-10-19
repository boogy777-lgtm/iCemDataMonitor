//! Derived operator display. Owned domain snapshots, never emulator JSON.
use crate::{Model, ProfileChoice, VariableRow};
use icem_emulation::{
    Channel,
    control::{
        Execution,
        application::{ErrorCategory, Outcome},
        payload::*,
    },
};
pub struct View {
    pub status: Option<Status>,
    pub com: ChannelView,
    pub lan: ChannelView,
    pub pid: String,
    pub uptime: String,
    pub preset: String,
    pub units: String,
}
pub struct ChannelView {
    pub streaming: bool,
    pub emulating: bool,
    pub endpoint: String,
    pub count: String,
    pub rate: String,
    pub template: String,
}
impl Default for ChannelView {
    fn default() -> Self {
        Self {
            streaming: false,
            emulating: false,
            endpoint: "—".into(),
            count: "—".into(),
            rate: "—".into(),
            template: "—".into(),
        }
    }
}
impl Default for View {
    fn default() -> Self {
        Self {
            status: None,
            com: ChannelView::default(),
            lan: ChannelView::default(),
            pid: "—".into(),
            uptime: "—".into(),
            preset: "—".into(),
            units: "—".into(),
        }
    }
}
fn optional(value: Option<&str>) -> String {
    value.unwrap_or("—").into()
}
/// Match v1's established nonfinite-null display while retaining raw IEEE in Status.
fn number(value: f64) -> String {
    serde_json::Number::from_f64(value).map_or_else(|| "—".into(), |v| v.to_string())
}
impl View {
    pub fn streaming(&self, channel: Channel) -> bool {
        match channel {
            Channel::Com => self.com.streaming,
            Channel::Lan => self.lan.streaming,
        }
    }
    pub fn emulating(&self, channel: Channel) -> bool {
        match channel {
            Channel::Com => self.com.emulating,
            Channel::Lan => self.lan.emulating,
        }
    }
    pub fn firmware(&self) -> bool {
        self.status.as_ref().is_some_and(|s| s.lan.firmware_sv)
    }
    pub fn equipment(&self) -> Option<String> {
        self.status
            .as_ref()
            .map(|s| format!("0x{:08X}", s.lan.equipment))
    }
    pub fn unit_override(&self, index: usize) -> String {
        self.status
            .as_ref()
            .and_then(|s| match index {
                0 => s.units.flow_override.as_deref(),
                1 => s.units.temperature_override.as_deref(),
                2 => s.units.density_override.as_deref(),
                3 => s.units.volume_override.as_deref(),
                4 => s.units.level_override.as_deref(),
                5 => s.units.pressure_override.as_deref(),
                _ => None,
            })
            .unwrap_or("")
            .into()
    }
    fn update(&mut self, s: Status) {
        self.com = ChannelView {
            streaming: s.com.streaming,
            emulating: s.com.emulating,
            endpoint: optional(s.com.port.as_deref()),
            count: s.com.lines_sent.to_string(),
            rate: number(s.com.lines_per_sec),
            template: optional(s.com.template.as_deref()),
        };
        self.lan = ChannelView {
            streaming: s.lan.streaming,
            emulating: s.lan.emulating,
            endpoint: optional(s.lan.adapter.as_deref()),
            count: s.lan.frames_sent.to_string(),
            rate: number(s.lan.frames_per_sec),
            template: String::new(),
        };
        self.pid = s.pid.to_string();
        self.uptime = number(s.uptime_sec);
        self.preset = s.units.preset.clone();
        // Preserve the previous compact, sorted-key units summary without a JSON payload seam.
        let u = &s.units;
        let units = std::collections::BTreeMap::from([
            ("densityOverride", u.density_override.as_deref()),
            ("flowOverride", u.flow_override.as_deref()),
            ("levelOverride", u.level_override.as_deref()),
            ("preset", Some(u.preset.as_str())),
            ("pressureOverride", u.pressure_override.as_deref()),
            ("temperatureOverride", u.temperature_override.as_deref()),
            ("volumeOverride", u.volume_override.as_deref()),
        ]);
        self.units = serde_json::to_string(&units).unwrap_or_else(|_| "—".into());

        self.status = Some(s);
    }
    pub fn lan_header(&self) -> String {
        if self.status.as_ref().is_some_and(|s| !s.lan.npcap_available) {
            "LAN: ⚠ Npcap не установлен".into()
        } else if self.lan.streaming {
            format!("LAN: стрим → {}", self.lan.endpoint)
        } else if matches!(self.lan.endpoint.as_str(), "" | "—") {
            "LAN: ⚠ адаптер не выбран".into()
        } else {
            "LAN: выкл".into()
        }
    }
}
impl Model {
    pub fn apply_emulator(&mut self, expected: &str, execution: Execution) {
        if !execution.matches_request(expected) {
            if expected == "status" {
                self.connected = false;
            }
            self.note="⚠ Ответ emulator не соответствует запросу; результат неизвестен, автоматический повтор отключён".into();
            return;
        }
        if !execution.result.ok() {
            if execution.error.as_ref().is_some_and(|e| {
                matches!(
                    e.category,
                    ErrorCategory::Unavailable
                        | ErrorCategory::Disconnected
                        | ErrorCategory::Deadline
                )
            }) {
                self.connected = false;
            }
            let effect = match execution.outcome {
                Outcome::AppliedWithError => "Состояние частично изменено; ",
                Outcome::Unknown => "Результат неизвестен, автоматический повтор отключён; ",
                _ => "",
            };
            self.note = format!("⚠ {effect}{}", execution.result.error().unwrap_or("error"));
            return;
        }
        if let Some(payload) = execution.result.data() {
            match payload {
                Payload::Status(s) => {
                    self.emulator.update(s.as_ref().clone());
                    self.connected = true;
                }
                Payload::Profiles(profiles) => {
                    self.profiles = profiles
                        .iter()
                        .map(|p| ProfileChoice {
                            name: p.profile.name.clone(),
                            device_type: p.profile.device_type.clone(),
                        })
                        .collect()
                }
                Payload::Ports(ports) => {
                    self.ports = ports.iter().map(|p| p.name.clone()).collect()
                }
                Payload::Adapters(adapters) => {
                    self.adapters = adapters.adapters.iter().map(|p| p.name.clone()).collect()
                }
                Payload::Variables(variables) => {
                    let com = self.selected(false).map(|r| r.identity.clone());
                    let lan = self.selected(true).map(|r| r.identity.clone());
                    if let Some(com) = &variables.com {
                        self.com_variables = com
                            .variables
                            .iter()
                            .map(|v| VariableRow {
                                identity: Signal::Com {
                                    name: v.name.clone(),
                                },
                                value: number(v.value),
                                uom: v.uom.clone(),
                                overridden: v.overridden,
                            })
                            .collect();
                    }
                    if let Some(lan) = &variables.lan {
                        self.lan_variables = lan
                            .iter()
                            .map(|v| VariableRow {
                                identity: Signal::Lan {
                                    sv: v.sv,
                                    name: v.name.clone(),
                                },
                                value: v.value.map_or_else(|| "—".into(), number),
                                uom: v.uom.clone(),
                                overridden: v.overridden,
                            })
                            .collect();
                    }
                    self.selected_com = com.and_then(|identity| {
                        self.com_variables
                            .iter()
                            .position(|r| r.identity == identity)
                    });
                    self.selected_lan = lan.and_then(|identity| {
                        self.lan_variables
                            .iter()
                            .position(|r| r.identity == identity)
                    });
                }
                Payload::Start(_)
                | Payload::Stop(_)
                | Payload::Set(_)
                | Payload::Get(_)
                | Payload::Clear(_)
                | Payload::Profile(_)
                | Payload::Watch
                | Payload::Quit
                | Payload::Help
                | Payload::SetPort(_)
                | Payload::SetAdapter(_)
                | Payload::SetEquipment(_)
                | Payload::SetFirmware(_)
                | Payload::SetEmulation(_)
                | Payload::SetPreset(_)
                | Payload::SetUnit(_) => self.note = format!("✓ {expected}"),
            }
        }
        if execution.outcome == Outcome::AppliedNotPersisted {
            self.note = format!(
                "⚠ {expected}: применено в памяти, не сохранено: {}",
                execution.error.as_ref().map_or("—", |e| e.message.as_str())
            );
        }
    }
}
