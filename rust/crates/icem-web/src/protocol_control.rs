//! Typed MQTT/OPC UA/OPC DA control, exclusively on the local Host pipe.
use crate::WebState;
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    routing::get,
};
use serde::{Deserialize, Serialize};
use std::sync::mpsc;

pub const PATH: &str = "/internal/desktop/v1/protocols";
pub const MAX_ERROR_BYTES: usize = 512;

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Protocol {
    Mqtt,
    OpcUa,
    OpcDa,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Command {
    Get,
    Set { protocol: Protocol, enabled: bool },
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct Status {
    pub configured: bool,
    pub enabled: bool,
    pub running: bool,
    pub error: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct Reply {
    pub ok: bool,
    pub mqtt: Status,
    pub opcua: Status,
    pub opcda: Status,
}

impl Reply {
    /// A successful mutation must be confirmed by the actual owner state.
    pub fn validate(&self, command: Command) -> Result<(), String> {
        for status in [&self.mqtt, &self.opcua, &self.opcda] {
            if status.error.len() > MAX_ERROR_BYTES {
                return Err("protocol error exceeds size limit".into());
            }
            if (!status.configured && (status.enabled || status.running))
                || (status.running && !status.enabled)
            {
                return Err("contradictory protocol status".into());
            }
        }
        if let Command::Set { protocol, enabled } = command {
            let status = match protocol {
                Protocol::Mqtt => &self.mqtt,
                Protocol::OpcUa => &self.opcua,
                Protocol::OpcDa => &self.opcda,
            };
            if self.ok
                && (!status.configured || status.enabled != enabled || (enabled && !status.running))
            {
                return Err("unconfirmed protocol operation".into());
            }
        }
        Ok(())
    }
}

pub struct ProtocolRequest {
    pub command: Command,
    pub response: tokio::sync::oneshot::Sender<Reply>,
}

impl WebState {
    pub fn enable_protocol_control(&mut self) -> mpsc::Receiver<ProtocolRequest> {
        let (sender, receiver) = mpsc::sync_channel(16);
        self.protocol_requests = Some(sender);
        receiver
    }
    async fn protocols(&self, command: Command) -> Result<Json<Reply>, (StatusCode, &'static str)> {
        let mut stopping = self.shutdown.subscribe();
        if *stopping.borrow() {
            return Err((StatusCode::SERVICE_UNAVAILABLE, "Host stopping"));
        }
        let (response, wait) = tokio::sync::oneshot::channel();
        self.protocol_requests
            .as_ref()
            .ok_or((
                StatusCode::SERVICE_UNAVAILABLE,
                "protocol owner unavailable",
            ))?
            .try_send(ProtocolRequest { command, response })
            .map_err(|_| {
                (
                    StatusCode::SERVICE_UNAVAILABLE,
                    "protocol queue full or stopped",
                )
            })?;
        tokio::select! {
            result = wait => {
                let reply = result.map_err(|_| (StatusCode::SERVICE_UNAVAILABLE, "owner stopped; outcome unknown"))?;
                reply.validate(command).map_err(|_| (StatusCode::BAD_GATEWAY, "invalid owner acknowledgement"))?;
                Ok(Json(reply))
            },
            _ = tokio::time::sleep(std::time::Duration::from_secs(5)) => Err((StatusCode::GATEWAY_TIMEOUT, "outcome unknown; do not retry automatically")),
            _ = stopping.changed() => Err((StatusCode::SERVICE_UNAVAILABLE, "Host stopping; outcome unknown")),
        }
    }
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct SetRequest {
    protocol: Protocol,
    enabled: bool,
}
async fn read(State(state): State<WebState>) -> Result<Json<Reply>, (StatusCode, &'static str)> {
    state.protocols(Command::Get).await
}
async fn set(
    State(state): State<WebState>,
    Json(request): Json<SetRequest>,
) -> Result<Json<Reply>, (StatusCode, &'static str)> {
    state
        .protocols(Command::Set {
            protocol: request.protocol,
            enabled: request.enabled,
        })
        .await
}
pub(crate) fn router(state: WebState) -> Router {
    Router::new()
        .route(PATH, get(read).put(set))
        .layer(DefaultBodyLimit::max(1024))
        .with_state(state)
}

#[cfg(windows)]
pub async fn request(pipe: &str, command: Command) -> Result<Reply, String> {
    use http_body_util::{BodyExt, Full};
    use hyper::Request;
    use tonic::codegen::Bytes;
    if pipe.is_empty() || pipe.contains(['\\', '/', '\0']) {
        return Err("invalid pipe name".into());
    }
    let (method, body) = match command {
        Command::Get => ("GET", Vec::new()),
        Command::Set { protocol, enabled } => (
            "PUT",
            serde_json::to_vec(&SetRequest { protocol, enabled }).map_err(|e| e.to_string())?,
        ),
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
            .body(Full::new(Bytes::from(body)))
            .map_err(|e| e.to_string())?;
        let response = sender
            .send_request(request)
            .await
            .map_err(|e| format!("outcome unknown: {e}"))?;
        if !response.status().is_success() {
            return Err(format!(
                "protocol control: {}; outcome unknown",
                response.status()
            ));
        }
        let bytes = http_body_util::Limited::new(response.into_body(), 16 * 1024)
            .collect()
            .await
            .map_err(|e| e.to_string())?
            .to_bytes();
        let reply: Reply = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        reply.validate(command)?;
        Ok(reply)
    })
    .await
    .map_err(|_| "protocol control deadline; outcome unknown".to_owned())?
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn acknowledgement_requires_actual_running_and_configuration() {
        let off = Status {
            configured: false,
            enabled: false,
            running: false,
            error: "--mqtt-config required".into(),
        };
        let mut reply = Reply {
            ok: true,
            mqtt: off.clone(),
            opcua: off.clone(),
            opcda: off,
        };
        let set = Command::Set {
            protocol: Protocol::Mqtt,
            enabled: true,
        };
        assert!(reply.validate(set).is_err());
        reply.mqtt.configured = true;
        reply.mqtt.enabled = true;
        assert!(reply.validate(set).is_err());
        reply.mqtt.running = true;
        assert!(reply.validate(set).is_ok());
        reply.mqtt.enabled = false;
        assert!(reply.validate(Command::Get).is_err());
    }
    #[tokio::test]
    async fn private_route_not_public_and_missing_owner_fails()
    -> Result<(), Box<dyn std::error::Error>> {
        use tower::ServiceExt;
        let (state, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let req = || {
            hyper::Request::builder()
                .uri(PATH)
                .body(axum::body::Body::empty())
        };
        assert_eq!(
            router(state.clone()).oneshot(req()?).await?.status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        assert_eq!(
            crate::router(state).oneshot(req()?).await?.status(),
            StatusCode::NOT_FOUND
        );
        Ok(())
    }

    #[tokio::test]
    async fn malformed_requests_never_reach_owner() -> Result<(), Box<dyn std::error::Error>> {
        use tower::ServiceExt;
        let (mut state, _) =
            WebState::new(Default::default(), Default::default(), Default::default());
        let receiver = state.enable_protocol_control();
        for body in [
            r#"{"protocol":"mqtt"}"#,
            r#"{"protocol":"other","enabled":true}"#,
            r#"{"protocol":"mqtt","enabled":true,"extra":1}"#,
        ] {
            let req = hyper::Request::builder()
                .method("PUT")
                .uri(PATH)
                .header("content-type", "application/json")
                .body(axum::body::Body::from(body))?;
            assert!(
                !router(state.clone())
                    .oneshot(req)
                    .await?
                    .status()
                    .is_success()
            );
        }
        assert!(receiver.try_recv().is_err());
        Ok(())
    }

    #[test]
    fn every_protocol_checks_its_own_acknowledgement() {
        let off = Status {
            configured: true,
            enabled: false,
            running: false,
            error: String::new(),
        };
        for protocol in [Protocol::Mqtt, Protocol::OpcUa, Protocol::OpcDa] {
            let mut reply = Reply {
                ok: true,
                mqtt: off.clone(),
                opcua: off.clone(),
                opcda: off.clone(),
            };
            let command = Command::Set {
                protocol,
                enabled: true,
            };
            assert!(reply.validate(command).is_err());
            let status = match protocol {
                Protocol::Mqtt => &mut reply.mqtt,
                Protocol::OpcUa => &mut reply.opcua,
                Protocol::OpcDa => &mut reply.opcda,
            };
            status.enabled = true;
            status.running = true;
            assert!(reply.validate(command).is_ok());
        }
    }

    #[test]
    fn oversized_diagnostics_are_rejected() {
        let status = Status {
            configured: false,
            enabled: false,
            running: false,
            error: "x".repeat(MAX_ERROR_BYTES),
        };
        let mut reply = Reply {
            ok: true,
            mqtt: status.clone(),
            opcua: status.clone(),
            opcda: status,
        };
        assert!(reply.validate(Command::Get).is_ok());
        reply.opcua.error.push('x');
        assert!(reply.validate(Command::Get).is_err());
    }

    #[tokio::test]
    async fn shutdown_and_full_queue_reject_before_acceptance()
    -> Result<(), Box<dyn std::error::Error>> {
        let (mut state, _) =
            WebState::new(Default::default(), Default::default(), Default::default());
        let owner = state.enable_protocol_control();
        let mut waits = Vec::new();
        for _ in 0..16 {
            let (response, wait) = tokio::sync::oneshot::channel();
            state
                .protocol_requests
                .as_ref()
                .ok_or("missing sender")?
                .try_send(ProtocolRequest {
                    command: Command::Get,
                    response,
                })?;
            waits.push(wait);
        }
        assert!(matches!(
            state.protocols(Command::Get).await,
            Err((StatusCode::SERVICE_UNAVAILABLE, _))
        ));
        assert_eq!(owner.try_iter().count(), 16);
        state.shutdown.send_replace(true);
        assert!(matches!(
            state.protocols(Command::Get).await,
            Err((StatusCode::SERVICE_UNAVAILABLE, _))
        ));
        assert!(owner.try_recv().is_err());
        Ok(())
    }

    #[test]
    fn cancelled_queued_request_is_observable_before_owner_execution()
    -> Result<(), Box<dyn std::error::Error>> {
        let (mut state, _) =
            WebState::new(Default::default(), Default::default(), Default::default());
        let owner = state.enable_protocol_control();
        let (response, wait) = tokio::sync::oneshot::channel();
        state
            .protocol_requests
            .as_ref()
            .ok_or("missing sender")?
            .try_send(ProtocolRequest {
                command: Command::Set {
                    protocol: Protocol::OpcDa,
                    enabled: true,
                },
                response,
            })?;
        drop(wait);
        assert!(owner.try_recv()?.response.is_closed());
        Ok(())
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn owned_pipe_roundtrips_all_protocols_and_does_not_replay_lost_ack()
    -> Result<(), Box<dyn std::error::Error>> {
        use std::time::{Duration, SystemTime, UNIX_EPOCH};
        let name = format!(
            "icem-protocol-control-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos()
        );
        let (mut state, _) =
            WebState::new(Default::default(), Default::default(), Default::default());
        let owner = state.enable_protocol_control();
        let server = crate::LocalRpcServer::bind(&name, state)?;
        let thread = std::thread::spawn(move || -> Result<Vec<Command>, String> {
            let off = Status {
                configured: true,
                enabled: false,
                running: false,
                error: String::new(),
            };
            let mut reply = Reply {
                ok: true,
                mqtt: off.clone(),
                opcua: off.clone(),
                opcda: off,
            };
            let mut seen = Vec::new();
            for index in 0..8 {
                let request = owner
                    .recv_timeout(Duration::from_secs(10))
                    .map_err(|e| e.to_string())?;
                if request.response.is_closed() {
                    return Err("unexpected cancelled request".into());
                }
                seen.push(request.command);
                if index == 7 {
                    drop(request.response);
                    continue;
                }
                if let Command::Set { protocol, enabled } = request.command {
                    let status = match protocol {
                        Protocol::Mqtt => &mut reply.mqtt,
                        Protocol::OpcUa => &mut reply.opcua,
                        Protocol::OpcDa => &mut reply.opcda,
                    };
                    status.enabled = enabled;
                    status.running = enabled;
                }
                request
                    .response
                    .send(reply.clone())
                    .map_err(|_| "reply lost".to_owned())?;
            }
            if owner.recv_timeout(Duration::from_millis(100)).is_ok() {
                return Err("unexpected replay".into());
            }
            Ok(seen)
        });
        assert!(request(&name, Command::Get).await?.ok);
        for protocol in [Protocol::Mqtt, Protocol::OpcUa, Protocol::OpcDa] {
            for enabled in [true, false] {
                let command = Command::Set { protocol, enabled };
                let reply = request(&name, command).await?;
                assert!(reply.ok);
                reply.validate(command)?;
            }
        }
        assert!(
            request(
                &name,
                Command::Set {
                    protocol: Protocol::Mqtt,
                    enabled: true
                }
            )
            .await
            .is_err()
        );
        let seen = thread.join().map_err(|_| "fake owner panicked")??;
        assert_eq!(seen.len(), 8);
        assert_eq!(
            seen[7],
            Command::Set {
                protocol: Protocol::Mqtt,
                enabled: true
            }
        );
        server.stop()?;
        Ok(())
    }
}
