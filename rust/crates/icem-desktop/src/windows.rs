//! XAML-derived emulator controls. All commands cross the existing owned-value bridge.
use crate::tray::{ExitEmulator, OpenEmulator, emulator_tray};
use crate::{
    Bridge, Model, PRESETS, Page, UNIT_GROUPS, UiPort,
    input::{self, TextInput},
    text, theme,
};
use gpui::{
    App, Application, Bounds, Context, Entity, SharedString, Window, WindowBounds, WindowOptions,
    div, prelude::*, px, rgba, size,
};
use icem_emulation::{
    Channel,
    control::{Command, application::Scope},
    units::Preset,
};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    sync::Arc,
    time::{Duration, Instant},
};

#[derive(Clone)]
enum ChoiceAction {
    Command(Command),
    Variable { lan: bool, sv: String, name: String },
    Group(usize),
}
#[derive(Clone)]
struct Choice {
    label: String,
    action: ChoiceAction,
}
fn endpoints(names: &[String], lan: bool) -> Vec<Choice> {
    names
        .iter()
        .map(|name| Choice {
            label: name.clone(),
            action: ChoiceAction::Command(if lan {
                Command::SetAdapter(name.clone())
            } else {
                Command::SetPort(name.clone())
            }),
        })
        .collect()
}
struct View {
    model: Model,
    bridge: Bridge,
    com_value: Entity<TextInput>,
    lan_value: Entity<TextInput>,
    equipment: Entity<TextInput>,
    unit: Entity<TextInput>,
    popup: Option<&'static str>,
    opened_at: Instant,
    visibility: Option<crate::window_visibility::Request>,
}
impl View {
    fn request_visibility(
        &mut self,
        window: &Window,
        visible: bool,
    ) -> Result<(), icem_windows_native::NativeError> {
        icem_windows_native::request_window_visible(window, visible)?;
        self.visibility = Some(crate::window_visibility::Request::new(
            visible,
            self.opened_at.elapsed(),
        ));
        Ok(())
    }
    fn observe_visibility(&mut self, window: &mut Window) {
        let Some(request) = &self.visibility else {
            return;
        };
        let observation =
            icem_windows_native::window_visibility(window).map(|(actual, minimized)| {
                request.observe(actual, minimized, self.opened_at.elapsed())
            });
        use crate::window_visibility::Observation;
        match observation {
            Ok(Observation::Waiting) => return,
            Ok(Observation::Confirmed(true)) => {
                window.refresh();
                window.activate_window();
            }
            Ok(Observation::Confirmed(false)) => {}
            Ok(Observation::TimedOut) => {
                self.model.note =
                    "Видимость окна не подтверждена за 2 с; автоматического повтора нет".into()
            }
            Err(error) => self.model.note = format!("Видимость окна: {error}"),
        }
        self.visibility = None;
    }
    fn value_input(&self, lan: bool) -> Entity<TextInput> {
        if lan {
            self.lan_value.clone()
        } else {
            self.com_value.clone()
        }
    }
    fn choose(&mut self, action: &ChoiceAction, cx: &mut Context<Self>) {
        match action {
            ChoiceAction::Command(command) => self.send(command.clone()),
            ChoiceAction::Variable { lan, sv, name } => {
                if let Some(value) = self.model.select_variable(*lan, sv, name) {
                    self.value_input(*lan).update(cx, |input, cx| {
                        input.set(value);
                        cx.notify();
                    });
                }
            }
            ChoiceAction::Group(index) => {
                self.model.group = *index;
                let value = self.model.snapshot["units"]
                    [format!("{}Override", UNIT_GROUPS[*index].0)]
                .as_str()
                .unwrap_or("")
                .to_owned();
                self.unit.update(cx, |input, cx| {
                    input.set(value);
                    cx.notify();
                });
            }
        }
        self.popup = None;
        cx.notify();
    }
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
    fn send_logs(&mut self, command: Value) {
        if !self.model.can_submit_log(&command) {
            return;
        }
        let logs = command["cmd"] == "local_logs";
        match self.bridge.send(command) {
            Ok(()) => {
                if logs {
                    self.model.local_logs.pending = true;
                }
            }
            Err(error) => self.model.note = format!("⚠ {error}"),
        }
    }
    fn send(&mut self, command: Command) {
        if let Err(error) = self.bridge.send_emulator(command) {
            self.model.note = format!("⚠ {error}");
        }
    }
    fn poll(&mut self) {
        if !self.model.local_logs.pending {
            self.send_logs(json!({"cmd":"local_logs","action":"get"}));
        }
        self.send(Command::Status);
        self.send(Command::Variables(Scope::default()));
    }
    fn button(
        &self,
        id: impl Into<gpui::ElementId>,
        label: impl Into<SharedString>,
        command: Command,
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
        command: Command,
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
        values: Vec<Choice>,
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
                let action = value.action;
                options = options.child(
                    div()
                        .id((key, index))
                        .p(px(5.))
                        .cursor_pointer()
                        .hover(|s| s.bg(rgba(0x0088ff30)))
                        .child(value.label)
                        .on_click(cx.listener(move |v, _, _, cx| {
                            v.choose(&action, cx);
                        })),
                );
            }
            root = root.child(options);
        }
        root
    }
    fn channel(&self, lan: bool, cx: &Context<Self>) -> impl IntoElement {
        let channel_id = if lan { Channel::Lan } else { Channel::Com };
        let channel = channel_id.as_str();
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
                if streaming {
                    Command::Stop(channel_id.into())
                } else {
                    Command::Start(channel_id.into())
                },
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
                Command::SetEmulation(channel_id.into(), !emulating),
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
                    endpoints(&self.model.adapters, true),
                    cx,
                ))
                .child(self.button(
                    "RefreshAdaptersButton",
                    "Обновить адаптеры",
                    Command::Adapters,
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
                                    match crate::equipment_command(&value) {
                                        Ok(command) => v.send(command),
                                        Err(e) => v.model.note = format!("⚠ {e}"),
                                    };
                                    cx.notify();
                                })),
                        ),
                )
                .child(self.toggle(
                    "FirmwareSwitch",
                    "firmware-таблицы (доп. SV в суперкадре)",
                    self.model.flag("lan", "firmwareSv"),
                    Command::SetFirmware(!self.model.flag("lan", "firmwareSv")),
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
                    endpoints(&self.model.ports, false),
                    cx,
                ))
                .child(self.button("RefreshPortsButton", "Обновить порты", Command::Ports, cx))
                .child(
                    self.picker(
                        "ProfileCombo",
                        self.model.field("com", "template"),
                        self.model
                            .profiles
                            .iter()
                            .map(|p| Choice {
                                label: format!("{} · {}", p.name, p.device_type),
                                action: ChoiceAction::Command(p.command()),
                            })
                            .collect(),
                        cx,
                    ),
                );
        }
        card = card.child(self.button(
            "RefreshVarsButton",
            "Обновить переменные",
            Command::Variables(Scope::default()),
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
            let action = ChoiceAction::Variable {
                lan,
                sv: row.sv.clone(),
                name: row.name.clone(),
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
                        v.choose(&action, cx);
                    })),
            );
        }
        card = card
            .child(list)
            .child("Переопределения значений")
            .child(
                self.picker(
                    if lan { "LanVarCombo" } else { "ComVarCombo" },
                    self.model
                        .selected(lan)
                        .map(|r| r.name.clone())
                        .unwrap_or_else(|| "Выберите переменную".into()),
                    rows.iter()
                        .map(|row| Choice {
                            label: if lan {
                                format!("{} · {}", row.sv, row.name)
                            } else {
                                row.name.clone()
                            },
                            action: ChoiceAction::Variable {
                                lan,
                                sv: row.sv.clone(),
                                name: row.name.clone(),
                            },
                        })
                        .collect(),
                    cx,
                ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(8.))
                    .child(div().w(px(130.)).child(self.value_input(lan)))
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
                                let value = v.value_input(lan).read(cx).content.to_string();
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
        card = card.child(
            self.picker(
                "PresetCombo",
                text(&self.model.snapshot["units"]["preset"]),
                PRESETS
                    .iter()
                    .zip([
                        Preset::FirmwareDefault,
                        Preset::LanNative,
                        Preset::Metric,
                        Preset::Imperial,
                    ])
                    .map(|(label, preset)| Choice {
                        label: (*label).into(),
                        action: ChoiceAction::Command(Command::SetPreset(preset)),
                    })
                    .collect(),
                cx,
            ),
        );
        let groups = self.picker(
            "UnitGroupCombo",
            UNIT_GROUPS[self.model.group].1.into(),
            UNIT_GROUPS
                .iter()
                .enumerate()
                .map(|(index, (_, name))| Choice {
                    label: (*name).into(),
                    action: ChoiceAction::Group(index),
                })
                .collect(),
            cx,
        );
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
        for input in [
            &self.com_value,
            &self.lan_value,
            &self.equipment,
            &self.unit,
        ] {
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
            .tab_group()
            .on_key_down(cx.listener(|_, event: &gpui::KeyDownEvent, window, cx| {
                if event.keystroke.key == "tab" {
                    if event.keystroke.modifiers.shift {
                        window.focus_prev();
                    } else {
                        window.focus_next();
                    }
                    cx.stop_propagation();
                }
            }))
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
                    .child(crate::logs::controls(
                        &self.model.local_logs,
                        "EmulatorLogs",
                        true,
                        "Логи эмулятора",
                        self.model.light,
                        Self::send_logs,
                        cx,
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
        move |window, cx| {
            let closing = view.clone();
            window.on_window_should_close(cx, move |window, cx| {
                closing.update(cx, |view, cx| {
                    if let Err(error) = view.request_visibility(window, false) {
                        view.model.note = format!("Не удалось скрыть окно: {error}");
                    }
                    cx.notify();
                });
                // X/Alt+F4 only hide this window. Explicit engine quit uses cx.quit().
                false
            });
            view
        },
    )
}
fn reveal(handle: gpui::WindowHandle<View>, cx: &mut App) -> gpui::Result<()> {
    handle.update(cx, |view, window, _| {
        view.request_visibility(window, true)?;
        Ok(())
    })?
}
/// X hides the live window; the independent engine and tray remain active.
pub fn run_emulator(
    port: Arc<dyn UiPort>,
    should_stop: Arc<dyn Fn() -> bool + Send + Sync>,
    icon: PathBuf,
) -> std::io::Result<()> {
    let (bridge, completion) = Bridge::start(port)?;
    let outcome = std::rc::Rc::new(std::cell::RefCell::new(None));
    let failure = outcome.clone();
    Application::new().run(move |cx: &mut App| {
        input::bind_keys(cx);
        let view = cx.new(|cx| View {
            model: Model::default(),
            bridge,
            com_value: input::new_input("", "Значение", cx),
            lan_value: input::new_input("", "Значение", cx),
            equipment: input::new_input("0x00A86F46", "Оборудование", cx),
            unit: input::new_input("", "gpm, bar…", cx),
            popup: None,
            opened_at: Instant::now(),
            visibility: None,
        });
        view.update(cx, |v, _| {
            for command in [Command::Profiles, Command::Ports, Command::Adapters] {
                v.send(command);
            }
            v.poll();
        });
        let window = match open(view.clone(), cx) {
            Ok(window) => window,
            Err(e) => {
                *failure.borrow_mut() = Some(format!("GPUI window: {e}"));
                cx.quit();
                return;
            }
        };
        let tray = match emulator_tray(icon, cx) {
            Ok(tray) => tray,
            Err(error) => {
                *failure.borrow_mut() = Some(format!("GPUI tray: {error}"));
                cx.quit();
                return;
            }
        };
        let showing_view = view.clone();
        cx.on_action(move |_: &OpenEmulator, cx| {
            // Reuse the exact HWND/view, including input, popup and scroll state.
            if let Err(error) = reveal(window, cx) {
                showing_view.update(cx, |view, cx| {
                    view.model.note = format!("Не удалось открыть окно: {error}");
                    cx.notify();
                });
                eprintln!("GPUI show: {error}");
            }
            cx.activate(true);
        });
        let quitting = view.clone();
        cx.on_action(move |_: &ExitEmulator, cx| {
            quitting.update(cx, |view, _| view.send(Command::Quit));
        });
        cx.activate(true);
        cx.spawn(async move |cx| {
            let mut due = Instant::now();
            loop {
                cx.background_executor()
                    .timer(Duration::from_millis(50))
                    .await;
                if should_stop() {
                    let _ = cx.update(|cx| {
                        if let Err(error) = tray.close(cx) {
                            *failure.borrow_mut() = Some(format!("GPUI tray close: {error}"));
                        }
                        cx.quit();
                    });
                    break;
                }
                if cx
                    .update(|cx| {
                        let _ =
                            window.update(cx, |view, window, _| view.observe_visibility(window));
                        view.update(cx, |v, cx| {
                            let first = v.model.snapshot.is_null();
                            v.bridge.drain(&mut v.model);
                            if first
                                && let Some(equipment) =
                                    v.model.snapshot["lan"]["equipment"].as_str()
                            {
                                let equipment = equipment.to_owned();
                                v.equipment.update(cx, |input, cx| {
                                    input.set(equipment);
                                    cx.notify();
                                });
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
    completion.finish()?;
    let failure = outcome.borrow_mut().take();
    failure.map_or(Ok(()), |error| Err(std::io::Error::other(error)))
}
