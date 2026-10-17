//! Read-only native dashboard adapter for the existing local REST projections.
//! No browser, redirects or remote endpoint discovery. One bounded response at a time.
use http_body_util::{BodyExt, Empty, Limited};
use hyper::{Request, body::Bytes};
use hyper_util::rt::TokioIo;
use serde_json::{Value, json};
use std::{net::SocketAddr, time::Duration};
const MAX_RESPONSE: usize = 16 * 1024 * 1024;
async fn get(endpoint: SocketAddr, path: &str) -> Result<Value, String> {
    if !endpoint.ip().is_loopback() {
        return Err("dashboard endpoint must be loopback".into());
    }
    tokio::time::timeout(Duration::from_secs(3), async {
        let stream = tokio::net::TcpStream::connect(endpoint)
            .await
            .map_err(|e| e.to_string())?;
        let (mut sender, connection) = hyper::client::conn::http1::handshake(TokioIo::new(stream))
            .await
            .map_err(|e| e.to_string())?;
        let task = tokio::spawn(connection);
        let response = async {
            let request = Request::builder()
                .uri(path)
                .header("Host", endpoint.to_string())
                .header("Connection", "close")
                .body(Empty::<Bytes>::new())
                .map_err(|e| e.to_string())?;
            let response = sender
                .send_request(request)
                .await
                .map_err(|e| e.to_string())?;
            let status = response.status();
            let bytes = Limited::new(response.into_body(), MAX_RESPONSE)
                .collect()
                .await
                .map_err(|e| e.to_string())?
                .to_bytes();
            let value: Value =
                serde_json::from_slice(&bytes).map_err(|e| format!("HTTP {status}: {e}"))?;
            if !status.is_success() {
                return Err(format!(
                    "HTTP {status}: {}",
                    value["error"]
                        .as_str()
                        .unwrap_or("dashboard request failed")
                ));
            }
            Ok(value)
        }
        .await;
        task.abort();
        response
    })
    .await
    .map_err(|_| "dashboard request timed out".to_owned())?
}
pub async fn read(endpoint: SocketAddr, selected: &[u16]) -> Result<Value, String> {
    if selected.len() > 298
        || selected
            .iter()
            .any(|a| !(4..600).contains(a) || !a.is_multiple_of(2))
    {
        return Err("invalid dashboard selection".into());
    }
    let registers = get(endpoint, "/api/regs").await?;
    if selected.is_empty() {
        return Ok(json!({"registers":registers,"history":{}}));
    }
    let query = selected
        .iter()
        .map(|a| format!("addr={a}"))
        .collect::<Vec<_>>()
        .join("&");
    match get(endpoint, &format!("/api/regs/history?{query}")).await {
        Ok(history) => Ok(json!({"registers":registers,"history":history})),
        Err(error) => Ok(json!({"registers":registers,"historyError":error})),
    }
}
