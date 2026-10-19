//! Log state is acknowledged by its owner, never toggled optimistically by GPUI.
use icem_logging::{Command, ControlError, Outcome, Reply, Status};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Request {
    pub local: bool,
    pub command: Command,
}
#[derive(Default)]
pub struct LogView {
    pub data: Option<Status>,
    pub fresh: bool,
    pub pending: bool,
    pub note: String,
}
impl LogView {
    pub fn enabled(&self) -> Option<bool> {
        self.data.as_ref().and_then(|s| s.enabled)
    }
    pub fn actionable(&self) -> bool {
        self.fresh && !self.pending && self.data.as_ref().is_some_and(|s| s.available)
    }
    pub fn command(&self, local: bool, command: Command) -> Request {
        Request { local, command }
    }
    pub fn apply(&mut self, result: Result<Reply, ControlError>) {
        self.pending = false;
        match result {
            Ok(reply) if reply.status.consistent() => {
                self.fresh = true;
                self.data = Some(reply.status);
                self.note = if reply.outcome == Outcome::Incomplete {
                    "Операция не завершена; возможен частичный результат".into()
                } else {
                    String::new()
                };
            }
            other => {
                self.fresh = false;
                self.note = match other {
                    Err(e) => match e.kind {
                        icem_logging::ErrorKind::Rejected => {
                            format!("Операция отклонена: {}", e.message)
                        }
                        icem_logging::ErrorKind::Unknown => format!(
                            "{}; результат операции неизвестен, автоматического повтора нет",
                            e.message
                        ),
                    },
                    _ => "Неверный ответ владельца логов".into(),
                };
            }
        }
    }
    pub fn description(&self) -> String {
        let data = self.data.as_ref();
        format!(
            "{} · {}\n{}\n{} {} {}",
            match self.enabled() {
                Some(true) => "Запись включена",
                Some(false) => "Запись выключена",
                None => "Настройка неизвестна",
            },
            if !self.fresh {
                "нет актуальной связи"
            } else if self.pending {
                "выполняется…"
            } else if data.is_some_and(|s| s.recording) {
                "writer готов"
            } else {
                "writer остановлен"
            },
            data.map_or("", |s| s.directory.as_str()),
            data.map_or("", |s| s.error.as_str()),
            data.map_or("", |s| s.operation.as_str()),
            self.note
        )
    }
}

#[cfg(windows)]
pub fn controls<T: 'static>(
    view: &LogView,
    id: &'static str,
    local: bool,
    title: &str,
    light: bool,
    send: fn(&mut T, Request),
    cx: &gpui::Context<T>,
) -> gpui::Div {
    use gpui::{div, prelude::*, px, rgba};
    let palette = if light {
        crate::theme::LIGHT
    } else {
        crate::theme::DARK
    };
    let color = |key: &str| {
        rgba(
            palette
                .iter()
                .find(|(name, _)| *name == key)
                .map(|(_, v)| v.rotate_left(8))
                .unwrap_or(0x808080ff),
        )
    };
    let idle = color("MacButtonBgIdleColor");
    let pressed = color("MacButtonBgPressedColor");
    let focus = color("MacFocusRingColor");
    let track = color(if view.enabled() == Some(true) {
        "MacAccentColor"
    } else {
        "MacSwitchTrackOffColor"
    });
    let enabled = view.actionable();
    let mut root = div().flex().flex_col().gap(px(8.)).child(title.to_owned());
    for (index, (label, action)) in [
        (
            "Записывать логи",
            Command::SetEnabled(view.enabled() != Some(true)),
        ),
        ("Очистить логи", Command::Clear),
    ]
    .into_iter()
    .enumerate()
    {
        let command = view.command(local, action);
        let key_command = command;
        let mut button = div()
            .id((id, index))
            .tab_index(0)
            .tab_stop(enabled)
            .flex()
            .items_center()
            .gap(px(10.))
            .px(px(10.))
            .py(px(5.))
            .rounded(px(6.))
            .border_1()
            .border_color(gpui::transparent_black())
            .bg(idle)
            .when(enabled, |s| {
                s.cursor_pointer()
                    .hover(move |s| s.bg(pressed))
                    .active(move |s| s.bg(pressed))
            })
            .when(!enabled, |s| s.opacity(0.45))
            .focus(move |s| s.border_color(focus))
            .on_click(cx.listener(move |v, _, _, cx| {
                if enabled {
                    send(v, command);
                    cx.notify();
                }
            }))
            .on_key_down(cx.listener(move |v, event: &gpui::KeyDownEvent, _, cx| {
                if enabled && matches!(event.keystroke.key.as_str(), "enter" | "space") {
                    send(v, key_command);
                    cx.notify();
                    cx.stop_propagation();
                }
            }));
        if matches!(action, Command::SetEnabled(_)) {
            button = button.child(
                div()
                    .w(px(32.))
                    .h(px(20.))
                    .rounded(px(10.))
                    .p(px(2.))
                    .flex()
                    .when(view.enabled() == Some(true), |s| s.justify_end())
                    .bg(track)
                    .child(div().size(px(16.)).rounded(px(8.)).bg(rgba(0xffffffff))),
            );
        }
        root = root.child(button.child(label));
    }
    root.child(view.description())
}
