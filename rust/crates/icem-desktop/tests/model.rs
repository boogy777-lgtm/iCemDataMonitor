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
