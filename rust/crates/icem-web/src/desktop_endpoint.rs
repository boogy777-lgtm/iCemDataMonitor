//! Read-only discovery on the existing local desktop HTTP/2 pipe, never TCP.
use crate::WebState;
#[cfg(any(windows, test))]
use axum::{Json, Router, extract::State, http::StatusCode, routing::get};
#[cfg(any(windows, test))]
use serde::{Deserialize, Serialize};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::{Arc, Mutex};

pub(crate) type EndpointSlot = Arc<Mutex<Option<Publication>>>;

pub(crate) struct Publication {
    address: Option<SocketAddr>,
    owner: Arc<()>,
}

/// The worker owns this guard for precisely the lifetime of its listener.
pub(crate) struct PublishedEndpoint {
    slot: EndpointSlot,
    owner: Arc<()>,
}

impl PublishedEndpoint {
    pub(crate) fn new(state: &WebState, address: SocketAddr) -> std::io::Result<Self> {
        let slot = Arc::clone(&state.desktop_endpoint);
        let owner = Arc::new(());
        *slot
            .lock()
            .map_err(|_| std::io::Error::other("desktop endpoint lock poisoned"))? =
            Some(Publication {
                address: loopback_address(address),
                owner: Arc::clone(&owner),
            });
        Ok(Self { slot, owner })
    }
}

impl Drop for PublishedEndpoint {
    fn drop(&mut self) {
        let mut slot = self.slot.lock().unwrap_or_else(|error| error.into_inner());
        if slot
            .as_ref()
            .is_some_and(|publication| Arc::ptr_eq(&publication.owner, &self.owner))
        {
            *slot = None;
        }
    }
}

#[cfg(any(windows, test))]
const PATH: &str = "/internal/desktop/v1/endpoint";

#[cfg(any(windows, test))]
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Endpoint {
    address: SocketAddr,
}

#[cfg(any(windows, test))]
fn decode(bytes: &[u8]) -> Result<SocketAddr, String> {
    let endpoint: Endpoint = serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
    if !endpoint.address.ip().is_loopback() || endpoint.address.port() == 0 {
        return Err("invalid Host dashboard endpoint".into());
    }
    Ok(endpoint.address)
}

pub(crate) fn loopback_address(address: SocketAddr) -> Option<SocketAddr> {
    let ip = match address.ip() {
        IpAddr::V4(ip) if ip.is_unspecified() => IpAddr::V4(Ipv4Addr::LOCALHOST),
        IpAddr::V6(ip) if ip.is_unspecified() => IpAddr::V6(Ipv6Addr::LOCALHOST),
        ip if ip.is_loopback() => ip,
        _ => return None,
    };
    (address.port() != 0).then(|| SocketAddr::new(ip, address.port()))
}

#[cfg(any(windows, test))]
async fn read(State(state): State<WebState>) -> Result<Json<Endpoint>, StatusCode> {
    let address = state
        .desktop_endpoint
        .lock()
        .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?
        .as_ref()
        .and_then(|publication| publication.address)
        .ok_or(StatusCode::SERVICE_UNAVAILABLE)?;
    Ok(Json(Endpoint { address }))
}

/// This router belongs exclusively to the local pipe, not the public web server.
#[cfg(any(windows, test))]
pub(crate) fn router(state: WebState) -> Router {
    Router::new().route(PATH, get(read)).with_state(state)
}

#[cfg(windows)]
pub async fn request(pipe: &str) -> Result<SocketAddr, String> {
    use http_body_util::{BodyExt, Full};
    use hyper::Request;
    use tonic::codegen::Bytes;
    if pipe.is_empty() || pipe.contains(['\\', '/', '\0']) {
        return Err("invalid pipe name".into());
    }
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
            .uri(format!("http://localhost{PATH}"))
            .body(Full::new(Bytes::new()))
            .map_err(|e| e.to_string())?;
        let response = sender
            .send_request(request)
            .await
            .map_err(|e| e.to_string())?;
        if !response.status().is_success() {
            return Err(format!("Host dashboard endpoint: {}", response.status()));
        }
        let bytes = http_body_util::Limited::new(response.into_body(), 1024)
            .collect()
            .await
            .map_err(|e| e.to_string())?
            .to_bytes();
        decode(&bytes)
    })
    .await
    .map_err(|_| "Host dashboard endpoint deadline exceeded".to_owned())?
}

#[cfg(test)]
mod tests {
    use super::*;
    use tower::ServiceExt;

    #[test]
    fn only_local_listeners_produce_local_nonzero_endpoints()
    -> Result<(), Box<dyn std::error::Error>> {
        for (input, expected) in [
            ("0.0.0.0:1234", Some("127.0.0.1:1234")),
            ("[::]:1234", Some("[::1]:1234")),
            ("127.0.0.1:1234", Some("127.0.0.1:1234")),
            ("192.0.2.1:1234", None),
            ("127.0.0.1:0", None),
        ] {
            assert_eq!(
                loopback_address(input.parse()?),
                expected.map(str::parse).transpose()?
            );
        }
        Ok(())
    }

    #[test]
    fn reply_rejects_remote_missing_zero_and_extra_fields() {
        assert_eq!(
            decode(br#"{"address":"127.0.0.1:8091"}"#).ok(),
            "127.0.0.1:8091".parse().ok()
        );
        for invalid in [
            br#"{"address":"192.0.2.1:80"}"#.as_slice(),
            br#"{"address":"127.0.0.1:0"}"#,
            br#"{}"#,
            br#"{"address":"127.0.0.1:1","extra":true}"#,
        ] {
            assert!(decode(invalid).is_err());
        }
    }

    #[tokio::test]
    async fn old_owner_cannot_clear_successor_even_if_address_is_reused()
    -> Result<(), Box<dyn std::error::Error>> {
        let (state, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let address = "127.0.0.1:8091".parse()?;
        let old = PublishedEndpoint::new(&state, address)?;
        let successor = PublishedEndpoint::new(&state, address)?;
        drop(old);
        assert_eq!(
            read(State(state.clone()))
                .await
                .map_err(|s| s.to_string())?
                .0
                .address,
            address
        );
        drop(successor);
        assert_eq!(
            read(State(state)).await.err(),
            Some(StatusCode::SERVICE_UNAVAILABLE)
        );
        Ok(())
    }

    #[tokio::test]
    async fn endpoint_is_private_and_tracks_actual_bound_port()
    -> Result<(), Box<dyn std::error::Error>> {
        let (state, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let query = || {
            hyper::Request::builder()
                .uri(PATH)
                .body(axum::body::Body::empty())
        };
        assert_eq!(
            router(state.clone()).oneshot(query()?).await?.status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        let server = crate::WebServer::bind("127.0.0.1:0".parse()?, state.clone())?;
        let response = router(state.clone()).oneshot(query()?).await?;
        assert_eq!(response.status(), StatusCode::OK);
        let bytes = http_body_util::BodyExt::collect(response.into_body())
            .await?
            .to_bytes();
        let endpoint: Endpoint = serde_json::from_slice(&bytes)?;
        assert_eq!(endpoint.address, server.local_addr());
        assert_eq!(
            crate::router(state.clone())
                .oneshot(query()?)
                .await?
                .status(),
            StatusCode::NOT_FOUND
        );
        server.stop()?;
        assert_eq!(
            router(state.clone()).oneshot(query()?).await?.status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        let successor = crate::WebServer::bind("127.0.0.1:0".parse()?, state.clone())?;
        let response = router(state.clone()).oneshot(query()?).await?;
        let bytes = http_body_util::BodyExt::collect(response.into_body())
            .await?
            .to_bytes();
        assert_eq!(decode(&bytes)?, successor.local_addr());
        successor.stop()?;
        assert_eq!(
            router(state).oneshot(query()?).await?.status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        Ok(())
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn pipe_reports_unavailable_after_listener_stops_then_publishes_successor()
    -> Result<(), Box<dyn std::error::Error>> {
        let name = format!("icem-endpoint-lifetime-{}", std::process::id());
        let (state, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let first = crate::WebServer::bind("127.0.0.1:0".parse()?, state.clone())?;
        let pipe = crate::LocalRpcServer::bind(&name, state.clone())?;
        assert_eq!(request(&name).await?, first.local_addr());
        first.stop()?;
        assert!(matches!(request(&name).await, Err(error) if error.contains("503")));
        let successor = crate::WebServer::bind("127.0.0.1:0".parse()?, state)?;
        assert_eq!(request(&name).await?, successor.local_addr());
        successor.stop()?;
        assert!(request(&name).await.is_err());
        pipe.stop()?;
        Ok(())
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn pipe_discovery_follows_new_owner_without_cached_endpoint()
    -> Result<(), Box<dyn std::error::Error>> {
        let name = format!("icem-endpoint-test-{}", std::process::id());
        assert!(request(&name).await.is_err());
        let (first, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let first_web = crate::WebServer::bind("127.0.0.1:0".parse()?, first.clone())?;
        let first_pipe = crate::LocalRpcServer::bind(&name, first)?;
        assert_eq!(request(&name).await?, first_web.local_addr());
        first_pipe.stop()?;
        // Keep the first listener bound to guarantee that the successor chooses another port.
        let (second, _) = WebState::new(Default::default(), Default::default(), Default::default());
        let second_web = crate::WebServer::bind("127.0.0.1:0".parse()?, second.clone())?;
        let second_pipe = crate::LocalRpcServer::bind(&name, second)?;
        assert_ne!(first_web.local_addr(), second_web.local_addr());
        assert_eq!(request(&name).await?, second_web.local_addr());
        second_pipe.stop()?;
        second_web.stop()?;
        first_web.stop()?;
        Ok(())
    }
}
