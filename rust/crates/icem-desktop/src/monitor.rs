//! Native GPUI monitor. Host owns telemetry, history and station preferences.
#[path = "dashboard_view.rs"]
mod dashboard_view;
use crate::{Bridge, Model, UiPort, theme};
use gpui::{
    App, Application, Bounds, Context, Window, WindowBounds, WindowOptions, div, prelude::*, px,
    rgba, size,
};
use serde_json::{Value, json};
use std::{
    sync::Arc,
    time::{Duration, Instant},
};
struct Monitor {
    model: Model,
    bridge: Bridge,
    panel: bool,
    focus: gpui::FocusHandle,
    selection_path: Option<std::path::PathBuf>,
    zoom: f32,
    status_pending: bool,
    prefs_pending: bool,
    boot_pending: bool,
    navigation: crate::navigation::NavigationPoll,
    metrics_page: bool,
}
impl Monitor {
    fn color(&self, key: &str) -> gpui::Rgba {
        let palette = if self.model.light {
            theme::LIGHT
        } else {
            theme::DARK
        };
        rgba(
            palette
                .iter()
                .find(|(name, _)| *name == key)
                .map(|(_, v)| v.rotate_left(8))
                .unwrap_or(0x808080ff),
        )
    }
    fn send(&mut self, command: Value) {
        let cmd = command["cmd"].as_str().unwrap_or("").to_owned();
        match self.bridge.send(command) {
            Ok(()) => match cmd.as_str() {
                "logs" => self.model.host_logs.pending = true,
                "local_logs" => self.model.local_logs.pending = true,
                _ => {}
            },
            Err(error) => self.model.note = format!("⚠ {error}"),
        }
    }
    fn toggle(
        &self,
        id: &'static str,
        label: &str,
        on: bool,
        command: Value,
        enabled: bool,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        self.action_button(
            id,
            "",
            MonitorAction::Command(command),
            self.model.connected && enabled,
            cx,
        )
        .flex()
        .items_center()
        .gap(px(10.))
        .child(
            div()
                .w(px(32.))
                .h(px(20.))
                .rounded(px(10.))
                .p(px(2.))
                .flex()
                .when(on, |s| s.justify_end())
                .bg(self.color(if on {
                    "MacAccentColor"
                } else {
                    "MacSwitchTrackOffColor"
                }))
                .child(div().size(px(16.)).rounded(px(8.)).bg(rgba(0xffffffff))),
        )
        .child(label.to_owned())
    }
}
#[derive(Clone)]
enum MonitorAction {
    Page(bool),
    DashboardStatus,
    Command(Value),
    Select(u16),
    Clear,
    Collapse(String),
    Zoom(f32),
}
impl Monitor {
    fn act(&mut self, action: MonitorAction, cx: &mut Context<Self>) {
        let mut save = false;
        match action {
            MonitorAction::Page(panel) => {
                self.panel = panel;
                if panel {
                    self.poll_dashboard();
                }
            }
            MonitorAction::DashboardStatus => self.metrics_page = !self.metrics_page,
            MonitorAction::Command(command) => self.send(command),
            MonitorAction::Select(addr) => {
                self.model.dashboard.toggle(addr);
                save = true;
            }
            MonitorAction::Clear => {
                self.model.dashboard.clear();
                save = true;
            }
            MonitorAction::Collapse(key) => {
                if !self.model.dashboard.collapsed.remove(&key) {
                    self.model.dashboard.collapsed.insert(key);
                }
            }
            MonitorAction::Zoom(step) => self.zoom = (self.zoom + step).clamp(0.8, 2.),
        }
        if save {
            if let Some(path) = &self.selection_path {
                let result = (|| -> Result<(), Box<dyn std::error::Error>> {
                    if let Some(parent) = path.parent() {
                        std::fs::create_dir_all(parent)?;
                    }
                    std::fs::write(path, self.model.dashboard.selection_bytes()?)?;
                    Ok(())
                })();
                if let Err(error) = result {
                    self.model.note = format!("dashboard preferences: {error}");
                }
            }
            self.poll_dashboard();
        }
        cx.notify();
    }
    fn poll_dashboard(&mut self) {
        if self.model.dashboard.pending {
            return;
        }
        match self.bridge.send(self.model.dashboard.request()) {
            Ok(()) => self.model.dashboard.pending = true,
            Err(error) => self.model.dashboard.error = Some(error),
        }
    }
    fn action_button(
        &self,
        id: impl Into<gpui::ElementId>,
        label: &str,
        action: MonitorAction,
        enabled: bool,
        cx: &Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let hover = self.color("MacButtonBgPressedColor");
        let focus = self.color("MacFocusRingColor");
        let key_action = action.clone();
        div()
            .id(id)
            .tab_index(0)
            .tab_stop(enabled)
            .px(px(10.))
            .py(px(5.))
            .rounded(px(6.))
            .border_1()
            .border_color(gpui::transparent_black())
            .bg(self.color("MacButtonBgIdleColor"))
            .when(enabled, |s| {
                s.cursor_pointer()
                    .hover(move |s| s.bg(hover))
                    .active(move |s| s.bg(hover))
            })
            .when(!enabled, |s| s.opacity(0.45))
            .focus(move |s| s.border_color(focus))
            .child(label.to_owned())
            .on_click(cx.listener(move |v, _, _, cx| {
                if enabled {
                    v.act(action.clone(), cx);
                }
            }))
            .on_key_down(cx.listener(move |v, e: &gpui::KeyDownEvent, _, cx| {
                if enabled && matches!(e.keystroke.key.as_str(), "enter" | "space") {
                    v.act(key_action.clone(), cx);
                    cx.stop_propagation();
                }
            }))
    }
}
impl Render for Monitor {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let status = &self.model.snapshot;
        let mut facts = div()
            .flex()
            .flex_col()
            .gap(px(12.))
            .p(px(20.))
            .rounded(px(10.))
            .bg(self.color("MacSurfaceColor"))
            .child(format!(
                "Шина: {} · AddIn: {} · heartbeat: {}",
                if status["busOpen"] == true {
                    "открыта"
                } else {
                    "нет данных"
                },
                if status["addinAlive"] == true {
                    "активен"
                } else {
                    "не активен"
                },
                crate::text(&status["heartbeat"])
            ));
        for (index, label) in ["COM", "LAN", "RAW"].iter().enumerate() {
            let zone = &status["zones"][index];
            facts = facts.child(format!(
                "{label}: {} · возраст {} ms",
                if zone["active"] == true {
                    "активна"
                } else {
                    "нет свежих данных"
                },
                crate::text(&zone["ageMs"])
            ));
        }
        let layers = &status["layers"];
        facts = facts
            .child(self.toggle(
                "ModbusSwitch",
                "Modbus TCP",
                layers["modbus"] == true,
                json!({"cmd":"layer","layer":1}),
                true,
                cx,
            ))
            .child(self.toggle(
                "SnifferSwitch",
                "LAN sniffer",
                layers["sniffer"] == true,
                json!({"cmd":"layer","layer":3}),
                self.model.can_toggle_sniffer(),
                cx,
            ))
            .child(format!("OPC DA: {}", crate::text(&layers["opcDa"])))
            .child(self.toggle(
                "ConsoleSwitch",
                "Консоль",
                status["consoleVisible"] == true,
                json!({"cmd":"console","value":status["consoleVisible"]!=true}),
                true,
                cx,
            ))
            .child(self.toggle(
                "AutostartSwitch",
                "Автозагрузка",
                status["autostartEnabled"] == true,
                json!({"cmd":"autostart","value":status["autostartEnabled"]!=true}),
                true,
                cx,
            ))
            .child(layers["lastError"].as_str().unwrap_or("").to_owned())
            .child(crate::logs::controls(
                &self.model.host_logs,
                "HostLogs",
                false,
                "Логи Host",
                self.model.light,
                Self::send,
                cx,
            ))
            .child(crate::logs::controls(
                &self.model.local_logs,
                "ShellLogs",
                true,
                "Логи оболочки",
                self.model.light,
                Self::send,
                cx,
            ));
        let mut root = div()
            .size_full()
            .track_focus(&self.focus)
            .tab_group()
            .on_key_down(cx.listener(|v, event: &gpui::KeyDownEvent, window, cx| {
                if event.keystroke.key == "tab" {
                    if event.keystroke.modifiers.shift {
                        window.focus_prev();
                    } else {
                        window.focus_next();
                    }
                    cx.stop_propagation();
                } else if event.keystroke.modifiers.control {
                    match event.keystroke.key.as_str() {
                        "+" | "=" => v.act(MonitorAction::Zoom(0.1), cx),
                        "-" => v.act(MonitorAction::Zoom(-0.1), cx),
                        _ => return,
                    }
                    cx.stop_propagation();
                }
            }))
            .bg(self.color("MacWindowColor"))
            .text_color(self.color("MacLabelColor"))
            .font_family("Segoe UI")
            .text_size(px(13. * self.zoom))
            .flex()
            .flex_col()
            .child(
                div()
                    .h(px(80.))
                    .p(px(16.))
                    .flex()
                    .justify_between()
                    .items_center()
                    .child(
                        div()
                            .flex()
                            .gap(px(12.))
                            .child(self.action_button(
                                "StatusPageTab",
                                "Состояние",
                                MonitorAction::Page(false),
                                true,
                                cx,
                            ))
                            .child(self.action_button(
                                "WebPageTab",
                                "Панель",
                                MonitorAction::Page(true),
                                true,
                                cx,
                            )),
                    )
                    .child(
                        div()
                            .flex()
                            .gap(px(8.))
                            .child(self.action_button(
                                "ZoomOut",
                                "−",
                                MonitorAction::Zoom(-0.1),
                                true,
                                cx,
                            ))
                            .child(format!("{:.0}%", self.zoom * 100.))
                            .child(self.action_button(
                                "ZoomIn",
                                "+",
                                MonitorAction::Zoom(0.1),
                                true,
                                cx,
                            ))
                            .child(self.action_button(
                                "ThemeSwitch",
                                if self.model.light {
                                    "Тёмная тема"
                                } else {
                                    "Светлая тема"
                                },
                                MonitorAction::Command(
                                    json!({"cmd":"theme","value":!self.model.light}),
                                ),
                                self.model.connected,
                                cx,
                            )),
                    ),
            );
        if !self.model.boot_ready {
            let mut boot = div()
                .p(px(12.))
                .bg(self.color("MacSurface2Color"))
                .flex()
                .flex_col()
                .gap(px(4.))
                .child("Запуск Host");
            for stage in self.model.boot_stages.values() {
                boot = boot.child(format!(
                    "{} {} {}",
                    if stage.completed { "☑" } else { "☐" },
                    stage.title,
                    stage.detail
                ));
            }
            if let Some(error) = &self.model.boot_error {
                boot = boot.child(format!("Boot: {error}"));
            }
            root = root.child(boot);
        }
        if !self.panel {
            root = root.child(
                div()
                    .id("StatusPage")
                    .flex_1()
                    .overflow_y_scroll()
                    .p(px(20.))
                    .flex()
                    .flex_col()
                    .gap(px(16.))
                    .child(if self.model.connected {
                        "● Host подключён"
                    } else {
                        "○ Нет связи с Host"
                    })
                    .child(facts)
                    .child(self.model.note.clone()),
            );
        }
        if self.panel {
            root = root.child(self.dashboard_view(cx));
        }
        root
    }
}
pub fn run_monitor(port: Arc<dyn UiPort>) -> std::io::Result<()> {
    let singleton =
        icem_windows_native::NamedMutex::open_or_create(icem_platform_windows::SHELL_MUTEX)
            .map_err(std::io::Error::other)?;
    let Some(_guard) = singleton
        .lock(Duration::ZERO)
        .map_err(std::io::Error::other)?
    else {
        icem_windows_native::signal_existing_event(icem_platform_windows::SHELL_SHOW)
            .map_err(std::io::Error::other)?;
        return Ok(());
    };
    let show = icem_windows_native::OwnedEvent::create_reset(icem_platform_windows::SHELL_SHOW)
        .map_err(std::io::Error::other)?;
    let shutdown =
        icem_windows_native::OwnedEvent::create_reset(icem_platform_windows::SHELL_SHUTDOWN)
            .map_err(std::io::Error::other)?;
    let theme = icem_windows_native::OwnedEvent::create_reset(icem_platform_windows::THEME_CHANGED)
        .map_err(std::io::Error::other)?;
    let bridge = Bridge::start(Arc::new(crate::logs::LoggedPort {
        inner: port,
        logs: Arc::new(icem_logging::SharedLogs::new(icem_logging::Owner::Shell)),
    }))?;
    let selection_path = std::env::var_os("LOCALAPPDATA")
        .map(std::path::PathBuf::from)
        .map(|p| {
            p.join("iCemDataMonitor")
                .join("native-dashboard-selection.json")
        });
    let mut model = Model::default();
    if let Some(path) = &selection_path {
        match std::fs::read(path) {
            Ok(bytes) => {
                if let Err(error) = model.dashboard.restore_selection(&bytes) {
                    model.note = error;
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => model.note = format!("dashboard preferences: {error}"),
        }
    }
    Application::new().run(move |cx: &mut App| {
        let bounds = Bounds::centered(None, size(px(1000.), px(760.)), cx);
        let handle = match cx.open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("iCem DataMonitor — Rust development".into()),
                    ..Default::default()
                }),
                ..Default::default()
            },
            move |_, cx| {
                cx.new(|cx| Monitor {
                    model,
                    focus: cx.focus_handle(),
                    bridge,
                    panel: false,
                    selection_path,
                    zoom: 1.,
                    status_pending: false,
                    prefs_pending: false,
                    boot_pending: false,
                    navigation: crate::navigation::NavigationPoll::default(),
                    metrics_page: false,
                })
            },
        ) {
            Ok(handle) => handle,
            Err(error) => {
                eprintln!("monitor window: {error}");
                cx.quit();
                return;
            }
        };
        let _ = handle.update(cx, |v, window, _| window.focus(&v.focus));
        cx.on_window_closed(|cx| cx.quit()).detach();
        cx.activate(true);
        cx.spawn(async move |cx| {
            let mut due = Instant::now();
            loop {
                cx.background_executor()
                    .timer(Duration::from_millis(50))
                    .await;
                if shutdown.poll().unwrap_or(false) {
                    let _ = cx.update(|cx| cx.quit());
                    break;
                }
                let show = show.poll().unwrap_or(false);
                let prefs = theme.poll().unwrap_or(false);
                if handle
                    .update(cx, |v, window, cx| {
                        if show {
                            v.navigation.show();
                            window.activate_window();
                        }
                        while let Ok(reply) = v.bridge.receiver.try_recv() {
                            match reply.command.as_str() {
                                "status" => {
                                    v.status_pending = false;
                                }
                                "navigate" => {
                                    v.navigation.complete();
                                }
                                "prefs" => v.prefs_pending = false,
                                "boot" => v.boot_pending = false,
                                _ => {}
                            }
                            v.model.apply(&reply.command, reply.result);
                            if let Some(panel) = v.model.navigation.take() {
                                v.panel = panel;
                                if panel {
                                    v.poll_dashboard();
                                }
                            }
                        }
                        if Instant::now() >= due {
                            if !v.model.local_logs.pending {
                                v.send(json!({"cmd":"local_logs","action":"get"}));
                            }
                            if v.model.connected && !v.model.host_logs.pending {
                                v.send(json!({"cmd":"logs","action":"get"}));
                            }
                            if let Err(error) = v.navigation.submit(v.model.connected, || {
                                v.bridge.send(json!({"cmd":"navigate"}))
                            }) {
                                v.model.note = format!("Navigation: {error}");
                            }
                            if !v.status_pending && v.bridge.send(json!({"cmd":"status"})).is_ok() {
                                v.status_pending = true;
                            }
                            if !v.prefs_pending && v.bridge.send(json!({"cmd":"prefs"})).is_ok() {
                                v.prefs_pending = true;
                            }
                            if v.panel
                                && v.metrics_page
                                && !v.model.dashboard.metrics_pending
                                && v.bridge.send(json!({"cmd":"metrics"})).is_ok()
                            {
                                v.model.dashboard.metrics_pending = true;
                            }
                            // Keep the stream supervised after READY: a fast Host restart
                            // may happen entirely between successful status probes.
                            if !v.boot_pending && v.bridge.send(json!({"cmd":"boot"})).is_ok() {
                                v.boot_pending = true;
                            }
                            if v.panel {
                                v.poll_dashboard();
                            }
                            due = Instant::now() + Duration::from_secs(1);
                        }
                        if prefs
                            && !v.prefs_pending
                            && v.bridge.send(json!({"cmd":"prefs"})).is_ok()
                        {
                            v.prefs_pending = true;
                        }
                        cx.notify();
                    })
                    .is_err()
                {
                    break;
                }
            }
        })
        .detach();
    });
    Ok(())
}
