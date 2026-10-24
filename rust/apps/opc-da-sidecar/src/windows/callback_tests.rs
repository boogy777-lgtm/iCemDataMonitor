#![forbid(unsafe_code)]
use super::*;
use icem_opc_da_codec::Frame;
use opc_da_bindings::client::{DaClient, DataEvent, DataEventKind};
use std::{sync::mpsc, time::Duration};
fn model() -> std::result::Result<Model, Box<dyn std::error::Error>> {
    let snapshot = Arc::new(icem_bus::Snapshot::decode(include_bytes!(
        "../../../../tests/fixtures/bus-v5-minimal.bin"
    ))?);
    Ok(Arc::new(Mutex::new(Some(Received::new(
        Frame {
            sequence: 1,
            generation: 1,
            capture_age: Some(Duration::ZERO),
            completion_age: Some(Duration::ZERO),
            status: 1,
            snapshot: Some(snapshot),
        },
        Instant::now(),
    )))))
}
#[test]
fn two_sinks_native_bits_clocks_state_activity_and_async()
-> std::result::Result<(), Box<dyn std::error::Error>> {
    let apartment = ComApartment::mta()?;
    let source = model()?;
    let expected = lock(&source)?
        .as_ref()
        .ok_or("missing source")?
        .sample(4, Instant::now())
        .ok_or("missing item")?
        .filetime;
    let server = DaServer::new(Service::new(source.clone(), Arc::new(Limits::new()))?);
    let client = DaClient::from_object(&apartment, &server.object())?;
    let mut options = GroupOptions::new("two-sink");
    options.active = false;
    options.client_handle = 41;
    options.requested_update_rate = 20;
    let group = client.add_group(options)?;
    assert!(group.enabled().is_err());
    let items = group.add_items(&[
        ItemSpec::new("var0", 11),
        ItemSpec::new("var4", 12),
        ItemSpec::new("var10", 13),
    ])?;
    let handles = items
        .iter()
        .map(|i| {
            i.as_ref()
                .map(|i| i.server_handle)
                .map_err(|e| e.to_string())
        })
        .collect::<std::result::Result<Vec<_>, _>>()?;
    let (sa, ra) = mpsc::sync_channel::<DataEvent>(64);
    let (sb, rb) = mpsc::sync_channel::<DataEvent>(64);
    let mut a = group.subscribe(Arc::new(move |event| {
        let _ = sa.try_send(event);
    }))?;
    let mut b = group.subscribe(Arc::new(move |event| {
        let _ = sb.try_send(event);
    }))?;
    assert!(ra.recv_timeout(Duration::from_millis(40)).is_err());
    assert!(group.refresh(DataSource::Cache, 77).is_err());
    let mut state = group.state()?;
    state.active = true;
    assert_eq!(group.set_state(&state)?, 20);
    for event in [
        ra.recv_timeout(Duration::from_secs(1))?,
        rb.recv_timeout(Duration::from_secs(1))?,
    ] {
        assert_eq!(event.kind, DataEventKind::DataChange);
        assert_eq!(event.group_handle, 41);
        assert_eq!(event.items.len(), 3);
        assert!(matches!(event.items[0].value, Value::U16(7)));
        assert!(matches!(event.items[1].value, Value::I32(1_700_000_000)));
        assert!(matches!(event.items[2].value,Value::F32(v)if v.to_bits()==0x7fc01234));
        assert_eq!(event.items[2].client_handle, ClientItemHandle(13));
        assert_eq!(event.items[0].timestamp.ticks(), 0);
        assert_eq!(event.items[1].timestamp.ticks(), expected);
        assert_eq!(event.items[1].quality, 0xc0);
    }
    // Bitwise NaN comparison must not emit unchanged data every poll.
    assert!(ra.recv_timeout(Duration::from_millis(60)).is_err());
    group.set_enable(false)?;
    assert!(!group.enabled()?);
    std::thread::sleep(Duration::from_millis(30));
    while ra.try_recv().is_ok() {}
    while rb.try_recv().is_ok() {}
    state.client_handle = 42;
    state.update_rate = 30;
    group.set_state(&state)?;
    let (id, errors) = group.async_read(&[handles[0], ServerItemHandle::from_raw(u32::MAX)], 99)?;
    assert_ne!(id, 0);
    assert!(errors[0].is_ok());
    assert!(errors[1].is_err());
    for r in [&ra, &rb] {
        let e = r.recv_timeout(Duration::from_secs(1))?;
        assert_eq!(
            (e.kind, e.transaction, e.group_handle, e.items.len()),
            (DataEventKind::ReadComplete, 99, 42, 1)
        );
    }
    assert!(group.cancel(id).is_err());
    let (id, errors) = group.async_write(
        &[opc_da_bindings::client::WriteValue {
            server_handle: handles[0],
            value: Value::U16(99),
        }],
        97,
    )?;
    assert_eq!(id, 0);
    assert!(errors[0].is_err());
    let (id, errors) = group.async_read(&[ServerItemHandle::from_raw(u32::MAX)], 98)?;
    assert_eq!(id, 0);
    assert!(errors[0].is_err());
    assert!(ra.recv_timeout(Duration::from_millis(30)).is_err());
    let mut retained = lock(&source)?
        .as_ref()
        .ok_or("missing source")?
        .frame()
        .clone();
    retained.capture_age = Some(Duration::from_secs(10));
    *lock(&source)? = Some(Received::new(retained, Instant::now()));
    group.refresh(DataSource::Device, 100)?;
    for r in [&ra, &rb] {
        let e = r.recv_timeout(Duration::from_secs(1))?;
        assert_eq!(
            (e.kind, e.transaction, e.items.len()),
            (DataEventKind::DataChange, 100, 3)
        );
        assert_eq!(e.items[1].quality, 0x44);
        assert_eq!(e.items[1].timestamp.ticks(), expected);
    }
    let cookie = a.cookie().ok_or("missing cookie")?;
    a.close()?;
    a.close()?;
    assert!(group.unadvise(cookie).is_err());
    assert!(group.unadvise(u32::MAX).is_err());
    group.set_items_client_handles(&[(handles[0], ClientItemHandle(111))])?;
    group.set_items_active(&[handles[1]], false)?;
    group.refresh(DataSource::Cache, 101)?;
    let e = rb.recv_timeout(Duration::from_secs(1))?;
    assert_eq!((e.transaction, e.items.len()), (101, 2));
    assert_eq!(e.items[0].client_handle, ClientItemHandle(111));
    assert!(ra.recv_timeout(Duration::from_millis(30)).is_err());
    group.set_items_active(&handles, false)?;
    assert!(group.refresh(DataSource::Cache, 102).is_err());
    group.set_items_active(&handles, true)?;
    state.active = false;
    group.set_state(&state)?;
    assert!(group.refresh(DataSource::Cache, 103).is_err());
    state.active = true;
    group.set_state(&state)?;
    group.set_enable(true)?;
    assert_eq!(rb.recv_timeout(Duration::from_secs(1))?.items.len(), 3);
    b.close()?;
    assert!(group.enabled().is_err());
    Ok(())
}
#[test]
fn two_blocked_sinks_cancel_and_capacity_are_atomic()
-> std::result::Result<(), Box<dyn std::error::Error>> {
    let apartment = ComApartment::mta()?;
    let server = DaServer::new(Service::new(model()?, Arc::new(Limits::new()))?);
    let client = DaClient::from_object(&apartment, &server.object())?;
    let group = client.add_group(GroupOptions::new("cancel"))?;
    let items = group.add_items(&[ItemSpec::new("var0", 1)])?;
    let handle = items[0].as_ref().map_err(|e| e.to_string())?.server_handle;
    let (entered, entry) = mpsc::sync_channel(2);
    let mut subscriptions = Vec::new();
    let mut releases = Vec::new();
    let mut receivers = Vec::new();
    for _ in 0..2 {
        let entered = entered.clone();
        let (release, gate) = mpsc::sync_channel(1);
        let gate = Mutex::new(gate);
        let first = std::sync::atomic::AtomicBool::new(true);
        let (send, receive) = mpsc::sync_channel::<DataEvent>(128);
        subscriptions.push(group.subscribe(Arc::new(move |e| {
            if first.swap(false, Ordering::AcqRel) {
                let _ = entered.try_send(());
                if let Ok(gate) = gate.lock() {
                    let _ = gate.recv_timeout(Duration::from_secs(5));
                }
            }
            let _ = send.try_send(e);
        }))?);
        releases.push(release);
        receivers.push(receive);
    }
    for _ in 0..2 {
        entry.recv_timeout(Duration::from_secs(1))?;
    }
    let mut ids = Vec::new();
    for t in 1..=32 {
        let (id, errors) = group.async_read(&[handle], t)?;
        assert!(errors[0].is_ok());
        ids.push(id);
    }
    assert!(group.async_read(&[handle], 33).is_err());
    group.cancel(ids[0])?;
    assert!(group.cancel(ids[0]).is_err());
    for release in releases {
        release.send(())?;
    }
    for receive in receivers {
        assert_eq!(receive.recv_timeout(Duration::from_secs(1))?.transaction, 0);
        let (mut reads, mut cancels) = (0, 0);
        for _ in 0..32 {
            match receive.recv_timeout(Duration::from_secs(1))?.kind {
                DataEventKind::ReadComplete => reads += 1,
                DataEventKind::CancelComplete => cancels += 1,
                _ => return Err("unexpected callback".into()),
            }
        }
        assert_eq!((reads, cancels), (31, 1));
        assert!(receive.recv_timeout(Duration::from_millis(30)).is_err());
    }
    for mut subscription in subscriptions {
        subscription.close()?;
    }
    Ok(())
}
#[test]
fn profile_rollback_minimum_rate_and_reconnect()
-> std::result::Result<(), Box<dyn std::error::Error>> {
    let apartment = ComApartment::mta()?;
    let server = DaServer::new(Service::new(model()?, Arc::new(Limits::new()))?);
    let client = DaClient::from_object(&apartment, &server.object())?;
    let group = client.add_group(GroupOptions::new("profile"))?;
    let initial = group.state()?;
    let mut invalid = initial.clone();
    invalid.active = false;
    invalid.client_handle = 99;
    invalid.percent_deadband = 1.;
    assert_eq!(
        group
            .set_state(&invalid)
            .err()
            .ok_or("deadband accepted")?
            .code(),
        ErrorCode::from_raw(0xc0040400_u32.cast_signed())
    );
    assert_eq!(group.state()?.client_handle, initial.client_handle);
    assert!(group.state()?.active);
    invalid.percent_deadband = 0.;
    invalid.locale = 1033;
    assert!(group.set_state(&invalid).is_err());
    invalid = initial;
    invalid.update_rate = 0;
    assert_eq!(group.set_state(&invalid)?, 10);
    assert_eq!(group.state()?.update_rate, 10);
    group.add_items(&[ItemSpec::new("var0", 7)])?;
    for _ in 0..4 {
        let (s, r) = mpsc::sync_channel(8);
        let mut subscription = group.subscribe(Arc::new(move |e| {
            let _ = s.try_send(e);
        }))?;
        assert_eq!(
            r.recv_timeout(Duration::from_secs(1))?.items[0].client_handle,
            ClientItemHandle(7)
        );
        subscription.close()?;
    }
    Ok(())
}
#[test]
fn actual_reentrant_unadvise_and_remove_group()
-> std::result::Result<(), Box<dyn std::error::Error>> {
    let apartment = ComApartment::mta()?;
    let monitor = CallbackMonitor::new();
    let server = DaServer::new_with_callbacks(
        Service::new(model()?, Arc::new(Limits::new()))?,
        monitor.clone(),
    );
    let client = DaClient::from_object(&apartment, &server.object())?;
    let mut options = GroupOptions::new("reentrant");
    options.active = false;
    let group = client.add_group(options)?;
    group.add_items(&[ItemSpec::new("var0", 1)])?;
    let cookie = Arc::new(std::sync::atomic::AtomicU32::new(0));
    let copied = cookie.clone();
    let (s, r) = mpsc::sync_channel(1);
    let mut subscription = group.subscribe_reentrant(Arc::new(move |_, commands| {
        let result = (|| -> Result<()> {
            let _ = commands.state()?;
            commands.unadvise(copied.load(Ordering::Acquire))?;
            commands.remove_group(true)?;
            Ok(())
        })();
        let _ = s.try_send(result.map_err(|e| e.to_string()));
    }))?;
    cookie.store(
        subscription.cookie().ok_or("missing cookie")?,
        Ordering::Release,
    );
    let mut state = group.state()?;
    state.active = true;
    group.set_state(&state)?;
    r.recv_timeout(Duration::from_secs(1))??;
    let deadline = Instant::now() + Duration::from_secs(1);
    while monitor.health(Instant::now()).live_workers > 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    assert_eq!(monitor.health(Instant::now()).live_workers, 0);
    assert!(subscription.close().is_err());
    Ok(())
}
#[test]
fn sink_cap_and_4096_item_native_callback() -> std::result::Result<(), Box<dyn std::error::Error>> {
    let apartment = ComApartment::mta()?;
    let monitor = CallbackMonitor::new();
    let server = DaServer::new_with_callbacks(
        Service::new(model()?, Arc::new(Limits::new()))?,
        monitor.clone(),
    );
    let client = DaClient::from_object(&apartment, &server.object())?;
    let group = client.add_group(GroupOptions::new("limits"))?;
    let specs = (1..=4096)
        .map(|handle| ItemSpec::new("var10", handle))
        .collect::<Vec<_>>();
    assert!(group.add_items(&specs)?.iter().all(|item| item.is_ok()));
    let mut subscriptions = Vec::new();
    for _ in 0..4 {
        let (s, r) = mpsc::sync_channel(2);
        subscriptions.push(group.subscribe(Arc::new(move |e| {
            let _ = s.try_send(e);
        }))?);
        let e = r.recv_timeout(Duration::from_secs(1))?;
        assert_eq!(e.items.len(), 4096);
        assert_eq!(e.items[4095].client_handle, ClientItemHandle(4096));
        assert!(matches!(e.items[4095].value,Value::F32(v)if v.to_bits()==0x7fc01234));
    }
    assert!(group.subscribe(Arc::new(|_| {})).is_err());
    assert_eq!(monitor.health(Instant::now()).live_workers, 4);
    drop(subscriptions);
    let deadline = Instant::now() + Duration::from_secs(1);
    while monitor.health(Instant::now()).live_workers > 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    assert_eq!(monitor.health(Instant::now()).live_workers, 0);
    Ok(())
}
#[test]
fn hung_callback_idle_60s_child_helper() -> std::result::Result<(), Box<dyn std::error::Error>> {
    if std::env::var_os("ICEM_CALLBACK_HUNG_CHILD").is_none() {
        return Ok(());
    }
    let monitor = CallbackMonitor::new();
    let _watchdog = CallbackWatchdog::start(monitor.clone())?;
    let apartment = ComApartment::mta()?;
    let server = DaServer::new_with_callbacks(
        Service::new(model()?, Arc::new(Limits::new()))?,
        monitor.clone(),
    );
    let client = DaClient::from_object(&apartment, &server.object())?;
    let group = client.add_group(GroupOptions::new("hung"))?;
    group.add_items(&[ItemSpec::new("var0", 1)])?;
    let (s, r) = mpsc::sync_channel(1);
    let mut subscription = group.subscribe(Arc::new(move |_| {
        let _ = s.try_send(());
        loop {
            std::thread::park();
        }
    }))?;
    r.recv_timeout(Duration::from_secs(1))?;
    let before = Instant::now();
    subscription.close()?;
    assert!(before.elapsed() < Duration::from_millis(500));
    assert_eq!(monitor.health(Instant::now()).in_flight, 1);
    std::thread::sleep(Duration::from_secs(60));
    Err("watchdog failed".into())
}
#[test]
fn hung_child_exits_without_ipc_frames() -> std::result::Result<(), Box<dyn std::error::Error>> {
    let mut child = std::process::Command::new(std::env::current_exe()?)
        .args([
            "--exact",
            "windows::callback_tests::hung_callback_idle_60s_child_helper",
            "--nocapture",
        ])
        .env("ICEM_CALLBACK_HUNG_CHILD", "1")
        .spawn()?;
    let start = Instant::now();
    loop {
        if let Some(status) = child.try_wait()? {
            assert_eq!(status.code(), Some(70));
            break;
        }
        if start.elapsed() > Duration::from_secs(6) {
            child.kill()?;
            let _ = child.wait();
            return Err("hung child exceeded6s".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    Ok(())
}
