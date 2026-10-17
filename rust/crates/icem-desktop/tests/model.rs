use icem_desktop::{Model, VariableRow, equipment_command, unit_command};
use serde_json::json;
#[test]
fn manual_value_commands_preserve_sv_native_names_and_finite_numbers() -> Result<(), String> {
    let model = Model {
        lan_variables: vec![VariableRow {
            sv: "0x0137".into(),
            name: "Discharge pressure".into(),
            ..Default::default()
        }],
        selected_lan: Some(0),
        ..Default::default()
    };
    assert_eq!(
        model.edit_command(true, Some("1,25"))?,
        json!({"cmd":"set","channel":"lan","sv":"0x0137","name":"Discharge pressure","value":1.25})
    );
    for bad in ["NaN", "inf", "1e999", "invalid"] {
        assert!(model.edit_command(true, Some(bad)).is_err());
    }
    assert_eq!(
        equipment_command("0xFFFFFFFF")?,
        json!({"cmd":"setequipment","value":u32::MAX})
    );
    assert!(equipment_command("4294967296").is_err());
    assert_eq!(
        unit_command(5, Some(" bar ")),
        json!({"cmd":"setunit","channel":"pressure","name":"bar"})
    );
    assert_eq!(
        unit_command(0, None),
        json!({"cmd":"setunit","channel":"flow"})
    );
    Ok(())
}
#[test]
fn command_refusal_is_visible_and_does_not_project_requested_state() {
    let mut model = Model::default();
    model.apply(
        "status",
        Ok(json!({"ok":true,"data":{"lan":{"streaming":false}}})),
    );
    model.apply(
        "start",
        Ok(json!({"ok":false,"error":"no LAN adapter configured"})),
    );
    assert!(!model.flag("lan", "streaming"));
    assert!(model.note.contains("no LAN adapter"));
    assert!(model.connected);
    model.apply("status", Err("pipe disconnected".into()));
    assert!(!model.connected);
}
#[test]
fn refresh_cannot_retarget_an_override_to_another_variable() {
    let mut model = Model {
        com_variables: vec![VariableRow {
            name: "Pressure".into(),
            ..Default::default()
        }],
        selected_com: Some(0),
        ..Default::default()
    };
    model.apply(
        "vars",
        Ok(json!({"ok":true,"data":{"com":{"variables":[{"name":"Flow"},{"name":"Pressure"}]}}})),
    );
    assert_eq!(model.selected_com, Some(1));
    model.apply(
        "vars",
        Ok(json!({"ok":true,"data":{"com":{"variables":[{"name":"Flow"}]}}})),
    );
    assert_eq!(model.selected_com, None);
    assert!(model.edit_command(false, Some("4")).is_err());
}
#[test]
fn profile_choices_keep_device_identity_and_variable_clicks_keep_signal_identity()
-> Result<(), String> {
    let mut model = Model::default();
    model.apply("profiles",Ok(json!({"ok":true,"data":{"profiles":[{"name":"Shared","deviceType":"A"},{"name":"Shared","deviceType":"B"}]}})));
    assert_eq!(
        model.profiles[1].command(),
        json!({"cmd":"profile","name":"Shared","deviceType":"B"})
    );
    model.lan_variables = vec![
        VariableRow {
            sv: "0x2".into(),
            name: "Pressure".into(),
            value: "42".into(),
            ..Default::default()
        },
        VariableRow {
            sv: "0x1".into(),
            name: "Pressure".into(),
            value: "12".into(),
            ..Default::default()
        },
    ];
    assert_eq!(
        model.select_variable(true, "0x1", "Pressure"),
        Some("12".into())
    );
    assert_eq!(model.edit_command(true, Some("14"))?["sv"], "0x1");
    assert_eq!(model.select_variable(true, "0x999", "Pressure"), None);
    assert!(model.edit_command(true, Some("14")).is_err());
    Ok(())
}
#[test]
fn boot_is_ordered_ready_is_authoritative_and_disconnect_starts_new_replay() {
    let mut m = Model::default();
    m.apply(
        "boot",
        Ok(json!({"ok":true,"data":{"stages":[
        {"ordinal":2,"id":"ready","title":"готовность","completed":false},
        {"ordinal":1,"id":"ini","title":"конфигурация","completed":true}]}})),
    );
    assert!(!m.boot_ready);
    assert_eq!(
        m.boot_stages.keys().copied().collect::<Vec<_>>(),
        vec![1, 2]
    );
    m.apply("boot",Ok(json!({"ok":true,"data":{"stages":[{"ordinal":2,"id":"ready","title":"готовность","completed":true}]}})));
    assert!(m.boot_ready);
    assert_eq!(m.boot_stages.len(), 2);
    m.apply("boot", Err("stream ended".into()));
    assert!(m.boot_error.is_some());
    assert!(!m.boot_ready);
    assert!(m.boot_stages.is_empty());
    m.apply("status", Err("disconnected".into()));
    assert!(!m.boot_ready);
    assert!(m.boot_stages.is_empty());
}
#[test]
fn fresh_replay_replaces_old_ready_even_without_a_failed_status_probe() {
    let mut m = Model::default();
    m.apply(
        "boot",
        Ok(json!({"ok":true,"data":{"reset":true,"stages":[
        {"ordinal":7,"id":"ready","title":"old process","completed":true}]}})),
    );
    assert!(m.boot_ready);
    // A fast restart may never fail GetStatus. New stream replay is authoritative.
    m.apply(
        "boot",
        Ok(json!({"ok":true,"data":{"reset":true,"stages":[
        {"ordinal":1,"id":"ini","title":"new process","completed":false}]}})),
    );
    assert!(!m.boot_ready);
    assert_eq!(m.boot_stages.len(), 1);
    assert_eq!(m.boot_stages[&1].title, "new process");
    m.apply(
        "boot",
        Ok(json!({"ok":true,"data":{"stages":[
        {"ordinal":2,"id":"ready","title":"new ready","completed":true}]}})),
    );
    assert!(m.boot_ready);
    m.apply(
        "boot",
        Ok(json!({"ok":true,"data":{"reset":true,"stages":[]}})),
    );
    assert!(m.boot_stages.is_empty());
    assert!(!m.boot_ready);
    m.apply("boot", Ok(json!({"ok":false,"error":"lag; reconnect"})));
    assert!(m.boot_error.as_deref().is_some_and(|s| s.contains("lag")));
}
#[test]
fn navigation_uses_contract_sections_without_changing_connection_truth() {
    let mut m = Model {
        connected: true,
        ..Default::default()
    };
    for (section, panel) in [(1, Some(true)), (2, Some(false)), (0, None)] {
        m.apply(
            "navigate",
            Ok(json!({"ok":true,"data":{"pending":section}})),
        );
        assert_eq!(m.navigation, panel);
    }
    m.apply("navigate", Err("unavailable".into()));
    assert!(m.connected);
    assert!(m.note.contains("unavailable"));
}
