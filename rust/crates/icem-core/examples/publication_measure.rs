//! Safe ownership/byte accounting, not an allocator profiler. For allocator-wide
//! counts run this executable under a platform heap profiler. Harness allocations
//! (the fixture, Weak list, stdout and timing) must be separated in that profile.
use std::sync::{Arc, Weak};
use std::time::Instant;

use icem_bus::Snapshot;
use icem_core::publication::latest_snapshot;
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead};

fn main() -> Result<(), String> {
    const CAPTURES: usize = 10_000;
    const READERS: usize = 8;
    let bytes = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");
    let (mut publisher, reader) = latest_snapshot();
    let readers: Vec<_> = (0..READERS).map(|_| reader.clone()).collect();
    let mut live: Vec<Weak<Snapshot>> = Vec::with_capacity(3);
    let mut slow = None;
    let mut peak_live = 0;
    let mut peak_live_register_bytes = 0;
    let mut decoded_register_bytes = 0;
    let mut reads = 0;
    let start = Instant::now();
    for _ in 0..CAPTURES {
        let snapshot = Snapshot::decode(bytes).map_err(|e| e.to_string())?;
        decoded_register_bytes += std::mem::size_of_val(snapshot.words());
        let now = Instant::now();
        publisher
            .publish(
                Ok(SnapshotRead {
                    snapshot,
                    freshness: Freshness::Fresh,
                    origin: SnapshotOrigin::Captured,
                }),
                now,
                now,
                None,
            )
            .map_err(|e| format!("publication: {e:?}"))?;
        let latest = reader.latest().map_err(|e| format!("read: {e:?}"))?;
        let snapshot = latest.snapshot.ok_or("missing successful snapshot")?;
        live.retain(|weak| weak.strong_count() > 0);
        live.push(Arc::downgrade(&snapshot));
        if slow.is_none() {
            slow = Some(Arc::clone(&snapshot));
        }
        let mut retained_bytes = 0;
        for weak in &live {
            if let Some(frame) = weak.upgrade() {
                retained_bytes += std::mem::size_of_val(frame.words());
            }
        }
        peak_live = peak_live.max(live.len());
        peak_live_register_bytes = peak_live_register_bytes.max(retained_bytes);
        for reader in &readers {
            let state = reader.latest().map_err(|e| format!("read: {e:?}"))?;
            let shared = state.snapshot.ok_or("missing successful snapshot")?;
            if !Arc::ptr_eq(&snapshot, &shared) {
                return Err("consumer body was copied".into());
            }
            reads += 1;
        }
    }
    println!("fixture_registers=650 captures={CAPTURES} consumer_reads={reads}");
    println!(
        "decoded_payload_constructions={CAPTURES} decoded_register_bytes={decoded_register_bytes}"
    );
    println!("peak_live_snapshots={peak_live} peak_live_register_bytes={peak_live_register_bytes}");
    println!(
        "elapsed_us={} allocator_allocations=NOT_MEASURED allocator_bytes=NOT_MEASURED",
        start.elapsed().as_micros()
    );
    // Optional Linux process-wide high-water RSS includes code/runtime/harness,
    // so it is intentionally separate from shared register-body accounting.
    if let Ok(status) = std::fs::read_to_string("/proc/self/status")
        && let Some(peak) = status.lines().find(|line| line.starts_with("VmHWM:"))
    {
        println!("process_peak_rss_{peak}");
    }
    Ok(())
}
