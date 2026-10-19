use icem_desktop::Model;
use icem_web::{
    proto,
    shell::{Operation, Response},
};
#[test]
fn sniffer_availability_controls_actions_without_erasing_last_known_truth() {
    let mut m = Model::default();
    assert!(!m.can_toggle_sniffer());
    for available in [false, true] {
        m.apply_host(
            Operation::Status,
            Response::status(proto::HostStatus {
                layers: Some(proto::LayerState {
                    sniffer_available: available,
                    ..Default::default()
                }),
                zones: (1..=3)
                    .map(|kind| proto::ZoneHealth {
                        kind,
                        ..Default::default()
                    })
                    .collect(),
                ..Default::default()
            }),
        );
        assert_eq!(m.can_toggle_sniffer(), available);
    }
    m.apply_host(
        Operation::Layer,
        Ok(Response::Layer(proto::LayerState {
            sniffer: true,
            sniffer_available: true,
            busy: true,
            last_error: "write failed".into(),
            ..Default::default()
        })),
    );
    assert!(!m.can_toggle_sniffer());
    assert_eq!(m.note, "write failed");
    assert!(m.host.as_ref().is_some_and(|s| s.layers.sniffer));
    m.apply_host(Operation::Status, Err("pipe closed".into()));
    assert!(!m.can_toggle_sniffer());
    assert!(m.host.as_ref().is_some_and(|s| s.layers.sniffer));
}
