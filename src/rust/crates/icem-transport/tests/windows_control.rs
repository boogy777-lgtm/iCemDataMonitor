#![cfg(windows)]
use icem_emulation::control::{ControlReply, to_json};
use icem_transport::emulator_control::{ControlServer, Executor, request};
use std::{sync::Arc, time::Duration};
struct Status;
impl Executor for Status {
    fn execute(&self, line: &str) -> ControlReply {
        let line = icem_emulation::control::parse_line(line)
            .map(|c| c.name())
            .unwrap_or("");
        ControlReply {
            cmd: line.into(),
            json: to_json(true, line, None, None),
            quit: line == "quit",
            start_watch: line == "watch",
        }
    }
    fn watch_tick(&self) -> String {
        to_json(true, "status", None, None)
    }
}
#[test]
fn exclusive_pipe_roundtrip_cleanup_and_rebind() -> Result<(), Box<dyn std::error::Error>> {
    let pipe = format!("icem-control-test-{}", std::process::id());
    assert!(request(&pipe, "status", Duration::from_millis(50)).is_err());
    assert!(ControlServer::bind(r"invalid\name", Arc::new(Status)).is_err());
    let server = ControlServer::bind(&pipe, Arc::new(Status))?;
    assert!(
        ControlServer::bind(&pipe, Arc::new(Status)).is_err(),
        "second owner must fail synchronously"
    );
    let response = request(&pipe, "status", Duration::from_secs(3))?;
    assert!(response.contains("\"cmd\":\"status\""));
    assert!(request(&pipe, "status\nquit", Duration::from_millis(50)).is_err());
    let response = request(&pipe, "quit", Duration::from_secs(3))?;
    assert!(response.contains("quit"));
    server.stop()?;
    assert!(request(&pipe, "status", Duration::from_millis(50)).is_err());
    ControlServer::bind(&pipe, Arc::new(Status))?.stop()?;
    Ok(())
}

#[test]
fn real_pipe_subscription_uses_common_client_and_releases_on_cancel()
-> Result<(), Box<dyn std::error::Error>> {
    use icem_transport::emulator_control::{ClientEnd, stream_request};
    let pipe = format!("icem-control-stream-{}", std::process::id());
    let server = ControlServer::bind(&pipe, Arc::new(Status))?;
    for command in ["watch", r#"{"cmd":"watch"}"#] {
        let (_stop, cancel) = tokio::sync::watch::channel(false);
        let mut commands = Vec::new();
        let result = stream_request(
            &pipe,
            command,
            Duration::from_secs(3),
            Duration::from_secs(3),
            cancel,
            |line| {
                let value: serde_json::Value = serde_json::from_str(line)?;
                commands.push(value["cmd"].as_str().unwrap_or("").to_owned());
                Ok(if commands.len() == 3 {
                    std::ops::ControlFlow::Break(())
                } else {
                    std::ops::ControlFlow::Continue(())
                })
            },
        )?;
        assert_eq!(result, ClientEnd::Cancelled);
        assert_eq!(commands, ["watch", "status", "status"]);
    }
    server.stop()?;
    ControlServer::bind(&pipe, Arc::new(Status))?.stop()?;
    Ok(())
}
