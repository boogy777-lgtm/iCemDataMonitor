use icem_desktop::Model;
use serde_json::json;
#[test]
fn state_is_owner_acknowledged_disconnect_retains_facts_and_blocks_mutations() {
    let mut model = Model::default();
    assert!(!model.host_logs.actionable());
    model.apply(
        "logs",
        Ok(json!({"ok":true,"data":{"enabled":true,"available":true,"recording":true}})),
    );
    assert!(model.host_logs.actionable());
    model.host_logs.pending = true;
    assert!(!model.host_logs.actionable());
    assert_eq!(model.host_logs.enabled(), Some(true));
    model.apply("logs", Err("response lost; outcome unknown".into()));
    assert!(!model.host_logs.actionable());
    assert_eq!(model.host_logs.enabled(), Some(true));
    model.apply(
        "logs",
        Ok(json!({"ok":true,"data":{"enabled":false,"available":true,"recording":false}})),
    );
    assert_eq!(model.host_logs.enabled(), Some(false));
    model.apply("status", Err("Host disconnected".into()));
    assert!(!model.host_logs.actionable());
    model.apply("local_logs",Ok(json!({"ok":false,"data":{"enabled":true,"available":true,"recording":true,"error":"partial clear: locked file"}})));
    assert!(model.local_logs.description().contains("partial clear"));
    assert!(!model.local_logs.description().contains("успешно"));
    assert_eq!(model.local_logs.enabled(), Some(true));
}
