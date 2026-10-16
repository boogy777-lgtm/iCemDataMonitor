use std::io::{self, BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc::{self, Receiver};
use std::time::Duration;
static NEXT: AtomicUsize = AtomicUsize::new(0);
pub struct Station {
    dir: PathBuf,
    pub path: PathBuf,
    pub map: String,
    pub mutex: String,
}
impl Station {
    pub fn new() -> io::Result<Self> {
        let id = format!(
            "{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let dir = std::env::temp_dir().join(format!("icem-rust-bus-{id}"));
        std::fs::create_dir_all(&dir)?;
        Ok(Self {
            path: dir.join("Data.dat"),
            dir,
            map: format!("Local\\icem-test-map-{id}"),
            mutex: format!("Global\\icem-test-mutex-{id}"),
        })
    }
    pub fn seed(&self, count: u16) -> io::Result<()> {
        let mut bytes = include_bytes!("../fixtures/bus-v5-minimal.bin").to_vec();
        bytes[8..10].copy_from_slice(&count.to_le_bytes());
        bytes.resize(64 + 2 * usize::from(count), 0);
        std::fs::write(&self.path, bytes)
    }
}
impl Drop for Station {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}
pub struct Producer {
    child: Child,
    input: ChildStdin,
    output: Receiver<io::Result<String>>,
}
impl Producer {
    pub fn start(station: &Station, small: bool) -> Result<Self, Box<dyn std::error::Error>> {
        let mut command = Command::new("pwsh");
        command
            .arg("-NoProfile")
            .arg("-File")
            .arg(
                PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .join("../../tests/support/BusProducer.ps1"),
            )
            .arg("-Path")
            .arg(&station.path)
            .arg("-Map")
            .arg(&station.map)
            .arg("-Mutex")
            .arg(&station.mutex)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit());
        if small {
            command.arg("-SmallSection");
        }
        let mut child = command.spawn()?;
        let input = child.stdin.take().ok_or("no producer stdin")?;
        let stdout = child.stdout.take().ok_or("no producer stdout")?;
        let (send, output) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                if send.send(line).is_err() {
                    break;
                }
            }
        });
        let producer = Self {
            child,
            input,
            output,
        };
        assert_eq!(producer.line()?, "READY");
        Ok(producer)
    }
    fn line(&self) -> io::Result<String> {
        self.output
            .recv_timeout(Duration::from_secs(30))
            .map_err(|error| io::Error::other(error.to_string()))?
    }
    pub fn command(&mut self, command: &str, expected: &str) -> io::Result<()> {
        writeln!(self.input, "{command}")?;
        self.input.flush()?;
        assert_eq!(self.line()?, expected);
        Ok(())
    }
}
impl Drop for Producer {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
