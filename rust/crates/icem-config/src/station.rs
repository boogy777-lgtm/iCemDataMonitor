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
        let options = match IniOptions::read_existing(selected) {
            Ok(options) => options,
            Err(e) if selected == self.canonical && !self.canonical.try_exists()? => {
                let _ = e;
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
