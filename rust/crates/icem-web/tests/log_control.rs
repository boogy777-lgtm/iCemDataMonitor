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
    let owner = state.enable_log_control();
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
        "null".into(),
        "{\"enabled\":null}".into(),
        "{\"enabled\":true,\"other\":false}".into(),
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
    assert_eq!(owner.try_iter().count(), 0);
    Ok(())
}
#[cfg(windows)]
#[test]
fn real_pipe_logger_restart_and_lost_mutation_response_never_retry()
-> Result<(), Box<dyn std::error::Error>> {
    use icem_logging::{Command, ErrorKind, Outcome};
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
                    let reply = logs.request(request.command);
                    if matches!(request.command, Command::SetEnabled(_)) {
                        count.fetch_add(1, Ordering::AcqRel);
                        drop(request.response);
                        continue;
                    } // fault AFTER persistence
                    let _ = request.response.send(reply);
                }
            }
        });
        let server = icem_web::LocalRpcServer::bind(&pipe, state)?;
        let get = runtime.block_on(log_control::request(&pipe, Command::Get))?;
        assert_eq!(get.status.enabled, Some(pass == 0));
        if pass == 0 {
            let error = runtime
                .block_on(log_control::request(&pipe, Command::SetEnabled(false)))
                .err()
                .ok_or("expected lost response")?;
            assert_eq!(error.kind, ErrorKind::Unknown);
            let truth = runtime.block_on(log_control::request(&pipe, Command::Get))?;
            assert_eq!(truth.status.enabled, Some(false));
            assert_eq!(attempts.load(Ordering::Acquire), 1);
        }
        let clear = runtime.block_on(log_control::request(&pipe, Command::Clear))?;
        assert_eq!(clear.outcome, Outcome::Applied);
        assert_eq!(clear.status.enabled, Some(false));
        server.stop()?;
        stop.store(true, Ordering::Release);
        worker.join().map_err(|_| "log owner panic")?;
        assert!(
            runtime
                .block_on(log_control::request(&pipe, Command::Get))
                .is_err()
        );
    }
    assert_eq!(attempts.load(Ordering::Acquire), 1);
    std::fs::remove_dir_all(root)?;
    Ok(())
}

#[test]
fn typed_v1_codec_preserves_facts_and_never_invents_clear_counts()
-> Result<(), Box<dyn std::error::Error>> {
    use icem_logging::{Command, Outcome};
    use serde_json::{Value, json};
    let value = json!({"ok":true,"data":{
        "enabled":null,"available":true,"recording":false,"directory":"logs",
        "error":"damaged preference","operation":"","written":9
    }});
    for (command, outcome) in [
        (Command::Get, Outcome::Completed),
        (Command::Clear, Outcome::Applied),
    ] {
        let reply = log_control::decode_reply(&value, command)?;
        assert_eq!(reply.status.enabled, None);
        assert_eq!(reply.status.error, "damaged preference");
        assert_eq!(reply.outcome, outcome);
        assert_eq!(reply.clear, None);
        assert_eq!(log_control::encode_reply(&reply), value);
    }
    for enabled in [false, true] {
        let mut set = value.clone();
        set["data"]["enabled"] = json!(enabled);
        set["data"]["recording"] = json!(enabled);
        let reply = log_control::decode_reply(&set, Command::SetEnabled(enabled))?;
        assert_eq!(reply.outcome, Outcome::Applied);
        assert_eq!(log_control::encode_reply(&reply), set);
        assert!(log_control::decode_reply(&set, Command::SetEnabled(!enabled)).is_err());
    }
    assert!(log_control::decode_reply(&value, Command::SetEnabled(false)).is_err());
    let mut stopped = value.clone();
    stopped["data"]["enabled"] = json!(true);
    assert!(log_control::decode_reply(&stopped, Command::SetEnabled(true)).is_err());
    stopped["ok"] = json!(false);
    assert_eq!(
        log_control::decode_reply(&stopped, Command::SetEnabled(false))?.outcome,
        Outcome::Incomplete
    );
    let data = value["data"].as_object().ok_or("missing fixture data")?;
    for key in data.keys() {
        let mut missing = value.clone();
        missing["data"]
            .as_object_mut()
            .ok_or("missing data")?
            .remove(key);
        assert!(
            log_control::decode_reply(&missing, Command::Get).is_err(),
            "missing {key}"
        );
        let mut wrong = value.clone();
        wrong["data"][key] = json!([]);
        assert!(
            log_control::decode_reply(&wrong, Command::Clear).is_err(),
            "wrong {key}"
        );
    }
    for bad in [
        Value::Null,
        json!({"ok":true}),
        json!({"data":value["data"]}),
        json!({"ok":"true","data":value["data"]}),
    ] {
        assert!(log_control::decode_reply(&bad, Command::Get).is_err());
    }
    for enabled in [Value::Null, json!(false)] {
        let mut inconsistent = value.clone();
        inconsistent["data"]["enabled"] = enabled;
        inconsistent["data"]["recording"] = json!(true);
        assert!(log_control::decode_reply(&inconsistent, Command::Get).is_err());
    }
    let mut inconsistent = value.clone();
    inconsistent["data"]["enabled"] = json!(true);
    inconsistent["data"]["recording"] = json!(true);
    inconsistent["data"]["available"] = json!(false);
    assert!(log_control::decode_reply(&inconsistent, Command::Get).is_err());
    let mut incomplete = value.clone();
    incomplete["ok"] = json!(false);
    let reply = log_control::decode_reply(&incomplete, Command::Clear)?;
    assert_eq!(reply.outcome, Outcome::Incomplete);
    assert_eq!(reply.clear, None);
    assert_eq!(log_control::encode_reply(&reply), incomplete);
    Ok(())
}
