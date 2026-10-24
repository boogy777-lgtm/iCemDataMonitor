use super::*;
use icem_core::{
    Freshness, SnapshotOrigin, SnapshotRead, SourceError,
    publication::{SnapshotPublisher, latest_snapshot},
};
use std::{
    io::{Read, Write},
    net::{TcpListener, TcpStream},
};
type TestResult = Result<(), Box<dyn std::error::Error>>;
const FIXTURE: &[u8] = include_bytes!("../../../../tests/fixtures/bus-v5-minimal.bin");
fn config(address: std::net::SocketAddr, qos: u8) -> Config {
    Config::parse(format!(r#"{{"broker":"{address}","client_id":"icem-test","topic":"icem/state","qos":{qos},"retain":true,"publish_interval_ms":50,"reconnect_delay_ms":50}}"#).as_bytes()).unwrap()
}
fn publish(publisher: &mut SnapshotPublisher, value: u32) {
    let mut bytes = FIXTURE.to_vec();
    for address in [200, 400] {
        bytes[64 + address * 2..66 + address * 2].copy_from_slice(&(value as u16).to_be_bytes());
        bytes[66 + address * 2..68 + address * 2]
            .copy_from_slice(&((value >> 16) as u16).to_be_bytes());
    }
    let now = Instant::now();
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: Arc::new(icem_bus::Snapshot::decode(&bytes).unwrap()),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
}
fn json(state: &PublishedSnapshot, now: Instant) -> serde_json::Value {
    serde_json::from_slice(&payload::encode(state, now).unwrap()).unwrap()
}
fn variable(value: &serde_json::Value, id: &str) -> serde_json::Value {
    value["variables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|v| v["id"] == id)
        .unwrap()
        .clone()
}
fn wait_for(mut test: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(3);
    while !test() {
        assert!(Instant::now() < deadline, "condition deadline");
        thread::sleep(Duration::from_millis(5));
    }
}
#[test]
fn payload_bits_types_scope_initial_failure_and_expiry() -> TestResult {
    let (mut publisher, reader) = latest_snapshot();
    let initial = json(&reader.latest().unwrap(), Instant::now());
    assert_eq!(initial["capture"]["quality"], "absent");
    assert!(initial["variables"].is_null());
    assert!(initial["com_source_time"].is_null());
    publish(&mut publisher, 0xff80_0000);
    let state = reader.latest().unwrap();
    let value = json(&state, Instant::now());
    assert_eq!(variable(&value, "var0")["type"], "u16");
    assert_eq!(variable(&value, "var4")["type"], "i32_com_unix_seconds");
    assert_eq!(variable(&value, "var4")["value"], 1_700_000_000);
    assert_eq!(variable(&value, "var8")["raw_bits"], 0x8000_0000_u32);
    assert_eq!(variable(&value, "var10")["raw_bits"], 0x7fc0_1234_u32);
    assert_eq!(variable(&value, "var10")["non_finite"], "nan");
    assert!(variable(&value, "var10")["value"].is_null());
    assert_eq!(
        variable(&value, "var200")["non_finite"],
        "negative_infinity"
    );
    assert_eq!(variable(&value, "var600")["type"], "u16");
    assert_eq!(value["variables"].as_array().unwrap().len(), 352);
    assert_eq!(value["zones"][0]["write_count"], 42);
    assert!(!value["zones"][1]["producer_active"].as_bool().unwrap());
    assert!(value["zones"][1]["upstream_measurement_time"].is_null());
    assert_eq!(
        json(&state, Instant::now() + Duration::from_secs(6))["capture"]["quality"],
        "expired"
    );
    let now = Instant::now();
    publisher
        .publish(Err(SourceError::Unavailable), now, now, None)
        .unwrap();
    let failed = json(&reader.latest().unwrap(), Instant::now());
    assert_eq!(failed["capture"]["status"], "failed");
    assert_eq!(failed["capture"]["source_error"], "Unavailable");
    assert_eq!(failed["capture"]["quality"], "retained");
    assert_eq!(
        failed["capture"]["generation"],
        value["capture"]["generation"]
    );
    assert_eq!(failed["variables"], value["variables"]);
    assert_eq!(failed["zones"], value["zones"]);
    assert_eq!(failed["com_source_time"], value["com_source_time"]);
    for bits in [0_u32, 0x8000_0000, 0xffff_ffff, 0x7fff_ffff] {
        let mut bytes = FIXTURE.to_vec();
        bytes[72..74].copy_from_slice(&(bits as u16).to_be_bytes());
        bytes[74..76].copy_from_slice(&((bits >> 16) as u16).to_be_bytes());
        let mut boundary = reader.latest().unwrap();
        boundary.snapshot = Some(Arc::new(icem_bus::Snapshot::decode(&bytes)?));
        let encoded = json(&boundary, Instant::now());
        assert_eq!(variable(&encoded, "var4")["raw_bits"], bits);
        assert_eq!(
            encoded["com_source_time"]["unix_seconds"],
            bits.cast_signed()
        );
    }
    // D09 Fresh on retained return cannot improve source quality or replace bytes.
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: state.snapshot.unwrap(),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Retained,
            }),
            now,
            now,
            Some(SourceError::Io),
        )
        .unwrap();
    let retained = json(&reader.latest().unwrap(), Instant::now());
    assert_eq!(retained["capture"]["status"], "retained");
    assert_eq!(retained["capture"]["quality"], "retained");
    Ok(())
}
#[test]
fn latest_pending_retention_is_constant_and_maximum_layout_fits_payload_bound() -> TestResult {
    let (mut publisher, reader) = latest_snapshot();
    let mut pending = LatestPending::default();
    let mut previous = None::<std::sync::Weak<icem_bus::Snapshot>>;
    for value in 0..2000 {
        publish(&mut publisher, value);
        pending.replace(reader.latest().unwrap());
        if let Some(previous) = previous {
            assert!(previous.upgrade().is_none());
        }
        previous = pending
            .state
            .as_ref()
            .and_then(|s| s.snapshot.as_ref())
            .map(Arc::downgrade);
    }
    assert_eq!(pending.take().unwrap().capture_generation, 2000);
    assert!(pending.take().is_none());
    let mut bytes = FIXTURE.to_vec();
    bytes[8..10].copy_from_slice(&u16::MAX.to_le_bytes());
    bytes.resize(64 + usize::from(u16::MAX) * 2, 0);
    let mut state = reader.latest().unwrap();
    state.snapshot = Some(Arc::new(icem_bus::Snapshot::decode(&bytes)?));
    let encoded = payload::encode(&state, Instant::now()).unwrap();
    assert!(encoded.len() < payload::MAX_PAYLOAD);
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&encoded)?["variables"]
            .as_array()
            .unwrap()
            .len(),
        usize::from(u16::MAX) - 298
    );
    Ok(())
}
// Minimal loopback MQTT protocol fixture, not a broker/interoperability claim.
fn packet(stream: &mut TcpStream) -> (u8, Vec<u8>) {
    let mut first = [0];
    stream.read_exact(&mut first).unwrap();
    let mut length = 0;
    let mut multiplier = 1;
    loop {
        let mut byte = [0];
        stream.read_exact(&mut byte).unwrap();
        length += usize::from(byte[0] & 127) * multiplier;
        if byte[0] & 128 == 0 {
            break;
        }
        multiplier *= 128;
        assert!(multiplier <= 128 * 128 * 128);
    }
    assert!(length < payload::MAX_PAYLOAD + 2048);
    let mut body = vec![0; length];
    stream.read_exact(&mut body).unwrap();
    (first[0], body)
}
fn accept(listener: &TcpListener) -> TcpStream {
    listener.set_nonblocking(true).unwrap();
    let deadline = Instant::now() + Duration::from_secs(3);
    let mut stream = loop {
        match listener.accept() {
            Ok((stream, _)) => break stream,
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(Instant::now() < deadline);
                thread::sleep(Duration::from_millis(5));
            }
            Err(e) => panic!("accept: {e}"),
        }
    };
    // Windows accepted sockets inherit the listener's nonblocking mode; packet()
    // uses blocking read_exact with bounded socket timeouts on every platform.
    stream.set_nonblocking(false).unwrap();
    stream
        .set_read_timeout(Some(Duration::from_secs(3)))
        .unwrap();
    stream
        .set_write_timeout(Some(Duration::from_secs(3)))
        .unwrap();
    let (header, connect) = packet(&mut stream);
    assert_eq!(header, 0x10);
    assert_eq!(&connect[..7], b"\0\x04MQTT\x04");
    assert_ne!(connect[7] & 2, 0);
    stream.write_all(&[0x20, 2, 0, 0]).unwrap();
    stream
}
fn publication(stream: &mut TcpStream, qos: u8, acknowledge: bool) -> serde_json::Value {
    let (header, bytes) = packet(stream);
    assert_eq!(header >> 4, 3);
    assert_eq!((header >> 1) & 3, qos);
    assert_eq!(header & 1, 1);
    let len = usize::from(u16::from_be_bytes([bytes[0], bytes[1]]));
    assert_eq!(&bytes[2..2 + len], b"icem/state");
    let offset = 2 + len;
    let payload_offset = offset + if qos == 0 { 0 } else { 2 };
    let json = serde_json::from_slice(&bytes[payload_offset..]).unwrap();
    if acknowledge && qos > 0 {
        stream
            .write_all(&[
                if qos == 1 { 0x40 } else { 0x50 },
                2,
                bytes[offset],
                bytes[offset + 1],
            ])
            .unwrap();
        if qos == 2 {
            let (header, released) = packet(stream);
            assert_eq!(header, 0x62);
            assert_eq!(&released, &bytes[offset..offset + 2]);
            stream
                .write_all(&[0x70, 2, bytes[offset], bytes[offset + 1]])
                .unwrap();
        }
    }
    json
}
#[test]
fn real_wire_all_qos_and_failed_source_without_generation_change() -> TestResult {
    for qos in 0..=2 {
        let listener = TcpListener::bind("127.0.0.1:0")?;
        let (mut source, reader) = latest_snapshot();
        publish(&mut source, 1);
        let mut owner = start(config(listener.local_addr()?, qos), reader)?;
        let mut stream = accept(&listener);
        let first = publication(&mut stream, qos, true);
        wait_for(|| owner.status() == Status::Running);
        let now = Instant::now();
        source
            .publish(Err(SourceError::Io), now, now, None)
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let failed = loop {
            let value = publication(&mut stream, qos, true);
            if value["capture"]["status"] == "failed" {
                break value;
            }
            assert!(Instant::now() < deadline);
        };
        assert_eq!(
            failed["capture"]["generation"],
            first["capture"]["generation"]
        );
        assert_eq!(failed["com_source_time"], first["com_source_time"]);
        owner.stop(Instant::now() + Duration::from_secs(1))?;
        assert_eq!(owner.status(), Status::Stopped);
    }
    Ok(())
}
#[test]
fn reconnect_coalesces_versions_and_slow_ack_stop_keeps_source_independent() -> TestResult {
    let listener = TcpListener::bind("127.0.0.1:0")?;
    let (mut source, reader) = latest_snapshot();
    publish(&mut source, 1);
    let mut owner = start(config(listener.local_addr()?, 1), reader.clone())?;
    let mut stream = accept(&listener);
    let first = publication(&mut stream, 1, false);
    drop(stream);
    wait_for(|| owner.status() == Status::Faulted);
    for value in 2..=500 {
        publish(&mut source, value);
    }
    thread::sleep(Duration::from_millis(100));
    let mut stream = accept(&listener);
    let latest = publication(&mut stream, 1, false);
    assert_eq!(latest["capture"]["generation"], 500);
    assert_eq!(variable(&latest, "var200")["raw_bits"], 500);
    assert_eq!(latest["com_source_time"], first["com_source_time"]);
    // No PUBACK: one in-flight, newest source still progresses independently.
    for value in 501..=1000 {
        publish(&mut source, value);
    }
    assert_eq!(reader.latest().unwrap().capture_generation, 1000);
    let at = Instant::now();
    let immediate = owner.stop(at);
    assert!(at.elapsed() < Duration::from_millis(100));
    if let Err(error) = immediate {
        assert!(error.starts_with("MQTT shutdown deadline:"));
    }
    owner.stop(Instant::now() + Duration::from_secs(1))?;
    assert_eq!(owner.status(), Status::Stopped);
    Ok(())
}
#[test]
fn broker_offline_and_unresponsive_handshake_are_cancellable() -> TestResult {
    for handshake in [false, true] {
        let listener = TcpListener::bind("127.0.0.1:0")?;
        let address = listener.local_addr()?;
        let mut held_listener = Some(listener);
        if !handshake {
            held_listener = None;
        }
        let (mut source, reader) = latest_snapshot();
        let mut owner = start(config(address, 1), reader.clone())?;
        let _stream = if let Some(listener) = held_listener.as_ref() {
            listener.set_nonblocking(true)?;
            let mut accepted = None;
            wait_for(|| {
                if let Ok((stream, _)) = listener.accept() {
                    accepted = Some(stream);
                    true
                } else {
                    false
                }
            });
            accepted
        } else {
            None
        };
        publish(&mut source, 42);
        assert_eq!(reader.latest().unwrap().capture_generation, 1);
        assert_eq!(owner.status(), Status::Faulted);
        owner.stop(Instant::now() + Duration::from_millis(500))?;
        assert_eq!(owner.status(), Status::Stopped);
    }
    Ok(())
}

#[test]
fn registry_offline_mqtt_does_not_stop_real_acquisition_or_modbus() -> TestResult {
    use crate::{
        acquisition::Acquisition,
        consumers::{ConsumerId, Registry},
        modbus_consumer,
    };
    use icem_core::SnapshotSource;
    use icem_protocols::modbus::RegisterBank;
    use icem_transport::modbus_tcp::ServerOptions;
    use std::sync::Mutex;
    struct Source;
    impl SnapshotSource for Source {
        fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
            Ok(SnapshotRead {
                snapshot: Arc::new(
                    icem_bus::Snapshot::decode(FIXTURE).map_err(|_| SourceError::InvalidLayout)?,
                ),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            })
        }
    }
    let unused = TcpListener::bind("127.0.0.1:0")?.local_addr()?;
    let mut acquisition = Acquisition::start(|| Ok(Box::new(Source)))?;
    let reader = acquisition.snapshot_reader();
    let mqtt = start(config(unused, 1), reader.clone())?;
    let (slot, modbus) = modbus_consumer::start(
        modbus_consumer::Config {
            address: "127.0.0.1:0".parse()?,
            enabled: true,
            options: ServerOptions::default(),
        },
        Arc::new(Mutex::new(
            RegisterBank::new(650).map_err(|e| format!("{e:?}"))?,
        )),
    )?;
    let address = slot.lock().unwrap().as_ref().unwrap().local_addr();
    let mut registry = Registry::new();
    assert!(
        registry
            .register(ConsumerId("mqtt"), Box::new(mqtt))
            .is_ok()
    );
    assert!(
        registry
            .register(ConsumerId("modbus"), Box::new(modbus))
            .is_ok()
    );
    wait_for(|| reader.latest().unwrap().capture_generation >= 3);
    assert_eq!(registry.status(ConsumerId("mqtt")), Some(Status::Faulted));
    assert_eq!(registry.status(ConsumerId("modbus")), Some(Status::Running));
    assert!(TcpStream::connect(address).is_ok());
    let before = reader.latest().unwrap().capture_generation;
    registry.stop_all(Duration::from_secs(1))?;
    assert_eq!(registry.status(ConsumerId("mqtt")), Some(Status::Stopped));
    wait_for(|| reader.latest().unwrap().capture_generation > before);
    acquisition.stop(Duration::from_secs(1))?;
    Ok(())
}
