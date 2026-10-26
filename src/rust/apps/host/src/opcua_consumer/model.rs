use icem_bus::{Value, Zone};
use icem_core::publication::{CaptureStatus, PublishedSnapshot};
use opcua::{
    server::{
        SubscriptionCache,
        address_space::{NodeType, Variable},
        node_manager::memory::SimpleNodeManager,
    },
    types::{
        AttributeId, DataValue, DateTime, NodeId, ObjectId, QualifiedName, StatusCode,
        TimestampsToReturn, Variant,
    },
};
use std::{
    sync::Arc,
    time::{Duration, Instant},
};
const ORIGIN: &str = "measurement_time_unknown;COM_var4_raw_only;zone_ticks_AddIn_writer_not_measurement;ServerTimestamp_local_receipt";
const SCOPE: &str = "acquisition_age_and_producer_active_not_measurement_freshness";
pub struct Model {
    manager: Arc<SimpleNodeManager>,
    ns: u16,
    metadata: Vec<NodeId>,
    variables: Vec<NodeId>,
    register_count: Option<u16>,
}
fn data(value: Variant, quality: StatusCode, receipt: DateTime) -> DataValue {
    DataValue {
        value: Some(value),
        status: Some(quality),
        source_timestamp: None,
        server_timestamp: Some(receipt),
        ..Default::default()
    }
}
pub fn quality(state: &PublishedSnapshot, now: Instant) -> StatusCode {
    if state.snapshot.is_none() {
        StatusCode::BadWaitingForInitialData
    } else if !matches!(state.status, CaptureStatus::Captured) {
        StatusCode::UncertainLastUsableValue
    } else if state
        .capture_age(now)
        .is_none_or(|age| age >= Duration::from_secs(5))
        || state
            .completed_at_monotonic
            .and_then(|t| now.checked_duration_since(t))
            .is_none_or(|age| age >= Duration::from_secs(5))
    {
        StatusCode::UncertainNoCommunicationLastUsableValue
    } else {
        StatusCode::Good
    }
}
pub fn variable_quality(
    snapshot: &icem_bus::Snapshot,
    address: u16,
    base: StatusCode,
) -> StatusCode {
    let zone = match address {
        4..200 => Some(Zone::Com),
        200..400 => Some(Zone::Lan),
        400..600 => Some(Zone::Raw),
        _ => None,
    };
    if base == StatusCode::Good && zone.is_some_and(|zone| !snapshot.zone(zone).producer_active) {
        StatusCode::BadOutOfService
    } else {
        base
    }
}
fn variant(value: Value) -> Variant {
    match value {
        Value::Word(v) => Variant::UInt16(v),
        Value::ComUnixSeconds(v) => Variant::Int32(v),
        Value::Float32(v) => Variant::Float(v),
    }
}
impl Model {
    pub fn new(manager: Arc<SimpleNodeManager>, ns: u16) -> Result<Self, String> {
        let folder = NodeId::new(ns, "Bus");
        let mut metadata = Vec::new();
        {
            let mut space = manager.address_space().write();
            if !space.add_folder(
                &folder,
                QualifiedName::new(ns, "Bus"),
                "iCem Bus",
                &ObjectId::ObjectsFolder.into(),
            ) {
                return Err("OPC UA folder creation failed".into());
            }
            for (name, value) in [
                ("capture_generation", Variant::UInt64(0)),
                ("capture_age_ms", Variant::UInt64(0)),
                ("completion_age_ms", Variant::UInt64(0)),
                ("capture_status", Variant::String("initial".into())),
                ("source_error", Variant::String("none".into())),
                ("quality_scope", Variant::String(SCOPE.into())),
                ("time_origin", Variant::String(ORIGIN.into())),
                ("COM_writer_ticks", Variant::Int64(0)),
                ("COM_producer_active", Variant::Boolean(false)),
                ("COM_write_count", Variant::UInt32(0)),
                ("LAN_writer_ticks", Variant::Int64(0)),
                ("LAN_producer_active", Variant::Boolean(false)),
                ("LAN_write_count", Variant::UInt32(0)),
                ("RAW_writer_ticks", Variant::Int64(0)),
                ("RAW_producer_active", Variant::Boolean(false)),
                ("RAW_write_count", Variant::UInt32(0)),
            ] {
                let id = NodeId::new(ns, name);
                if space
                    .add_variables(
                        vec![Variable::new(
                            &id,
                            QualifiedName::new(ns, name),
                            name,
                            value,
                        )],
                        &folder,
                    )
                    .contains(&false)
                {
                    return Err("OPC UA metadata creation failed".into());
                }
                metadata.push(id);
            }
        }
        Ok(Self {
            manager,
            ns,
            metadata,
            variables: Vec::new(),
            register_count: None,
        })
    }
    pub fn update(
        &mut self,
        state: PublishedSnapshot,
        subscriptions: &SubscriptionCache,
        now: Instant,
    ) -> Result<(), String> {
        let mut space = self.manager.address_space().write();
        if let Some(snapshot) = &state.snapshot {
            if self
                .register_count
                .is_some_and(|count| count != snapshot.register_count())
            {
                return Err("OPC UA source geometry changed; restart required".into());
            }
            if self.register_count.is_none() {
                let folder = NodeId::new(self.ns, "Bus");
                for variable in snapshot.variables() {
                    let name = variable.id.to_string();
                    let id = NodeId::new(self.ns, name.as_str());
                    if space
                        .add_variables(
                            vec![Variable::new(
                                &id,
                                QualifiedName::new(self.ns, name.as_str()),
                                name.as_str(),
                                variant(variable.value),
                            )],
                            &folder,
                        )
                        .contains(&false)
                    {
                        return Err("OPC UA variable creation failed".into());
                    }
                    self.variables.push(id);
                }
                self.register_count = Some(snapshot.register_count());
            }
        }
        let receipt = DateTime::now();
        let quality = quality(&state, now);
        let (status, error) = match state.status {
            CaptureStatus::Initial => ("initial", None),
            CaptureStatus::Captured => ("captured", None),
            CaptureStatus::Retained { error } => ("retained", error),
            CaptureStatus::Failed(error) => ("failed", Some(error)),
        };
        let age = |time: Option<Instant>| {
            time.and_then(|t| now.checked_duration_since(t))
                .map(|v| v.as_millis().min(u128::from(u64::MAX)) as u64)
        };
        let mut values = vec![data(
            state.capture_generation.into(),
            StatusCode::Good,
            receipt,
        )];
        for time in [state.captured_at_monotonic, state.completed_at_monotonic] {
            values.push(data(
                age(time).unwrap_or(0).into(),
                if time.is_some() {
                    StatusCode::Good
                } else {
                    StatusCode::BadWaitingForInitialData
                },
                receipt,
            ));
        }
        values.extend([
            data(status.into(), StatusCode::Good, receipt),
            data(
                error
                    .map_or_else(|| "none".into(), |e| format!("{e:?}"))
                    .into(),
                StatusCode::Good,
                receipt,
            ),
            data(SCOPE.into(), StatusCode::Good, receipt),
            data(ORIGIN.into(), StatusCode::Good, receipt),
        ]);
        for zone in [Zone::Com, Zone::Lan, Zone::Raw] {
            if let Some(snapshot) = &state.snapshot {
                let z = snapshot.zone(zone);
                values.extend([
                    data(z.last_write_utc_ticks.into(), quality, receipt),
                    data(z.producer_active.into(), quality, receipt),
                    data(z.write_count.into(), quality, receipt),
                ]);
            } else {
                values.extend([
                    data(Variant::Int64(0), quality, receipt),
                    data(false.into(), quality, receipt),
                    data(Variant::UInt32(0), quality, receipt),
                ]);
            }
        }
        // One guard covers initial node creation, full-frame update and subscription sampling.
        let metadata = self.metadata.iter().zip(values);
        let variables = state
            .snapshot
            .iter()
            .flat_map(|s| s.variables().map(move |v| (s, v)))
            .zip(&self.variables)
            .map(|((snapshot, v), id)| {
                (
                    id,
                    data(
                        variant(v.value),
                        variable_quality(snapshot, v.id.register_address(), quality),
                        receipt,
                    ),
                )
            });
        for (id, value) in metadata.chain(variables) {
            match space.find_mut(id) {
                Some(NodeType::Variable(variable)) => variable.set_data_value(value),
                _ => return Err("OPC UA model node missing".into()),
            }
        }
        subscriptions.maybe_notify(
            self.metadata
                .iter()
                .chain(&self.variables)
                .map(|id| (id, AttributeId::Value)),
            |id, attribute, range, encoding| {
                space.find(id)?.as_node().get_attribute(
                    TimestampsToReturn::Both,
                    attribute,
                    range,
                    encoding,
                )
            },
        );
        Ok(())
    }
}
