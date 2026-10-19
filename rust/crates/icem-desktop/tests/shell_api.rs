use icem_desktop::Model;
use icem_web::{
    proto,
    shell::{Layer, LogCommand, Operation, Request, Response, Selection},
};
use serde_json::json;
fn status() -> proto::HostStatus {
    proto::HostStatus {
        layers: Some(proto::LayerState::default()),
        zones: (1..=3)
            .map(|kind| proto::ZoneHealth {
                kind,
                ..Default::default()
            })
            .collect(),
        ..Default::default()
    }
}
#[test]
fn malformed_status_cannot_confirm_connection_or_freshness() {
    let mut model = Model::default();
    model.apply("status", Ok(json!({"ok":true})));
    assert!(!model.connected);
    for bad in [
        proto::HostStatus::default(),
        proto::HostStatus {
            zones: vec![],
            ..status()
        },
        proto::HostStatus {
            zones: vec![
                proto::ZoneHealth {
                    kind: 1,
                    ..Default::default()
                };
                3
            ],
            ..status()
        },
    ] {
        model.apply_host(Operation::Status, Response::status(status()));
        assert!(model.connected);
        model.host_logs.fresh = true;
        model.boot_ready = true;
        model.apply_host(Operation::Status, Response::status(bad));
        assert!(!model.connected);
        assert!(!model.host_logs.fresh);
        assert!(!model.boot_ready);
        assert!(model.host.is_some(), "retain last-known truth");
    }
    model.apply_host(
        Operation::Status,
        Ok(Response::Prefs(proto::UiPrefs::default())),
    );
    assert!(!model.connected);
}
#[test]
fn invalid_parameters_are_rejected_before_transport() -> Result<(), String> {
    for bad in [i32::MIN, -1, 0, 5, i32::MAX] {
        assert!(Layer::try_from(bad).is_err());
    }
    for value in 1..=4 {
        assert_eq!(
            proto::LayerKind::from(Layer::try_from(value)?) as i32,
            value
        );
    }
    for bad in [
        vec![0],
        vec![3],
        vec![5],
        vec![600],
        vec![u16::MAX],
        vec![4; 299],
    ] {
        assert!(Selection::new(bad).is_err());
    }
    assert_eq!(
        Selection::new((4..600).step_by(2).collect())?
            .addresses()
            .len(),
        298
    );
    assert_eq!(LogCommand::SetEnabled(false).v1(), ("set", Some(false)));
    Ok(())
}
#[test]
fn all_shell_operations_have_distinct_checked_response_pairs() -> Result<(), String> {
    let pairs = [
        (Request::Status, Response::status(status())?),
        (Request::Prefs, Response::Prefs(proto::UiPrefs::default())),
        (
            Request::Theme(true),
            Response::Theme(proto::UiPrefs { theme_light: true }),
        ),
        (
            Request::Layer(Layer::Modbus),
            Response::Layer(proto::LayerState::default()),
        ),
        (
            Request::Console(true),
            Response::Console(status().try_into()?),
        ),
        (
            Request::Autostart(false),
            Response::Autostart(status().try_into()?),
        ),
        (
            Request::Navigate,
            Response::Navigate(proto::NavigateUiResponse::default()),
        ),
        (
            Request::Boot,
            Response::Boot(icem_web::shell::BootBatch {
                stages: vec![],
                reset: true,
            }),
        ),
        (
            Request::Metrics,
            Response::Metrics(proto::MetricsSample {
                timestamp_utc: Some(Default::default()),
                ..Default::default()
            }),
        ),
        (
            Request::Dashboard(Selection::new(vec![])?),
            Response::Dashboard(json!({"registers":{"system":{"scalars":[]}},"history":{}})),
        ),
        (
            Request::Logs(LogCommand::Get),
            Response::Logs(json!({"ok":true,"data":{}})),
        ),
        (
            Request::LocalLogs(LogCommand::Clear),
            Response::LocalLogs(json!({"ok":true,"data":{}})),
        ),
    ];
    let mut seen = std::collections::HashSet::new();
    for (request, reply) in pairs {
        assert_eq!(request.operation(), reply.operation());
        assert!(seen.insert(format!("{:?}", request.operation())));
        Model::default().apply_host(request.operation(), Ok(reply));
    }
    assert_eq!(seen.len(), 12);
    Ok(())
}
#[test]
fn boot_reset_stale_metrics_and_unknown_consume_preserve_semantics() {
    let mut m = Model::default();
    let batch = |id: &str, completed| {
        Response::Boot(icem_web::shell::BootBatch {
            stages: vec![proto::BootStage {
                ordinal: 1,
                id: id.into(),
                completed,
                ..Default::default()
            }],
            reset: true,
        })
    };
    m.apply_host(Operation::Boot, Ok(batch("ready", true)));
    assert!(m.boot_ready);
    m.apply_host(Operation::Boot, Ok(batch("ini", false)));
    assert!(!m.boot_ready);
    assert_eq!(m.boot_stages.len(), 1);
    m.apply_host(Operation::Boot, Err("stream EOF".into()));
    assert!(m.boot_stages.is_empty());
    m.apply_host(
        Operation::Metrics,
        Ok(Response::Metrics(proto::MetricsSample {
            timestamp_utc: Some(Default::default()),
            deltas: vec![proto::RegisterDelta {
                addr: 4,
                value: Some(proto::register_delta::Value::Int32Value(42)),
            }],
            ..Default::default()
        })),
    );
    m.apply_host(Operation::Metrics, Err("stream EOF".into()));
    assert_eq!(m.dashboard.metrics[&4].value, "42");
    assert_eq!(m.dashboard.metrics[&4].at, 0);
    assert!(m.dashboard.metrics_error.is_some());
    m.apply_host(Operation::Navigate, Err("lost reply".into()));
    assert!(m.navigation.is_none());
    assert!(m.note.contains("результат неизвестен"));
}
#[test]
fn shell_close_discards_queued_mutations_and_finishes_started_work()
-> Result<(), Box<dyn std::error::Error>> {
    use std::{
        sync::{
            Arc, Mutex,
            atomic::{AtomicUsize, Ordering},
            mpsc,
        },
        time::Duration,
    };
    struct Port {
        started: mpsc::Sender<()>,
        release: Mutex<mpsc::Receiver<()>>,
        calls: Arc<AtomicUsize>,
    }
    impl icem_desktop::host::Port for Port {
        fn request(&self, request: Request) -> Result<Response, String> {
            assert!(matches!(request, Request::Console(true)));
            self.calls.fetch_add(1, Ordering::AcqRel);
            self.started.send(()).map_err(|e| e.to_string())?;
            self.release
                .lock()
                .map_err(|e| e.to_string())?
                .recv_timeout(Duration::from_secs(2))
                .map_err(|e| e.to_string())?;
            Ok(Response::Console(status().try_into()?))
        }
    }
    let (started, began) = mpsc::channel();
    let (release, gate) = mpsc::channel();
    let calls = Arc::new(AtomicUsize::new(0));
    let (bridge, completion) = icem_desktop::host::start(Arc::new(Port {
        started,
        release: Mutex::new(gate),
        calls: calls.clone(),
    }))?;
    bridge.send(Request::Console(true))?;
    began.recv_timeout(Duration::from_secs(2))?;
    bridge.send(Request::Autostart(true))?;
    drop(bridge);
    release.send(())?;
    completion.finish()?;
    assert_eq!(calls.load(Ordering::Acquire), 1);
    Ok(())
}
