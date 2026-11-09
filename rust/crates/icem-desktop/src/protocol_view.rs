//! Presentation facts for the three Host-owned native protocol controls.
use icem_web::protocol_control::{Command, Protocol, Reply, Status};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Descriptor {
    pub protocol: Protocol,
    pub id: &'static str,
    pub label: &'static str,
    pub config_flag: &'static str,
}

pub const CONTROLS: [Descriptor; 3] = [
    Descriptor {
        protocol: Protocol::OpcUa,
        id: "OpcUaSwitch",
        label: "OPC UA",
        config_flag: "--opcua-config",
    },
    Descriptor {
        protocol: Protocol::Mqtt,
        id: "MqttSwitch",
        label: "MQTT",
        config_flag: "--mqtt-config",
    },
    Descriptor {
        protocol: Protocol::OpcDa,
        id: "OpcDaSwitch",
        label: "OPC DA",
        config_flag: "--opc-da-config",
    },
];

#[derive(Default)]
pub struct ProtocolView {
    pub reply: Option<Reply>,
    pub mutation: Option<Command>,
    pub get_pending: bool,
    stale_get: bool,
    stale_mutation: bool,
    pub fresh: bool,
    pub note: String,
}
impl ProtocolView {
    pub fn invalidate(&mut self) {
        self.fresh = false;
        self.stale_get |= self.get_pending;
        self.stale_mutation |= self.mutation.is_some();
    }
    pub fn can_submit(&self, command: Command) -> bool {
        match command {
            Command::Get => !self.get_pending && self.mutation.is_none(),
            Command::Set { .. } => self.mutation.is_none() && self.fresh,
        }
    }
    pub fn submitted(&mut self, command: Command) {
        match command {
            Command::Get => self.get_pending = true,
            Command::Set { .. } => self.mutation = Some(command),
        }
    }
    pub fn state(&self, protocol: Protocol) -> Option<&Status> {
        self.reply.as_ref().map(|reply| match protocol {
            Protocol::Mqtt => &reply.mqtt,
            Protocol::OpcUa => &reply.opcua,
            Protocol::OpcDa => &reply.opcda,
        })
    }
    pub fn apply(&mut self, result: Result<Reply, String>) {
        let (command, stale) = if self.get_pending {
            self.get_pending = false;
            (Command::Get, std::mem::take(&mut self.stale_get))
        } else if let Some(command) = self.mutation.take() {
            (command, std::mem::take(&mut self.stale_mutation))
        } else {
            return;
        };
        if stale {
            if matches!(command, Command::Set { .. }) {
                self.note =
                    "Связь потеряна; результат операции неизвестен, автоматического повтора нет"
                        .into();
            }
            return;
        }
        match result.and_then(|reply| {
            reply.validate(command)?;
            Ok(reply)
        }) {
            Ok(reply) => {
                self.fresh = true;
                if !reply.ok {
                    self.note = "Операция не подтверждена; см. состояние протокола".into();
                } else {
                    self.note.clear();
                }
                self.reply = Some(reply);
            }
            Err(error) => {
                self.fresh = false;
                self.note = if matches!(command, Command::Get) {
                    format!("Протоколы: {error}")
                } else {
                    format!("Протоколы: {error}; результат неизвестен, автоматического повтора нет")
                };
            }
        }
    }
}

pub struct Display {
    pub on: bool,
    pub actionable: bool,
    pub detail: String,
}

pub fn display(
    descriptor: Descriptor,
    state: Option<&Status>,
    connected: bool,
    pending: bool,
) -> Display {
    let on = state.is_some_and(|s| s.enabled);
    let actionable = connected && !pending && state.is_some_and(|s| s.configured);
    let detail = match state {
        None => "Состояние неизвестно".to_owned(),
        Some(s) if !s.configured => format!("Не настроено: {}", descriptor.config_flag),
        Some(s) if !s.error.is_empty() => format!(
            "{} · {}",
            if s.enabled {
                "Включён"
            } else {
                "Выключен"
            },
            s.error
        ),
        Some(s) if s.running => "Включён · Работает".to_owned(),
        Some(s) if s.enabled => "Включён, не работает".to_owned(),
        Some(_) => "Выключен".to_owned(),
    };
    Display {
        on,
        actionable,
        detail,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn controls_are_distinct_and_unconfigured_explains_required_flag() {
        let state = Status {
            configured: false,
            enabled: false,
            running: false,
            error: String::new(),
        };
        for (i, descriptor) in CONTROLS.into_iter().enumerate() {
            assert!(CONTROLS[..i].iter().all(|other| other.id != descriptor.id));
            let view = display(descriptor, Some(&state), true, false);
            assert!(!view.on && !view.actionable);
            assert!(view.detail.contains(descriptor.config_flag));
        }
    }

    #[test]
    fn enabled_controls_switch_and_running_is_distinct() {
        let descriptor = CONTROLS[0];
        let mut state = Status {
            configured: true,
            enabled: true,
            running: false,
            error: "start failed".into(),
        };
        let failed = display(descriptor, Some(&state), true, false);
        assert!(failed.on && failed.actionable);
        assert_eq!(failed.detail, "Включён · start failed");
        state.error.clear();
        assert_eq!(
            display(descriptor, Some(&state), true, false).detail,
            "Включён, не работает"
        );
        state.running = true;
        assert!(display(descriptor, Some(&state), true, false).on);
        assert!(!display(descriptor, Some(&state), true, true).actionable);
        assert!(!display(descriptor, Some(&state), false, false).actionable);
        assert!(!display(descriptor, None, true, false).actionable);
    }

    #[test]
    fn lost_mutation_is_unknown_and_refresh_never_retries_it() {
        let status = Status {
            configured: true,
            enabled: false,
            running: false,
            error: String::new(),
        };
        let reply = Reply {
            ok: true,
            mqtt: status.clone(),
            opcua: status.clone(),
            opcda: status,
        };
        let mut view = ProtocolView::default();
        view.submitted(Command::Get);
        view.apply(Ok(reply.clone()));
        assert!(view.fresh);
        view.submitted(Command::Set {
            protocol: Protocol::Mqtt,
            enabled: true,
        });
        view.apply(Err("lost response".into()));
        assert!(!view.fresh);
        assert!(view.note.contains("результат неизвестен"));
        assert!(view.mutation.is_none());
        view.submitted(Command::Get);
        view.apply(Ok(reply));
        assert!(view.fresh);
        assert!(view.note.is_empty());
        assert!(!view.state(Protocol::Mqtt).is_some_and(|s| s.running));
    }

    #[test]
    fn repeated_get_does_not_disable_and_fifo_preserves_mutation() {
        let status = Status {
            configured: true,
            enabled: false,
            running: false,
            error: String::new(),
        };
        let reply = Reply {
            ok: true,
            mqtt: status.clone(),
            opcua: status.clone(),
            opcda: status,
        };
        let mut view = ProtocolView::default();
        view.submitted(Command::Get);
        view.apply(Ok(reply.clone()));
        for _ in 0..100 {
            assert!(view.can_submit(Command::Get));
            view.submitted(Command::Get);
            assert!(
                display(
                    CONTROLS[1],
                    view.state(Protocol::Mqtt),
                    view.fresh,
                    view.mutation.is_some()
                )
                .actionable
            );
            view.apply(Ok(reply.clone()));
        }
        let set = Command::Set {
            protocol: Protocol::Mqtt,
            enabled: true,
        };
        view.submitted(Command::Get);
        assert!(view.can_submit(set));
        view.submitted(set);
        view.apply(Ok(reply));
        assert_eq!(view.mutation, Some(set));
        assert!(!view.can_submit(Command::Get));
        view.apply(Err("lost".into()));
        assert!(!view.fresh && view.mutation.is_none());
    }
}
