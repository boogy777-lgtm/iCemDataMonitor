use axum::{
    body::Body,
    http::{Request, StatusCode},
};
use http_body_util::BodyExt;
use icem_bus::Snapshot;
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead};
use icem_web::{BusReply, HostLayers, WebState, proto};
use prost::Message;
use serde_json::Value;
use std::{
    collections::BTreeMap,
    sync::{Arc, Mutex},
    time::Duration,
};
use tower::ServiceExt;
type TestResult = Result<(), Box<dyn std::error::Error>>;
fn image() -> Vec<u8> {
    let mut bytes = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
    bytes[64 + 20..64 + 24].fill(0); // finite UI fixture; codec fixture intentionally contains NaN
    bytes
}
fn reply(bytes: &[u8], freshness: Freshness) -> Result<BusReply, Box<dyn std::error::Error>> {
    Ok(BusReply {
        read: Some(SnapshotRead {
            snapshot: Snapshot::decode(bytes)?,
            freshness,
            origin: SnapshotOrigin::Captured,
        }),
        is_open: true,
    })
}
struct Harness {
    state: WebState,
    current: Arc<Mutex<BusReply>>,
    theme: std::path::PathBuf,
}
impl Harness {
    fn new(web: bool) -> Result<Self, Box<dyn std::error::Error>> {
        static NEXT: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
        let theme = std::env::temp_dir().join(format!(
            "icem-web-test-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        let (state, requests) = WebState::new(
            HostLayers {
                web,
                modbus_bound: true,
                modbus_port: 1502,
                ..HostLayers::default()
            },
            theme.clone(),
            BTreeMap::from([(6, "UnitNum".into())]),
        );
        let first = reply(&image(), Freshness::Fresh)?;
        state.observe(&first);
        state.probe(&first, 1_700_000_000_000)?;
        let current = Arc::new(Mutex::new(first));
        let source = Arc::clone(&current);
        let observer = state.clone();
        std::thread::spawn(move || {
            while let Ok(request) = requests.recv_timeout(Duration::from_secs(2)) {
                if let Ok(reply) = source.lock() {
                    observer.observe(&reply);
                    let _ = request.response.send(reply.clone());
                }
            }
        });
        Ok(Self {
            state,
            current,
            theme,
        })
    }
    async fn get(&self, path: &str) -> Result<(StatusCode, Vec<u8>), Box<dyn std::error::Error>> {
        let response = icem_web::router(self.state.clone())
            .oneshot(Request::builder().uri(path).body(Body::empty())?)
            .await?;
        Ok((
            response.status(),
            response.into_body().collect().await?.to_bytes().to_vec(),
        ))
    }
    async fn rpc_response<M: Message>(
        &self,
        method: &str,
        message: M,
    ) -> Result<(axum::http::HeaderMap, Vec<u8>), Box<dyn std::error::Error>> {
        let bytes = message.encode_to_vec();
        let mut frame = vec![0];
        frame.extend_from_slice(&(bytes.len() as u32).to_be_bytes());
        frame.extend(bytes);
        let response = icem_web::router(self.state.clone())
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri(format!("/icemdatamonitor.v1.TelemetryService/{method}"))
                    .header("content-type", "application/grpc-web+proto")
                    .body(Body::from(frame))?,
            )
            .await?;
        assert_eq!(response.status(), StatusCode::OK);
        let headers = response.headers().clone();
        Ok((
            headers,
            response.into_body().collect().await?.to_bytes().to_vec(),
        ))
    }
    async fn rpc<M: Message>(
        &self,
        method: &str,
        message: M,
    ) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        Ok(self.rpc_response(method, message).await?.1)
    }
}
impl Drop for Harness {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.theme);
    }
}
fn data(frame: &[u8]) -> Result<&[u8], Box<dyn std::error::Error>> {
    assert_eq!(frame[0], 0);
    let len = u32::from_be_bytes(frame[1..5].try_into()?) as usize;
    assert_eq!(frame[5 + len], 128);
    assert!(
        String::from_utf8_lossy(&frame[10 + len..]).contains("grpc-status:0")
            || String::from_utf8_lossy(&frame[10 + len..]).contains("grpc-status: 0")
    );
    Ok(&frame[5..5 + len])
}
#[tokio::test]
async fn original_assets_and_route_boundaries_are_preserved() -> TestResult {
    let h = Harness::new(true)?;
    for (path, expected) in [
        (
            "/",
            include_bytes!("../../../web/dashboard/index.html").as_slice(),
        ),
        (
            "/main.css?v=32",
            include_bytes!("../../../web/dashboard/main.css").as_slice(),
        ),
        (
            "/main.js?v=32",
            include_bytes!("../../../web/dashboard/main.js").as_slice(),
        ),
        (
            "/favicon.ico",
            include_bytes!("../../../web/dashboard/favicon.ico").as_slice(),
        ),
        (
            "/favicon.png",
            include_bytes!("../../../web/dashboard/favicon.png").as_slice(),
        ),
    ] {
        let (status, body) = h.get(path).await?;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body, expected);
    }
    for path in [
        "/api/engine",
        "/api/not-a-route",
        "/missing.js",
        "/favicon.ico/../../Cargo.toml",
    ] {
        assert_eq!(h.get(path).await?.0, StatusCode::NOT_FOUND);
    }
    assert_eq!(h.get("/status").await?.0, StatusCode::OK);
    Ok(())
}
#[tokio::test]
async fn web_off_keeps_ops_and_native_grpc_but_disables_spa_history_and_grpc_web() -> TestResult {
    let h = Harness::new(false)?;
    assert_eq!(h.get("/").await?.0, StatusCode::NOT_FOUND);
    assert_eq!(h.get("/api/status").await?.0, StatusCode::OK);
    assert_eq!(h.get("/api/regs").await?.0, StatusCode::OK);
    for query in ["", "?addr=3"] {
        assert_eq!(
            h.get(&format!("/api/regs/history{query}")).await?.0,
            StatusCode::SERVICE_UNAVAILABLE
        );
    }
    let response = icem_web::router(h.state.clone())
        .oneshot(
            Request::builder()
                .method("POST")
                .version(axum::http::Version::HTTP_2)
                .uri("/icemdatamonitor.v1.TelemetryService/GetStatus")
                .header("content-type", "application/grpc")
                .body(Body::from(vec![0, 0, 0, 0, 0]))?,
        )
        .await?;
    let collected = response.into_body().collect().await?;
    assert_eq!(
        collected
            .trailers()
            .and_then(|t| t.get("grpc-status"))
            .ok_or("no native grpc status")?,
        "0"
    );
    let bytes = collected.to_bytes();
    let status = proto::HostStatus::decode(&bytes[5..])?;
    assert!(status.bus_open);
    assert!(!status.layers.ok_or("no layers")?.web);

    Ok(())
}
#[tokio::test]
async fn rest_projection_labels_timestamp_names_and_history_validation() -> TestResult {
    let h = Harness::new(true)?;
    let (code, body) = h.get("/api/regs").await?;
    assert_eq!(code, StatusCode::OK);
    let body: Value = serde_json::from_slice(&body)?;
    assert_eq!(
        body["zones"]["com"]["pairs"][0]["value"],
        "2023-11-14T22:13:20Z"
    );
    assert_eq!(body["zones"]["com"]["pairs"][0]["kind"], "unix");
    assert_eq!(body["zones"]["com"]["pairs"][1]["name"], "UnitNum");
    assert_eq!(body["zones"]["raw"]["pairs"][0]["name"], "field 0");
    assert_eq!(body["system"]["scalars"][2]["label"], "None");
    assert_eq!(
        body["zones"]["com"]["pairs"]
            .as_array()
            .ok_or("not array")?
            .len(),
        98
    );
    assert_eq!(
        body["zones"]["lan"]["pairs"]
            .as_array()
            .ok_or("not array")?
            .len(),
        100
    );
    for query in [
        "",
        "?addr=3",
        "?addr=599",
        "?addr=600",
        "?addr=word",
        "?addr=4&addr=7",
    ] {
        assert_eq!(
            h.get(&format!("/api/regs/history{query}")).await?.0,
            StatusCode::BAD_REQUEST
        );
    }
    let (_, body) = h.get("/api/regs/history?addr=4&addr=200&addr=4").await?;
    let body: Value = serde_json::from_slice(&body)?;
    assert_eq!(body.as_object().ok_or("not object")?.len(), 2);
    assert_eq!(body["4"][0]["v"], 1_700_000_000_f32);
    assert_eq!(body["4"][0]["ts"], "2023-11-14T22:13:20.0000000+00:00");
    Ok(())
}
#[tokio::test]
async fn history_fifo_stale_skip_and_health_decay_match_source() -> TestResult {
    let h = Harness::new(true)?;
    let mut bytes = image();
    for tick in 0..605 {
        let stamp = (tick as u32).to_le_bytes();
        let bits = u32::from_le_bytes(stamp);
        let lo = (bits as u16).to_be_bytes();
        let hi = ((bits >> 16) as u16).to_be_bytes();
        bytes[72..74].copy_from_slice(&lo);
        bytes[74..76].copy_from_slice(&hi);
        let r = reply(&bytes, Freshness::Fresh)?;
        h.state.probe(&r, 1_700_000_000_000 + tick * 1000)?;
    }
    let r = reply(&bytes, Freshness::LastGood)?;
    h.state.probe(&r, 1_800_000_000_000)?;
    let (_, body) = h.get("/api/regs/history?addr=4").await?;
    let body: Value = serde_json::from_slice(&body)?;
    let rows = body["4"].as_array().ok_or("not array")?;
    assert_eq!(rows.len(), 600);
    assert_eq!(rows[0]["v"], 5.0);
    assert_eq!(rows[599]["v"], 604.0);
    let (_, body) = h.get("/api/regs").await?;
    let body: Value = serde_json::from_slice(&body)?;
    assert_eq!(body["stale"], true);
    assert!(body["updatedAtUtc"].is_null());
    Ok(())
}
#[tokio::test]
async fn grpc_registers_fresh_at_call_oneof_and_theme_truth() -> TestResult {
    let h = Harness::new(true)?;
    let frame = h
        .rpc("GetRegisters", proto::RegistersRequest { zone: 1 })
        .await?;
    let values = proto::RegistersReply::decode(data(&frame)?)?.values;
    assert_eq!(values.len(), 98);
    assert_eq!(values[0].addr, 4);
    assert_eq!(
        values[0].value,
        Some(proto::register_value::Value::Int32Value(1_700_000_000))
    );
    assert_eq!(
        values[1].value,
        Some(proto::register_value::Value::FloatValue(1.0))
    );
    let set = h
        .rpc("SetUiTheme", proto::SetUiThemeRequest { theme_light: true })
        .await?;
    assert!(proto::UiPrefs::decode(data(&set)?)?.theme_light);
    std::fs::write(&h.theme, "dark")?;
    let get = h.rpc("GetUiPrefs", proto::UiPrefsRequest {}).await?;
    assert!(!proto::UiPrefs::decode(data(&get)?)?.theme_light);
    let (headers, _) = h
        .rpc_response("GetRegisters", proto::RegistersRequest { zone: 99 })
        .await?;
    assert_eq!(
        headers.get("grpc-status").ok_or("missing error status")?,
        "3"
    );
    h.current.lock().map_err(|_| "poisoned")?.is_open = false;
    let (headers, _) = h
        .rpc_response("GetRegisters", proto::RegistersRequest { zone: 0 })
        .await?;
    assert_eq!(
        headers
            .get("grpc-status")
            .ok_or("missing unavailable status")?,
        "14"
    );

    Ok(())
}
#[tokio::test]
async fn nonfinite_rest_is_failure_while_protobuf_preserves_ieee() -> TestResult {
    let h = Harness::new(true)?;
    let bad = reply(
        include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin"),
        Freshness::Fresh,
    )?;
    h.state.observe(&bad);
    *h.current.lock().map_err(|_| "poisoned")? = bad;
    assert_eq!(
        h.get("/api/regs").await?.0,
        StatusCode::INTERNAL_SERVER_ERROR
    );
    let frame = h
        .rpc("GetRegisters", proto::RegistersRequest { zone: 1 })
        .await?;
    let value = proto::RegistersReply::decode(data(&frame)?)?
        .values
        .into_iter()
        .find(|v| v.addr == 10)
        .and_then(|x| x.value)
        .ok_or("no NaN")?;
    assert!(matches!(value,proto::register_value::Value::FloatValue(v) if v.is_nan()));
    Ok(())
}

async fn next_web_frame(
    body: &mut Body,
    pending: &mut Vec<u8>,
) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    loop {
        if pending.len() >= 5 {
            let length = u32::from_be_bytes(pending[1..5].try_into()?) as usize;
            if pending.len() >= 5 + length {
                return Ok(pending.drain(..5 + length).collect());
            }
        }
        let frame = tokio::time::timeout(Duration::from_secs(3), body.frame())
            .await?
            .ok_or("unexpected EOS")??;
        if let Ok(bytes) = frame.into_data() {
            pending.extend_from_slice(&bytes);
        }
    }
}
#[tokio::test]
async fn metrics_full_then_deltas_clamp_and_unavailable_trailer() -> TestResult {
    let h = Harness::new(true)?;
    let response = icem_web::router(h.state.clone())
        .oneshot(
            Request::builder()
                .method("POST")
                .uri("/icemdatamonitor.v1.TelemetryService/SubscribeMetrics")
                .header("content-type", "application/grpc-web+proto")
                .body(Body::from(vec![0, 0, 0, 0, 2, 8, 1]))?,
        )
        .await?;
    let mut body = response.into_body();
    let mut pending = Vec::new();
    let frame = next_web_frame(&mut body, &mut pending).await?;
    let sample = proto::MetricsSample::decode(&frame[5..])?;
    assert!(sample.interval_clamped);
    assert_eq!(sample.deltas.len(), 302);
    let frame = next_web_frame(&mut body, &mut pending).await?;
    assert!(proto::MetricsSample::decode(&frame[5..])?.deltas.is_empty());
    let mut bytes = image();
    bytes[64 + 12..64 + 16].copy_from_slice(&[0, 0, 0x40, 0]);
    *h.current.lock().map_err(|_| "poisoned")? = reply(&bytes, Freshness::Fresh)?;
    let frame = next_web_frame(&mut body, &mut pending).await?;
    let sample = proto::MetricsSample::decode(&frame[5..])?;
    assert_eq!(sample.deltas.len(), 1);
    assert_eq!(sample.deltas[0].addr, 6);
    assert_eq!(
        sample.deltas[0].value,
        Some(proto::register_delta::Value::FloatValue(2.0))
    );
    h.current.lock().map_err(|_| "poisoned")?.is_open = false;
    let frame = next_web_frame(&mut body, &mut pending).await?;
    assert_eq!(frame[0], 128);
    assert!(
        String::from_utf8_lossy(&frame[5..])
            .replace(": ", ":")
            .contains("grpc-status:14")
    );
    Ok(())
}
#[test]
fn listener_bind_failure_and_drop_release_port() -> TestResult {
    let h = Harness::new(true)?;
    let server = icem_web::WebServer::bind("127.0.0.1:0".parse()?, h.state.clone())?;
    let addr = server.local_addr();
    assert!(icem_web::WebServer::bind(addr, h.state.clone()).is_err());
    server.stop()?;
    let listener = std::net::TcpListener::bind(addr)?;
    drop(listener);
    Ok(())
}

#[tokio::test]
async fn native_dashboard_reads_existing_rest_shapes_and_history_errors() -> TestResult {
    for enabled in [true, false] {
        let h = Harness::new(enabled)?;
        let server = icem_web::WebServer::bind("127.0.0.1:0".parse()?, h.state.clone())?;
        let data = icem_web::dashboard_client::read(server.local_addr(), &[4, 6])
            .await
            .map_err(std::io::Error::other)?;
        assert!(data["registers"]["system"]["scalars"].is_array());
        assert_eq!(
            data["registers"]["zones"]["com"]["pairs"][1]["name"],
            "UnitNum"
        );
        if enabled {
            assert!(data["history"]["6"].is_array());
            assert!(data["historyError"].is_null());
        } else {
            assert!(
                data["historyError"]
                    .as_str()
                    .is_some_and(|e| e.contains("503"))
            );
        }
        server.stop()?;
    }
    assert!(
        icem_web::dashboard_client::read("192.0.2.1:80".parse()?, &[])
            .await
            .is_err()
    );
    assert!(
        icem_web::dashboard_client::read("127.0.0.1:1".parse()?, &[5])
            .await
            .is_err()
    );
    Ok(())
}
