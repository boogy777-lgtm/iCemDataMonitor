use icem_config::{IniOptions, StationPaths};
use std::{
    fs,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};
type Result = std::result::Result<(), Box<dyn std::error::Error>>;
struct Directory(PathBuf);
impl Directory {
    fn new() -> std::io::Result<Self> {
        static ID: AtomicU64 = AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "icem-station-{}-{}",
            std::process::id(),
            ID.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path)?;
        Ok(Self(path))
    }
    fn paths(&self) -> StationPaths {
        StationPaths::at(&self.0.join("station"), &self.0.join("bin"))
    }
}
impl Drop for Directory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
#[test]
fn absent_creates_defaults_then_preserves_all_original_bytes() -> Result {
    let root = Directory::new()?;
    let paths = root.paths();
    let first = paths.load()?;
    assert_eq!(first.options, IniOptions::default());
    assert_eq!(first.path, paths.canonical);
    let custom = format!(
        "; operator comment\r\n{}",
        first.options.render().replace("modbus_on=1", "modbus_on=0")
    );
    fs::write(&paths.canonical, &custom)?;
    assert_eq!(paths.load()?.options.modbus_on, 0);
    assert_eq!(fs::read_to_string(&paths.canonical)?, custom);
    assert!(!root.0.join("station/Data.dat").exists());
    Ok(())
}
#[test]
fn legacy_precedence_copy_only_and_active_write_path() -> Result {
    let root = Directory::new()?;
    let paths = root.paths();
    for (i, path) in paths.legacy.iter().enumerate().rev() {
        fs::create_dir_all(path.parent().ok_or("parent")?)?;
        let text = format!(
            "; candidate {i}\n{}",
            IniOptions {
                web_port: 8100 + i as i32,
                ..IniOptions::default()
            }
            .render()
        );
        fs::write(path, &text)?;
    }
    let original = fs::read(&paths.legacy[0])?;
    let loaded = paths.load()?;
    assert_eq!(loaded.options.web_port, 8100);
    assert_eq!(loaded.path, paths.canonical);
    assert_eq!(fs::read(&paths.canonical)?, original);
    assert_eq!(fs::read(&paths.legacy[0])?, original);
    assert_eq!(paths.load()?.options.web_port, 8100);
    Ok(())
}
#[test]
fn corrupt_canonical_blocks_valid_legacy_and_is_never_overwritten() -> Result {
    let root = Directory::new()?;
    let paths = root.paths();
    fs::create_dir_all(root.0.join("station"))?;
    fs::write(&paths.legacy[0], IniOptions::default().render())?;
    for bytes in [b"invalid".as_slice(), b"", b"[station]\nmodbus_on=999\n"] {
        fs::write(&paths.canonical, bytes)?;
        let error = paths.load().err().ok_or("accepted corrupt configuration")?;
        assert!(error.to_string().contains("config.ini"));
        assert_eq!(fs::read(&paths.canonical)?, bytes);
    }
    Ok(())
}
#[test]
fn corrupt_legacy_is_preserved_without_publication_and_oversize_is_rejected() -> Result {
    let root = Directory::new()?;
    let paths = root.paths();
    fs::create_dir_all(root.0.join("station"))?;
    fs::write(&paths.legacy[0], b"broken")?;
    assert!(paths.load().is_err());
    assert!(!paths.canonical.exists());
    fs::write(&paths.legacy[0], vec![b';'; 1_048_577])?;
    assert!(paths.load().is_err());
    Ok(())
}
#[test]
fn directory_instead_of_canonical_file_is_an_error() -> Result {
    let root = Directory::new()?;
    let source = root.0.join("station.ini");
    fs::write(&source, IniOptions::default().render())?;
    // Selection has a distinct absent target; failure is injected at publication.
    let paths = StationPaths {
        canonical: root.0.join("missing/config.ini"),
        legacy: vec![source.clone()],
    };
    fs::create_dir(root.0.join("missing"))?;
    // A same-name directory counts as present and fails honestly, never falls through.
    fs::create_dir(&paths.canonical)?;
    assert!(paths.load().is_err());
    fs::remove_dir(&paths.canonical)?;
    // Read-only filesystem behavior is token/platform dependent, tested on Windows separately.
    assert_eq!(paths.load()?.path, paths.canonical);
    assert!(source.exists());
    Ok(())
}
#[test]
fn concurrent_creation_publishes_only_complete_valid_file() -> Result {
    let root = Directory::new()?;
    let paths = root.paths();
    std::thread::scope(|scope| {
        let handles: Vec<_> = (0..8)
            .map(|_| {
                let paths = &paths;
                scope.spawn(move || paths.load().map(|v| v.options).map_err(|e| e.to_string()))
            })
            .collect();
        for handle in handles {
            assert_eq!(
                handle.join().map_err(|_| "thread panicked")??,
                IniOptions::default()
            );
        }
        Ok::<_, Box<dyn std::error::Error>>(())
    })?;
    assert_eq!(
        fs::read_dir(paths.canonical.parent().ok_or("parent")?)?.count(),
        1
    );
    Ok(())
}
