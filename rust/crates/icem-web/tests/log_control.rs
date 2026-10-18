use axum::{body::Body, http::Request};
use icem_web::{HostLayers, WebState, log_control};
use tower::ServiceExt;
#[tokio::test]
async fn local_extension_is_not_on_web_and_rejects_unbounded_or_invalid_commands()
-> Result<(), Box<dyn std::error::Error>> {
    let (mut state, _) = WebState::new(
        HostLayers::default(),
        std::path::PathBuf::new(),
        Default::default(),
    );
    let _owner = state.enable_log_control();
    let response = icem_web::router(state.clone())
        .oneshot(
            Request::builder()
                .uri(log_control::PATH)
                .body(Body::empty())?,
        )
        .await?;
    assert!(!response.status().is_success());
    for body in [
        "{}".to_owned(),
        "{\"enabled\":1}".into(),
        "{\"enabled\":false,\"path\":\"C:/\"}".into(),
        "x".repeat(1025),
    ] {
        let response = log_control::router(state.clone())
            .oneshot(
                Request::builder()
                    .method("PUT")
                    .uri(log_control::PATH)
                    .header("content-type", "application/json")
                    .body(Body::from(body))?,
            )
            .await?;
        assert!(response.status().is_client_error());
    }
    Ok(())
}
#[cfg(windows)]
#[test]
fn real_pipe_logger_restart_and_lost_mutation_response_never_retry()
-> Result<(), Box<dyn std::error::Error>> {
    use serde_json::json;
    use std::sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    };
    let root = std::env::temp_dir().join(format!("icem-log-pipe-{}", std::process::id()));
    std::fs::create_dir_all(&root)?;
    let pipe = format!("icem-log-control-{}", std::process::id());
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let attempts = Arc::new(AtomicUsize::new(0));
    for pass in 0..2 {
        let (mut state, _reads) = WebState::new(
            HostLayers::default(),
            std::path::PathBuf::new(),
            Default::default(),
        );
        let requests = state.enable_log_control();
        let stop = Arc::new(AtomicBool::new(false));
        let stopping = stop.clone();
        let root = root.clone();
        let count = attempts.clone();
        let worker = std::thread::spawn(move || {
            let mut logs = icem_logging::Logs::open(&root, icem_logging::Owner::Host);
            while !stopping.load(Ordering::Acquire) {
                if let Ok(request) = requests.recv_timeout(std::time::Duration::from_millis(20)) {
                    if request.response.is_closed() {
                        continue;
                    }
                    let ok = logs.request(request.action, request.enabled).is_ok();
                    if request.action == "set" {
                        count.fetch_add(1, Ordering::AcqRel);
                        drop(request.response);
                        continue;
                    } // fault AFTER persistence
                    let _ = request.response.send(json!({"ok":ok,"data":logs.status()}));
                }
            }
        });
        let server = icem_web::LocalRpcServer::bind(&pipe, state)?;
        let get = runtime.block_on(log_control::request(&pipe, "get", None))?;
        assert_eq!(get["data"]["enabled"], pass == 0);
        if pass == 0 {
            assert!(
                runtime
                    .block_on(log_control::request(&pipe, "set", Some(false)))
                    .is_err()
            );
            let truth = runtime.block_on(log_control::request(&pipe, "get", None))?;
            assert_eq!(truth["data"]["enabled"], false);
            assert_eq!(attempts.load(Ordering::Acquire), 1);
        }
        let clear = runtime.block_on(log_control::request(&pipe, "clear", None))?;
        assert_eq!(clear["ok"], true);
        assert_eq!(clear["data"]["enabled"], false);
        server.stop()?;
        stop.store(true, Ordering::Release);
        worker.join().map_err(|_| "log owner panic")?;
        assert!(
            runtime
                .block_on(log_control::request(&pipe, "get", None))
                .is_err()
        );
    }
    assert_eq!(attempts.load(Ordering::Acquire), 1);
    std::fs::remove_dir_all(root)?;
    Ok(())
}
