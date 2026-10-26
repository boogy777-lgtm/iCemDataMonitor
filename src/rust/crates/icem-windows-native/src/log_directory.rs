//! Pinned local directories and handle-checked log files. ADR 0008.
use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle};
use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Component, Path, PathBuf},
    sync::Arc,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_RENAME_INFORMATION, FileRenameInformation, NtSetInformationFile,
    },
    Win32::{
        Foundation::{HANDLE, RtlNtStatusToDosError},
        Storage::FileSystem::{
            BY_HANDLE_FILE_INFORMATION, FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO,
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
            FileDispositionInfo, GetFileInformationByHandle, SetFileInformationByHandle,
        },
        System::IO::IO_STATUS_BLOCK,
    },
};

fn denied(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}
fn regular(file: &File) -> io::Result<()> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: live owned File handle and initialized writable structure of the API's type.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle() as HANDLE, &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    if !file.metadata()?.is_file()
        || info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0
        || info.nNumberOfLinks != 1
    {
        return Err(denied(
            "log file is a reparse point, directory or hard link",
        ));
    }
    Ok(())
}
/// Pins every ancestor against replacement and reparse mutation for its lifetime.
/// Only local absolute disk paths and single-component filenames are accepted.
pub struct LogDirectory {
    root: Arc<Root>,
}
struct Root {
    path: PathBuf,
    _pins: Vec<File>,
    _owner: File,
}
/// Synchronous owned file capability. Construction and raw handle stay in this module.
/// Retains directory pins and the exclusive owner lock until the file is closed.
pub struct LogFile {
    file: File,
    root: Arc<Root>,
}
impl LogFile {
    pub fn metadata(&self) -> io::Result<std::fs::Metadata> {
        self.file.metadata()
    }
    pub fn set_len(&self, length: u64) -> io::Result<()> {
        self.file.set_len(length)
    }
    pub fn sync_all(&self) -> io::Result<()> {
        self.file.sync_all()
    }
    pub fn sync_data(&self) -> io::Result<()> {
        self.file.sync_data()
    }
}
impl Read for LogFile {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        self.file.read(buffer)
    }
}
impl Write for LogFile {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        self.file.write(buffer)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.file.flush()
    }
}
impl Seek for LogFile {
    fn seek(&mut self, position: SeekFrom) -> io::Result<u64> {
        self.file.seek(position)
    }
}
impl LogDirectory {
    pub fn open(path: &Path) -> io::Result<Self> {
        let components: Vec<_> = path.components().collect();
        if components.len() > 32 || path.as_os_str().encode_wide().count() > 1000 {
            return Err(denied("log path exceeds resource limit"));
        }
        if !matches!(components.first(), Some(Component::Prefix(p)) if matches!(p.kind(), std::path::Prefix::Disk(_)))
            || !matches!(components.get(1), Some(Component::RootDir))
            || components
                .iter()
                .skip(2)
                .any(|c| !matches!(c, Component::Normal(_)))
        {
            return Err(denied(
                "logs require an absolute local disk path without traversal",
            ));
        }
        let mut current = PathBuf::new();
        let mut pins = Vec::new();
        for (index, component) in components.iter().enumerate() {
            current.push(component.as_os_str());
            if index == 0 {
                continue;
            }
            if index > 1 {
                match std::fs::create_dir(&current) {
                    Ok(()) => {}
                    Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
                    Err(e) => return Err(e),
                }
            }
            let pin = OpenOptions::new()
                .read(true)
                .share_mode(FILE_SHARE_READ)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
                .open(&current)?;
            use std::os::windows::fs::MetadataExt;
            let metadata = pin.metadata()?;
            if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
            {
                return Err(denied(
                    "log directory ancestor is a reparse point or not a directory",
                ));
            }
            pins.push(pin);
        }
        let owner = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .share_mode(0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(path.join("rust-logger.lock"))?;
        regular(&owner)?;
        Ok(Self {
            root: Arc::new(Root {
                path: path.to_owned(),
                _pins: pins,
                _owner: owner,
            }),
        })
    }
    fn path(&self, name: &str) -> io::Result<PathBuf> {
        let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
        let reserved = matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        ) || (stem.len() == 4
            && (stem.starts_with("COM") || stem.starts_with("LPT"))
            && stem.as_bytes()[3].is_ascii_digit());
        if !name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-' | b'_'))
            || reserved
            || name.len() > 128
            || name.chars().any(|c| c.is_control())
            || name.is_empty()
            || name.contains(['/', '\\', ':', '\0'])
            || name == "."
            || name == ".."
            || name.ends_with(['.', ' '])
        {
            return Err(denied("invalid log filename"));
        }
        Ok(self.root.path.join(name))
    }
    pub fn open_file(&self, name: &str, create: bool) -> io::Result<LogFile> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(create)
            .truncate(false)
            .access_mode(0xC000_0000 | 0x0001_0000)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(name)?)?;
        regular(&file)?;
        Ok(LogFile {
            file,
            root: Arc::clone(&self.root),
        })
    }
    /// Deletes the validated identity, never a path reopened after validation.
    pub fn remove_file(&self, name: &str) -> io::Result<bool> {
        let file = match OpenOptions::new()
            .read(true)
            .access_mode(0x8000_0000 | 0x0001_0000)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(name)?)
        {
            Ok(file) => file,
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(false),
            Err(e) => return Err(e),
        };
        regular(&file)?;
        let disposition = FILE_DISPOSITION_INFO { DeleteFile: true };
        // SAFETY: owned validated handle, correct structure and size; file lives through the call.
        if unsafe {
            SetFileInformationByHandle(
                file.as_raw_handle() as HANDLE,
                FileDispositionInfo,
                &disposition as *const _ as *const _,
                std::mem::size_of::<FILE_DISPOSITION_INFO>() as u32,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(true)
    }
    /// Renames the checked source handle, never reopening its path.
    pub fn replace(&self, source: &LogFile, target: &str) -> io::Result<()> {
        if !Arc::ptr_eq(&self.root, &source.root) {
            return Err(denied("log source belongs to another directory owner"));
        }
        regular(&source.file)?;
        #[repr(C)]
        struct Rename {
            flags: u32,
            root: HANDLE,
            length: u32,
            name: [u16; 1024],
        }
        const _: () = assert!(
            std::mem::offset_of!(Rename, name)
                == std::mem::offset_of!(FILE_RENAME_INFORMATION, FileName)
        );
        self.path(target)?; // Single component only; NT rename keeps the source parent.
        let path: Vec<u16> = std::ffi::OsStr::new(target).encode_wide().collect();
        if path.len() >= 1024 {
            return Err(denied("log path too long"));
        }
        let mut info = Rename {
            flags: 1,
            root: std::ptr::null_mut(),
            length: (path.len() * 2) as u32,
            name: [0; 1024],
        };
        info.name[..path.len()].copy_from_slice(&path);
        let mut completed = IO_STATUS_BLOCK::default();
        // SAFETY: repr(C) layout is checked against the SDK. The private LogFile
        // is always synchronous (no OVERLAPPED flag), so NT completes before these
        // stack buffers go out of scope. Root=NULL + one component is documented
        // in-place rename: no CWD/full-path resolution or ancestor pin release.
        let status = unsafe {
            NtSetInformationFile(
                source.file.as_raw_handle() as HANDLE,
                &mut completed,
                &info as *const _ as *const _,
                std::mem::size_of::<Rename>() as u32,
                FileRenameInformation,
            )
        };
        if status < 0 {
            // SAFETY: conversion of an NTSTATUS value, no pointers or effects.
            return Err(io::Error::from_raw_os_error(
                unsafe { RtlNtStatusToDosError(status) }.cast_signed(),
            ));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
    fn step<T>(stage: &str, result: io::Result<T>) -> Result<T> {
        result.map_err(|error| format!("{stage}: {error}").into())
    }
    struct Root(PathBuf);
    impl Drop for Root {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    #[test]
    fn owned_file_write_read_replace_delete_and_sibling_pins() -> Result {
        let root = Root(std::env::temp_dir().join(format!(
            "icem-log-boundary-{}-{}", std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)?.as_nanos()
        )));
        let first = step("first directory", LogDirectory::open(&root.0.join("first")))?;
        let sibling = step(
            "sibling directory",
            LogDirectory::open(&root.0.join("sibling")),
        )?;
        let mut writer = step("writer open", first.open_file("owned.log", true))?;
        step("writer write", writer.write_all(b"old\r\n"))?;
        step("writer sync", writer.sync_all())?;
        assert_eq!(
            step(
                "read while writer lives",
                std::fs::read(root.0.join("first/owned.log"))
            )?,
            b"old\r\n"
        );
        drop(writer);
        let mut foreign = step("sibling file", sibling.open_file("neighbor.log", true))?;
        step("sibling write", foreign.write_all(b"neighbor"))?;
        assert!(first.replace(&foreign, "owned.log").is_err());
        assert_eq!(
            step(
                "foreign source unchanged",
                std::fs::read(root.0.join("sibling/neighbor.log"))
            )?,
            b"neighbor"
        );
        drop(foreign);
        for record in [b"new\r\n", b"end\r\n"] {
            let mut temp = step("temp open", first.open_file("temp.tmp", true))?;
            step("temp truncate", temp.set_len(0))?;
            step("temp write", temp.write_all(record))?;
            step("temp sync", temp.sync_all())?;
            step(
                "replace existing destination",
                first.replace(&temp, "owned.log"),
            )?;
            drop(temp);
            let file = step(
                "reopen replaced destination",
                first.open_file("owned.log", false),
            )?;
            assert_eq!(
                step(
                    "read replaced destination",
                    std::fs::read(root.0.join("first/owned.log"))
                )?,
                record
            );
            drop(file);
        }
        assert!(step(
            "delete checked destination",
            first.remove_file("owned.log")
        )?);
        drop(first);
        let retained = step("retained file", sibling.open_file("retained.log", true))?;
        drop(sibling);
        assert!(LogDirectory::open(&root.0.join("sibling")).is_err());
        drop(retained);
        drop(step(
            "owner released with final file",
            LogDirectory::open(&root.0.join("sibling")),
        )?);
        step(
            "rename after all pins released",
            std::fs::rename(root.0.join("first"), root.0.join("moved")),
        )?;
        Ok(())
    }
}
