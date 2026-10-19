use icem_desktop::dashboard::{Dashboard, HISTORY_CAPACITY, Sample};
use icem_web::{
    proto,
    shell::{Operation, Response},
};
use serde_json::json;
mod support;
use support::response;
#[test]
fn selection_is_addressed_validated_and_survives_reload() -> Result<(), String> {
    let mut d = Dashboard::default();
    for a in [0, 3, 5, 600, u16::MAX] {
        d.toggle(a);
    }
    assert!(d.selected.is_empty());
    d.toggle(6);
    d.toggle(4);
    d.apply(Ok(response(json!({}))));
    assert_eq!(
        d.selected_rows().iter().map(|r| r.addr).collect::<Vec<_>>(),
        vec![4, 6]
    );
    assert_eq!(d.rows("com")[0].value, "2026-10-17T12:00:00Z");
    let mut restored = Dashboard::default();
    restored.restore_selection(&d.selection_bytes().map_err(|e| e.to_string())?)?;
    assert_eq!(restored.selected, d.selected);
    for bad in [b"[3]".as_slice(), b"[600]", b"[65536]", b"bad"] {
        assert!(restored.restore_selection(bad).is_err());
    }
    d.clear();
    assert!(d.selected.is_empty());
    assert!(d.history.is_empty());
    Ok(())
}
#[test]
fn histories_are_bounded_and_retain_last_data_on_failures() {
    let mut d = Dashboard::default();
    d.toggle(6);
    let samples: Vec<_> = (0..700)
        .map(|i| json!({"ts":format!("2026-10-17T12:{:02}:{:02}Z",i/60,i%60),"v":i}))
        .collect();
    d.apply(Ok(response(json!({"6":samples}))));
    assert_eq!(d.history[&6].len(), HISTORY_CAPACITY);
    assert_eq!(d.history[&6][0].value, 100.);
    let chart = d.chart().unwrap_or_else(|| panic!("missing chart"));
    assert_eq!(chart.point(&d.history[&6][0]), (0., 1.));
    assert_eq!(chart.point(&d.history[&6][599]), (1., 0.));
    d.pending = true;
    d.apply(Err("offline".into()));
    assert!(!d.pending);
    assert!(d.loaded());
    assert_eq!(d.error.as_deref(), Some("offline"));
    let old = d.history.clone();
    d.apply(Ok(response(json!({"6":[{"ts":"invalid","v":1}]}))));
    assert!(d.error.is_none());
    assert!(d.history_error.is_some());
    assert_eq!(d.history, old);
    d.toggle(6);
    assert!(d.history.is_empty());
}
#[test]
fn constant_and_extreme_finite_values_have_finite_chart_coordinates() {
    let mut d = Dashboard::default();
    d.toggle(6);
    d.apply(Ok(response(
        json!({"6":[{"ts":"2026-10-17T12:00:00Z","v":1}]}),
    )));
    let chart = d.chart().unwrap_or_else(|| panic!("missing chart"));
    assert_eq!(chart.point(&d.history[&6][0]), (0., 0.5));
    d.history.insert(
        6,
        vec![
            Sample {
                at: 0,
                value: -f64::MAX,
            },
            Sample {
                at: 1000,
                value: f64::MAX,
            },
        ],
    );
    let chart = d.chart().unwrap_or_else(|| panic!("missing chart"));
    for s in &d.history[&6] {
        let (x, y) = chart.point(s);
        assert!(x.is_finite() && y.is_finite());
    }
}
#[test]
fn rest_errors_do_not_disconnect_host_or_discard_registers() {
    let mut m = icem_desktop::Model {
        connected: true,
        ..Default::default()
    };
    m.apply_host(Operation::Dashboard, Err("REST offline".into()));
    assert!(m.connected);
    let mut result = response(json!({}));
    result.history = Err("HTTP 503 history disabled".into());
    m.apply_host(
        Operation::Dashboard,
        Ok(Response::Dashboard(Box::new(result))),
    );
    assert!(m.dashboard.loaded());
    assert!(m.dashboard.history_error.is_some());
}
#[test]
fn metric_deltas_keep_update_age_and_survive_disconnect_without_changing_host_truth() {
    let mut m = icem_desktop::Model {
        connected: true,
        ..Default::default()
    };
    for (seconds, heartbeat, deltas) in [
        (
            1,
            7,
            vec![
                proto::RegisterDelta {
                    addr: 4,
                    value: Some(proto::register_delta::Value::Int32Value(-1)),
                },
                proto::RegisterDelta {
                    addr: 6,
                    value: Some(proto::register_delta::Value::FloatValue(f32::NAN)),
                },
            ],
        ),
        (
            2,
            8,
            vec![proto::RegisterDelta {
                addr: 4,
                value: Some(proto::register_delta::Value::Int32Value(0)),
            }],
        ),
    ] {
        let mut sample = proto::MetricsSample {
            timestamp_utc: Some(Default::default()),
            heartbeat,
            deltas,
            ..Default::default()
        };
        if let Some(timestamp) = &mut sample.timestamp_utc {
            timestamp.seconds = seconds;
        }
        m.apply_host(Operation::Metrics, Ok(Response::Metrics(sample)));
    }
    assert_eq!(m.dashboard.metrics[&6].at, 1000);
    assert_eq!(m.dashboard.metrics[&6].value, "NaN");
    assert_eq!(m.dashboard.metrics[&4].at, 2000);
    assert_eq!(m.dashboard.sample_count, 2);
    m.dashboard.metrics_pending = true;
    m.apply_host(Operation::Metrics, Err("stream ended".into()));
    assert!(m.connected);
    assert!(!m.dashboard.metrics_pending);
    assert_eq!(m.dashboard.metrics.len(), 2);
    assert!(m.dashboard.metrics_error.is_some());
}

#[test]
fn partial_history_keeps_new_registers_and_previous_good_series_through_actual_host_dispatch() {
    use icem_web::dashboard_client::Registers;
    let mut model = icem_desktop::Model {
        connected: true,
        ..Default::default()
    };
    model.dashboard.toggle(6);
    model.apply_host(
        Operation::Dashboard,
        Ok(Response::Dashboard(Box::new(response(
            json!({"6":[{"ts":"2026-10-17T12:00:00.1230000+00:00","v":2}]}),
        )))),
    );
    let previous = model.dashboard.history.clone();
    for history in [
        json!({"6":[{"ts":"bad","v":4}]}),
        json!({"6":[{"ts":"2026-10-17T12:00:00Z","v":null}]}),
        json!({}),
    ] {
        let mut data = response(json!({}));
        let mut registers = support::registers();
        registers["system"]["scalars"][0]["value"] = json!(65535);
        data.registers = Registers::decode(&serde_json::to_vec(&registers).unwrap_or_default())
            .unwrap_or_else(|e| panic!("{e}"));
        data.history = icem_web::dashboard_client::History::decode(
            &serde_json::to_vec(&history).unwrap_or_default(),
            &icem_web::shell::Selection::new(vec![6]).unwrap_or_else(|e| panic!("{e}")),
        );
        model.apply_host(
            Operation::Dashboard,
            Ok(Response::Dashboard(Box::new(data))),
        );
        assert!(model.connected && model.dashboard.loaded());
        assert_eq!(model.dashboard.rows("system")[0].value, "65535");
        assert!(model.dashboard.error.is_none() && model.dashboard.history_error.is_some());
        assert_eq!(model.dashboard.history, previous);
    }
    model.apply_host(Operation::Dashboard, Err("invalid register body".into()));
    assert_eq!(model.dashboard.rows("system")[0].value, "65535");
    assert_eq!(model.dashboard.history, previous);
}
#[test]
fn selection_change_during_read_cannot_resurrect_removed_series_or_reject_new_selection() {
    let mut d = Dashboard::default();
    d.toggle(6);
    let read = response(json!({"6":[{"ts":"2026-10-17T12:00:00Z","v":1}]}));
    d.toggle(6);
    d.toggle(8);
    d.apply(Ok(read));
    assert!(d.loaded());
    assert!(d.history.is_empty());
    assert!(d.history_error.is_none());
    assert_eq!(d.selected.iter().copied().collect::<Vec<_>>(), vec![8]);
}
#[test]
fn display_keeps_null_names_unix_text_scalar_unsigned_and_signed_zero_policy() {
    use icem_web::dashboard_client::Registers;
    let mut data = response(json!({}));
    let mut registers = support::registers();
    registers["zones"]["com"]["pairs"][1]["value"] = json!(-0.0);
    registers["system"]["scalars"][0]["value"] = json!(65535);
    data.registers = Registers::decode(&serde_json::to_vec(&registers).unwrap_or_default())
        .unwrap_or_else(|e| panic!("{e}"));
    let mut d = Dashboard::default();
    d.apply(Ok(data));
    assert_eq!(d.rows("system")[0].value, "65535");
    assert_eq!(d.rows("com")[0].value, "2026-10-17T12:00:00Z");
    assert_eq!(d.rows("com")[1].value, "-0");
    assert_eq!(d.rows("com")[2].name, "—");
    assert!(d.rows("system")[0].label.is_empty());
}
#[test]
fn malformed_initial_registers_do_not_publish_loaded_state() {
    let mut d = Dashboard::default();
    let mut registers = support::registers();
    registers["zones"]["lan"]["pairs"][0]["addr"] = json!(5);
    let response = icem_web::dashboard_client::Registers::decode(
        &serde_json::to_vec(&registers).unwrap_or_default(),
    )
    .map(|registers| icem_web::dashboard_client::Read {
        registers,
        history: Ok(Default::default()),
    });
    d.pending = true;
    d.apply(response);
    assert!(!d.loaded() && !d.pending);
    assert!(d.error.is_some());
    assert!(d.rows("system").is_empty());
}
