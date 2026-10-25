//! Dev-only file-backed producer and separately measured snapshot retention load.
#[cfg(not(windows))]
fn main() -> Result<(), String> {
    Err("Windows live load requires Windows".into())
}

#[cfg(windows)]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    use icem_bus::{Snapshot, Zone};
    use icem_core::publication::latest_snapshot;
    use icem_core::{SnapshotOrigin, SnapshotSource};
    use icem_platform_windows::WindowsBusReader;
    use icem_windows_native::NamedMutex;
    use std::io::{Seek, SeekFrom, Write};
    use std::sync::{Arc, Weak};
    use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

    let args: Vec<String> = std::env::args().collect();
    if args.len() != 7 {
        return Err("MODE PATH MUTEX DURATION_MS OUTPUT MISSING_MAP required".into());
    }
    let duration = Duration::from_millis(args[4].parse()?);
    if !(Duration::from_secs(5)..=Duration::from_secs(120)).contains(&duration) {
        return Err("duration must be 5..120 seconds".into());
    }
    let path = std::path::PathBuf::from(&args[2]);
    let started = Instant::now();
    if args[1] == "--writer" {
        let mutex = NamedMutex::open_or_create(&args[3])?;
        let mut bytes = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
        let mut file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(&path)?;
        let mut output = std::io::BufWriter::new(std::fs::File::create(&args[5])?);
        writeln!(output, "elapsed_ns,generation,lan_raw_bits,writer_ticks")?;
        let mut generation = 0_u32;
        let mut due = started;
        while started.elapsed() < duration {
            generation += 1;
            let bits = 0x3f00_0000 | generation;
            let ticks = i64::try_from(SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos() / 100)?
                + 621_355_968_000_000_000;
            for index in 0..3 {
                // COM source words/time/count remain fixed; only its active bit is enabled.
                bytes[18 + index * 9] = 1;
                if index != 0 {
                    bytes[10 + index * 9..18 + index * 9].copy_from_slice(&ticks.to_le_bytes());
                    bytes[37 + index * 4..41 + index * 4].copy_from_slice(&generation.to_le_bytes());
                }
            }
            for address in [200, 400] {
                let offset = 64 + address * 2;
                bytes[offset..offset + 2].copy_from_slice(&(bits as u16).to_be_bytes());
                bytes[offset + 2..offset + 4].copy_from_slice(&((bits >> 16) as u16).to_be_bytes());
            }
            // RAW finite/IEEE boundary probes travel without numeric conversion.
            for (address, value) in [(402, 0x8000_0000_u32), (404, 0x0000_0001)] {
                let offset = 64 + address * 2;
                bytes[offset..offset + 2].copy_from_slice(&(value as u16).to_be_bytes());
                bytes[offset + 2..offset + 4].copy_from_slice(&((value >> 16) as u16).to_be_bytes());
            }
            let guard = mutex
                .lock(Duration::from_millis(500))?
                .ok_or("writer lock timeout")?;
            file.seek(SeekFrom::Start(0))?;
            file.write_all(&bytes)?;
            file.flush()?;
            guard.release()?;
            writeln!(
                output,
                "{},{generation},{bits},{ticks}",
                started.elapsed().as_nanos()
            )?;
            output.flush()?;
            due += Duration::from_millis(50);
            std::thread::sleep(due.saturating_duration_since(Instant::now()));
        }
        output.flush()?;
        return Ok(());
    }
    if args[1] != "--probe" {
        return Err("unknown mode".into());
    }
    let expected = Snapshot::decode(include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin"))?;
    let mut source = WindowsBusReader::new(path, args[6].clone(), args[3].clone());
    let (mut publisher, reader) = latest_snapshot();
    let periods = [10_u64, 50, 200, 1_000];
    let mut next_read = [started; 4];
    let mut held = [None, None, None, None];
    let mut pinned = None;
    let mut pinned_words = Vec::new();
    let mut tracked: Vec<Weak<Snapshot>> = Vec::with_capacity(8);
    let mut output = std::io::BufWriter::new(std::fs::File::create(&args[5])?);
    writeln!(output, "elapsed_ns,capture_publication_ns,live_snapshot_peak,lan_bits,raw_bits,lan_count,raw_count")?;
    let mut next_capture = started;
    let mut captures = 0_u64;
    let mut peak = 0;
    while started.elapsed() < duration {
        let now = Instant::now();
        if now >= next_capture {
            let read = source
                .snapshot()
                .map_err(|e| format!("probe capture: {e:?}"))?;
            let finished = Instant::now();
            if read.origin != SnapshotOrigin::Captured {
                return Err("probe did not physically capture".into());
            }
            let frame = Arc::clone(&read.snapshot);
            tracked.retain(|weak| weak.strong_count() != 0);
            tracked.push(Arc::downgrade(&frame));
            peak = peak.max(tracked.len());
            let publication_start = Instant::now();
            publisher
                .publish(Ok(read), now, finished, None)
                .map_err(|e| format!("publish: {e:?}"))?;
            let published = Instant::now();
            if frame.unix_seconds()? != expected.unix_seconds()?
                || frame.pair_bits(200)? != frame.pair_bits(400)?
                || frame.pair_bits(402)? != 0x8000_0000
                || frame.pair_bits(404)? != 1
                || frame.zone(Zone::Lan).write_count != frame.zone(Zone::Raw).write_count
                || peak > 7
            {
                return Err("probe bits, fixed COM, metadata or live snapshot bound failed".into());
            }
            if pinned.is_none() {
                pinned_words = frame.words().to_vec();
                pinned = Some(Arc::clone(&frame));
            }
            writeln!(
                output,
                "{},{},{peak},{},{},{},{}",
                now.duration_since(started).as_nanos(),
                (finished.duration_since(now) + published.duration_since(publication_start))
                    .as_nanos(),
                frame.pair_bits(200)?,
                frame.pair_bits(400)?,
                frame.zone(Zone::Lan).write_count,
                frame.zone(Zone::Raw).write_count
            )?;
            captures += 1;
            next_capture += Duration::from_millis(200);
            if next_capture <= published {
                next_capture = published + Duration::from_millis(200);
            }
        }
        for index in 0..periods.len() {
            if now >= next_read[index] {
                held[index] = reader
                    .latest()
                    .map_err(|e| format!("consumer: {e:?}"))?
                    .snapshot;
                next_read[index] = now + Duration::from_millis(periods[index]);
            }
        }
        std::thread::sleep(Duration::from_millis(1));
    }
    let old = pinned.ok_or("no pinned snapshot")?;
    if old.words() != pinned_words || captures < 20 {
        return Err("old snapshot or capture count failed".into());
    }
    for frame in held.iter().flatten() {
        if frame.unix_seconds()? != expected.unix_seconds()? {
            return Err("consumer changed COM time".into());
        }
    }
    output.flush()?;
    println!("probe captures={captures} peak_live_snapshots={peak}; separate from Host process");
    Ok(())
}
