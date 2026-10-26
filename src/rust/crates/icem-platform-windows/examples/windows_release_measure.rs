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
    if std::env::args().nth(1).as_deref() == Some("--heap-window") {
        return heap_window();
    }
    if std::env::args().nth(1).as_deref() == Some("--heap") {
        return heap_workload();
    }
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
            publisher
                .publish(Ok(read), captured, captured, None)
                .map_err(|e| format!("reader publication: {e:?}"))?;
            let finished = Instant::now();
            let state = reader
                .latest()
                .map_err(|e| format!("reader check: {e:?}"))?;
            if state.status != icem_core::publication::CaptureStatus::Captured
                || state.snapshot.ok_or("missing reader snapshot")?.words() != expected.words()
            {
                return Err("reader did not physically capture expected bits".into());
            }
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

/// Heap runs have no latency sampling/CSV vectors. Total-process ETW counts also
/// include startup, fixture setup, correctness checks and teardown.
#[cfg(windows)]
fn heap_workload() -> Result<(), Box<dyn std::error::Error>> {
    use icem_bus::Snapshot;
    use icem_core::publication::latest_snapshot;
    use icem_core::{SnapshotOrigin, SnapshotSource};
    use icem_platform_windows::WindowsBusReader;
    use std::time::Instant;

    let iterations: usize = std::env::args()
        .nth(2)
        .ok_or("heap iterations required")?
        .parse()?;
    if iterations > 100_000 {
        return Err("heap iterations exceed bound".into());
    }
    let fixture = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");
    let path = std::env::temp_dir().join(format!("icem-heap-{}.bin", std::process::id()));
    std::fs::write(&path, fixture)?;
    let result = (|| -> Result<(), Box<dyn std::error::Error>> {
        let expected = Snapshot::decode(fixture)?;
        let mut source = WindowsBusReader::new(
            path.clone(),
            format!(r"Local\icem-heap-map-{}", std::process::id()),
            format!(r"Local\icem-heap-mutex-{}", std::process::id()),
        );
        let (mut publisher, reader) = latest_snapshot();
        for _ in 0..iterations {
            let read = source
                .snapshot()
                .map_err(|e| format!("heap capture: {e:?}"))?;
            if read.origin != SnapshotOrigin::Captured || read.snapshot.words() != expected.words()
            {
                return Err("heap capture bits/origin mismatch".into());
            }
            let captured = Instant::now();
            publisher
                .publish(Ok(read), captured, captured, None)
                .map_err(|e| format!("heap publication: {e:?}"))?;
            let state = reader.latest().map_err(|e| format!("heap read: {e:?}"))?;
            if state.snapshot.ok_or("heap missing snapshot")?.words() != expected.words() {
                return Err("heap publication bits mismatch".into());
            }
        }
        println!(
            "heap workload pid={} capture_publications={iterations}",
            std::process::id()
        );
        Ok(())
    })();
    std::fs::remove_file(path)?;
    result
}

/// Windowed ETW workload. Driver owns the bounded wait and kills on protocol errors.
#[cfg(windows)]
fn heap_window() -> Result<(), Box<dyn std::error::Error>> {
    use icem_core::SnapshotSource;
    use icem_core::publication::latest_snapshot;
    use icem_platform_windows::WindowsBusReader;
    use icem_protocols::modbus::{MirrorInput, RegisterBank};
    use std::io::{BufRead, Write};
    use std::time::Instant;

    let kind = std::env::args().nth(2).ok_or("window kind required")?;
    let iterations: usize = std::env::args()
        .nth(3)
        .ok_or("window count required")?
        .parse()?;
    if !matches!(kind.as_str(), "capture" | "modbus") || iterations > 100_000 {
        return Err("invalid heap window kind/count".into());
    }
    let path = std::env::temp_dir().join(format!("icem-heap-window-{}.bin", std::process::id()));
    std::fs::write(
        &path,
        include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin"),
    )?;
    let result = (|| -> Result<(), Box<dyn std::error::Error>> {
        let mut source = WindowsBusReader::new(
            path.clone(),
            format!(r"Local\icem-window-map-{}", std::process::id()),
            format!(r"Local\icem-window-mutex-{}", std::process::id()),
        );
        let (mut publisher, reader) = latest_snapshot();
        let initial = source
            .snapshot()
            .map_err(|e| format!("window init: {e:?}"))?;
        let mut bank = RegisterBank::new(initial.snapshot.register_count())
            .map_err(|e| format!("bank init: {e:?}"))?;
        bank.tick(MirrorInput::Fresh(&initial.snapshot));
        drop(initial);
        let mut perform = || -> Result<(), Box<dyn std::error::Error>> {
            if kind == "capture" {
                let read = source
                    .snapshot()
                    .map_err(|e| format!("window capture: {e:?}"))?;
                let now = Instant::now();
                publisher
                    .publish(Ok(read), now, now, None)
                    .map_err(|e| format!("window publish: {e:?}"))?;
                std::hint::black_box(
                    reader
                        .latest()
                        .map_err(|e| format!("window latest: {e:?}"))?,
                );
            } else {
                // Real production FC03 codec; fixed prebuilt 100-word request, no socket claim.
                std::hint::black_box(bank.process(&[1, 3, 0, 0, 0, 100]));
            }
            Ok(())
        };
        for _ in 0..100 {
            perform()?;
        }
        let mut input = std::io::stdin().lock();
        let mut output = std::io::stdout().lock();
        let mut line = String::with_capacity(32);
        writeln!(output, "WARM")?;
        output.flush()?;
        input.read_line(&mut line)?;
        if line.trim() != "WARMUP" {
            return Err("WARMUP required".into());
        }
        line.clear();
        writeln!(output, "READY {}", std::process::id())?;
        output.flush()?;
        input.read_line(&mut line)?;
        if line.trim() != "START" {
            return Err("START required".into());
        }
        for _ in 0..iterations {
            perform()?;
        }
        writeln!(output, "DONE {iterations}")?;
        output.flush()?;
        line.clear();
        input.read_line(&mut line)?;
        if line.trim() != "RELEASE" {
            return Err("RELEASE required".into());
        }
        Ok(())
    })();
    std::fs::remove_file(path)?;
    result
}
