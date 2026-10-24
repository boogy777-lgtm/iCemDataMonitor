#![forbid(unsafe_code)]
#![cfg(windows)]
//! Opt-in real cross-process COM gate: requires matching OPC Foundation proxy/stubs.
use icem_opc_da_codec::{Frame, READY, write_frame};
use opc_classic_types::{ClassContext, Guid, Value, ValueType};
use opc_classic_utils::ComApartment;
use opc_da_bindings::client::{DaClient, DataSource, GroupOptions, ItemSpec};
use std::{
    io::Read,
    process::{Child, Command, Stdio},
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
struct OwnedChild(Arc<Mutex<Child>>);
impl Drop for OwnedChild {
    fn drop(&mut self) {
        if let Ok(mut child) = self.0.lock() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}
#[test]
#[ignore = "requires real Windows COM and matching installed OPC Foundation proxy/stubs"]
fn actual_cross_process_class_factory_group_read_and_callbacks()
-> Result<(), Box<dyn std::error::Error>> {
    let mut process = Command::new(env!("CARGO_BIN_EXE_icem-opc-da-sidecar"))
        .arg("--ipc-v1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()?;
    let mut input = process.stdin.take().ok_or("missing stdin")?;
    let mut output = process.stdout.take().ok_or("missing stdout")?;
    let child = OwnedChild(Arc::new(Mutex::new(process)));
    // A separate owner enforces a hard process lifetime even if COM blocks.
    let owned = child.0.clone();
    let done = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let stop = done.clone();
    let watchdog = std::thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(15);
        while !stop.load(std::sync::atomic::Ordering::Acquire) && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(10));
        }
        if !stop.load(std::sync::atomic::Ordering::Acquire)
            && let Ok(mut child) = owned.lock()
            && matches!(child.try_wait(), Ok(None))
        {
            let _ = child.kill();
            let _ = child.wait();
        }
    });
    let result = (|| -> Result<(), Box<dyn std::error::Error>> {
        let mut ack = [0; 8];
        output.read_exact(&mut ack)?;
        assert_eq!(&ack, READY);
        let snapshot = Arc::new(icem_bus::Snapshot::decode(include_bytes!(
            "../../../tests/fixtures/bus-v5-minimal.bin"
        ))?);
        write_frame(
            &mut input,
            &Frame {
                sequence: 1,
                generation: 1,
                capture_age: Some(Duration::ZERO),
                completion_age: Some(Duration::ZERO),
                status: 1,
                snapshot: Some(snapshot.clone()),
            },
        )?;
        output.read_exact(&mut ack)?;
        assert_eq!(u64::from_le_bytes(ack), 1);
        let apartment = ComApartment::mta()?;
        let id = Guid::new(
            0xc69b6b01,
            0x878c,
            0x4927,
            [0x8c, 0xc0, 0xc6, 0xba, 0x26, 0xc4, 0xb7, 0x1a],
        );
        let client = DaClient::connect_class_id(&apartment, &id, ClassContext::LOCAL_SERVER)?;
        let group = client.add_group(GroupOptions::new("cross-process"))?;
        let items = group.add_items(&[
            ItemSpec::new("var0", 1),
            ItemSpec::new("var4", 2),
            ItemSpec::new("var10", 3),
            ItemSpec::new("var200", 4),
            ItemSpec::new("var600", 5),
        ])?;
        let items = items
            .iter()
            .map(|r| r.as_ref().map_err(|e| e.to_string()))
            .collect::<Result<Vec<_>, _>>()?;
        assert_eq!(items[0].canonical_data_type, ValueType::U16);
        assert_eq!(items[1].canonical_data_type, ValueType::I32);
        assert_eq!(items[2].canonical_data_type, ValueType::F32);
        let handles = items.iter().map(|i| i.server_handle).collect::<Vec<_>>();
        let samples = group.read(DataSource::Cache, &handles)?;
        let samples = samples
            .iter()
            .map(|r| r.as_ref().map_err(|e| e.to_string()))
            .collect::<Result<Vec<_>, _>>()?;
        assert!(matches!(samples[1].value, Value::I32(1_700_000_000)));
        assert!(matches!(samples[2].value,Value::F32(v) if v.to_bits()==0x7fc01234));
        assert_eq!(
            samples[1].timestamp.ticks(),
            (snapshot.zone(icem_bus::Zone::Com).last_write_utc_ticks - 504_911_232_000_000_000)
                as u64
        );
        assert_eq!(samples[3].quality, 0x1c);
        assert_eq!(samples[4].timestamp.ticks(), 0);
        assert_eq!(samples[4].quality, 0x40);
        let write = group.write(&[opc_da_bindings::client::WriteValue {
            server_handle: handles[0],
            value: Value::U16(999),
        }])?;
        assert!(write[0].is_err());
        let (send, receive) = std::sync::mpsc::sync_channel(8);
        let mut subscription = group.subscribe(Arc::new(move |event| {
            let _ = send.try_send(event);
        }))?;
        let event = receive.recv_timeout(Duration::from_secs(2))?;
        assert_eq!(
            event.kind,
            opc_da_bindings::client::DataEventKind::DataChange
        );
        assert_eq!(event.items.len(), 5);
        assert!(matches!(event.items[2].value,Value::F32(v)if v.to_bits()==0x7fc01234));
        group.set_enable(false)?;
        group.refresh(DataSource::Device, 500)?;
        assert_eq!(
            receive.recv_timeout(Duration::from_secs(2))?.transaction,
            500
        );
        subscription.close()?;
        drop(subscription);
        group.close(false)?;
        Ok(())
    })();
    done.store(true, std::sync::atomic::Ordering::Release);
    watchdog.join().map_err(|_| "watchdog panicked")?;
    result
}
