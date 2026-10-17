//! XAML-derived emulator controls. All commands cross the existing owned-value bridge.
use crate::{
    Bridge, Model, PRESETS, Page, UNIT_GROUPS, UiPort,
    input::{self, TextInput},
    text, theme,
};
use gpui::{
    App, Application, Bounds, Context, Entity, SharedString, Window, WindowBounds, WindowOptions,
    div, prelude::*, px, rgba, size,
};
use icem_windows_native::{Tray, TrayItem};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    sync::Arc,
    time::{Duration, Instant},
};

struct View {
    model: Model,
    bridge: Bridge,
    value: Entity<TextInput>,
    equipment: Entity<TextInput>,
    unit: Entity<TextInput>,
    popup: Option<&'static str>,
}
impl View {
    fn color(&self, key: &str) -> gpui::Rgba {
        let palette = if self.model.light {
            theme::LIGHT
        } else {
            theme::DARK
        };
        let argb = palette
            .iter()
            .find(|(name, _)| *name == key)
            .map(|(_, v)| *v)
            .unwrap_or(0xff808080);
        rgba(argb.rotate_left(8))
    }
    fn send(&mut self, command: Value) {
        if let Err(error) = self.bridge.send(command) {
            self.model.note = format!("⚠ {error}");
        }
    }
    fn poll(&mut self) {
        self.send(json!({"cmd":"status"}));
        self.send(json!({"cmd":"vars"}));
    }
    fn button(
        &self,
        id: impl Into<gpui::ElementId>,
        label: impl Into<SharedString>,
        command: Value,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let hover = self.color("MacButtonBgPressedColor");
        div()
            .id(id)
            .cursor_pointer()
            .rounded(px(6.))
            .px(px(10.))
            .py(px(4.))
            .bg(self.color("MacButtonBgIdleColor"))
            .hover(move |s| s.bg(hover))
            .active(move |s| s.bg(hover))
            .child(label.into())
            .on_click(cx.listener(move |view, _, _, cx| {
                view.send(command.clone());
                cx.notify();
            }))
    }
    fn toggle(
        &self,
        id: &'static str,
        label: &str,
        on: bool,
        command: Value,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let track = if on {
            self.color("MacAccentColor")
        } else {
            self.color("MacSwitchTrackOffColor")
        };
        div()
            .id(id)
            .flex()
            .items_center()
            .gap(px(10.))
            .cursor_pointer()
            .child(
                div()
                    .w(px(32.))
                    .h(px(20.))
                    .p(px(2.))
                    .rounded(px(10.))
                    .bg(track)
                    .flex()
                    .when(on, |s| s.justify_end())
                    .child(div().size(px(16.)).rounded(px(8.)).bg(rgba(0xffffffff))),
            )
            .child(label.to_owned())
            .on_click(cx.listener(move |view, _, _, cx| {
                view.send(command.clone());
                cx.notify();
            }))
    }
    fn card(&self) -> gpui::Div {
        div()
            .flex()
            .flex_col()
            .gap(px(12.))
            .p(px(16.))
            .rounded(px(10.))
            .bg(self.color("MacSurfaceColor"))
    }
    fn picker(
        &self,
        key: &'static str,
        label: String,
        values: Vec<String>,
        command: &'static str,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let mut root = div().flex().flex_col().gap(px(2.)).child(
            div()
                .id(key)
                .rounded(px(6.))
                .p(px(5.))
                .bg(self.color("MacButtonBgIdleColor"))
                .cursor_pointer()
                .child(format!("{label}  ▾"))
                .on_click(cx.listener(move |v, _, _, cx| {
                    v.popup = if v.popup == Some(key) {
                        None
                    } else {
                        Some(key)
                    };
                    cx.notify();
                })),
        );
        if self.popup == Some(key) {
            let mut options = div()
                .id(SharedString::from(format!("{key}-options")))
                .max_h(px(180.))
                .overflow_y_scroll()
                .bg(self.color("MacPopupBgColor"));
            for (index, value) in values.into_iter().enumerate() {
                let arg = value.clone();
                options = options.child(
                    div()
                        .id((key, index))
                        .p(px(5.))
                        .cursor_pointer()
                        .hover(|s| s.bg(rgba(0x0088ff30)))
                        .child(value)
                        .on_click(cx.listener(move |v, _, _, cx| {
                            v.send(json!({"cmd":command,"name":arg}));
                            v.popup = None;
                            cx.notify();
                        })),
                );
            }
            root = root.child(options);
        }
        root
    }
    fn channel(&self, lan: bool, cx: &Context<Self>) -> impl IntoElement {
        let channel = if lan { "lan" } else { "com" };
        let streaming = self.model.flag(channel, "streaming");
        let emulating = self.model.flag(channel, "emulating");
        let mut card = self
            .card()
            .child(self.toggle(
                if lan {
                    "LanStreamSwitch"
                } else {
                    "ComStreamSwitch"
                },
                if streaming {
                    "Поток включён"
                } else {
                    "Поток выключен"
                },
                streaming,
                json!({"cmd":if streaming{"stop"}else{"start"},"channel":channel}),
                cx,
            ))
            .child(self.toggle(
                if lan {
                    "LanEmulateSwitch"
                } else {
                    "ComEmulateSwitch"
                },
                "Эмуляция (автогенерация значений)",
                emulating,
                json!({"cmd":"setemulate","channel":channel,"value":u8::from(!emulating)}),
                cx,
            ));
        if lan {
            card = card
                .child(format!(
                    "{} · {} кадров · {} кадр/с",
                    self.model.field("lan", "adapter"),
                    self.model.field("lan", "framesSent"),
                    self.model.field("lan", "framesPerSec")
                ))
                .child(self.picker(
                    "LanAdapterCombo",
                    self.model.field("lan", "adapter"),
                    self.model.adapters.clone(),
                    "setadapter",
                    cx,
                ))
                .child(self.button(
                    "RefreshAdaptersButton",
                    "Обновить адаптеры",
                    json!({"cmd":"adapters"}),
                    cx,
                ));
            if self.model.adapters.is_empty() {
                card = card.child("⚠ Нет доступных адаптеров. Проверьте Npcap и подключение.");
            }
            card = card
                .child(
                    div()
                        .flex()
                        .gap(px(8.))
                        .items_center()
                        .child("Оборудование")
                        .child(div().w(px(140.)).child(self.equipment.clone()))
                        .child(
                            div()
                                .id("ApplyEquipmentButton")
                                .cursor_pointer()
                                .child("Применить")
                                .on_click(cx.listener(|v, _, _, cx| {
                                    let value = v.equipment.read(cx).content.to_string();
                                    match crate::equipment_command(&value) { Ok(command)=>v.send(command), Err(e)=>v.model.note=format!("⚠ {e}") };
                                    cx.notify();
                                })),
                        ),
                )
                .child(self.toggle(
                    "FirmwareSwitch",
                    "firmware-таблицы (доп. SV в суперкадре)",
                    self.model.flag("lan", "firmwareSv"),
                    json!({"cmd":"setfirmware","value":u8::from(!self.model.flag("lan","firmwareSv"))}),
                    cx,
                ));
        } else {
            card = card
                .child(format!(
                    "{} · 9600 baud · {} строк · {} строк/с",
                    self.model.field("com", "port"),
                    self.model.field("com", "linesSent"),
                    self.model.field("com", "linesPerSec")
                ))
                .child(self.picker(
                    "ComPortCombo",
                    self.model.field("com", "port"),
                    self.model.ports.clone(),
                    "setport",
                    cx,
                ))
                .child(self.button(
                    "RefreshPortsButton",
                    "Обновить порты",
                    json!({"cmd":"ports"}),
                    cx,
                ))
                .child(self.picker(
                    "ProfileCombo",
                    self.model.field("com", "template"),
                    self.model.profiles.clone(),
                    "profile",
                    cx,
                ));
        }
        card = card.child(self.button(
            "RefreshVarsButton",
            "Обновить переменные",
            json!({"cmd":"vars"}),
            cx,
        ));
        let rows = if lan {
            &self.model.lan_variables
        } else {
            &self.model.com_variables
        };
        let selected = if lan {
            self.model.selected_lan
        } else {
            self.model.selected_com
        };
        let mut list = div()
            .id(if lan { "LanVarsList" } else { "ComVarsList" })
            .max_h(px(250.))
            .overflow_y_scroll()
            .rounded(px(6.))
            .bg(self.color("MacListBackgroundColor"));
        for (index, row) in rows.iter().enumerate() {
            let label = if lan {
                format!("{}  {}", row.sv, row.name)
            } else {
                row.name.clone()
            };
            list = list.child(
                div()
                    .id(("variable", index))
                    .flex()
                    .gap(px(14.))
                    .p(px(5.))
                    .cursor_pointer()
                    .when(selected == Some(index), |s| {
                        s.bg(self.color("MacSoftHighlightColor"))
                    })
                    .hover(|s| s.bg(self.color("MacListRowHoverColor")))
                    .child(div().flex_1().child(label))
                    .child(
                        div()
                            .w(px(110.))
                            .text_color(self.color("MacAccentColor"))
                            .child(row.value.clone()),
                    )
                    .child(div().w(px(70.)).child(row.uom.clone()))
                    .on_click(cx.listener(move |v, _, _, cx| {
                        if lan {
                            v.model.selected_lan = Some(index);
                        } else {
                            v.model.selected_com = Some(index);
                        }
                        if let Some(row) = v.model.selected(lan) {
                            let value = row.value.clone();
                            v.value.update(cx, |input, cx| {
                                input.set(value);
                                cx.notify();
                            });
                        }
                        cx.notify();
                    })),
            );
        }
        card = card
            .child(list)
            .child("Переопределения значений")
            .child(
                self.model
                    .selected(lan)
                    .map(|r| r.name.clone())
                    .unwrap_or_else(|| "Выберите переменную в списке".into()),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(8.))
                    .child(div().w(px(130.)).child(self.value.clone()))
                    .child(
                        div()
                            .id("SetButton")
                            .px(px(10.))
                            .py(px(4.))
                            .rounded(px(6.))
                            .bg(self.color("MacAccentColor"))
                            .text_color(rgba(0xffffffff))
                            .cursor_pointer()
                            .child("Установить")
                            .on_click(cx.listener(move |v, _, _, cx| {
                                let value = v.value.read(cx).content.to_string();
                                match v.model.edit_command(lan, Some(&value)) {
                                    Ok(command) => v.send(command),
                                    Err(e) => v.model.note = format!("⚠ {e}"),
                                };
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .id("ClearButton")
                            .cursor_pointer()
                            .child("Сброс")
                            .on_click(cx.listener(move |v, _, _, cx| {
                                match v.model.edit_command(lan, None) {
                                    Ok(command) => v.send(command),
                                    Err(e) => v.model.note = format!("⚠ {e}"),
                                };
                                cx.notify();
                            })),
                    ),
            );
        div()
            .flex()
            .flex_col()
            .gap(px(8.))
            .child(if lan {
                "ПОТОК LAN (cRIO)"
            } else {
                "ПОТОК COM"
            })
            .child(card)
    }
    fn units(&self, cx: &Context<Self>) -> impl IntoElement {
        let mut card = self.card().child(format!(
            "Пресет: {}",
            text(&self.model.snapshot["units"]["preset"])
        ));
        let mut presets = div().flex().gap(px(6.));
        for (index, preset) in PRESETS.iter().enumerate() {
            presets = presets.child(self.button(
                ("preset", index),
                *preset,
                json!({"cmd":"setpreset","value":index}),
                cx,
            ));
        }
        card = card.child(presets);
        let mut groups = div().flex().flex_wrap().gap(px(6.));
        for (index, (_, name)) in UNIT_GROUPS.iter().enumerate() {
            groups = groups.child(
                div()
                    .id(("group", index))
                    .p(px(4.))
                    .rounded(px(6.))
                    .cursor_pointer()
                    .when(index == self.model.group, |s| {
                        s.bg(self.color("MacSoftHighlightColor"))
                    })
                    .child(*name)
                    .on_click(cx.listener(move |v, _, _, cx| {
                        v.model.group = index;
                        cx.notify();
                    })),
            );
        }
        card.child(text(&self.model.snapshot["units"]))
            .child(groups)
            .child(self.unit.clone())
            .child(
                div()
                    .flex()
                    .gap(px(10.))
                    .child(
                        div()
                            .id("UnitApplyButton")
                            .cursor_pointer()
                            .child("Применить")
                            .on_click(cx.listener(|v, _, _, cx| {
                                let unit = v.unit.read(cx).content.to_string();
                                v.send(crate::unit_command(v.model.group, Some(&unit)));
                                cx.notify();
                            })),
                    )
                    .child(self.button(
                        "UnitClearButton",
                        "Сбросить",
                        crate::unit_command(self.model.group, None),
                        cx,
                    )),
            )
    }
}
impl Render for View {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let background = self.color("MacTextFieldBgColor");
        let foreground = self.color("MacLabelColor");
        for input in [&self.value, &self.equipment, &self.unit] {
            input.update(cx, |v, _| {
                v.background = background;
                v.foreground = foreground;
            });
        }
        let mut tabs = div()
            .flex()
            .gap(px(2.))
            .rounded(px(6.))
            .p(px(2.))
            .bg(self.color("MacSegmentedTrackColor"));
        for (index, (label, page)) in [
            ("COM", Page::Com),
            ("LAN", Page::Lan),
            ("Units", Page::Units),
        ]
        .into_iter()
        .enumerate()
        {
            tabs = tabs.child(
                div()
                    .id(("tab", index))
                    .flex_1()
                    .flex()
                    .justify_center()
                    .py(px(3.))
                    .rounded(px(4.))
                    .cursor_pointer()
                    .when(self.model.page == page, |s| {
                        s.bg(self.color("MacSegmentedSelectedColor"))
                    })
                    .child(label)
                    .on_click(cx.listener(move |v, _, _, cx| {
                        v.model.page = page;
                        v.popup = None;
                        cx.notify();
                    })),
            );
        }
        div()
            .id("EmuContentScroll")
            .size_full()
            .overflow_y_scroll()
            .bg(self.color("MacWindowColor"))
            .text_color(self.color("MacLabelColor"))
            .font_family("Segoe UI")
            .text_size(px(13.))
            .child(
                div()
                    .max_w(px(760.))
                    .mx_auto()
                    .p(px(20.))
                    .flex()
                    .flex_col()
                    .gap(px(16.))
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .child(div().text_size(px(17.)).child("Эмулятор iCemEmulator"))
                            .child(
                                div()
                                    .id("ThemeSwitch")
                                    .cursor_pointer()
                                    .child(if self.model.light {
                                        "◐ Тёмная"
                                    } else {
                                        "◑ Светлая"
                                    })
                                    .on_click(cx.listener(|v, _, _, cx| {
                                        v.model.light = !v.model.light;
                                        cx.notify();
                                    })),
                            ),
                    )
                    .child(format!(
                        "{} · PID {} · {} s · {}",
                        if self.model.connected {
                            "● Подключён"
                        } else {
                            "○ Нет связи"
                        },
                        text(&self.model.snapshot["pid"]),
                        text(&self.model.snapshot["uptimeSec"]),
                        self.model.lan_header()
                    ))
                    .child(tabs)
                    .child(
                        div()
                            .text_color(self.color("MacOrangeColor"))
                            .child(self.model.note.clone()),
                    )
                    .child(match self.model.page {
                        Page::Lan => self.channel(true, cx).into_any_element(),
                        Page::Units => self.units(cx).into_any_element(),
                        _ => self.channel(false, cx).into_any_element(),
                    }),
            )
    }
}
fn open(view: Entity<View>, cx: &mut App) -> gpui::Result<gpui::WindowHandle<View>> {
    let bounds = Bounds::centered(None, size(px(900.), px(760.)), cx);
    cx.open_window(
        WindowOptions {
            window_bounds: Some(WindowBounds::Windowed(bounds)),
            titlebar: Some(gpui::TitlebarOptions {
                title: Some("iCemEmulator — Rust development".into()),
                ..Default::default()
            }),
            ..Default::default()
        },
        move |_, _| view,
    )
}
/// The engine remains alive when X closes the window. Tray open recreates the same view.
pub fn run_emulator(
    port: Arc<dyn UiPort>,
    should_stop: Arc<dyn Fn() -> bool + Send + Sync>,
    icon: PathBuf,
) -> std::io::Result<()> {
    let bridge = Bridge::start(port)?;
    let tray = Tray::start(
        icon,
        "iCemEmulator".into(),
        vec![
            TrayItem {
                id: 1,
                label: "Открыть iCemEmulator".into(),
            },
            TrayItem {
                id: 2,
                label: "Выход".into(),
            },
        ],
    )?;
    Application::new().run(move |cx: &mut App| {
        input::bind_keys(cx);
        let view = cx.new(|cx| View {
            model: Model::default(),
            bridge,
            value: input::new_input("", "Значение", cx),
            equipment: input::new_input("0x00A86F46", "Оборудование", cx),
            unit: input::new_input("", "gpm, bar…", cx),
            popup: None,
        });
        view.update(cx, |v, _| {
            for command in ["profiles", "ports", "adapters"] {
                v.send(json!({"cmd":command}));
            }
            v.poll();
        });
        let mut window = match open(view.clone(), cx) {
            Ok(window) => Some(window),
            Err(e) => {
                eprintln!("GPUI window: {e}");
                cx.quit();
                return;
            }
        };
        cx.activate(true);
        cx.spawn(async move |cx| {
            let mut due = Instant::now();
            loop {
                cx.background_executor()
                    .timer(Duration::from_millis(50))
                    .await;
                if should_stop() {
                    let _ = cx.update(|cx| cx.quit());
                    break;
                }
                let mut show = false;
                let mut quit = false;
                while let Some(event) = tray.try_event() {
                    match event {
                        1 => show = true,
                        2 => quit = true,
                        _ => {}
                    }
                }
                if cx
                    .update(|cx| {
                        if show {
                            let active = window.is_some_and(|handle| {
                                handle
                                    .update(cx, |_, window, _| window.activate_window())
                                    .is_ok()
                            });
                            if !active {
                                match open(view.clone(), cx) {
                                    Ok(handle) => window = Some(handle),
                                    Err(e) => eprintln!("GPUI reopen: {e}"),
                                };
                            }
                            cx.activate(true);
                        }
                        view.update(cx, |v, cx| {
                            v.bridge.drain(&mut v.model);
                            if quit {
                                v.send(json!({"cmd":"quit"}));
                            }
                            if Instant::now() >= due {
                                v.poll();
                                due = Instant::now() + Duration::from_secs(1);
                            }
                            cx.notify();
                        });
                    })
                    .is_err()
                {
                    break;
                }
            }
            // Tray removed before returning to the engine shutdown path.
            drop(tray);
        })
        .detach();
    });
    Ok(())
}
