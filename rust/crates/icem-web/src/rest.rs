use crate::WebState;
use crate::model::{dotnet_ticks, history_iso, iso, now_ms};
use axum::{
    Json, Router,
    extract::{RawQuery, State},
    http::{StatusCode, Uri, header},
    response::{IntoResponse, Response},
    routing::get,
};
use icem_bus::{Snapshot, Zone};
use icem_core::Freshness;
use serde_json::{Value, json};

pub fn router(state: WebState) -> Router {
    use tower::Layer;
    let routes = Router::new()
        .route("/api/status", get(status))
        .route("/api/status/summary", get(summary))
        .route("/api/regs", get(registers))
        .route("/api/regs/history", get(history));
    let routes = if state.web_enabled {
        routes.route_service(
            "/icemdatamonitor.v1.TelemetryService/{method}",
            tonic_web::GrpcWebLayer::new().layer(crate::rpc::service(state.clone())),
        )
    } else {
        routes.route_service(
            "/icemdatamonitor.v1.TelemetryService/{method}",
            crate::rpc::service(state.clone()),
        )
    };
    routes.fallback(assets).with_state(state)
}

fn error(code: StatusCode, message: impl Into<String>) -> Response {
    (code, Json(json!({"error":message.into()}))).into_response()
}
async fn status(State(state): State<WebState>) -> Response {
    status_inner(&state, false)
}
async fn summary(State(state): State<WebState>) -> Response {
    status_inner(&state, true)
}
fn status_inner(state: &WebState, summary: bool) -> Response {
    let Ok(layers) = state.layers() else {
        return error(StatusCode::INTERNAL_SERVER_ERROR, "layer lock poisoned");
    };
    let Ok(store) = state.store.lock() else {
        return error(StatusCode::INTERNAL_SERVER_ERROR, "web store poisoned");
    };
    let h = &store.health;
    let mut result = json!({"busOpen":h.bus_open,"addInAlive":h.alive,"heartbeat":h.header[3],"zones":{
        "com":{"active":h.zones[0].0,"ageMs":h.zones[0].1},
        "lan":{"active":h.zones[1].0,"ageMs":h.zones[1].1},
        "raw":{"active":h.zones[2].0,"ageMs":h.zones[2].1}},"updatedAtUtc":iso(h.at)});
    if summary {
        result["layers"] = json!({"modbus":layers.modbus_bound,"modbusPort":layers.modbus_port,"sniffer":layers.sniffer.as_ref().is_some_and(|s|s.running),"opcDa":layers.opc_da,"setAsDefault":layers.set_as_default});
        result["snifferStats"] = layers.sniffer.as_ref().map_or(Value::Null, |s| json!({
            "up":s.available,"state":s.state,"totalFrames":s.total_frames,"aceFrames":s.ace_frames,
            "framesPerSec":s.frames_per_sec,"lastFrameAgeSec":s.last_frame_age_sec,
            "distinctMacs":s.distinct_macs,"macLimitReached":s.mac_limit_reached,"adapter":s.adapter
        }));
    } else {
        result["comPort"] = json!(h.header[0]);
        result["lanStatus"] = json!(h.header[2]);
    }
    Json(result).into_response()
}
async fn registers(State(state): State<WebState>) -> Response {
    let cached = match state.store.lock() {
        Ok(store) => store.last.clone(),
        Err(_) => return error(StatusCode::INTERNAL_SERVER_ERROR, "web store poisoned"),
    };
    let read = match cached {
        Some(read) => Some(read),
        None => match state.read().await {
            Ok(reply) => reply.read,
            Err(_) => None,
        },
    };
    let Some(read) = read else {
        return error(StatusCode::SERVICE_UNAVAILABLE, "bus not available");
    };
    let snapshot = &read.snapshot;
    let at = now_ms();
    let names = [
        "COM port active",
        "Case state",
        "COM/LAN port state",
        "Heartbeat",
    ];
    let scalars: Vec<_> = names
        .into_iter()
        .enumerate()
        .map(|(reg, name)| {
            let value = snapshot.words()[reg];
            let label = scalar_label(reg, value);
            json!({"reg":reg,"name":name,"value":value,"label":label})
        })
        .collect();
    let mut zones = serde_json::Map::new();
    let mut liveness = serde_json::Map::new();
    for (zone, key, label) in [
        (Zone::Com, "com", "COM"),
        (Zone::Lan, "lan", "LAN"),
        (Zone::Raw, "raw", "RAW"),
    ] {
        let (base, end) = zone.range();
        let pairs:Vec<_> = (base..end).step_by(2).enumerate().map(|(idx,addr)| {
            let name = if zone == Zone::Raw { Some(format!("field {idx}")) } else { state.names.get(&(addr as u16)).cloned() };
            let (value,kind) = pair_json(snapshot,addr as u16);
            json!({"idx":idx,"addr":addr,"addrEnd":addr+1,"name":name,"value":value,"kind":kind})
        }).collect();
        zones.insert(
            key.into(),
            json!({"label":label,"baseReg":base,"len":end-base,"pairs":pairs}),
        );
        let z = snapshot.zone(zone);
        liveness.insert(key.into(),json!({"active":z.is_live(dotnet_ticks(at)),"producerFlag":z.producer_active,"ageMs":z.age_ms(dotnet_ticks(at)),"writeCount":z.write_count}));
    }
    // ASP.NET default JSON refuses non-finite floats. Preserve failure rather than
    // silently replacing them with JSON null (serde_json's default float conversion).
    if (6..600)
        .step_by(2)
        .any(|addr| snapshot.float_pair(addr).is_ok_and(|v| !v.is_finite()))
    {
        return error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "non-finite register value cannot be serialized",
        );
    }
    Json(json!({"system":{"scalars":scalars,"liveness":null},"zones":zones,"liveness":liveness,
        "updatedAtUtc":if read.freshness == Freshness::Fresh {Some(iso(at))} else {None},"stale":read.freshness==Freshness::LastGood})).into_response()
}
fn pair_json(snapshot: &Snapshot, addr: u16) -> (Value, &'static str) {
    if addr == 4 {
        let seconds = snapshot.unix_seconds().unwrap_or_default();
        let text = chrono::DateTime::from_timestamp(i64::from(seconds), 0)
            .map(|x| x.format("%Y-%m-%dT%H:%M:%SZ").to_string())
            .unwrap_or_default();
        (json!(text), "unix")
    } else {
        (
            json!(snapshot.float_pair(addr).unwrap_or_default()),
            "float",
        )
    }
}
fn scalar_label(reg: usize, value: u16) -> Option<&'static str> {
    match reg {
        0 => Some(if value == 0 {
            "no COM device"
        } else {
            "COM active"
        }),
        1 => Some(
            [
                "Unknown / no active case",
                "NotStarted",
                "InProgress",
                "Completed",
                "Suspended",
                "SimulationRunning",
            ]
            .get(usize::from(value))
            .copied()
            .unwrap_or("invalid"),
        ),
        2 => Some(match value {
            0 => "None",
            1 => "Selected",
            2 => "Streaming",
            4 => "Recording",
            8 => "LostConnection",
            16 => "Pausing",
            _ => "invalid",
        }),
        _ => None,
    }
}
async fn history(State(state): State<WebState>, RawQuery(raw): RawQuery) -> Response {
    if !state.web_enabled {
        return error(
            StatusCode::SERVICE_UNAVAILABLE,
            "history disabled (web layer is off)",
        );
    }
    let query: Vec<(String, String)> =
        match serde_urlencoded::from_str(raw.as_deref().unwrap_or("")) {
            Ok(q) => q,
            Err(_) => return error(StatusCode::BAD_REQUEST, "invalid query encoding"),
        };
    let values: Vec<_> = query
        .into_iter()
        .filter(|(key, _)| key == "addr")
        .map(|(_, v)| v)
        .collect();
    if values.is_empty() {
        return error(
            StatusCode::BAD_REQUEST,
            "missing addr — expected ?addr=N (repeatable)",
        );
    }
    let mut addrs = Vec::new();
    for raw in values {
        match raw.trim().parse::<u16>() {
            Ok(addr) if (4..600).contains(&addr) && addr.is_multiple_of(2) => addrs.push(addr),
            _ => {
                return error(
                    StatusCode::BAD_REQUEST,
                    format!("addr must be an even integer in [4..599]: '{raw}'"),
                );
            }
        }
    }
    let Ok(store) = state.store.lock() else {
        return error(StatusCode::INTERNAL_SERVER_ERROR, "web store poisoned");
    };
    let mut result = serde_json::Map::new();
    for addr in addrs {
        let samples = store.history.get(&addr).into_iter().flatten();
        let mut items = Vec::new();
        for &(at, value) in samples {
            if !value.is_finite() {
                return error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "non-finite history value cannot be serialized",
                );
            }
            items.push(json!({"ts":history_iso(at),"v":float_json(value)}));
        }
        result.insert(addr.to_string(), Value::Array(items));
    }
    Json(Value::Object(result)).into_response()
}
async fn assets(State(state): State<WebState>, uri: Uri, method: axum::http::Method) -> Response {
    if method != axum::http::Method::GET && method != axum::http::Method::HEAD {
        return StatusCode::NOT_FOUND.into_response();
    }
    if !state.web_enabled {
        return StatusCode::NOT_FOUND.into_response();
    }
    let path = uri.path();
    let asset: Option<(&str, &[u8])> = match path {
        "/index.html" => Some((
            "text/html; charset=utf-8",
            include_bytes!("../../../web/dashboard/index.html"),
        )),
        "/main.js" => Some((
            "text/javascript; charset=utf-8",
            include_bytes!("../../../web/dashboard/main.js"),
        )),
        "/main.css" => Some((
            "text/css; charset=utf-8",
            include_bytes!("../../../web/dashboard/main.css"),
        )),
        "/favicon.ico" => Some((
            "image/x-icon",
            include_bytes!("../../../web/dashboard/favicon.ico"),
        )),
        "/favicon.png" => Some((
            "image/png",
            include_bytes!("../../../web/dashboard/favicon.png"),
        )),
        _ if path != "/api"
            && !path.starts_with("/api/")
            && !path.contains('.')
            && !path.contains('%') =>
        {
            Some((
                "text/html; charset=utf-8",
                include_bytes!("../../../web/dashboard/index.html"),
            ))
        }
        _ => None,
    };
    let mut response = match asset {
        Some((kind, bytes)) => (
            [
                (header::CONTENT_TYPE, kind),
                (header::CACHE_CONTROL, "no-cache"),
            ],
            bytes,
        )
            .into_response(),
        None => StatusCode::NOT_FOUND.into_response(),
    };
    if method == axum::http::Method::HEAD {
        *response.body_mut() = axum::body::Body::empty();
    }
    response
}

fn float_json(value: f32) -> Value {
    serde_json::to_string(&value)
        .and_then(|text| serde_json::from_str(&text))
        .unwrap_or(Value::Null)
}
