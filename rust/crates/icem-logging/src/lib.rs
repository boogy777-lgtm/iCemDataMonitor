//! One serialized file owner per application. No background threads or global logger.
#![forbid(unsafe_code)]
use serde_json::{Value, json};
use std::{
    fs::File,
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

pub const MAX_BYTES: u64 = 25 * 1024 * 1024;
pub const KEEP_BYTES: u64 = MAX_BYTES / 2;
pub const MAX_RECORD: usize = 8192;
const SETTINGS: &str = "rust-logging-v1.json";
const TEMP: &str = "rust-logging.tmp";

#[derive(Clone, Copy, Debug)]
pub enum Owner {
    Host,
    Shell,
    Emulator,
}
impl Owner {
    pub fn directory(self) -> &'static str {
        match self {
            Self::Host => "iCemDataMonitor.Host",
            Self::Shell => "iCemDataMonitor.Shell",
            Self::Emulator => "iCemEmulator",
        }
    }
    fn files(self) -> &'static [&'static str] {
        match self {
            Self::Host => &["startup.log", "data-bus.log"],
            Self::Shell => &["shell-crash.log"],
            Self::Emulator => &["crash.log"],
        }
    }
}
#[cfg(windows)]
use icem_windows_native::LogDirectory as Directory;
#[cfg(windows)]
use icem_windows_native::LogFile as OwnedFile;
#[cfg(not(windows))]
type OwnedFile = File;
#[cfg(not(windows))]
struct Directory;
#[cfg(not(windows))]
impl Directory {
    fn open(_: &Path) -> io::Result<Self> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "file logs require Windows",
        ))
    }
    fn open_file(&self, _: &str, _: bool) -> io::Result<File> {
        Err(io::Error::other("unsupported"))
    }
    fn remove_file(&self, _: &str) -> io::Result<bool> {
        Err(io::Error::other("unsupported"))
    }
    fn replace(&self, _: &File, _: &str) -> io::Result<()> {
        Err(io::Error::other("unsupported"))
    }
}

/// Kept by the existing application/Bridge owner, never by the GPUI thread.
pub struct Logs {
    path: PathBuf,
    owner: Owner,
    directory: Option<Directory>,
    enabled: Option<bool>,
    writer: Option<OwnedFile>,
    active_file: usize,
    error: String,
    operation: String,
    faulted: bool,
    written: u64,
    limit: u64,
    keep: u64,
}
/// Lazy shared seam for existing non-UI application workers. No queue, thread or global state.
pub struct SharedLogs {
    owner: Owner,
    logs: std::sync::Mutex<Option<Logs>>,
}
impl SharedLogs {
    pub fn new(owner: Owner) -> Self {
        Self {
            owner,
            logs: std::sync::Mutex::new(None),
        }
    }
    pub fn request(&self, action: &str, enabled: Option<bool>) -> Result<Value, String> {
        let mut logs = self.logs.lock().map_err(|_| "log owner poisoned")?;
        let logs = logs.get_or_insert_with(|| Logs::installed(self.owner));
        let ok = logs.request(action, enabled).is_ok();
        Ok(json!({"ok":ok,"data":logs.status()}))
    }
    pub fn record(&self, message: &str) {
        if let Ok(mut logs) = self.logs.lock() {
            logs.get_or_insert_with(|| Logs::installed(self.owner))
                .record(message);
        }
    }
}
impl Logs {
    pub fn installed(owner: Owner) -> Self {
        let root = PathBuf::from(
            std::env::var_os("PROGRAMDATA").unwrap_or_else(|| r"C:\ProgramData".into()),
        )
        .join("Halliburton/iCem/DataMonitoring/Logs");
        Self::open(&root, owner)
    }
    /// Root is chosen by composition/test, never accepted from control requests.
    pub fn open(root: &Path, owner: Owner) -> Self {
        let path = root.join(owner.directory());
        let mut logs = Self {
            path,
            owner,
            directory: None,
            enabled: None,
            writer: None,
            active_file: 0,
            error: String::new(),
            operation: String::new(),
            faulted: false,
            written: 0,
            limit: MAX_BYTES,
            keep: KEEP_BYTES,
        };
        match Directory::open(&logs.path) {
            Ok(directory) => {
                logs.directory = Some(directory);
                match logs.read_setting() {
                    Ok(value) => logs.enabled = Some(value),
                    Err(error) => logs.error = format!("Настройка логов: {error}"),
                }
            }
            Err(error) => logs.error = format!("Каталог логов {}: {error}", logs.path.display()),
        }
        logs
    }
    fn dir(&self) -> io::Result<&Directory> {
        self.directory.as_ref().ok_or_else(|| {
            io::Error::new(io::ErrorKind::PermissionDenied, "каталог логов недоступен")
        })
    }
    fn read_setting(&self) -> io::Result<bool> {
        let mut file = match self.dir()?.open_file(SETTINGS, false) {
            Ok(file) => file,
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(true),
            Err(e) => return Err(e),
        };
        let mut bytes = Vec::new();
        (&mut file).take(129).read_to_end(&mut bytes)?;
        let value: Value = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
        if bytes.len() > 128
            || value.as_object().map(|v| v.len()) != Some(2)
            || value["version"] != 1
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "неподдерживаемый или повреждённый файл настройки",
            ));
        }
        value["enabled"]
            .as_bool()
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "enabled должен быть bool"))
    }
    pub fn status(&self) -> Value {
        json!({"enabled":self.enabled,"available":self.directory.is_some(),"recording":self.enabled==Some(true)&&!self.faulted&&self.writer.is_some(),
            "directory":self.path.to_string_lossy(),"error":self.error,"operation":self.operation,"written":self.written})
    }
    /// Idempotent set; result confirms durable preference and writer readiness.
    fn set_enabled(&mut self, enabled: bool) -> io::Result<()> {
        self.writer = None;
        // Probe before saving enabled=true. An inaccessible file cannot produce success.
        if enabled {
            self.prepare_writer()?;
        }
        let bytes = serde_json::to_vec(&json!({"version":1,"enabled":enabled}))
            .map_err(io::Error::other)?;
        let result = (|| {
            let directory = self.dir()?;
            let mut temp = directory.open_file(TEMP, true)?;
            temp.set_len(0)?;
            temp.write_all(&bytes)?;
            temp.sync_all()?;
            directory.replace(&temp, SETTINGS)
        })();
        if result.is_err() {
            self.writer = None;
        }
        result?;
        self.enabled = Some(enabled);
        self.error.clear();
        self.faulted = false;
        self.operation = if enabled {
            "Запись логов включена"
        } else {
            "Запись логов выключена; файлы сохранены"
        }
        .into();
        Ok(())
    }
    fn prepare_writer(&mut self) -> io::Result<()> {
        if self.writer.is_none() {
            let mut file = self
                .dir()?
                .open_file(self.owner.files()[self.active_file], true)?;
            if matches!(self.owner, Owner::Emulator)
                && file
                    .metadata()?
                    .modified()?
                    .elapsed()
                    .is_ok_and(|age| age > std::time::Duration::from_secs(7 * 86400))
            {
                file.set_len(0)?;
            }
            self.recover_tail(&mut file)?;
            self.writer = Some(file);
        }
        Ok(())
    }
    // Recover a partial last write from a previous crash, preserving complete records.
    fn recover_tail(&self, file: &mut OwnedFile) -> io::Result<()> {
        let length = file.metadata()?.len();
        if length == 0 {
            return Ok(());
        }
        let start = length.saturating_sub(MAX_RECORD as u64);
        file.seek(SeekFrom::Start(start))?;
        let mut tail = Vec::new();
        file.take(MAX_RECORD as u64).read_to_end(&mut tail)?;
        if tail.last() != Some(&b'\n') {
            let end = tail
                .iter()
                .rposition(|&b| b == b'\n')
                .map_or(0, |i| start + i as u64 + 1);
            if end == 0 && start != 0 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "журнал содержит слишком длинную незавершённую запись",
                ));
            }
            file.set_len(end)?;
        }
        file.seek(SeekFrom::End(0))?;
        Ok(())
    }
    fn rotate(&mut self) -> io::Result<()> {
        let mut source = self
            .writer
            .take()
            .ok_or_else(|| io::Error::other("writer missing"))?;
        let length = source.metadata()?.len();
        source.seek(SeekFrom::Start(length.saturating_sub(self.keep)))?;
        let mut tail = Vec::new();
        source.take(self.keep).read_to_end(&mut tail)?;
        // Bounded 12.5 MiB allocation; skip a cut record, never split UTF-8.
        let first = tail
            .iter()
            .position(|&b| b == b'\n')
            .map_or(tail.len(), |i| i + 1);
        let directory = self.dir()?;
        let mut temp = directory.open_file(TEMP, true)?;
        temp.set_len(0)?;
        temp.write_all(&tail[first..])?;
        temp.sync_all()?;
        // Source was consumed by take(); no open writer blocks replacement.
        directory.replace(&temp, self.owner.files()[self.active_file])?;
        drop(temp);
        self.prepare_writer()
    }
    /// Diagnostic writes never propagate into the application's work or recurse.
    /// On failure pause writes until an explicit set/clear, retaining the saved preference.
    pub fn record(&mut self, message: &str) {
        self.record_file(self.owner.files()[0], message);
    }
    /// Selects only an owner-declared filename; keeps at most one file handle open.
    pub fn record_file(&mut self, name: &str, message: &str) {
        let Some(index) = self.owner.files().iter().position(|file| *file == name) else {
            self.error = "Журнал не принадлежит владельцу".into();
            return;
        };
        if self.active_file != index {
            self.writer = None;
            self.active_file = index;
        }
        if self.enabled != Some(true) || self.faulted {
            return;
        }
        let result = self.write_record(message);
        if let Err(error) = result {
            self.writer = None;
            self.faulted = true;
            self.error = format!("Запись логов остановлена: {error}");
        }
    }
    fn write_record(&mut self, message: &str) -> io::Result<()> {
        self.prepare_writer()?;
        let timestamp = timestamp(std::time::SystemTime::now());
        let mut line = format!("{timestamp} ");
        for c in message.chars() {
            if line.len() + c.len_utf8() + 16 > MAX_RECORD {
                line.push_str("…[truncated]");
                break;
            }
            line.push(if matches!(c, '\r' | '\n') { ' ' } else { c });
        }
        line.push_str("\r\n");
        if self
            .writer
            .as_ref()
            .ok_or_else(|| io::Error::other("writer missing"))?
            .metadata()?
            .len()
            + line.len() as u64
            > self.limit
        {
            self.rotate()?;
        }
        let file = self
            .writer
            .as_mut()
            .ok_or_else(|| io::Error::other("writer missing"))?;
        append_record(file, line.as_bytes())?;
        self.written = self.written.saturating_add(1);
        Ok(())
    }
    /// Close writer first, attempt every allowlisted identity once; no traversal or retry.
    fn clear(&mut self) -> io::Result<()> {
        self.writer = None;
        self.faulted = false;
        self.operation = "Очистка начата".into();
        let mut deleted = 0;
        let mut failed = Vec::new();
        for name in self.owner.files().iter().copied().chain([TEMP]) {
            match self.dir()?.remove_file(name) {
                Ok(true) => deleted += 1,
                Ok(false) => {}
                Err(e) => failed.push(format!("{name}: {e}")),
            }
        }
        // Reopen only for the currently saved setting. No record is written by clear itself.
        if self.enabled == Some(true)
            && let Err(e) = self.prepare_writer()
        {
            self.faulted = true;
            failed.push(format!("переоткрытие: {e}"));
        }
        self.operation = format!("Удалено файлов: {deleted}; ошибок: {}", failed.len());
        if !failed.is_empty() {
            return Err(io::Error::other(failed.join("; ")));
        }
        if self.enabled.is_some() {
            self.error.clear();
        }
        Ok(())
    }
    /// Three commands shared by the internal Host RPC and local Bridge owners.
    pub fn request(&mut self, action: &str, enabled: Option<bool>) -> Result<Value, String> {
        let result = match action {
            "get" => return Ok(self.status()),
            "set" => enabled
                .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "enabled missing"))
                .and_then(|v| self.set_enabled(v)),
            "clear" => self.clear(),
            _ => return Err("unknown logging action".into()),
        };
        if let Err(error) = result {
            self.error = format!("Логи: {error}");
            self.operation = if action == "clear" {
                format!("{}; очистка завершилась частично", self.operation)
            } else {
                "Операция не завершена; возможен частичный результат".into()
            };
            return Err(self.error.clone());
        }
        Ok(self.status())
    }
}
/// Roll back a failed append to its previous complete boundary. No recursive diagnostics.
trait RecordFile: Write + Seek {
    fn resize(&mut self, length: u64) -> io::Result<()>;
    fn sync(&mut self) -> io::Result<()>;
}
impl RecordFile for File {
    fn resize(&mut self, length: u64) -> io::Result<()> {
        self.set_len(length)
    }
    fn sync(&mut self) -> io::Result<()> {
        self.sync_data()
    }
}
#[cfg(windows)]
impl RecordFile for OwnedFile {
    fn resize(&mut self, length: u64) -> io::Result<()> {
        self.set_len(length)
    }
    fn sync(&mut self) -> io::Result<()> {
        self.sync_data()
    }
}
fn append_record(file: &mut impl RecordFile, record: &[u8]) -> io::Result<()> {
    let start = file.seek(SeekFrom::End(0))?;
    if let Err(error) = file.write_all(record).and_then(|_| file.sync()) {
        return match file.resize(start).and_then(|_| file.sync()) {
            Ok(()) => Err(error),
            Err(rollback) => Err(io::Error::other(format!(
                "{error}; rollback: {rollback}; recovery required"
            ))),
        };
    }
    Ok(())
}

// .NET round-trip UTC precision is 100 ns. Chrono has %.3f/%.6f/%.9f,
// but %.7f is invalid and panics when Display is written to a String.
fn timestamp(time: std::time::SystemTime) -> String {
    let date = chrono::DateTime::<chrono::Utc>::from(time);
    format!(
        "{}.{:07}Z",
        date.format("%Y-%m-%dT%H:%M:%S"),
        date.timestamp_subsec_nanos() / 100
    )
}

#[cfg(test)]
mod tests;
