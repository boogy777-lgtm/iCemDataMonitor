use super::*;
use icem_core::{
    Freshness, SnapshotOrigin, SnapshotRead, SourceError, publication::latest_snapshot,
};
use opcua::{
    client::{ClientBuilder, IdentityToken, MonitoredItem, OnSubscriptionNotification},
    types::*,
};
use std::time::Duration;
type TestResult = Result<(), Box<dyn std::error::Error>>;
fn config() -> Config {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    Config {
        bind: listener.local_addr().unwrap(),
        interval: Duration::from_millis(50),
    }
}
fn snapshot(bits: u32) -> Arc<icem_bus::Snapshot> {
    let mut bytes = include_bytes!("../../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
    bytes[18] = 0; // COM inactive while LAN and RAW continue independently.
    bytes[27] = 1;
    bytes[36] = 1;
    for address in [200, 400] {
        bytes[64 + address * 2..66 + address * 2].copy_from_slice(&(bits as u16).to_be_bytes());
        bytes[66 + address * 2..68 + address * 2]
            .copy_from_slice(&((bits >> 16) as u16).to_be_bytes());
    }
    Arc::new(icem_bus::Snapshot::decode(&bytes).unwrap())
}
fn read(ns: u16, id: &str, attribute: AttributeId) -> ReadValueId {
    ReadValueId {
        node_id: NodeId::new(ns, id),
        attribute_id: attribute as u32,
        ..Default::default()
    }
}
struct Notifications(tokio::sync::mpsc::Sender<DataValue>);
impl OnSubscriptionNotification for Notifications {
    fn on_data_value(&mut self, value: DataValue, _: &MonitoredItem) {
        let _ = self.0.try_send(value);
    }
    fn on_event(&mut self, _: Option<Vec<Variant>>, _: &MonitoredItem) {}
}
#[test]
fn strict_config_and_quality() {
    let good =
        r#"{"bind":"127.0.0.1:4840","profile":"loopback-anonymous-none","update_interval_ms":50}"#;
    assert!(Config::parse(good.as_bytes()).is_ok());
    for bad in [
        good.replace("127.0.0.1", "0.0.0.0"),
        good.replace("4840", "0"),
        good.replace("loopback-anonymous-none", "none"),
        good.replace(":50", ":0"),
        good.replace("{", "{\"bind\":\"127.0.0.1:1\","),
        good.replace("{", "{\"extra\":1,"),
    ] {
        assert!(Config::parse(bad.as_bytes()).is_err());
    }
    let (mut publisher, reader) = latest_snapshot();
    let now = Instant::now();
    assert_eq!(
        model::quality(&reader.latest().unwrap(), now),
        StatusCode::BadWaitingForInitialData
    );
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: snapshot(1),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
    assert_eq!(
        model::quality(&reader.latest().unwrap(), now),
        StatusCode::Good
    );
    assert_eq!(
        model::quality(&reader.latest().unwrap(), now + Duration::from_secs(5)),
        StatusCode::UncertainNoCommunicationLastUsableValue
    );
    publisher
        .publish(Err(SourceError::Unavailable), now, now, None)
        .unwrap();
    assert_eq!(
        model::quality(&reader.latest().unwrap(), now),
        StatusCode::UncertainLastUsableValue
    );
}
#[tokio::test(flavor = "current_thread")]
async fn real_sdk_client_types_subscriptions_outage_and_lifecycle() -> TestResult {
    let (mut publisher, reader) = latest_snapshot();
    let now = Instant::now();
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: snapshot(0x7fc01234),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
    let config = config();
    let address = config.bind;
    let mut server = start(config.clone(), reader.clone())?;
    assert_eq!(server.status(), Status::Running);
    assert!(start(config, reader.clone()).is_err());
    let mut client = ClientBuilder::new()
        .application_name("icem-UA-test")
        .application_uri("urn:icem:test")
        .create_sample_keypair(false)
        .session_retry_limit(1)
        .client()
        .map_err(|e| e.join("; "))?;
    let endpoint = format!("opc.tcp://{address}/");
    let (session, events) = client
        .connect_to_matching_endpoint(
            (endpoint.as_str(), "None", MessageSecurityMode::None),
            IdentityToken::Anonymous,
        )
        .await?;
    events.spawn();
    tokio::time::timeout(Duration::from_secs(5), session.wait_for_connection()).await?;
    // The namespace URI is stable; clients resolve its index rather than hard-code it.
    let ns = session.get_namespace_index(NAMESPACE).await?;
    let browse = session
        .browse(
            &[BrowseDescription {
                node_id: NodeId::new(ns, "Bus"),
                browse_direction: BrowseDirection::Forward,
                reference_type_id: ReferenceTypeId::Organizes.into(),
                include_subtypes: true,
                node_class_mask: NodeClassMask::VARIABLE.bits(),
                result_mask: BrowseResultMask::All as u32,
            }],
            1024,
            None,
        )
        .await?;
    let references = browse[0].references.as_ref().ok_or("browse empty")?;
    assert_eq!(references.len(), snapshot(1).variable_count() + 16);
    assert!(
        references
            .iter()
            .any(|r| r.node_id.node_id == NodeId::new(ns, "var200"))
    );
    let write = session
        .write(&[WriteValue {
            node_id: NodeId::new(ns, "var200"),
            attribute_id: AttributeId::Value as u32,
            value: DataValue::value_only(9.0_f32),
            index_range: NumericRange::None,
        }])
        .await?;
    assert_ne!(write[0], StatusCode::Good);
    let fields = [
        "var0",
        "var4",
        "var200",
        "var400",
        "var600",
        "capture_generation",
    ];
    let values = session
        .read(
            &fields.map(|id| read(ns, id, AttributeId::Value)),
            TimestampsToReturn::Both,
            0.0,
        )
        .await?;
    assert!(matches!(values[0].value, Some(Variant::UInt16(_))));
    assert!(matches!(values[1].value, Some(Variant::Int32(_))));
    assert!(matches!(values[2].value, Some(Variant::Float(v)) if v.to_bits() == 0x7fc01234));
    assert!(matches!(values[3].value, Some(Variant::Float(v)) if v.to_bits() == 0x7fc01234));
    assert!(matches!(values[4].value, Some(Variant::UInt16(_))));
    assert!(
        values
            .iter()
            .all(|v| v.source_timestamp.is_none() && v.server_timestamp.is_some())
    );
    assert_eq!(values[1].status(), StatusCode::BadOutOfService);
    assert_eq!(values[2].status(), StatusCode::Good);
    assert_eq!(values[3].status(), StatusCode::Good);
    let com_time = values[1].value.clone();
    let data_types = session
        .read(
            &[
                read(ns, "var4", AttributeId::DataType),
                read(ns, "var200", AttributeId::DataType),
            ],
            TimestampsToReturn::Neither,
            0.0,
        )
        .await?;
    assert_eq!(
        data_types[0].value,
        Some(Variant::NodeId(Box::new(DataTypeId::Int32.into())))
    );
    assert_eq!(
        data_types[1].value,
        Some(Variant::NodeId(Box::new(DataTypeId::Float.into())))
    );
    let (tx, mut notifications) = tokio::sync::mpsc::channel(16);
    let sub = session
        .create_subscription(
            Duration::from_millis(100),
            30,
            10,
            100,
            0,
            true,
            Notifications(tx),
        )
        .await?;
    let monitored = session
        .create_monitored_items(
            sub,
            TimestampsToReturn::Both,
            vec![MonitoredItemCreateRequest {
                item_to_monitor: read(ns, "var200", AttributeId::Value),
                monitoring_mode: MonitoringMode::Reporting,
                requested_parameters: MonitoringParameters {
                    sampling_interval: 100.0,
                    queue_size: 999,
                    discard_oldest: true,
                    ..Default::default()
                },
            }],
        )
        .await?;
    assert_eq!(monitored[0].result.status_code, StatusCode::Good);
    assert_eq!(monitored[0].result.revised_queue_size, 2);
    tokio::time::timeout(Duration::from_secs(3), notifications.recv())
        .await?
        .ok_or("no initial notification")?;
    let now = Instant::now();
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: snapshot(0x40200000),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(3);
    loop {
        let value = tokio::time::timeout_at(deadline.into(), notifications.recv())
            .await?
            .ok_or("subscription closed")?;
        if value.value == Some(Variant::Float(2.5)) {
            break;
        }
    }
    let now = Instant::now();
    publisher
        .publish(Err(SourceError::Unavailable), now, now, None)
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(3);
    let failed = loop {
        let values = session
            .read(
                &[
                    read(ns, "var200", AttributeId::Value),
                    read(ns, "var4", AttributeId::Value),
                    read(ns, "capture_status", AttributeId::Value),
                ],
                TimestampsToReturn::Both,
                0.0,
            )
            .await?;
        if values[0].status() == StatusCode::UncertainLastUsableValue {
            break values;
        }
        assert!(Instant::now() < deadline, "source outage was not projected");
        tokio::time::sleep(Duration::from_millis(10)).await;
    };
    assert_eq!(failed[0].value, Some(Variant::Float(2.5)));
    assert_eq!(failed[0].status(), StatusCode::UncertainLastUsableValue);
    assert_eq!(failed[1].value, com_time);
    assert!(failed[0].source_timestamp.is_none());
    assert_eq!(failed[2].value, Some(Variant::String("failed".into())));
    session.disconnect().await?;
    let slow = std::net::TcpStream::connect(address)?;
    let _ = server.stop(Instant::now());
    server.stop(Instant::now() + Duration::from_secs(2))?;
    drop(slow);
    assert_eq!(server.status(), Status::Stopped);
    assert!(std::net::TcpListener::bind(address).is_ok());
    server.stop(Instant::now())?;
    Ok(())
}

#[tokio::test(flavor = "current_thread")]
async fn initial_absence_first_frame_and_client_reads_never_capture() -> TestResult {
    let (mut publisher, reader) = latest_snapshot();
    let config = config();
    let endpoint = format!("opc.tcp://{}/", config.bind);
    let mut server = start(config, reader.clone())?;
    let mut client = ClientBuilder::new()
        .application_name("icem-UA-initial-test")
        .application_uri("urn:icem:test:initial")
        .create_sample_keypair(false)
        .session_retry_limit(1)
        .client()
        .map_err(|e| e.join("; "))?;
    let (session, events) = client
        .connect_to_matching_endpoint(
            (endpoint.as_str(), "None", MessageSecurityMode::None),
            IdentityToken::Anonymous,
        )
        .await?;
    events.spawn();
    tokio::time::timeout(Duration::from_secs(5), session.wait_for_connection()).await?;
    let ns = session.get_namespace_index(NAMESPACE).await?;
    let initial = session
        .read(
            &[
                read(ns, "var200", AttributeId::Value),
                read(ns, "capture_age_ms", AttributeId::Value),
            ],
            TimestampsToReturn::Both,
            0.0,
        )
        .await?;
    assert_eq!(initial[0].status(), StatusCode::BadNodeIdUnknown);
    assert_eq!(initial[1].status(), StatusCode::BadWaitingForInitialData);
    assert_eq!(reader.latest().unwrap().capture_generation, 0);
    let now = Instant::now();
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: snapshot(0x40200000),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(3);
    loop {
        let value = session
            .read(
                &[read(ns, "var200", AttributeId::Value)],
                TimestampsToReturn::Both,
                0.0,
            )
            .await?;
        if value[0].status() != StatusCode::BadNodeIdUnknown {
            break;
        }
        assert!(Instant::now() < deadline, "first frame was not projected");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    for _ in 0..20 {
        let frame = session
            .read(
                &[
                    read(ns, "var200", AttributeId::Value),
                    read(ns, "var400", AttributeId::Value),
                    read(ns, "capture_generation", AttributeId::Value),
                ],
                TimestampsToReturn::Both,
                0.0,
            )
            .await?;
        assert_eq!(frame[0].value, frame[1].value);
        assert_eq!(frame[0].server_timestamp, frame[1].server_timestamp);
        assert!(frame[0].source_timestamp.is_none());
        assert_eq!(frame[2].value, Some(Variant::UInt64(1)));
    }
    assert_eq!(reader.latest().unwrap().capture_generation, 1);
    session.disconnect().await?;
    server.stop(Instant::now() + Duration::from_secs(2))?;
    Ok(())
}
