use icem_desktop::dashboard::{Dashboard, HISTORY_CAPACITY, Sample};
use serde_json::{Value, json};
fn response(history: Value) -> Value {
    json!({"ok":true,"data":{"registers":{"system":{"scalars":[{"reg":0,"name":"COM","value":1}]},"zones":{"com":{"pairs":[{"addr":4,"addrEnd":5,"name":"Unix","value":"2026-10-17T12:00:00Z"},{"addr":6,"addrEnd":7,"name":"Pressure","value":12.25}]}}},"history":history}})
}
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
    assert!(d.loaded);
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
    m.apply("dashboard", Err("REST offline".into()));
    assert!(m.connected);
    let mut result = response(json!({}));
    result["data"]["historyError"] = json!("HTTP 503 history disabled");
    m.apply("dashboard", Ok(result));
    assert!(m.dashboard.loaded);
    assert!(m.dashboard.history_error.is_some());
}
#[test]
fn metric_deltas_keep_update_age_and_survive_disconnect_without_changing_host_truth() {
    let mut m = icem_desktop::Model {
        connected: true,
        ..Default::default()
    };
    m.apply("metrics",Ok(json!({"ok":true,"data":{"at":1000,"heartbeat":7,"deltas":[{"addr":4,"kind":"int32","value":-1},{"addr":6,"kind":"float","value":"NaN"}]}})));
    m.apply("metrics",Ok(json!({"ok":true,"data":{"at":2000,"heartbeat":8,"deltas":[{"addr":4,"kind":"int32","value":0}]}})));
    assert_eq!(m.dashboard.metrics[&6].at, 1000);
    assert_eq!(m.dashboard.metrics[&6].value, "NaN");
    assert_eq!(m.dashboard.metrics[&4].at, 2000);
    assert_eq!(m.dashboard.sample_count, 2);
    m.dashboard.metrics_pending = true;
    m.apply("metrics", Err("stream ended".into()));
    assert!(m.connected);
    assert!(!m.dashboard.metrics_pending);
    assert_eq!(m.dashboard.metrics.len(), 2);
    assert!(m.dashboard.metrics_error.is_some());
}
