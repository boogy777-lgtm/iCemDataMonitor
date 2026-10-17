//! Monitor status uses GPUI controls; the frozen dashboard runs in a WebView child.
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
    web: Option<wry::WebView>,
    url: String,
    web_error: Option<String>,
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
        if let Err(error) = self.bridge.send(command) {
            self.model.note = format!("⚠ {error}");
        }
    }
    fn toggle(
        &self,
        id: &'static str,
        label: &str,
        on: bool,
        command: Value,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        div()
            .id(id)
            .flex()
            .gap(px(10.))
            .p(px(8.))
            .cursor_pointer()
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
            .on_click(cx.listener(move |v, _, _, cx| {
                v.send(command.clone());
                cx.notify();
            }))
    }
}
impl Render for Monitor {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.panel && self.web.is_none() && self.web_error.is_none() {
            match wry::WebViewBuilder::new()
                .with_url(&self.url)
                .build_as_child(window)
            {
                Ok(web) => self.web = Some(web),
                Err(error) => self.web_error = Some(error.to_string()),
            }
        }
        if let Some(web) = &self.web {
            let size = window.viewport_size();
            if let Err(error) = web.set_bounds(wry::Rect {
                position: wry::dpi::LogicalPosition::new(0., 80.).into(),
                size: wry::dpi::LogicalSize::new(
                    f64::from(size.width),
                    f64::from(size.height).max(80.) - 80.,
                )
                .into(),
            }) {
                self.web_error = Some(error.to_string());
            }
            if let Err(error) = web.set_visible(self.panel) {
                self.web_error = Some(error.to_string());
            }
        }
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
                cx,
            ))
            .child(self.toggle(
                "SnifferSwitch",
                "LAN sniffer",
                layers["sniffer"] == true,
                json!({"cmd":"layer","layer":3}),
                cx,
            ))
            .child(format!("OPC DA: {}", crate::text(&layers["opcDa"])))
            .child(self.toggle(
                "ConsoleSwitch",
                "Консоль",
                status["consoleVisible"] == true,
                json!({"cmd":"console","value":status["consoleVisible"]!=true}),
                cx,
            ))
            .child(self.toggle(
                "AutostartSwitch",
                "Автозагрузка",
                status["autostartEnabled"] == true,
                json!({"cmd":"autostart","value":status["autostartEnabled"]!=true}),
                cx,
            ));
        let mut root = div()
            .size_full()
            .bg(self.color("MacWindowColor"))
            .text_color(self.color("MacLabelColor"))
            .font_family("Segoe UI")
            .text_size(px(13.))
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
                            .gap(px(20.))
                            .child(
                                div()
                                    .id("StatusPageTab")
                                    .cursor_pointer()
                                    .child("Состояние")
                                    .on_click(cx.listener(|v, _, _, cx| {
                                        v.panel = false;
                                        cx.notify();
                                    })),
                            )
                            .child(
                                div()
                                    .id("WebPageTab")
                                    .cursor_pointer()
                                    .child("Панель")
                                    .on_click(cx.listener(|v, _, _, cx| {
                                        v.panel = true;
                                        cx.notify();
                                    })),
                            ),
                    )
                    .child(
                        div()
                            .id("ThemeSwitch")
                            .cursor_pointer()
                            .child(if self.model.light {
                                "Тёмная тема"
                            } else {
                                "Светлая тема"
                            })
                            .on_click(cx.listener(|v, _, _, cx| {
                                v.send(json!({"cmd":"theme","value":!v.model.light}));
                                cx.notify();
                            })),
                    ),
            );
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
        if let Some(error) = &self.web_error {
            root = root.child(format!("WebView: {error}"));
        }
        root
    }
}
pub fn run_monitor(port: Arc<dyn UiPort>, url: String) -> std::io::Result<()> {
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
    let bridge = Bridge::start(port)?;
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
                cx.new(|_| Monitor {
                    model: Model::default(),
                    bridge,
                    panel: false,
                    web: None,
                    url,
                    web_error: None,
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
                            window.activate_window();
                        }
                        v.bridge.drain(&mut v.model);
                        if Instant::now() >= due {
                            v.send(json!({"cmd":"status"}));
                            v.send(json!({"cmd":"prefs"}));
                            due = Instant::now() + Duration::from_secs(1);
                        }
                        if prefs {
                            v.send(json!({"cmd":"prefs"}));
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
