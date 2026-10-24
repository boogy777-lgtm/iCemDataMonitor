#![allow(clippy::unwrap_used)]
use std::sync::{Arc, Barrier};
use std::thread;
use std::time::{Duration, Instant};

use icem_bus::{Snapshot, Zone};
use icem_core::publication::{CaptureStatus, PublicationError, latest_snapshot};
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SourceError};

const FIXTURE: &[u8] = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");

fn read(marker: u16, origin: SnapshotOrigin) -> SnapshotRead {
    let mut bytes = FIXTURE.to_vec();
    bytes[64..66].copy_from_slice(&marker.to_be_bytes());
    SnapshotRead {
        snapshot: Arc::new(Snapshot::decode(&bytes).unwrap()),
        freshness: Freshness::Fresh,
        origin,
    }
}

#[test]
fn absence_and_failed_first_attempt_are_explicit() {
    let (mut writer, reader) = latest_snapshot();
    let initial = reader.latest().unwrap();
    assert!(initial.snapshot.is_none());
    assert_eq!(initial.status, CaptureStatus::Initial);
    assert_eq!(initial.capture_generation, 0);
    assert!(initial.captured_at_monotonic.is_none());
    assert!(initial.completed_at_monotonic.is_none());
    let now = Instant::now();
    writer
        .publish(Err(SourceError::Unavailable), now, now, None)
        .unwrap();
    let failed = reader.latest().unwrap();
    assert!(failed.snapshot.is_none());
    assert_eq!(
        failed.status,
        CaptureStatus::Failed(SourceError::Unavailable)
    );
    assert_eq!(failed.capture_generation, 0);
    assert!(failed.capture_age(now).is_none());
    assert_eq!(failed.completed_at_monotonic, Some(now));
    writer
        .publish(Ok(read(99, SnapshotOrigin::Retained)), now, now, None)
        .unwrap();
    let retained = reader.latest().unwrap();
    assert!(retained.snapshot.is_none());
    assert_eq!(retained.capture_generation, 0);
    assert!(retained.captured_at_monotonic.is_none());
}

#[test]
fn failure_and_legacy_fresh_retained_do_not_refresh_success_or_source_fields() {
    let (mut writer, reader) = latest_snapshot();
    let start = Instant::now();
    let first_completed = start + Duration::from_millis(50);
    writer
        .publish(
            Ok(read(1, SnapshotOrigin::Captured)),
            start,
            first_completed,
            None,
        )
        .unwrap();
    let first = reader.latest().unwrap();
    assert_eq!(first.completed_at_monotonic, Some(first_completed));
    assert_eq!(first.captured_at_monotonic, Some(start));
    let snapshot = first.snapshot.as_ref().unwrap();
    let com_time = snapshot.unix_seconds().unwrap();
    let zones = Zone::ALL.map(|zone| snapshot.zone(zone));
    let later = start + Duration::from_secs(10);
    writer
        .publish(Err(SourceError::LockTimeout), later, later, None)
        .unwrap();
    let failed = reader.latest().unwrap();
    assert!(Arc::ptr_eq(snapshot, failed.snapshot.as_ref().unwrap()));
    assert_eq!(
        failed.status,
        CaptureStatus::Failed(SourceError::LockTimeout)
    );
    writer
        .publish(
            Ok(read(99, SnapshotOrigin::Retained)),
            later,
            later,
            Some(SourceError::InvalidLayout),
        )
        .unwrap();
    let retained = reader.latest().unwrap();
    let bytes = retained.snapshot.as_ref().unwrap();
    assert!(Arc::ptr_eq(snapshot, bytes));
    assert_eq!(bytes.words()[0], 1);
    assert_eq!(bytes.unix_seconds().unwrap(), com_time);
    assert_eq!(Zone::ALL.map(|zone| bytes.zone(zone)), zones);
    assert_eq!(
        retained.status,
        CaptureStatus::Retained {
            error: Some(SourceError::InvalidLayout)
        }
    );
    assert_eq!(retained.capture_generation, 1);
    assert_eq!(retained.captured_at_monotonic, Some(start));
    assert_eq!(retained.completed_at_monotonic, Some(later));
    assert_eq!(retained.capture_age(later), Some(Duration::from_secs(10)));
    assert!(
        retained
            .capture_age(start - Duration::from_secs(1))
            .is_none()
    );
    // Identical physical bytes are a new capture, not a new producer event.
    writer
        .publish(Ok(read(1, SnapshotOrigin::Captured)), later, later, None)
        .unwrap();
    let second = reader.latest().unwrap();
    assert_eq!(second.capture_generation, 2);
    assert_eq!(second.captured_at_monotonic, Some(later));
    assert_eq!(
        second.snapshot.as_ref().unwrap().unix_seconds().unwrap(),
        com_time
    );
    assert_eq!(
        Zone::ALL.map(|zone| second.snapshot.as_ref().unwrap().zone(zone)),
        zones
    );
}

#[test]
fn rejected_time_does_not_partially_mutate_publication() {
    let (mut writer, reader) = latest_snapshot();
    let now = Instant::now();
    writer
        .publish(Ok(read(1, SnapshotOrigin::Captured)), now, now, None)
        .unwrap();
    let later = now + Duration::from_secs(1);
    assert_eq!(
        writer.publish(Ok(read(2, SnapshotOrigin::Captured)), later, now, None),
        Err(PublicationError::NonMonotonicTime)
    );
    assert_eq!(
        writer.publish(
            Err(SourceError::Io),
            now,
            now - Duration::from_secs(1),
            None
        ),
        Err(PublicationError::NonMonotonicTime)
    );
    assert_eq!(
        writer.publish(
            Ok(read(3, SnapshotOrigin::Captured)),
            now - Duration::from_secs(1),
            later,
            None
        ),
        Err(PublicationError::NonMonotonicTime)
    );
    let state = reader.latest().unwrap();
    assert_eq!(state.capture_generation, 1);
    assert_eq!(state.snapshot.as_ref().unwrap().words()[0], 1);
    assert_eq!(state.captured_at_monotonic, Some(now));
    assert_eq!(state.completed_at_monotonic, Some(now));
}

#[test]
fn readers_share_body_and_slow_reader_does_not_block_or_mutate_old_frame() {
    let (mut writer, reader) = latest_snapshot();
    let now = Instant::now();
    writer
        .publish(Ok(read(1, SnapshotOrigin::Captured)), now, now, None)
        .unwrap();
    let slow = reader.latest().unwrap().snapshot.unwrap();
    let other = reader.clone().latest().unwrap().snapshot.unwrap();
    assert!(Arc::ptr_eq(&slow, &other));
    assert_eq!(slow.words().as_ptr(), other.words().as_ptr());
    drop(other);
    let weak = Arc::downgrade(&slow);
    let mut previous = Arc::downgrade(&slow);
    for marker in 2..100 {
        writer
            .publish(Ok(read(marker, SnapshotOrigin::Captured)), now, now, None)
            .unwrap();
        if marker > 2 {
            // The cell has no backlog; no reader held the replaced frame.
            assert!(previous.upgrade().is_none());
        }
        let latest = reader.latest().unwrap().snapshot.unwrap();
        previous = Arc::downgrade(&latest);
        assert_eq!(slow.words()[0], 1);
        assert_eq!(latest.words()[0], marker);
    }
    assert!(weak.upgrade().is_some());
    drop(slow);
    assert!(weak.upgrade().is_none());
    drop(writer);
    drop(reader);
    assert!(previous.upgrade().is_none());
}

#[test]
fn concurrent_readers_observe_coherent_frames_with_one_writer() {
    let (mut writer, reader) = latest_snapshot();
    let barrier = Arc::new(Barrier::new(5));
    let mut threads = Vec::new();
    for _ in 0..4 {
        let reader = reader.clone();
        let barrier = Arc::clone(&barrier);
        threads.push(thread::spawn(move || {
            barrier.wait();
            let mut last_generation = 0;
            for _ in 0..2_000 {
                let state = reader.latest().unwrap();
                assert!(state.capture_generation >= last_generation);
                last_generation = state.capture_generation;
                if let Some(snapshot) = state.snapshot {
                    assert_eq!(u64::from(snapshot.words()[0]), state.capture_generation);
                    assert_eq!(state.status, CaptureStatus::Captured);
                    assert_eq!(state.captured_at_monotonic, state.completed_at_monotonic);
                    assert_eq!(snapshot.unix_seconds().unwrap(), 1_700_000_000);
                } else {
                    assert_eq!(state.capture_generation, 0);
                    assert_eq!(state.status, CaptureStatus::Initial);
                }
            }
        }));
    }
    barrier.wait();
    let now = Instant::now();
    for marker in 1..=1_000 {
        let time = now + Duration::from_millis(u64::from(marker));
        writer
            .publish(Ok(read(marker, SnapshotOrigin::Captured)), time, time, None)
            .unwrap();
    }
    for thread in threads {
        thread.join().unwrap();
    }
    assert_eq!(reader.latest().unwrap().capture_generation, 1_000);
}

#[test]
fn lan_and_raw_capture_progress_with_fixed_com_time() {
    let (mut publisher, reader) = latest_snapshot();
    let at = Instant::now();
    let mut bytes = FIXTURE.to_vec();
    let first = Arc::new(Snapshot::decode(&bytes).unwrap());
    let fixed_com_time = first.unix_seconds().unwrap();
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: Arc::clone(&first),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            at,
            at,
            None,
        )
        .unwrap();
    // Independent producer updates; Time & Date (4..5) and COM metadata stay fixed.
    bytes[64 + 200 * 2..64 + 200 * 2 + 4].copy_from_slice(&[0x12, 0x34, 0x7f, 0xc0]);
    bytes[64 + 400 * 2..64 + 400 * 2 + 4].copy_from_slice(&[0x56, 0x78, 0x80, 0x00]);
    bytes[19..23].copy_from_slice(&100_u32.to_le_bytes());
    bytes[28..32].copy_from_slice(&101_u32.to_le_bytes());
    let next = Arc::new(Snapshot::decode(&bytes).unwrap());
    let now = at + Duration::from_millis(200);
    publisher
        .publish(
            Ok(SnapshotRead {
                snapshot: Arc::clone(&next),
                freshness: Freshness::Fresh,
                origin: SnapshotOrigin::Captured,
            }),
            now,
            now,
            None,
        )
        .unwrap();
    let a = reader.latest().unwrap();
    let b = reader.clone().latest().unwrap();
    let a = a.snapshot.unwrap();
    let b = b.snapshot.unwrap();
    assert!(Arc::ptr_eq(&next, &a));
    assert!(Arc::ptr_eq(&a, &b));
    assert_eq!(a.unix_seconds().unwrap(), fixed_com_time);
    assert_eq!(a.zone(Zone::Com), first.zone(Zone::Com));
    assert_ne!(a.pair_bits(200).unwrap(), first.pair_bits(200).unwrap());
    assert_ne!(a.pair_bits(400).unwrap(), first.pair_bits(400).unwrap());
    assert_ne!(a.zone(Zone::Lan), first.zone(Zone::Lan));
    assert_ne!(a.zone(Zone::Raw), first.zone(Zone::Raw));
}
