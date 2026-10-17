//! Safe owned handles and bounded kernel-copy API. No Rust references into shared pages.
use std::ffi::c_void;
use std::fs::{File, OpenOptions};
use std::marker::PhantomData;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::path::Path;
use std::ptr;
use std::rc::Rc;
use std::time::Duration;
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};
use windows_sys::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows_sys::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_READ, MapViewOfFile, OpenFileMappingW, PAGE_READONLY,
    UnmapViewOfFile,
};
use windows_sys::Win32::System::Threading::{
    CreateMutexW, GetCurrentProcess, ReleaseMutex, WaitForSingleObject,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NativeError {
    pub operation: &'static str,
    pub code: u32,
}
impl std::fmt::Display for NativeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: Win32 {}", self.operation, self.code)
    }
}
impl std::error::Error for NativeError {}
impl NativeError {
    fn last(operation: &'static str) -> Self {
        // SAFETY: GetLastError has no preconditions; invoked immediately after the failed FFI.
        let code = unsafe { GetLastError() };
        Self { operation, code }
    }
    fn invalid(operation: &'static str) -> Self {
        Self {
            operation,
            code: 87,
        }
    }
}
fn wide_name(name: &str) -> Result<Vec<u16>, NativeError> {
    if name.is_empty() || name.contains('\0') {
        return Err(NativeError::invalid("object_name"));
    }
    Ok(name.encode_utf16().chain(std::iter::once(0)).collect())
}
struct OwnedHandle(HANDLE);
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        // SAFETY: stored only after successful creation/open; not Clone and never exposed.
        // Every borrowed guard/view is gone before its owning handle can be dropped.
        unsafe {
            CloseHandle(self.0);
        }
    }
}

/// Windows mutex owner. No raw handle escapes, no Send/Sync implementation.
pub struct NamedMutex {
    handle: OwnedHandle,
}
impl NamedMutex {
    pub fn open_or_create(name: &str) -> Result<Self, NativeError> {
        let name = wide_name(name)?;
        // SAFETY: valid NUL-terminated UTF-16 lives through call; default security,
        // initially unowned, no user pointers retained by Windows.
        let handle = unsafe { CreateMutexW(ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(NativeError::last("create_mutex"));
        }
        Ok(Self {
            handle: OwnedHandle(handle),
        })
    }
    pub fn lock(&self, timeout: Duration) -> Result<Option<NamedMutexGuard<'_>>, NativeError> {
        let milliseconds = u32::try_from(timeout.as_millis())
            .map_err(|_| NativeError::invalid("mutex_timeout"))?;
        if milliseconds == u32::MAX {
            return Err(NativeError::invalid("mutex_timeout"));
        }
        // SAFETY: handle is a live mutex borrowed for this call; finite timeout.
        let result = unsafe { WaitForSingleObject(self.handle.0, milliseconds) };
        match result {
            WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Some(NamedMutexGuard {
                mutex: self,
                abandoned: result == WAIT_ABANDONED,
                active: true,
                thread_bound: PhantomData,
            })),
            WAIT_TIMEOUT => Ok(None),
            _ => Err(NativeError::last("wait_mutex")),
        }
    }
}

/// A guard must remain on the thread that acquired the Windows mutex.
/// ```compile_fail
/// use icem_windows_native::NamedMutexGuard;
/// fn requires_send<T: Send>() {}
/// requires_send::<NamedMutexGuard<'static>>();
/// ```
pub struct NamedMutexGuard<'a> {
    mutex: &'a NamedMutex,
    abandoned: bool,
    active: bool,
    thread_bound: PhantomData<Rc<()>>,
}
impl NamedMutexGuard<'_> {
    pub fn was_abandoned(&self) -> bool {
        self.abandoned
    }
    pub fn release(mut self) -> Result<(), NativeError> {
        self.release_inner()
    }
    fn release_inner(&mut self) -> Result<(), NativeError> {
        if !self.active {
            return Ok(());
        }
        self.active = false;
        // SAFETY: guard constructed only for an acquired mutex, !Send/!Sync,
        // borrow keeps handle alive; this is the owning thread and releases once.
        if unsafe { ReleaseMutex(self.mutex.handle.0) } == 0 {
            return Err(NativeError::last("release_mutex"));
        }
        Ok(())
    }
}
impl Drop for NamedMutexGuard<'_> {
    fn drop(&mut self) {
        if let Err(error) = self.release_inner() {
            eprintln!("{error}");
        }
    }
}

/// Read-only mapping, either attached by exact name or unnamed from an existing file.
pub struct Mapping {
    handle: OwnedHandle,
    _file: Option<File>,
}
impl Mapping {
    pub fn open_existing(name: &str) -> Result<Self, NativeError> {
        let name = wide_name(name)?;
        // SAFETY: terminated name, read-only requested rights, inheritance disabled.
        let handle = unsafe { OpenFileMappingW(FILE_MAP_READ, 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(NativeError::last("open_section"));
        }
        Ok(Self {
            handle: OwnedHandle(handle),
            _file: None,
        })
    }
    pub fn unnamed_read_only(path: &Path) -> Result<Self, std::io::Error> {
        let file = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .open(path)?;
        // SAFETY: live borrowed read-only file handle; no named object is created,
        // PAGE_READONLY, maximum size 0 means current file size. Null security/name.
        let handle = unsafe {
            CreateFileMappingW(
                file.as_raw_handle(),
                ptr::null(),
                PAGE_READONLY,
                0,
                0,
                ptr::null(),
            )
        };
        if handle.is_null() {
            return Err(std::io::Error::from_raw_os_error(
                NativeError::last("create_unnamed_section")
                    .code
                    .cast_signed(),
            ));
        }
        Ok(Self {
            handle: OwnedHandle(handle),
            _file: Some(file),
        })
    }
    pub fn probe(&self, length: usize) -> Result<(), NativeError> {
        self.read_view(length).map(drop)
    }
    fn read_view(&self, length: usize) -> Result<View, NativeError> {
        // Bound independently of protocol decoder. No untrusted allocation beyond Bus v5 max.
        if !(64..=64 + 2 * usize::from(u16::MAX)).contains(&length) {
            return Err(NativeError::invalid("copy_length"));
        }
        // SAFETY: live section handle, offset 0, read-only access. Windows validates
        // requested length against section capacity; no pointer is dereferenced in Rust.
        let base = unsafe { MapViewOfFile(self.handle.0, FILE_MAP_READ, 0, 0, length) };
        if base.Value.is_null() {
            return Err(NativeError::last("map_read_view"));
        }
        Ok(View(base))
    }
    pub fn copy_bytes(&self, length: usize) -> Result<Vec<u8>, NativeError> {
        let view = self.read_view(length)?;
        let mut bytes = vec![0; length];
        let mut copied = 0;
        // SAFETY: GetCurrentProcess yields a pseudo-handle, never owned/closed here.
        // View stays mapped; destination is a unique initialized allocation with len bytes.
        // Source is passed to the kernel, not converted to Rust references. The OS reports
        // inaccessible/changed pages as an error; output is used only after full copy.
        let result = unsafe {
            ReadProcessMemory(
                GetCurrentProcess(),
                view.0.Value,
                bytes.as_mut_ptr().cast::<c_void>(),
                length,
                &mut copied,
            )
        };
        if result == 0 {
            return Err(NativeError::last("copy_view"));
        }
        if copied != length {
            return Err(NativeError {
                operation: "partial_copy",
                code: 299,
            });
        }
        Ok(bytes) // view drops before any bytes escape
    }
}
struct View(windows_sys::Win32::System::Memory::MEMORY_MAPPED_VIEW_ADDRESS);
impl Drop for View {
    fn drop(&mut self) {
        // SAFETY: unique view returned successfully by MapViewOfFile; never exposed/cloned,
        // no Rust references point into it, unmaps exactly once after copy/error.
        unsafe {
            UnmapViewOfFile(self.0);
        }
    }
}

/// Signal an existing shell-owned event; never create a second owner.
pub fn signal_existing_event(name: &str) -> Result<bool, NativeError> {
    use windows_sys::Win32::System::Threading::{EVENT_MODIFY_STATE, OpenEventW, SetEvent};
    let name = wide_name(name)?;
    // SAFETY: NUL-terminated name lives through call; request only signal rights,
    // inheritance disabled. OpenExisting preserves shell ownership and DACL.
    let raw = unsafe { OpenEventW(EVENT_MODIFY_STATE, 0, name.as_ptr()) };
    if raw.is_null() {
        let error = NativeError::last("open_event");
        return if error.code == 2 {
            Ok(false)
        } else {
            Err(error)
        };
    }
    let handle = OwnedHandle(raw);
    // SAFETY: successful OpenEventW yielded this live owned event handle;
    // SetEvent retains no references and handle closes once on function exit.
    if unsafe { SetEvent(handle.0) } == 0 {
        return Err(NativeError::last("signal_event"));
    }
    Ok(true)
}

/// Host owns these auto-reset lifecycle events; clients only open/signal them.
pub struct OwnedEvent(OwnedHandle);
impl OwnedEvent {
    pub fn create_reset(name: &str) -> Result<Self, NativeError> {
        use windows_sys::Win32::System::Threading::{CreateEventW, ResetEvent};
        let name = wide_name(name)?;
        // SAFETY: valid UTF-16 name, default security, auto-reset initially unsignalled.
        let handle = unsafe { CreateEventW(ptr::null(), 0, 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(NativeError::last("create_event"));
        }
        let event = Self(OwnedHandle(handle));
        // SAFETY: live owned event, explicitly clear a previous boot's signal.
        if unsafe { ResetEvent(event.0.0) } == 0 {
            return Err(NativeError::last("reset_event"));
        }
        Ok(event)
    }
    pub fn poll(&self) -> Result<bool, NativeError> {
        // SAFETY: live owned event, nonblocking poll consumes only this auto-reset event.
        match unsafe { WaitForSingleObject(self.0.0, 0) } {
            WAIT_OBJECT_0 => Ok(true),
            WAIT_TIMEOUT => Ok(false),
            _ => Err(NativeError::last("poll_event")),
        }
    }
}
pub fn console_visible() -> bool {
    use windows_sys::Win32::{
        System::Console::GetConsoleWindow, UI::WindowsAndMessaging::IsWindowVisible,
    };
    // SAFETY: OS-provided console HWND, checked before read-only visibility probe.
    unsafe {
        let window = GetConsoleWindow();
        !window.is_null() && IsWindowVisible(window) != 0
    }
}
pub fn set_console_visible(visible: bool) -> Result<bool, NativeError> {
    use windows_sys::Win32::{
        System::Console::{AllocConsole, GetConsoleWindow},
        UI::WindowsAndMessaging::{SW_HIDE, SW_RESTORE, ShowWindow},
    };
    // SAFETY: operates only on the calling process's console, no borrowed pointers.
    unsafe {
        let mut window = GetConsoleWindow();
        if window.is_null() && visible {
            if AllocConsole() == 0 {
                return Err(NativeError::last("allocate_console"));
            }
            window = GetConsoleWindow();
        }
        if !window.is_null() {
            ShowWindow(window, if visible { SW_RESTORE } else { SW_HIDE });
        }
    }
    Ok(console_visible())
}
