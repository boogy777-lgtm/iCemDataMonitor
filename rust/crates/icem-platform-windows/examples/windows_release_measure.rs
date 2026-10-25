//! Benchmark-only instrumentation of the existing safe Windows MMF boundary.
//! No production timers, unsafe allocator hooks, or allocator-count inference.
#[cfg(not(windows))]
fn main() -> Result<(), String> {
    Err("Windows measurement requires Windows".into())
}

#[cfg(windows)]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    use icem_bus::Snapshot;
    use icem_core::publication::latest_snapshot;
    use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource};
    use icem_platform_windows::WindowsBusReader;
    use icem_windows_native::{Mapping, NamedMutex};
    use std::io::Write;
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    const WARMUP: usize = 100;
    const SAMPLES: usize = 1_000;
    let output = std::env::args_os().nth(1).ok_or("output CSV required")?;
    let fixture = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");
    let path =
        std::env::temp_dir().join(format!("icem-release-measure-{}.bin", std::process::id()));
    std::fs::write(&path, fixture)?;
    let result = (|| -> Result<(), Box<dyn std::error::Error>> {
        // Force the real reader's existing unnamed, file-backed MMF fallback.
        // The uniquely named mutex has no writer contention in this workload.
        let mutex_name = format!(r"Local\icem-release-measure-{}", std::process::id());
        let missing_map = format!(r"Local\icem-release-missing-{}", std::process::id());
        let mapping = Mapping::unnamed_read_only(&path)?;
        let mutex = NamedMutex::open_or_create(&mutex_name)?;
        let mut source = WindowsBusReader::new(path.clone(), missing_map, mutex_name);
        let (mut publisher, reader) = latest_snapshot();
        let expected = Snapshot::decode(fixture)?;
        let mut rows = Vec::with_capacity(SAMPLES);
        let mut held = None;
        for index in 0..WARMUP + SAMPLES {
            let start = Instant::now();
            let guard = mutex
                .lock(Duration::from_millis(500))?
                .ok_or("lock timeout")?;
            let locked = Instant::now();
            let bytes = mapping.copy_bytes(fixture.len())?;
            // Upper-bound hold sample includes release syscall and timer overhead.
            guard.release()?;
            let released = Instant::now();
            let snapshot = Arc::new(Snapshot::decode(&bytes)?);
            let decoded = Instant::now();
            publisher
                .publish(
                    Ok(SnapshotRead {
                        snapshot: Arc::clone(&snapshot),
                        freshness: Freshness::Fresh,
                        origin: SnapshotOrigin::Captured,
                    }),
                    released,
                    decoded,
                    None,
                )
                .map_err(|e| format!("publication: {e:?}"))?;
            let published = Instant::now();
            // Correctness checks are outside the measured region.
            if snapshot.words() != expected.words() || bytes != fixture {
                return Err("MMF/decode bits changed".into());
            }
            let state = reader.latest().map_err(|e| format!("read: {e:?}"))?;
            let shared = state.snapshot.ok_or("missing snapshot")?;
            if !Arc::ptr_eq(&snapshot, &shared) {
                return Err("publication copied body".into());
            }
            if held.is_none() {
                held = Some(Arc::clone(&snapshot));
            }
            // Separately time the unmodified complete reader -> publisher path.
            let capture_start = Instant::now();
            let read = source.snapshot().map_err(|e| format!("capture: {e:?}"))?;
            let captured = Instant::now();
            if read.origin != SnapshotOrigin::Captured || read.snapshot.words() != expected.words()
            {
                return Err("reader did not physically capture expected bits".into());
            }
            publisher
                .publish(Ok(read), captured, captured, None)
                .map_err(|e| format!("reader publication: {e:?}"))?;
            let finished = Instant::now();
            if index >= WARMUP {
                rows.push([
                    locked.duration_since(start).as_nanos(),
                    released.duration_since(locked).as_nanos(),
                    decoded.duration_since(released).as_nanos(),
                    published.duration_since(decoded).as_nanos(),
                    published.duration_since(start).as_nanos(),
                    captured.duration_since(capture_start).as_nanos(),
                    finished.duration_since(capture_start).as_nanos(),
                ]);
            }
        }
        if held.ok_or("no held frame")?.words() != expected.words() {
            return Err("old frame changed".into());
        }
        let mut csv = std::io::BufWriter::new(std::fs::File::create(output)?);
        writeln!(
            csv,
            "lock_wait_ns,lock_hold_upper_ns,decode_arc_ns,publication_ns,boundary_total_ns,reader_capture_ns,reader_publication_total_ns"
        )?;
        for row in rows {
            writeln!(
                csv,
                "{},{},{},{},{},{},{}",
                row[0], row[1], row[2], row[3], row[4], row[5], row[6]
            )?;
        }
        csv.flush()?;
        println!(
            "Windows MMF Release: registers={} warmup={WARMUP} samples={SAMPLES}; allocations=NOT_MEASURED",
            expected.words().len()
        );
        Ok(())
    })();
    std::fs::remove_file(path)?;
    result
}
