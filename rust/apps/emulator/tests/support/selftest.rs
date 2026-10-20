use std::{
    collections::BTreeMap,
    io::Read,
    path::{Path, PathBuf},
    process::{Command, Output, Stdio},
    time::{Duration, Instant},
};
pub type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
pub struct Stage {
    pub root: PathBuf,
    pub exe: PathBuf,
    pub templates: PathBuf,
    pub cwd: PathBuf,
    pub local: PathBuf,
}
impl Stage {
    pub fn new(label: &str) -> Result<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "icem selftest т {label}-{}-{nonce}",
            std::process::id()
        ));
        let exe = root.join("package/icem-emulator-engine-dev.exe");
        let templates = root.join("package/emulator/templates");
        let cwd = root.join("unrelated cwd");
        let local = root.join("local");
        for p in [&templates, &cwd, &local] {
            std::fs::create_dir_all(p)?;
        }
        std::fs::copy(env!("CARGO_BIN_EXE_icem-emulator-engine-dev"), &exe)?;
        let source =
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../iCemEmulator/templates");
        for entry in std::fs::read_dir(source)? {
            let entry = entry?;
            if entry.path().extension().is_some_and(|x| x == "json") {
                std::fs::copy(entry.path(), templates.join(entry.file_name()))?;
            }
        }
        Ok(Self {
            root,
            exe,
            templates,
            cwd,
            local,
        })
    }
    pub fn command(&self) -> Command {
        let mut command = Command::new(&self.exe);
        command
            .current_dir(&self.cwd)
            .env("LOCALAPPDATA", &self.local);
        command
    }
    pub fn run(&self, args: &[&str]) -> Result<Output> {
        let mut command = self.command();
        command.args(args);
        bounded(command)
    }
    pub fn readonly(&self, yes: bool) -> Result {
        for entry in std::fs::read_dir(&self.templates)? {
            let p = entry?.path();
            let mut permissions = std::fs::metadata(&p)?.permissions();
            permissions.set_readonly(yes);
            std::fs::set_permissions(p, permissions)?;
        }
        Ok(())
    }
}
impl Drop for Stage {
    fn drop(&mut self) {
        let _ = self.readonly(false);
        let _ = std::fs::remove_dir_all(&self.root);
    }
}
pub fn bounded(mut command: Command) -> Result<Output> {
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = command.spawn()?;
    let stdout = child.stdout.take().ok_or("missing stdout")?;
    let stderr = child.stderr.take().ok_or("missing stderr")?;
    let reader = |mut stream: Box<dyn Read + Send>| {
        std::thread::spawn(move || {
            let mut bytes = Vec::new();
            stream.read_to_end(&mut bytes).map(|_| bytes)
        })
    };
    let out = reader(Box::new(stdout));
    let err = reader(Box::new(stderr));
    let deadline = Instant::now() + Duration::from_secs(45);
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break Ok(status),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            other => {
                let _ = child.kill();
                let _ = child.wait();
                break match other {
                    Err(e) => Err(Box::<dyn std::error::Error>::from(e)),
                    _ => Err("selftest child deadline exceeded".into()),
                };
            }
        }
    };
    let stdout = out.join().map_err(|_| "stdout reader panicked")??;
    let stderr = err.join().map_err(|_| "stderr reader panicked")??;
    Ok(Output {
        status: status?,
        stdout,
        stderr,
    })
}
pub fn snapshot(root: &Path) -> Result<BTreeMap<PathBuf, Vec<u8>>> {
    fn visit(root: &Path, path: &Path, map: &mut BTreeMap<PathBuf, Vec<u8>>) -> Result {
        for entry in std::fs::read_dir(path)? {
            let p = entry?.path();
            if p.is_dir() {
                visit(root, &p, map)?;
            } else {
                map.insert(p.strip_prefix(root)?.to_path_buf(), std::fs::read(p)?);
            }
        }
        Ok(())
    }
    let mut map = BTreeMap::new();
    visit(root, root, &mut map)?;
    Ok(map)
}
pub fn report(output: &Output, success: bool) -> Result<i32> {
    let text = std::str::from_utf8(&output.stdout)?;
    assert!(
        text.starts_with("=== iCemEmule Transport Self-Test ==="),
        "{text}"
    );
    let ids = text
        .lines()
        .filter_map(|line| {
            let (id, result) = line.split_once(' ')?;
            if result == "PASS" || result == "FAIL" {
                id.strip_prefix('T')?.parse::<u8>().ok()
            } else {
                None
            }
        })
        .collect::<Vec<_>>();
    assert_eq!(ids, (1..=26).collect::<Vec<_>>(), "{text}");
    if success {
        assert!(
            output.status.success(),
            "{text}\n{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(text.ends_with("ALL TESTS PASSED\n") || text.ends_with("ALL TESTS PASSED\r\n"));
        assert!(text.contains("T5 EVIDENCE:") && text.contains("T20 EVIDENCE:"));
        Ok(0)
    } else {
        assert!(!output.status.success(), "{text}");
        assert!(!text.contains("ALL TESTS PASSED"));
        let summary = text.lines().last().ok_or("missing summary")?;
        let count = summary
            .strip_suffix(" TEST(S) FAILED")
            .ok_or("failure summary")?
            .parse::<i32>()?;
        assert_eq!(
            output.status.code(),
            Some(count),
            "full Windows i32 failCount: {text}"
        );
        Ok(count)
    }
}
