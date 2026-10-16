use crate::model::{now_ms, timestamp};
use crate::{WebState, proto::*};
use icem_bus::Snapshot;
use std::{pin::Pin, time::Duration};
use telemetry_service_server::{TelemetryService, TelemetryServiceServer};
use tokio_stream::{Stream, StreamExt, wrappers::WatchStream};
use tonic::{Request, Response, Status};

pub(crate) fn service(state: WebState) -> TelemetryServiceServer<Rpc> {
    TelemetryServiceServer::new(Rpc(state))
        .max_decoding_message_size(64 * 1024)
        .max_encoding_message_size(128 * 1024)
}
pub(crate) struct Rpc(WebState);
#[tonic::async_trait]
impl TelemetryService for Rpc {
    async fn get_status(
        &self,
        _: Request<HostStatusRequest>,
    ) -> Result<Response<HostStatus>, Status> {
        let case = self
            .0
            .read()
            .await
            .ok()
            .and_then(|x| x.read)
            .map(|x| i32::from(x.snapshot.words()[1].cast_signed()))
            .unwrap_or_default();
        let layers = self.0.layers().map_err(Status::internal)?;
        let store = self
            .0
            .store
            .lock()
            .map_err(|_| Status::internal("web store poisoned"))?;
        let h = &store.health;
        Ok(Response::new(HostStatus {
            bus_open: h.bus_open,
            addin_alive: h.alive,
            heartbeat: h.header[3],
            com_port: h.header[0],
            case_state: case,
            case_lan_state: h.header[2],
            zones: h
                .zones
                .iter()
                .enumerate()
                .map(|(i, (active, age))| ZoneHealth {
                    kind: i as i32 + 1,
                    active: *active,
                    age_ms: *age,
                    write_count: 0,
                })
                .collect(),
            layers: Some(LayerState {
                modbus: layers.modbus_bound,
                modbus_bound: layers.modbus_bound,
                modbus_port: i32::from(layers.modbus_port),
                web: layers.web,
                opc_da: layers.opc_da,
                set_as_default: layers.set_as_default,
                ..LayerState::default()
            }),
            updated_at_utc: Some(timestamp(h.at)),
            console_visible: false,
            autostart_enabled: false,
        }))
    }
    async fn get_registers(
        &self,
        request: Request<RegistersRequest>,
    ) -> Result<Response<RegistersReply>, Status> {
        let (start, end) = match request.into_inner().zone {
            0 => (0, 600),
            1 => (4, 200),
            2 => (200, 400),
            3 => (400, 600),
            _ => return Err(Status::invalid_argument("unknown bus zone")),
        };
        let reply = self.0.read().await?;
        if !reply.is_open {
            return Err(Status::unavailable("bus is not open"));
        }
        let read = reply
            .read
            .ok_or_else(|| Status::unavailable("bus not available"))?;
        let values = values(&read.snapshot, None, start, end)
            .into_iter()
            .map(|x| RegisterValue {
                addr: x.addr,
                value: x.value.map(|v| match v {
                    register_delta::Value::Int32Value(v) => register_value::Value::Int32Value(v),
                    register_delta::Value::FloatValue(v) => register_value::Value::FloatValue(v),
                }),
            })
            .collect();
        Ok(Response::new(RegistersReply {
            timestamp_utc: Some(timestamp(now_ms())),
            heartbeat: i32::from(read.snapshot.words()[3].cast_signed()),
            values,
        }))
    }
    async fn subscribe_metrics(
        &self,
        request: Request<SubscribeRequest>,
    ) -> Result<
        Response<Pin<Box<dyn Stream<Item = Result<MetricsSample, Status>> + Send + 'static>>>,
        Status,
    > {
        let requested = request.into_inner().interval_ms;
        let period = Duration::from_millis(requested.max(200) as u64);
        let permit = self
            .0
            .streams
            .clone()
            .try_acquire_owned()
            .map_err(|_| Status::resource_exhausted("metrics subscription limit reached"))?;
        let state = self.0.clone();
        let mut stop = state.shutdown.subscribe();
        let (send, receive) = tokio::sync::watch::channel(None);
        tokio::spawn(async move {
            let _permit = permit;
            let mut previous: Option<Vec<u16>> = None;
            loop {
                let read = tokio::select! { _=send.closed()=>break,_=stop.changed()=>break,reply=state.read()=>reply };
                let result = match read {
                    Ok(reply) if reply.is_open => match reply.read {
                        Some(read) => {
                            let s = &read.snapshot;
                            let sample = MetricsSample {
                                timestamp_utc: Some(timestamp(now_ms())),
                                heartbeat: i32::from(s.words()[3].cast_signed()),
                                com_port_active: s.words()[0].cast_signed() > 0,
                                case_state: i32::from(s.words()[1].cast_signed()),
                                interval_clamped: requested < 200,
                                deltas: values(s, previous.as_deref(), 0, 600),
                            };
                            previous = Some(s.words().to_vec());
                            Ok(sample)
                        }
                        None => Err(Status::unavailable(
                            "metrics stream aborted: the bus is not open or stopped responding",
                        )),
                    },
                    _ => Err(Status::unavailable(
                        "metrics stream aborted: the bus is not open or stopped responding",
                    )),
                };
                let failed = result.is_err();
                // Same capacity-1 DropOldest behavior as the original MetricsSampler.
                if send.send(Some(result)).is_err() || failed {
                    break;
                }
                tokio::select! { _=send.closed()=>break,_=stop.changed()=>break,_=tokio::time::sleep(period)=>{} }
            }
        });
        Ok(Response::new(Box::pin(
            WatchStream::new(receive).filter_map(|item| item),
        )))
    }
    async fn get_ui_prefs(&self, _: Request<UiPrefsRequest>) -> Result<Response<UiPrefs>, Status> {
        Ok(Response::new(UiPrefs {
            theme_light: self.0.theme_light(),
        }))
    }
    async fn set_ui_theme(
        &self,
        request: Request<SetUiThemeRequest>,
    ) -> Result<Response<UiPrefs>, Status> {
        self.0.set_theme(request.into_inner().theme_light);
        Ok(Response::new(UiPrefs {
            theme_light: self.0.theme_light(),
        }))
    }
    // Lifecycle/console/autostart/layer control and boot replay are separate
    // migration stages. Generated default stubs return UNIMPLEMENTED, never fake success.
}
pub(crate) fn values(
    snapshot: &Snapshot,
    previous: Option<&[u16]>,
    start: usize,
    end: usize,
) -> Vec<RegisterDelta> {
    let words = snapshot.words();
    let mut result = Vec::new();
    for addr in start..end.min(4) {
        if previous.and_then(|p| p.get(addr)) != words.get(addr) {
            result.push(RegisterDelta {
                addr: addr as u32,
                value: Some(register_delta::Value::Int32Value(i32::from(words[addr]))),
            })
        }
    }
    for addr in (start.max(4)..end.min(words.len())).step_by(2) {
        if addr + 1 >= words.len() {
            break;
        }
        if previous.is_some_and(|p| p.get(addr..addr + 2) == words.get(addr..addr + 2)) {
            continue;
        }
        let value = if addr == 4 {
            register_delta::Value::Int32Value(snapshot.unix_seconds().unwrap_or_default())
        } else {
            register_delta::Value::FloatValue(snapshot.float_pair(addr as u16).unwrap_or_default())
        };
        result.push(RegisterDelta {
            addr: addr as u32,
            value: Some(value),
        });
    }
    result
}
