//! Internal HTTP/2 control on the EXISTING local pipe only; frozen RPC/web stay unchanged.
use crate::WebState;
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    routing::get,
};
use icem_logging::{Command, ControlError, Outcome, Reply as LogReply, Status};
use serde_json::{Value, json};
use std::sync::mpsc;
pub const PATH: &str = "/internal/desktop/v1/logs";
pub struct LogRequest {
    pub command: Command,
    pub response: tokio::sync::oneshot::Sender<LogReply>,
}
type Reply = Result<Json<Value>, (StatusCode, &'static str)>;
impl WebState {
    pub fn enable_log_control(&mut self) -> mpsc::Receiver<LogRequest> {
        let (sender, receiver) = mpsc::sync_channel(16);
        self.log_requests = Some(sender);
        receiver
    }
    async fn logs(&self, command: Command) -> Reply {
        let mut stopping = self.shutdown.subscribe();
        if *stopping.borrow() {
            return Err((StatusCode::SERVICE_UNAVAILABLE, "Host stopping"));
        }
        let (response, wait) = tokio::sync::oneshot::channel();
        self.log_requests
            .as_ref()
            .ok_or((StatusCode::SERVICE_UNAVAILABLE, "log owner unavailable"))?
            .try_send(LogRequest { command, response })
            .map_err(|_| (StatusCode::SERVICE_UNAVAILABLE, "log queue full or stopped"))?;
        tokio::select! {
            result=wait => result.map(|reply| Json(encode_reply(&reply))).map_err(|_|(StatusCode::SERVICE_UNAVAILABLE,"log owner stopped; outcome unknown")),
            _=tokio::time::sleep(std::time::Duration::from_secs(5)) => Err((StatusCode::GATEWAY_TIMEOUT,"log operation timed out; outcome unknown; do not retry automatically")),
            _=stopping.changed() => Err((StatusCode::SERVICE_UNAVAILABLE,"Host stopping; outcome unknown")),
        }
    }
}
async fn read(State(state): State<WebState>) -> Reply {
    state.logs(Command::Get).await
}
async fn set(State(state): State<WebState>, Json(value): Json<Value>) -> Reply {
    let enabled = value["enabled"]
        .as_bool()
        .filter(|_| value.as_object().is_some_and(|v| v.len() == 1))
        .ok_or((StatusCode::BAD_REQUEST, "expected {enabled: bool}"))?;
    state.logs(Command::SetEnabled(enabled)).await
}
async fn clear(State(state): State<WebState>) -> Reply {
    state.logs(Command::Clear).await
}
/// Composition must mount this ONLY in LocalRpcServer, never the TCP web router.
pub fn router(state: WebState) -> Router {
    Router::new()
        .route(PATH, get(read).put(set).delete(clear))
        .layer(DefaultBodyLimit::max(1024))
        .with_state(state)
}
/// The frozen internal v1 representation has exactly these seven status keys.
pub fn encode_reply(reply: &LogReply) -> Value {
    let s = &reply.status;
    json!({"ok": reply.outcome != Outcome::Incomplete, "data": {
        "enabled": s.enabled, "available": s.available, "recording": s.recording,
        "directory": s.directory, "error": s.error, "operation": s.operation, "written": s.written
    }})
}
/// Validate every required fact. No missing/null coercion, text parsing, or inferred clear counts.
pub fn decode_reply(value: &Value, command: Command) -> Result<LogReply, ControlError> {
    let invalid = || ControlError::unknown("Неверный ответ владельца логов");
    let ok = value
        .get("ok")
        .and_then(Value::as_bool)
        .ok_or_else(invalid)?;
    let data = value
        .get("data")
        .and_then(Value::as_object)
        .ok_or_else(invalid)?;
    let enabled = match data.get("enabled") {
        Some(Value::Null) => None,
        Some(Value::Bool(v)) => Some(*v),
        _ => return Err(invalid()),
    };
    let status = Status {
        enabled,
        available: data
            .get("available")
            .and_then(Value::as_bool)
            .ok_or_else(invalid)?,
        recording: data
            .get("recording")
            .and_then(Value::as_bool)
            .ok_or_else(invalid)?,
        directory: data
            .get("directory")
            .and_then(Value::as_str)
            .ok_or_else(invalid)?
            .into(),
        error: data
            .get("error")
            .and_then(Value::as_str)
            .ok_or_else(invalid)?
            .into(),
        operation: data
            .get("operation")
            .and_then(Value::as_str)
            .ok_or_else(invalid)?
            .into(),
        written: data
            .get("written")
            .and_then(Value::as_u64)
            .ok_or_else(invalid)?,
    };
    if ok
        && let Command::SetEnabled(enabled) = command
        && (status.enabled != Some(enabled) || (enabled && !status.recording))
    {
        return Err(invalid());
    }
    if !status.consistent() {
        return Err(invalid());
    }
    Ok(LogReply {
        outcome: if !ok {
            Outcome::Incomplete
        } else if command == Command::Get {
            Outcome::Completed
        } else {
            Outcome::Applied
        },
        status,
        clear: None,
    })
}
#[cfg(windows)]
pub async fn request(pipe: &str, command: Command) -> Result<LogReply, ControlError> {
    use http_body_util::{BodyExt, Full};
    use hyper::Request;
    use tonic::codegen::Bytes;
    if pipe.is_empty() || pipe.contains(['\\', '/', '\0']) {
        return Err(ControlError::rejected("invalid pipe name"));
    }
    let (method, bytes) = match command {
        Command::Get => ("GET", Vec::new()),
        Command::SetEnabled(enabled) => {
            ("PUT", json!({"enabled":enabled}).to_string().into_bytes())
        }
        Command::Clear => ("DELETE", Vec::new()),
    };
    tokio::time::timeout(std::time::Duration::from_secs(6), async {
        let io = tokio::net::windows::named_pipe::ClientOptions::new()
            .open(format!(r"\\.\pipe\{pipe}"))
            .map_err(|e| ControlError::rejected(e.to_string()))?;
        let (mut sender, connection) = hyper::client::conn::http2::handshake(
            hyper_util::rt::TokioExecutor::new(),
            hyper_util::rt::TokioIo::new(io),
        )
        .await
        .map_err(|e| ControlError::rejected(e.to_string()))?;
        struct Connection(tokio::task::JoinHandle<()>);
        impl Drop for Connection {
            fn drop(&mut self) {
                self.0.abort();
            }
        }
        let _connection = Connection(tokio::spawn(async move {
            let _ = connection.await;
        }));
        let request = Request::builder()
            .method(method)
            .uri(format!("http://localhost{PATH}"))
            .header("content-type", "application/json")
            .body(Full::new(Bytes::from(bytes)))
            .map_err(|e| ControlError::unknown(e.to_string()))?;
        let response = sender
            .send_request(request)
            .await
            .map_err(|e| ControlError::unknown(e.to_string()))?;
        let status = response.status();
        let bytes = http_body_util::Limited::new(response.into_body(), 16 * 1024)
            .collect()
            .await
            .map_err(|e| ControlError::unknown(e.to_string()))?
            .to_bytes();
        if !status.is_success() {
            return Err(ControlError::unknown(format!(
                "{status}: {}",
                String::from_utf8_lossy(&bytes)
            )));
        }
        let value: Value =
            serde_json::from_slice(&bytes).map_err(|e| ControlError::unknown(e.to_string()))?;
        decode_reply(&value, command)
    })
    .await
    .map_err(|_| ControlError::unknown("log request deadline exceeded"))?
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn full_control_queue_refuses_without_wait_or_automatic_retry()
    -> Result<(), Box<dyn std::error::Error>> {
        let (mut state, _) = WebState::new(
            crate::HostLayers::default(),
            std::path::PathBuf::new(),
            Default::default(),
        );
        let owner = state.enable_log_control();
        let mut waits = Vec::new();
        for _ in 0..16 {
            let (response, wait) = tokio::sync::oneshot::channel();
            waits.push(wait);
            state
                .log_requests
                .as_ref()
                .ok_or("no sender")?
                .try_send(LogRequest {
                    command: Command::Clear,
                    response,
                })?;
        }
        assert!(matches!(
            state.logs(Command::Clear).await,
            Err((StatusCode::SERVICE_UNAVAILABLE, _))
        ));
        assert_eq!(owner.try_iter().count(), 16);
        Ok(())
    }
}
