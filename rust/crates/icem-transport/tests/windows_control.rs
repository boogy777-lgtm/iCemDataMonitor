#![cfg(windows)]
use icem_emulation::control::{ControlReply, to_json};
use icem_transport::emulator_control::{ControlServer, Executor, request};
use std::{sync::Arc, time::Duration};
struct Status;
impl Executor for Status {
    fn execute(&self, line: &str) -> ControlReply {
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
