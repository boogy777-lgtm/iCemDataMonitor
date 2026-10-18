//! Log state is acknowledged by its owner, never toggled optimistically by GPUI.
use serde_json::{Value, json};
#[derive(Default)]
pub struct LogView {
    pub data: Value,
    pub fresh: bool,
    pub pending: bool,
    pub note: String,
}
impl LogView {
    pub fn enabled(&self) -> Option<bool> {
        self.data["enabled"].as_bool()
    }
    pub fn actionable(&self) -> bool {
        self.fresh && !self.pending && self.data["available"] == true
    }
    pub fn command(&self, local: bool, action: &str) -> Value {
        json!({"cmd":if local {"local_logs"} else {"logs"},"action":action,"enabled":self.enabled()!=Some(true)})
    }
    pub fn apply(&mut self, result: Result<Value, String>) {
        self.pending = false;
        match result {
            Ok(value) if value["data"].is_object() => {
                self.fresh = true;
                self.data = value["data"].clone();
                self.note = if value["ok"] == true {
                    String::new()
                } else {
                    "Операция не завершена; возможен частичный результат".into()
                };
            }
            other => {
                self.fresh = false;
                self.note = match other {
                    Err(e) => e,
                    _ => "Неверный ответ владельца логов".into(),
                };
            }
        }
    }
    pub fn description(&self) -> String {
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
            } else if self.data["recording"] == true {
                "writer готов"
            } else {
                "writer остановлен"
            },
            self.data["directory"].as_str().unwrap_or(""),
            self.data["error"].as_str().unwrap_or(""),
            self.data["operation"].as_str().unwrap_or(""),
            self.note
        )
    }
}
pub struct LoggedPort {
    pub inner: std::sync::Arc<dyn crate::UiPort>,
    pub logs: std::sync::Arc<icem_logging::SharedLogs>,
}
impl crate::UiPort for LoggedPort {
    fn request(&self, command: Value) -> Result<Value, String> {
        if command["cmd"] == "local_logs" {
            return self.logs.request(
                command["action"].as_str().unwrap_or("get"),
                command["enabled"].as_bool(),
            );
        }
        // Logger errors are never sent back into the logger.
        let result = self.inner.request(command);
        if let Err(error) = &result {
            self.logs.record(error);
        }
        result
    }
}

#[cfg(windows)]
pub fn controls<T: 'static>(
    view: &LogView,
    id: &'static str,
    local: bool,
    title: &str,
    light: bool,
    send: fn(&mut T, Value),
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
    for (index, (label, action)) in [("Записывать логи", "set"), ("Очистить логи", "clear")]
        .into_iter()
        .enumerate()
    {
        let command = view.command(local, action);
        let key_command = command.clone();
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
                    send(v, command.clone());
                    cx.notify();
                }
            }))
            .on_key_down(cx.listener(move |v, event: &gpui::KeyDownEvent, _, cx| {
                if enabled && matches!(event.keystroke.key.as_str(), "enter" | "space") {
                    send(v, key_command.clone());
                    cx.notify();
                    cx.stop_propagation();
                }
            }));
        if action == "set" {
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
