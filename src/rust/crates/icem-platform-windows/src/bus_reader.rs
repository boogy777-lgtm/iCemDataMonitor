//! Pinned .NET BusReader lifecycle, with explicit retained-origin diagnostics.
use icem_bus::{Snapshot, validate_shape};
use icem_core::{Freshness, SnapshotOrigin, SnapshotRead, SnapshotSource, SourceError};
use icem_windows_native::{Mapping, NamedMutex, NativeError};
use std::fs::OpenOptions;
use std::io::Read;
use std::os::windows::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::time::Duration;

pub struct WindowsBusReader {
    path: PathBuf,
    map_name: String,
    mutex_name: String,
    mapping: Option<Mapping>,
    mutex: Option<NamedMutex>,
    length: usize,
    last_good: Option<std::sync::Arc<Snapshot>>,
    last_error: Option<SourceError>,
    fatal: Option<SourceError>,
}
impl WindowsBusReader {
    pub fn new(path: PathBuf, map_name: String, mutex_name: String) -> Self {
        Self {
            path,
            map_name,
            mutex_name,
            mapping: None,
            mutex: None,
            length: 0,
            last_good: None,
            last_error: None,
            fatal: None,
        }
    }
    pub fn is_open(&self) -> bool {
        self.mapping.is_some()
    }
    pub fn last_error(&self) -> Option<SourceError> {
        self.last_error
    }
    fn close(&mut self) {
        self.mapping = None;
        self.mutex = None;
    }
    fn retained(
        &mut self,
        error: SourceError,
        freshness: Freshness,
    ) -> Result<SnapshotRead, SourceError> {
        self.last_error = Some(error);
        self.last_good
            .clone()
            .map(|snapshot| SnapshotRead {
                snapshot,
                freshness,
                origin: SnapshotOrigin::Retained,
            })
            .ok_or(error)
    }
    fn open(&mut self) -> Result<(), SourceError> {
        // Source BusMapping.ReadShape: read only geometry, never disk-read payload as MMF substitute.
        let mut file = OpenOptions::new()
            .read(true)
            .share_mode(3)
            .open(&self.path)
            .map_err(io_error)?;
        let length = usize::try_from(file.metadata().map_err(io_error)?.len())
            .map_err(|_| SourceError::InvalidLayout)?;
        let mut header = [0; 10];
        file.read_exact(&mut header)
            .map_err(|_| SourceError::InvalidLayout)?;
        validate_shape(&header, length).map_err(|_| SourceError::InvalidLayout)?;
        drop(file);
        let (mapping, attached) = match Mapping::open_existing(&self.map_name) {
            Ok(mapping) => (mapping, true),
            Err(error) if matches!(error.code, 2 | 3) => (
                Mapping::unnamed_read_only(&self.path).map_err(io_error)?,
                false,
            ),
            Err(error) => return Err(native_error(error)),
        };
        mapping.probe(length).map_err(|error| {
            if attached && matches!(error.code, 5 | 87) {
                SourceError::SectionCapacity
            } else {
                native_error(error)
            }
        })?;
        // Never create a mutex for a nonexistent/foreign/legacy file; no read-only named section created.
        let mutex = NamedMutex::open_or_create(&self.mutex_name).map_err(native_error)?;
        self.mapping = Some(mapping);
        self.mutex = Some(mutex);
        self.length = length;
        Ok(())
    }
}
impl SnapshotSource for WindowsBusReader {
    fn is_open(&self) -> bool {
        WindowsBusReader::is_open(self)
    }
    fn last_error(&self) -> Option<SourceError> {
        self.last_error
    }
    fn snapshot(&mut self) -> Result<SnapshotRead, SourceError> {
        // Same priority as .NET: missing backing name wins over an orphan section.
        if self.is_open() && !self.path.is_file() {
            self.close();
            return self.retained(SourceError::Unavailable, Freshness::LastGood);
        }
        if let Some(error) = self.fatal {
            return self.retained(error, Freshness::Fresh); // D09: legacy TryOpen failure replays Stale=false cache
        }
        if !self.is_open()
            && let Err(error) = self.open()
        {
            if matches!(
                error,
                SourceError::AccessDenied | SourceError::SectionCapacity
            ) {
                self.fatal = Some(error);
                self.last_error = Some(error);
                return Err(error); // first fatal exception is surfaced, then sticky fail-fast
            }
            return self.retained(error, Freshness::Fresh);
        }
        let result = (|| {
            let mutex = self.mutex.as_ref().ok_or(SourceError::Io)?;
            let mapping = self.mapping.as_ref().ok_or(SourceError::Io)?;
            let Some(guard) = mutex
                .lock(Duration::from_millis(500))
                .map_err(native_error)?
            else {
                return Err(SourceError::LockTimeout);
            };
            // No await; !Send guard stays on this thread. Kernel copy creates no Rust MMF references.
            let bytes = mapping.copy_bytes(self.length).map_err(native_error);
            guard.release().map_err(native_error)?;
            bytes // decode outside the Windows lock
        })();
        match result {
            Ok(bytes) => match Snapshot::decode(&bytes) {
                Ok(snapshot) => {
                    self.last_error = None;
                    let snapshot = std::sync::Arc::new(snapshot);
                    self.last_good = Some(std::sync::Arc::clone(&snapshot));
                    Ok(SnapshotRead {
                        snapshot,
                        freshness: Freshness::Fresh,
                        origin: SnapshotOrigin::Captured,
                    })
                }
                Err(_) => self.retained(SourceError::InvalidLayout, Freshness::Fresh), // D09 actual source, no silent stale fix
            },
            Err(SourceError::LockTimeout) => {
                self.retained(SourceError::LockTimeout, Freshness::LastGood)
            }
            Err(error) => self.retained(error, Freshness::Fresh),
        }
    }
}
fn native_error(error: NativeError) -> SourceError {
    match error.code {
        2 | 3 => SourceError::Unavailable,
        5 => SourceError::AccessDenied,
        _ => SourceError::Io,
    }
}
fn io_error(error: std::io::Error) -> SourceError {
    match error.kind() {
        std::io::ErrorKind::NotFound => SourceError::Unavailable,
        std::io::ErrorKind::PermissionDenied => SourceError::AccessDenied,
        _ => SourceError::Io,
    }
}
