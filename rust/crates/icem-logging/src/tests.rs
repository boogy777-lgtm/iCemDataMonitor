use super::*;
#[test]
fn disk_full_rolls_back_partial_record_and_reports_rollback_failure()
-> Result<(), Box<dyn std::error::Error>> {
    struct Full {
        data: std::io::Cursor<Vec<u8>>,
        fail: bool,
        rollback: bool,
    }
    impl Write for Full {
        fn write(&mut self, b: &[u8]) -> io::Result<usize> {
            if self.fail {
                return Err(io::Error::from_raw_os_error(112));
            }
            self.fail = true;
            self.data.write(&b[..b.len().min(3)])
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    impl Seek for Full {
        fn seek(&mut self, p: SeekFrom) -> io::Result<u64> {
            self.data.seek(p)
        }
    }
    impl RecordFile for Full {
        fn resize(&mut self, n: u64) -> io::Result<()> {
            if !self.rollback {
                return Err(io::Error::other("rollback denied"));
            }
            self.data.get_mut().truncate(n as usize);
            Ok(())
        }
        fn sync(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    let mut file = Full {
        data: std::io::Cursor::new(b"old\r\n".to_vec()),
        fail: false,
        rollback: true,
    };
    assert!(append_record(&mut file, b"new record\r\n").is_err());
    assert_eq!(file.data.get_ref(), b"old\r\n");
    file.fail = false;
    file.rollback = false;
    assert!(
        append_record(&mut file, b"new record\r\n")
            .err()
            .ok_or("missing error")?
            .to_string()
            .contains("recovery required")
    );
    Ok(())
}
#[cfg(windows)]
mod windows {
    use super::*;
    use std::{
        fs,
        sync::{
            Arc, Mutex,
            atomic::{AtomicU64, Ordering},
        },
    };
    type Result = std::result::Result<(), Box<dyn std::error::Error>>;
    static NEXT: AtomicU64 = AtomicU64::new(0);
    struct Root(PathBuf);
    impl Root {
        fn new() -> io::Result<Self> {
            let p = std::env::temp_dir().join(format!(
                "icem-logs-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&p)?;
            Ok(Self(p))
        }
    }
    impl Drop for Root {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    fn content(root: &Root) -> io::Result<String> {
        fs::read_to_string(root.0.join(Owner::Host.directory()).join("startup.log"))
    }
    #[test]
    fn off_on_write_off_restart_clear_both_states() -> Result {
        let root = Root::new()?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        assert_eq!(logs.status()["enabled"], true);
        logs.request("set", Some(false))?;
        logs.record("must not appear");
        assert_eq!(logs.written, 0);
        logs.request("set", Some(true))?;
        logs.record("first");
        assert!(content(&root)?.contains("first"));
        logs.request("set", Some(false))?;
        logs.record("must not appear");
        assert!(!content(&root)?.contains("must not appear"));
        drop(logs);
        let mut logs = Logs::open(&root.0, Owner::Host);
        assert_eq!(logs.status()["enabled"], false);
        logs.request("clear", None)?;
        assert!(
            !root
                .0
                .join(Owner::Host.directory())
                .join("startup.log")
                .exists()
        );
        logs.request("set", Some(true))?;
        logs.record("before clear");
        logs.request("clear", None)?;
        assert_eq!(content(&root)?, "");
        logs.record("after clear");
        assert!(content(&root)?.contains("after clear"));
        logs.request("clear", None)?;
        logs.request("clear", None)?;
        assert_eq!(logs.status()["enabled"], true);
        Ok(())
    }
    #[test]
    fn concurrent_write_clear_rotation_are_serialized_and_bounded() -> Result {
        let root = Root::new()?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        logs.limit = 4096;
        logs.keep = 2048;
        let logs = Arc::new(Mutex::new(logs));
        std::thread::scope(|scope| {
            let writer = logs.clone();
            scope.spawn(move || {
                for i in 0..400 {
                    writer
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .record(&format!("record-{i:04} Привет"));
                }
            });
            let cleaner = logs.clone();
            scope.spawn(move || {
                for _ in 0..20 {
                    let _ = cleaner
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .request("clear", None);
                }
            });
        });
        let mut logs = logs.lock().map_err(|_| "poisoned")?;
        logs.record("final record");
        let text = content(&root)?;
        assert!(text.len() <= 4096);
        assert!(text.ends_with("final record\r\n"));
        assert!(
            text.lines()
                .all(|line| line.contains("Z ") && line.contains("record"))
        );
        assert_eq!(logs.status()["error"], "");
        Ok(())
    }
    #[test]
    fn locked_file_partial_clear_preserves_neighbors_and_setting() -> Result {
        use std::os::windows::fs::OpenOptionsExt;
        let root = Root::new()?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        logs.record("old");
        let dir = root.0.join(Owner::Host.directory());
        for name in ["Data.dat", "config.ini", "foreign.log"] {
            fs::write(dir.join(name), b"keep")?;
        }
        fs::create_dir(dir.join("nested"))?;
        fs::write(dir.join("nested/startup.log"), b"keep")?;
        fs::write(dir.join("data-bus.log"), b"busy")?;
        let busy = fs::OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(dir.join("data-bus.log"))?;
        assert!(logs.request("clear", None).is_err());
        assert_eq!(content(&root)?, "");
        assert!(
            logs.status()["error"]
                .as_str()
                .is_some_and(|e| e.contains("data-bus.log"))
        );
        logs.record("continues despite partial clear");
        assert!(content(&root)?.contains("continues"));
        for name in [
            "Data.dat",
            "config.ini",
            "foreign.log",
            "nested/startup.log",
        ] {
            assert_eq!(fs::read(dir.join(name))?, b"keep");
        }
        assert!(!dir.join(SETTINGS).exists()); // absent setting still means default ON
        drop(busy);
        logs.request("clear", None)?;
        assert!(!dir.join("data-bus.log").exists());
        Ok(())
    }
    #[test]
    fn competing_owner_hardlink_junction_and_unavailable_directory_fail_closed() -> Result {
        let root = Root::new()?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        logs.record("owner");
        let competing = Logs::open(&root.0, Owner::Host);
        assert_eq!(competing.status()["available"], false);
        drop(competing);
        let dir = root.0.join(Owner::Host.directory());
        let outside = root.0.join("outside.txt");
        fs::write(&outside, b"must survive")?;
        fs::hard_link(&outside, dir.join("data-bus.log"))?;
        assert!(logs.request("clear", None).is_err());
        assert_eq!(fs::read(&outside)?, b"must survive");
        drop(logs);
        let renamed = root.0.join("old");
        fs::rename(&dir, &renamed)?;
        let result = std::process::Command::new("cmd")
            .args(["/C", "mklink", "/J"])
            .arg(&dir)
            .arg(&renamed)
            .output()?;
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        let logs = Logs::open(&root.0, Owner::Host);
        assert_eq!(logs.status()["available"], false);
        drop(logs);
        fs::remove_dir(&dir)?;
        let file_root = root.0.join("file-root");
        fs::write(&file_root, b"file")?;
        let mut logs = Logs::open(&file_root, Owner::Host);
        assert!(logs.request("set", Some(true)).is_err());
        assert_eq!(fs::read(file_root)?, b"file");
        Ok(())
    }
    #[test]
    fn readonly_log_locked_preference_and_corrupt_preference_report_truth() -> Result {
        use std::os::windows::fs::OpenOptionsExt;
        let root = Root::new()?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        logs.request("set", Some(false))?;
        let dir = root.0.join(Owner::Host.directory());
        fs::write(dir.join("startup.log"), b"old\r\n")?;
        let mut permissions = fs::metadata(dir.join("startup.log"))?.permissions();
        let original_permissions = permissions.clone();
        permissions.set_readonly(true);
        fs::set_permissions(dir.join("startup.log"), permissions)?;
        assert!(logs.request("set", Some(true)).is_err());
        assert_eq!(logs.status()["enabled"], false);
        fs::set_permissions(dir.join("startup.log"), original_permissions)?;
        let locked = fs::OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(dir.join(SETTINGS))?;
        assert!(logs.request("set", Some(true)).is_err());
        assert_eq!(logs.status()["enabled"], false);
        drop(locked);
        drop(logs);
        fs::write(dir.join(SETTINGS), b"invalid")?;
        let mut logs = Logs::open(&root.0, Owner::Host);
        logs.record("forbidden");
        assert_eq!(logs.status()["enabled"], Value::Null);
        assert!(
            !logs.status()["error"]
                .as_str()
                .ok_or("missing error")?
                .is_empty()
        );
        assert_eq!(fs::read(dir.join(SETTINGS))?, b"invalid");
        Ok(())
    }
    #[test]
    fn incomplete_tail_recovery_and_emulator_retention_touch_only_owned_file() -> Result {
        let root = Root::new()?;
        let dir = root.0.join(Owner::Emulator.directory());
        fs::create_dir(&dir)?;
        fs::write(dir.join("foreign.log"), b"foreign")?;
        fs::write(dir.join("crash.log"), b"old\r\npartial")?;
        let mut logs = Logs::open(&root.0, Owner::Emulator);
        logs.record("new");
        assert!(fs::read_to_string(dir.join("crash.log"))?.starts_with("old\r\n"));
        assert!(!fs::read_to_string(dir.join("crash.log"))?.contains("partial"));
        drop(logs);
        let file = File::options().write(true).open(dir.join("crash.log"))?;
        file.set_modified(
            std::time::SystemTime::now() - std::time::Duration::from_secs(8 * 86400),
        )?;
        drop(file);
        let mut logs = Logs::open(&root.0, Owner::Emulator);
        logs.record("retained new");
        assert!(!fs::read_to_string(dir.join("crash.log"))?.contains("old"));
        assert_eq!(fs::read(dir.join("foreign.log"))?, b"foreign");
        Ok(())
    }
}
