use icem_desktop::Model;
use serde_json::json;
#[test]
fn sniffer_availability_controls_actions_without_erasing_last_known_truth() {
    let mut m = Model::default();
    assert!(!m.can_toggle_sniffer());
    for available in [false, true] {
        m.apply(
            "status",
            Ok(json!({"ok":true,"data":{"layers":{"sniffer":false,"snifferAvailable":available}}})),
        );
        assert_eq!(m.can_toggle_sniffer(), available);
    }
    m.apply("layer",Ok(json!({"ok":true,"data":{"sniffer":true,"snifferAvailable":true,"busy":true,"lastError":"write failed"}})));
    assert!(!m.can_toggle_sniffer());
    assert_eq!(m.note, "write failed");
    assert_eq!(m.snapshot["layers"]["sniffer"], true);
    m.apply("status", Err("pipe closed".into()));
    assert!(!m.can_toggle_sniffer());
    assert_eq!(m.snapshot["layers"]["sniffer"], true);
}
