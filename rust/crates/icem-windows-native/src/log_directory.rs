//! Pinned local directories and handle-checked log files. ADR 0008.
use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle};
use std::{
    fs::{File, OpenOptions},
    io,
    path::{Component, Path, PathBuf},
};
use windows_sys::Win32::{
    Foundation::HANDLE,
    Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO,
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_RENAME_INFO,
        FILE_SHARE_READ, FileDispositionInfo, FileRenameInfo, GetFileInformationByHandle,
        SetFileInformationByHandle,
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
    path: PathBuf,
    _pins: Vec<File>,
    _owner: File,
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
            path: path.to_owned(),
            _pins: pins,
            _owner: owner,
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
        Ok(self.path.join(name))
    }
    pub fn open_file(&self, name: &str, create: bool) -> io::Result<File> {
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
        Ok(file)
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
    pub fn replace(&self, source: &File, target: &str) -> io::Result<()> {
        #[repr(C)]
        struct Rename {
            flags: u32,
            root: HANDLE,
            length: u32,
            name: [u16; 1024],
        }
        const _: () = assert!(
            std::mem::offset_of!(Rename, name) == std::mem::offset_of!(FILE_RENAME_INFO, FileName)
        );
        let path: Vec<u16> = self.path(target)?.as_os_str().encode_wide().collect();
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
        // SAFETY: repr(C) buffer has FILE_RENAME_INFO layout (offset checked), initialized
        // UTF-16 array and bounded length. Source is a live DELETE-capable owned handle.
        if unsafe {
            SetFileInformationByHandle(
                source.as_raw_handle() as HANDLE,
                FileRenameInfo,
                &info as *const _ as *const _,
                (std::mem::offset_of!(Rename, name) + path.len() * 2 + 2) as u32,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}
