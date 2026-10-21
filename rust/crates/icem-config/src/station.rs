//! Legacy search order. Host ownership precedes writes; publication never overwrites.
use crate::{IniOptions, ini::read_bytes};
use std::{
    fs,
    io::{self, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

#[derive(Debug, Clone)]
pub struct StationPaths {
    pub canonical: PathBuf,
    pub legacy: Vec<PathBuf>,
}
pub struct LoadedStation {
    pub path: PathBuf,
    pub options: IniOptions,
    pub note: Option<String>,
}
impl StationPaths {
    pub fn at(station: &Path, executable: &Path) -> Self {
        Self {
            canonical: station.join("config.ini"),
            legacy: vec![
                station.join("station.ini"),
                executable.join("config.ini"),
                executable.join("station.ini"),
            ],
        }
    }
    pub fn installed() -> io::Result<Self> {
        let station = PathBuf::from(
            std::env::var_os("PROGRAMDATA").unwrap_or_else(|| r"C:\ProgramData".into()),
        )
        .join("Halliburton/iCem/DataMonitoring");
        let exe = std::env::current_exe()?;
        Ok(Self::at(
            &station,
            exe.parent()
                .ok_or_else(|| io::Error::other("missing executable directory"))?,
        ))
    }
    pub fn explicit(path: PathBuf) -> Self {
        Self {
            canonical: path,
            legacy: Vec::new(),
        }
    }
    /// Selection is read-only, including for clients. Access errors are not absence.
    pub fn selected(&self) -> io::Result<&Path> {
        for path in std::iter::once(&self.canonical).chain(&self.legacy) {
            match fs::metadata(path) {
                Ok(_) => return Ok(path),
                Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                Err(e) => return Err(e),
            }
        }
        Ok(&self.canonical)
    }
    /// Called once by the Host owner. Corruption is fail-fast, never auto-reset.
    pub fn load(&self) -> Result<LoadedStation, Box<dyn std::error::Error>> {
        self.load_inner(publish).map_err(|e| format!("Не удалось загрузить конфигурацию {}: {e}. Файл сохранён; исправьте значения и повторите запуск.", self.selected().unwrap_or(&self.canonical).display()).into())
    }
    fn load_inner(
        &self,
        publish: fn(&Path, &[u8]) -> io::Result<()>,
    ) -> Result<LoadedStation, Box<dyn std::error::Error>> {
        let selected = self.selected()?;
        self.load_selected(selected, IniOptions::read_existing(selected), publish)
    }
    fn load_selected(
        &self,
        selected: &Path,
        captured_result: Result<IniOptions, Box<dyn std::error::Error>>,
        publish: fn(&Path, &[u8]) -> io::Result<()>,
    ) -> Result<LoadedStation, Box<dyn std::error::Error>> {
        let options = match captured_result {
            Ok(options) => options,
            Err(e)
                if selected == self.canonical
                    && e.downcast_ref::<io::Error>()
                        .is_some_and(|e| e.kind() == io::ErrorKind::NotFound) =>
            {
                let defaults = IniOptions::default();
                publish(
                    &self.canonical,
                    format!("\u{feff}{}", defaults.render()).as_bytes(),
                )?;
                return Ok(LoadedStation {
                    path: self.canonical.clone(),
                    options: IniOptions::read_existing(&self.canonical)?,
                    note: Some("Создан отсутствующий config.ini со штатными значениями".into()),
                });
            }
            Err(e) => return Err(e),
        };
        if selected == self.canonical {
            return Ok(LoadedStation {
                path: self.canonical.clone(),
                options,
                note: None,
            });
        }
        // Validate before copy. Copy original bytes, including comments/BOM/retired keys.
        let bytes = read_bytes(selected)?;
        match publish(&self.canonical, &bytes) {
            Ok(()) => Ok(LoadedStation {
                path: self.canonical.clone(),
                options: IniOptions::read_existing(&self.canonical)?,
                note: Some(format!(
                    "config migration: {} → {}; исходный файл сохранён",
                    selected.display(),
                    self.canonical.display()
                )),
            }),
            Err(e) => Ok(LoadedStation {
                path: selected.to_owned(),
                options,
                note: Some(format!(
                    "config migration FAILED: {e}; active path: {}",
                    selected.display()
                )),
            }),
        }
    }
}
fn publish(path: &Path, bytes: &[u8]) -> io::Result<()> {
    static SEQUENCE: AtomicU64 = AtomicU64::new(0);
    if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension(format!(
        "ini.{}.{}.{}.tmp",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(io::Error::other)?
            .as_nanos(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    struct Cleanup(PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.0);
        }
    }
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)?;
    let _cleanup = Cleanup(temporary.clone());
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    // Hard-link publication is atomic and no-clobber on NTFS. Readers never see a prefix.
    match fs::hard_link(&temporary, path) {
        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => Ok(()),
        result => result,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    struct Directory(PathBuf);
    impl Directory {
        fn new() -> io::Result<Self> {
            static ID: AtomicU64 = AtomicU64::new(0);
            let path = std::env::temp_dir().join(format!(
                "icem-station-policy-{}-{}",
                std::process::id(),
                ID.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir(&path)?;
            Ok(Self(path))
        }
        fn paths(&self) -> StationPaths {
            StationPaths::explicit(self.0.join("config.ini"))
        }
    }
    impl Drop for Directory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    #[test]
    fn captured_not_found_loads_published_winner_without_overwrite()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = Directory::new()?;
        let paths = root.paths();
        let selected = paths.selected()?;
        let captured = IniOptions::read_existing(selected);
        assert_eq!(
            captured
                .as_ref()
                .err()
                .and_then(|e| e.downcast_ref::<io::Error>())
                .map(io::Error::kind),
            Some(io::ErrorKind::NotFound)
        );
        let winner = IniOptions {
            web_port: 8123,
            ..IniOptions::default()
        };
        let bytes = format!("\u{feff}; operator winner\r\n{}", winner.render());
        publish(&paths.canonical, bytes.as_bytes())?;
        let loaded = paths.load_selected(selected, captured, publish)?;
        assert_eq!(loaded.path, paths.canonical);
        assert_eq!(loaded.options, winner);
        assert_eq!(fs::read(&paths.canonical)?, bytes.as_bytes());
        assert_eq!(fs::read_dir(&root.0)?.count(), 1);
        Ok(())
    }
    #[test]
    fn captured_not_found_rejects_corrupt_winner_without_overwrite()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = Directory::new()?;
        let paths = root.paths();
        let selected = paths.selected()?;
        let captured = IniOptions::read_existing(selected);
        assert_eq!(
            captured
                .as_ref()
                .err()
                .and_then(|e| e.downcast_ref::<io::Error>())
                .map(io::Error::kind),
            Some(io::ErrorKind::NotFound)
        );
        let bytes = b"\xef\xbb\xbf; corrupt operator winner\r\n[station]\nweb_port=broken\n";
        publish(&paths.canonical, bytes)?;
        let error = paths
            .load_selected(selected, captured, publish)
            .err()
            .ok_or("accepted corrupt winner")?;
        assert!(error.downcast_ref::<crate::ConfigError>().is_some());
        assert_eq!(fs::read(&paths.canonical)?, bytes);
        assert_eq!(fs::read_dir(&root.0)?.count(), 1);
        Ok(())
    }
    #[test]
    fn captured_malformed_read_remains_error_after_file_disappears()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = Directory::new()?;
        let paths = root.paths();
        fs::write(&paths.canonical, b"malformed")?;
        let selected = paths.selected()?;
        let captured = IniOptions::read_existing(selected);
        let original = captured
            .as_ref()
            .err()
            .and_then(|e| e.downcast_ref::<crate::ConfigError>())
            .ok_or("expected parse failure")?
            .clone();
        fs::remove_file(&paths.canonical)?;
        let error = paths
            .load_selected(selected, captured, publish)
            .err()
            .ok_or("replaced malformed read with defaults")?;
        assert_eq!(error.downcast_ref::<crate::ConfigError>(), Some(&original));
        assert_eq!(fs::read_dir(&root.0)?.count(), 0);
        Ok(())
    }
    #[test]
    fn captured_permission_denied_never_creates_defaults() -> Result<(), Box<dyn std::error::Error>>
    {
        let root = Directory::new()?;
        let paths = root.paths();
        let selected = paths.selected()?;
        // Policy injection only: this is not a reproduction of an OS ACL failure.
        let captured =
            Err(io::Error::new(io::ErrorKind::PermissionDenied, "captured access failure").into());
        let error = paths
            .load_selected(selected, captured, publish)
            .err()
            .ok_or("replaced denied read with defaults")?;
        let error = error.downcast_ref::<io::Error>().ok_or("lost I/O error")?;
        assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
        assert_eq!(error.to_string(), "captured access failure");
        assert_eq!(fs::read_dir(&root.0)?.count(), 0);
        Ok(())
    }
    #[test]
    fn captured_legacy_not_found_never_creates_defaults() -> Result<(), Box<dyn std::error::Error>>
    {
        let root = Directory::new()?;
        let source = root.0.join("station.ini");
        fs::write(&source, IniOptions::default().render())?;
        let paths = StationPaths {
            canonical: root.0.join("config.ini"),
            legacy: vec![source.clone()],
        };
        let selected = paths.selected()?;
        assert_eq!(selected, source);
        fs::remove_file(&source)?;
        let captured = IniOptions::read_existing(selected);
        assert_eq!(
            captured
                .as_ref()
                .err()
                .and_then(|e| e.downcast_ref::<io::Error>())
                .map(io::Error::kind),
            Some(io::ErrorKind::NotFound)
        );
        let error = paths
            .load_selected(selected, captured, publish)
            .err()
            .ok_or("created defaults for missing legacy")?;
        assert_eq!(
            error.downcast_ref::<io::Error>().map(io::Error::kind),
            Some(io::ErrorKind::NotFound)
        );
        assert_eq!(fs::read_dir(&root.0)?.count(), 0);
        Ok(())
    }
    #[test]
    fn publication_failure_retains_valid_legacy_and_its_bytes()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = std::env::temp_dir().join(format!("icem-copy-denied-{}", std::process::id()));
        fs::create_dir(&root)?;
        let source = root.join("station.ini");
        let bytes = IniOptions::default().render();
        fs::write(&source, &bytes)?;
        let paths = StationPaths {
            canonical: root.join("config.ini"),
            legacy: vec![source.clone()],
        };
        let loaded =
            paths.load_inner(|_, _| Err(io::Error::from(io::ErrorKind::PermissionDenied)))?;
        assert_eq!(loaded.path, source);
        assert_eq!(loaded.options, IniOptions::default());
        assert!(loaded.note.as_deref().is_some_and(|s| s.contains("FAILED")));
        assert!(!paths.canonical.exists());
        assert_eq!(fs::read_to_string(source)?, bytes);
        fs::remove_dir_all(root)?;
        Ok(())
    }
}
