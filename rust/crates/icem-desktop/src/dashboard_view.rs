//! GPUI-only tables and plot. Colors/order/geometry follow the preserved dashboard.
use super::*;
use crate::dashboard::{PALETTE, RegisterRow};
use gpui::{PathBuilder, canvas, point, rgb};
impl Monitor {
    fn table_row(&self, row: &RegisterRow, selected_table: bool, cx: &Context<Self>) -> gpui::Div {
        let selectable = row.addr >= 4;
        let selected = self.model.dashboard.selected.contains(&row.addr);
        div()
            .flex()
            .items_center()
            .gap(px(8.))
            .py(px(3.))
            .border_b_1()
            .border_color(self.color("MacSurface2Color"))
            .when(!selected_table, |s| {
                s.child(div().w(px(56.)).when(selectable, |s| {
                    s.child(self.action_button(
                        ("reg", usize::from(row.addr)),
                        if selected { "☑" } else { "☐" },
                        MonitorAction::Select(row.addr),
                        true,
                        cx,
                    ))
                }))
            })
            .child(
                div()
                    .w(px(90.))
                    .flex_shrink_0()
                    .font_family("Consolas")
                    .child(if row.end == row.addr {
                        row.addr.to_string()
                    } else {
                        format!("{}..{}", row.addr, row.end)
                    }),
            )
            .child(div().flex_1().min_w(px(140.)).child(row.name.clone()))
            .child(
                div()
                    .w(px(180.))
                    .flex_shrink_0()
                    .font_family("Consolas")
                    .child(row.value.clone()),
            )
            .when(!row.label.is_empty(), |s| {
                s.child(div().w(px(180.)).child(row.label.clone()))
            })
    }
    pub(super) fn dashboard_view(&self, cx: &Context<Self>) -> gpui::Stateful<gpui::Div> {
        let dashboard = &self.model.dashboard;
        if self.metrics_page {
            return self.dashboard_status(cx);
        }
        let mut content = div()
            .id("NativeDashboard")
            .flex_1()
            .min_h_0()
            .overflow_y_scroll()
            .p(px(16.))
            .flex()
            .flex_col()
            .gap(px(12.));
        let mut panel = div()
            .rounded(px(10.))
            .p(px(16.))
            .bg(self.color("MacSurfaceColor"))
            .flex()
            .flex_col()
            .gap(px(10.))
            .child(
                div()
                    .flex()
                    .flex_wrap()
                    .gap(px(12.))
                    .items_center()
                    .child("Разделы регистров")
                    .child(self.action_button(
                        "DashboardStatus",
                        "Status",
                        MonitorAction::DashboardStatus,
                        true,
                        cx,
                    ))
                    .child(self.action_button(
                        "ClearRegisters",
                        "Очистить выбор",
                        MonitorAction::Clear,
                        !dashboard.selected.is_empty(),
                        cx,
                    ))
                    .child(format!(
                        "{} выбрано · зеркало шины 0..599 · обновление 1 с",
                        dashboard.selected.len()
                    )),
            );
        if let Some(error) = &dashboard.error {
            panel = panel.child(
                div()
                    .text_color(self.color("MacOrangeColor"))
                    .child(format!(
                        "Ошибка загрузки регистров: {error} · показаны последние известные данные"
                    )),
            );
        }
        if dashboard
            .registers
            .as_ref()
            .is_some_and(|registers| registers.stale())
        {
            panel = panel.child(
                div()
                    .text_color(self.color("MacOrangeColor"))
                    .child("STALE · удерживаются последние данные шины"),
            );
        }
        let rows = dashboard.selected_rows();
        let legend =
            rows.iter()
                .enumerate()
                .fold(div().flex().flex_wrap().gap(px(12.)), |s, (i, r)| {
                    s.child(
                        div()
                            .text_color(rgb(PALETTE[i % PALETTE.len()]))
                            .child(format!("● {} [{}]", r.name, r.addr)),
                    )
                });
        panel = panel.child(legend);
        if let Some(error) = &dashboard.history_error {
            panel = panel.child(
                div()
                    .text_color(self.color("MacOrangeColor"))
                    .child(format!("Ошибка загрузки истории: {error}")),
            );
        }
        if let Some(chart) = dashboard.chart() {
            let lines: Vec<_> = rows
                .iter()
                .enumerate()
                .filter_map(|(i, r)| {
                    let samples = dashboard.history.get(&r.addr)?;
                    Some((
                        rgb(PALETTE[i % PALETTE.len()]),
                        samples.iter().map(|s| chart.point(s)).collect::<Vec<_>>(),
                    ))
                })
                .collect();
            let grid = self.color("MacSeparatorColor");
            let axes = (0..=4).fold(
                div()
                    .w(px(75.))
                    .h_full()
                    .flex()
                    .flex_col()
                    .justify_between(),
                |s, i| {
                    s.child(format!(
                        "{:.3}",
                        chart.v_max * (1. - i as f64 / 4.) + chart.v_min * (i as f64 / 4.)
                    ))
                },
            );
            panel = panel
                .child(
                    div()
                        .h(px(220. * self.zoom))
                        .flex()
                        .gap(px(6.))
                        .child(axes)
                        .child(
                            canvas(
                                move |_, _, _| (),
                                move |bounds, _, window, _| {
                                    let w = f32::from(bounds.size.width).max(1.);
                                    let h = f32::from(bounds.size.height).max(1.);
                                    let at = |x: f32, y: f32| {
                                        point(
                                            bounds.origin.x + px(x * w),
                                            bounds.origin.y + px(y * h),
                                        )
                                    };
                                    let mut grid_path = PathBuilder::stroke(px(1.));
                                    for i in 0..=4 {
                                        let y = i as f32 / 4.;
                                        grid_path.move_to(at(0., y));
                                        grid_path.line_to(at(1., y));
                                    }
                                    if let Ok(path) = grid_path.build() {
                                        window.paint_path(path, grid);
                                    }
                                    for (color, points) in lines {
                                        let mut line = PathBuilder::stroke(px(1.5));
                                        for (i, (x, y)) in points.iter().copied().enumerate() {
                                            if i == 0 {
                                                line.move_to(at(x, y));
                                            } else {
                                                line.line_to(at(x, y));
                                            }
                                        }
                                        // A single sample is visible before a second timestamp arrives.
                                        if let [(x, y)] = points.as_slice() {
                                            line.line_to(at((x + 0.004).min(1.), *y));
                                        }
                                        if let Ok(path) = line.build() {
                                            window.paint_path(path, color);
                                        }
                                    }
                                },
                            )
                            .flex_1()
                            .h_full(),
                        ),
                )
                .child(
                    div()
                        .flex()
                        .justify_between()
                        .child(format_time(chart.t_min))
                        .child(format_time(chart.t_max)),
                );
        } else {
            panel = panel.child(
                div()
                    .h(px(160.))
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(if dashboard.selected.is_empty() {
                        "Отметьте регистры чекбоксами, чтобы строить историю"
                    } else {
                        "Нет точек истории для выбранных регистров"
                    }),
            );
        }
        let mut selected = div()
            .id("SelectedRegisters")
            .max_h(px(200.))
            .overflow_y_scroll()
            .flex()
            .flex_col()
            .child(
                div()
                    .flex()
                    .child(div().w(px(90.)).child("addr"))
                    .child(div().flex_1().child("имя"))
                    .child(div().w(px(180.)).child("значение")),
            );
        if rows.is_empty() {
            selected = selected.child("нет отмеченных переменных");
        }
        for row in &rows {
            selected = selected.child(self.table_row(row, true, cx));
        }
        panel = panel.child(selected);
        if !dashboard.loaded() {
            panel = panel.child("загрузка зеркала регистров…");
        }
        for (key, label) in [
            ("system", "System [0..3]"),
            ("com", "COM [4..199]"),
            ("lan", "LAN [200..399]"),
            ("raw", "RAW [400..599]"),
        ] {
            let collapsed = dashboard.collapsed.contains(key);
            let zone = match key {
                "com" => Some(icem_web::dashboard_client::Zone::Com),
                "lan" => Some(icem_web::dashboard_client::Zone::Lan),
                "raw" => Some(icem_web::dashboard_client::Zone::Raw),
                _ => None,
            };
            let live = dashboard
                .registers
                .as_ref()
                .zip(zone)
                .map(|(registers, zone)| registers.zone(zone).liveness());
            panel = panel.child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(10.))
                    .child(self.action_button(
                        gpui::SharedString::from(format!("zone-{key}")),
                        &format!("{} {label}", if collapsed { "▸" } else { "▾" }),
                        MonitorAction::Collapse(key.into()),
                        true,
                        cx,
                    ))
                    .when(key != "system", |s| {
                        s.child(format!(
                            "{} · {} ms · записей {}",
                            if live.is_some_and(|live| live.active) {
                                "live"
                            } else {
                                "stale"
                            },
                            live.map(|live| live.age_ms.to_string())
                                .unwrap_or_else(|| "—".into()),
                            live.map(|live| live.write_count.to_string())
                                .unwrap_or_else(|| "—".into())
                        ))
                    }),
            );
            if !collapsed {
                let table = dashboard.rows(key).iter().fold(
                    div()
                        .id(gpui::SharedString::from(format!("table-{key}")))
                        .overflow_x_scroll()
                        .flex()
                        .flex_col(),
                    |s, row| s.child(self.table_row(row, false, cx)),
                );
                panel = panel.child(table);
            }
        }
        content = content.child(panel).child(self.model.note.clone());
        content
    }
}
fn format_time(ms: i64) -> String {
    chrono::DateTime::from_timestamp_millis(ms)
        .map(|t| t.format("%H:%M:%S UTC").to_string())
        .unwrap_or_else(|| "—".into())
}
impl Monitor {
    fn dashboard_status(&self, cx: &Context<Self>) -> gpui::Stateful<gpui::Div> {
        let dashboard = &self.model.dashboard;
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
            .unwrap_or_default();
        let mut root = div()
            .id("NativeDashboardStatus")
            .flex_1()
            .min_h_0()
            .overflow_y_scroll()
            .p(px(16.))
            .flex()
            .flex_col()
            .gap(px(12.))
            .child(self.action_button(
                "DashboardRegisters",
                "Разделы регистров",
                MonitorAction::DashboardStatus,
                true,
                cx,
            ));
        let mut cards = div().flex().flex_wrap().gap(px(12.));
        for (label, value) in [
            (
                "Шина",
                if self.model.host_display().bus_open {
                    "открыта".into()
                } else {
                    "закрыта".into()
                },
            ),
            (
                "iCEM",
                if self.model.host_display().addin_alive {
                    "жив".into()
                } else {
                    "нет".into()
                },
            ),
            ("Heartbeat", dashboard.heartbeat.to_string()),
        ] {
            cards = cards.child(
                div()
                    .min_w(px(160.))
                    .p(px(16.))
                    .rounded(px(10.))
                    .bg(self.color("MacSurfaceColor"))
                    .child(label)
                    .child(div().text_size(px(22. * self.zoom)).child(value)),
            );
        }
        for row in dashboard.rows("system").iter().take(3) {
            cards = cards.child(
                div()
                    .min_w(px(160.))
                    .p(px(16.))
                    .rounded(px(10.))
                    .bg(self.color("MacSurfaceColor"))
                    .child(row.name.clone())
                    .child(if row.label.is_empty() {
                        row.value.clone()
                    } else {
                        row.label.clone()
                    }),
            );
        }
        root = root.child(cards);
        for (i, label) in ["COM", "LAN", "RAW"].iter().enumerate() {
            let host = self.model.host_display();
            let zone = &host.zones[i];
            root = root.child(format!(
                "{label} · {} · {} ms",
                if zone.active { "live" } else { "stale" },
                zone.age_ms
            ));
        }
        let layers = self.model.host_display();
        root = root
            .child(format!(
                "Modbus: {} · Web: {} · Сниффер: {} · OPC: {}",
                layers.modbus, layers.web, layers.sniffer, layers.opc_da
            ))
            .child(layers.last_error)
            .child(format!(
                "Живые метрики · регистров: {} · сэмплов: {} · последний: {} ms назад",
                dashboard.metrics.len(),
                dashboard.sample_count,
                now.saturating_sub(dashboard.last_sample).max(0)
            ));
        if dashboard.interval_clamped {
            root = root.child("interval_clamped → min 200 ms");
        }
        if let Some(error) = &dashboard.metrics_error {
            root = root.child(
                div()
                    .text_color(self.color("MacOrangeColor"))
                    .child(format!(
                        "Ошибка потока: {error} · сохранены последние значения"
                    )),
            );
        }
        let mut table = div().flex().flex_col().gap(px(4.));
        for (addr, metric) in &dashboard.metrics {
            let age = now.saturating_sub(metric.at).max(0);
            table = table.child(
                div()
                    .flex()
                    .gap(px(16.))
                    .p(px(4.))
                    .when(age > 10_000, |s| s.text_color(self.color("MacOrangeColor")))
                    .child(div().w(px(70.)).child(addr.to_string()))
                    .child(div().flex_1().child(metric.value.clone()))
                    .child(div().w(px(80.)).child(metric.kind.clone()))
                    .child(
                        div()
                            .w(px(140.))
                            .child(format!("{:.1} s назад", age as f64 / 1000.)),
                    ),
            );
        }
        root.child(table)
    }
}
