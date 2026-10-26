//! Bounded latest-state cell. This module never reads the source or interprets
//! producer clocks, zone health, or legacy D09 freshness.
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use icem_bus::Snapshot;

use crate::{SnapshotOrigin, SnapshotRead, SourceError};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureStatus {
    Initial,
    Captured,
    /// Source supplied retained data; legacy Fresh does not make it a capture.
    Retained {
        error: Option<SourceError>,
    },
    Failed(SourceError),
}

#[derive(Debug, Clone)]
pub struct PublishedSnapshot {
    pub snapshot: Option<Arc<Snapshot>>,
    /// Local successful physical capture sequence, never a producer update ID.
    pub capture_generation: u64,
    pub captured_at_monotonic: Option<Instant>,
    /// Completion of the latest attempt, including failed/retained attempts.
    pub completed_at_monotonic: Option<Instant>,
    pub status: CaptureStatus,
}

impl PublishedSnapshot {
    /// Acquisition age only. COM time and zone writer clocks stay in Snapshot.
    /// A backwards injected clock has no valid age; absence has no age either.
    pub fn capture_age(&self, now: Instant) -> Option<Duration> {
        now.checked_duration_since(self.captured_at_monotonic?)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PublicationError {
    Poisoned,
    GenerationExhausted,
    NonMonotonicTime,
}

/// Sole publisher capability. Deliberately not Clone; publication requires &mut.
///
/// ```compile_fail
/// let (publisher, _) = icem_core::publication::latest_snapshot();
/// let second_writer = publisher.clone();
/// ```
pub struct SnapshotPublisher {
    cell: Arc<Mutex<PublishedSnapshot>>,
}

/// Cloneable read-only capability. Reads clone an Arc, never Snapshot's body.
///
/// ```compile_fail
/// let (_, mut reader) = icem_core::publication::latest_snapshot();
/// let now = std::time::Instant::now();
/// reader.publish(Err(icem_core::SourceError::Io), now, now, None);
/// ```
#[derive(Clone)]
pub struct SnapshotReader {
    cell: Arc<Mutex<PublishedSnapshot>>,
}

pub fn latest_snapshot() -> (SnapshotPublisher, SnapshotReader) {
    let cell = Arc::new(Mutex::new(PublishedSnapshot {
        snapshot: None,
        capture_generation: 0,
        captured_at_monotonic: None,
        completed_at_monotonic: None,
        status: CaptureStatus::Initial,
    }));
    (
        SnapshotPublisher {
            cell: Arc::clone(&cell),
        },
        SnapshotReader { cell },
    )
}

impl SnapshotReader {
    pub fn latest(&self) -> Result<PublishedSnapshot, PublicationError> {
        self.cell
            .lock()
            .map(|state| state.clone())
            .map_err(|_| PublicationError::Poisoned)
    }
}

impl SnapshotPublisher {
    /// Times are injected monotonic observations, not UTC or source timestamps.
    /// Only Captured installs bytes and advances generation/capture time. Retained
    /// input is discarded in favor of this owner's last successful capture, even
    /// when its legacy Fresh flag is set. With no success, absence is preserved.
    /// `source_error` carries the source's last_error on a retained return.
    pub fn publish(
        &mut self,
        result: Result<SnapshotRead, SourceError>,
        captured_at: Instant,
        completed_at: Instant,
        source_error: Option<SourceError>,
    ) -> Result<(), PublicationError> {
        let mut state = self.cell.lock().map_err(|_| PublicationError::Poisoned)?;
        if captured_at > completed_at
            || state
                .completed_at_monotonic
                .is_some_and(|previous| previous > completed_at)
            || (matches!(&result, Ok(read) if read.origin == SnapshotOrigin::Captured)
                && state
                    .completed_at_monotonic
                    .is_some_and(|previous| previous > captured_at))
        {
            return Err(PublicationError::NonMonotonicTime);
        }
        match result {
            Ok(read) => match read.origin {
                SnapshotOrigin::Captured => {
                    let generation = state
                        .capture_generation
                        .checked_add(1)
                        .ok_or(PublicationError::GenerationExhausted)?;
                    state.snapshot = Some(read.snapshot);
                    state.capture_generation = generation;
                    state.captured_at_monotonic = Some(captured_at);
                    state.status = CaptureStatus::Captured;
                }
                SnapshotOrigin::Retained => {
                    state.status = CaptureStatus::Retained {
                        error: source_error,
                    };
                }
            },
            Err(error) => state.status = CaptureStatus::Failed(error),
        }
        state.completed_at_monotonic = Some(completed_at);
        Ok(())
    }
}
