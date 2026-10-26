#![cfg(windows)]
use icem_core::{Freshness, SnapshotOrigin, SnapshotSource, SourceError};
use icem_platform_windows::WindowsBusReader;
use std::time::{Duration, Instant};
#[path = "../../../tests/support/windows.rs"]
mod windows;
use windows::{Producer, Station};
type TestResult = Result<(), Box<dyn std::error::Error>>;
fn make_reader(station: &Station) -> WindowsBusReader {
    WindowsBusReader::new(
        station.path.clone(),
        station.map.clone(),
        station.mutex.clone(),
    )
}
fn read(
    reader: &mut WindowsBusReader,
) -> Result<icem_core::SnapshotRead, Box<dyn std::error::Error>> {
    reader
        .snapshot()
        .map_err(|error| format!("{error:?}").into())
}

#[test]
fn absent_foreign_and_legacy_never_create_or_rewrite_producer_file() -> TestResult {
    let station = Station::new()?;
    let mut reader = make_reader(&station);
    assert!(matches!(reader.snapshot(), Err(SourceError::Unavailable)));
    assert!(!station.path.exists());
    station.seed(650)?;
    let original = std::fs::read(&station.path)?;
    let mut legacy = original.clone();
    legacy[6] = 4;
    std::fs::write(&station.path, &legacy)?;
    assert!(matches!(reader.snapshot(), Err(SourceError::InvalidLayout)));
    assert_eq!(std::fs::read(&station.path)?, legacy);
    std::fs::write(&station.path, b"foreign")?;
    assert!(matches!(reader.snapshot(), Err(SourceError::InvalidLayout)));
    assert_eq!(std::fs::read(&station.path)?, b"foreign");
    std::fs::write(&station.path, &original)?;
    assert_eq!(read(&mut reader)?.snapshot.register_count(), 650);
    assert_eq!(std::fs::read(&station.path)?, original);
    Ok(())
}
#[test]
fn named_dotnet_write_is_visible_and_previous_snapshot_remains_owned() -> TestResult {
    let station = Station::new()?;
    station.seed(650)?;
    let mut producer = Producer::start(&station, false)?;
    let mut reader = make_reader(&station);
    let before = read(&mut reader)?;
    producer.command("write 6 4660", "WRITTEN")?;
    let after = read(&mut reader)?;
    assert_eq!(after.snapshot.words()[6], 0x1234);
    assert_ne!(before.snapshot.words()[6], 0x1234);
    assert_eq!(after.origin, SnapshotOrigin::Captured);
    assert_eq!(after.freshness, Freshness::Fresh);
    assert!(reader.is_open());
    Ok(())
}
#[test]
fn host_first_unnamed_mapping_does_not_poison_producer_name() -> TestResult {
    let station = Station::new()?;
    station.seed(650)?;
    let mut reader = make_reader(&station);
    read(&mut reader)?; // no producer section exists: reader must map unnamed
    let mut producer = Producer::start(&station, false)?;
    producer.command("write 6 22136", "WRITTEN")?;
    assert_eq!(read(&mut reader)?.snapshot.words()[6], 0x5678);
    Ok(())
}
#[test]
fn mutex_timeout_retains_cache_and_recovers_after_release() -> TestResult {
    let station = Station::new()?;
    station.seed(650)?;
    let mut producer = Producer::start(&station, false)?;
    let mut reader = make_reader(&station);
    let before = read(&mut reader)?;
    producer.command("hold", "HELD")?;
    let start = Instant::now();
    let stale = read(&mut reader)?;
    assert!(start.elapsed() >= Duration::from_millis(450));
    assert!(start.elapsed() < Duration::from_secs(5));
    assert_eq!(stale.freshness, Freshness::LastGood);
    assert_eq!(stale.origin, SnapshotOrigin::Retained);
    assert_eq!(stale.snapshot.words(), before.snapshot.words());
    assert_eq!(reader.last_error(), Some(SourceError::LockTimeout));
    let mut cold = make_reader(&station);
    assert!(matches!(cold.snapshot(), Err(SourceError::LockTimeout)));
    producer.command("release", "RELEASED")?;
    assert_eq!(read(&mut reader)?.origin, SnapshotOrigin::Captured);
    assert_eq!(reader.last_error(), None);
    Ok(())
}
#[test]
fn abandoned_mutex_is_acquired_and_released_on_same_thread() -> TestResult {
    let station = Station::new()?;
    station.seed(650)?;
    let mut producer = Producer::start(&station, false)?;
    let mut reader = make_reader(&station);
    read(&mut reader)?;
    producer.command("abandon", "ABANDONED")?;
    assert_eq!(read(&mut reader)?.origin, SnapshotOrigin::Captured);
    producer.command("write 6 43981", "WRITTEN")?;
    assert_eq!(read(&mut reader)?.snapshot.words()[6], 0xabcd);
    Ok(())
}
#[test]
fn section_capacity_failure_is_sticky_until_reader_restart() -> TestResult {
    let station = Station::new()?;
    station.seed(10_001)?;
    let producer = Producer::start(&station, true)?;
    let mut reader = make_reader(&station);
    assert!(matches!(
        reader.snapshot(),
        Err(SourceError::SectionCapacity)
    ));
    assert!(!reader.is_open());
    drop(producer);
    assert!(matches!(
        reader.snapshot(),
        Err(SourceError::SectionCapacity)
    )); // deliberately sticky, not auto-repaired
    let mut restarted = make_reader(&station);
    assert_eq!(read(&mut restarted)?.snapshot.register_count(), 10_001);
    Ok(())
}
#[test]
fn file_name_disappearance_beats_orphan_section_and_reopens_new_file() -> TestResult {
    let station = Station::new()?;
    station.seed(650)?;
    let mut producer = Producer::start(&station, false)?;
    let mut reader = make_reader(&station);
    read(&mut reader)?;
    producer.command("orphan", "MOVED")?;
    let retained = read(&mut reader)?;
    assert_eq!(retained.freshness, Freshness::LastGood);
    assert!(!reader.is_open());
    // Preserve actual D09: subsequent failed TryOpen replays Stale=false, but origin stays honest.
    let retained = read(&mut reader)?;
    assert_eq!(retained.freshness, Freshness::Fresh);
    assert_eq!(retained.origin, SnapshotOrigin::Retained);
    drop(producer);
    station.seed(1000)?;
    assert_eq!(read(&mut reader)?.snapshot.register_count(), 1000);
    Ok(())
}
