//! Internal HTTP/2 control on the EXISTING local pipe only; frozen RPC/web stay unchanged.
use crate::WebState;
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    routing::get,
};
use serde_json::Value;
use std::sync::mpsc;
pub const PATH: &str = "/internal/desktop/v1/logs";
pub struct LogRequest {
    pub action: &'static str,
    pub enabled: Option<bool>,
    pub response: tokio::sync::oneshot::Sender<Value>,
}
type Reply = Result<Json<Value>, (StatusCode, &'static str)>;
impl WebState {
    pub fn enable_log_control(&mut self) -> mpsc::Receiver<LogRequest> {
        let (sender, receiver) = mpsc::sync_channel(16);
        self.log_requests = Some(sender);
        receiver
    }
    async fn logs(&self, action: &'static str, enabled: Option<bool>) -> Reply {
        let mut stopping = self.shutdown.subscribe();
        if *stopping.borrow() {
            return Err((StatusCode::SERVICE_UNAVAILABLE, "Host stopping"));
        }
        let (response, wait) = tokio::sync::oneshot::channel();
        self.log_requests
            .as_ref()
            .ok_or((StatusCode::SERVICE_UNAVAILABLE, "log owner unavailable"))?
            .try_send(LogRequest {
                action,
                enabled,
                response,
            })
            .map_err(|_| (StatusCode::SERVICE_UNAVAILABLE, "log queue full or stopped"))?;
        tokio::select! {
            result=wait => result.map(Json).map_err(|_|(StatusCode::SERVICE_UNAVAILABLE,"log owner stopped; outcome unknown")),
            _=tokio::time::sleep(std::time::Duration::from_secs(5)) => Err((StatusCode::GATEWAY_TIMEOUT,"log operation timed out; outcome unknown; do not retry automatically")),
            _=stopping.changed() => Err((StatusCode::SERVICE_UNAVAILABLE,"Host stopping; outcome unknown")),
        }
    }
}
async fn read(State(state): State<WebState>) -> Reply {
    state.logs("get", None).await
}
async fn set(State(state): State<WebState>, Json(value): Json<Value>) -> Reply {
    let enabled = value["enabled"]
        .as_bool()
        .filter(|_| value.as_object().is_some_and(|v| v.len() == 1))
        .ok_or((StatusCode::BAD_REQUEST, "expected {enabled: bool}"))?;
    state.logs("set", Some(enabled)).await
}
async fn clear(State(state): State<WebState>) -> Reply {
    state.logs("clear", None).await
}
/// Composition must mount this ONLY in LocalRpcServer, never the TCP web router.
pub fn router(state: WebState) -> Router {
    Router::new()
        .route(PATH, get(read).put(set).delete(clear))
        .layer(DefaultBodyLimit::max(1024))
        .with_state(state)
}
#[cfg(windows)]
pub async fn request(pipe: &str, action: &str, enabled: Option<bool>) -> Result<Value, String> {
    use http_body_util::{BodyExt, Full};
    use hyper::Request;
    use tonic::codegen::Bytes;
    if pipe.is_empty() || pipe.contains(['\\', '/', '\0']) {
        return Err("invalid pipe name".into());
    }
    let method = match action {
        "get" => "GET",
        "set" => "PUT",
        "clear" => "DELETE",
        _ => return Err("invalid log action".into()),
    };
    let bytes = if action == "set" {
        serde_json::to_vec(&serde_json::json!({"enabled":enabled.ok_or("missing enabled")?}))
            .map_err(|e| e.to_string())?
    } else {
        Vec::new()
    };
    tokio::time::timeout(std::time::Duration::from_secs(6), async {
        let io = tokio::net::windows::named_pipe::ClientOptions::new()
            .open(format!(r"\\.\pipe\{pipe}"))
            .map_err(|e| e.to_string())?;
        let (mut sender, connection) = hyper::client::conn::http2::handshake(
            hyper_util::rt::TokioExecutor::new(),
            hyper_util::rt::TokioIo::new(io),
        )
        .await
        .map_err(|e| e.to_string())?;
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
            .map_err(|e| e.to_string())?;
        let response = sender
            .send_request(request)
            .await
            .map_err(|e| e.to_string())?;
        let status = response.status();
        let bytes = http_body_util::Limited::new(response.into_body(), 16 * 1024)
            .collect()
            .await
            .map_err(|e| e.to_string())?
            .to_bytes();
        if !status.is_success() {
            return Err(format!("{status}: {}", String::from_utf8_lossy(&bytes)));
        }
        serde_json::from_slice(&bytes).map_err(|e| e.to_string())
    })
    .await
    .map_err(|_| "log request deadline exceeded; outcome unknown; no automatic retry".to_owned())?
    .map_err(|error| {
        if action == "get" {
            error
        } else {
            format!("{error}; результат операции неизвестен, автоматического повтора нет")
        }
    })
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
                    action: "clear",
                    enabled: None,
                    response,
                })?;
        }
        assert!(matches!(
            state.logs("clear", None).await,
            Err((StatusCode::SERVICE_UNAVAILABLE, _))
        ));
        assert_eq!(owner.try_iter().count(), 16);
        Ok(())
    }
}
