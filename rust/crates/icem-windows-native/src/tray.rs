//! Owned tray window/message pump. No callback pointers or HWNDs escape this module.
use std::{
    io,
    path::PathBuf,
    ptr,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
use windows_sys::Win32::{
    Foundation::*,
    System::LibraryLoader::GetModuleHandleW,
    UI::{Shell::*, WindowsAndMessaging::*},
};

#[derive(Clone)]
pub struct TrayItem {
    pub id: u16,
    pub label: String,
}
pub struct Tray {
    stop: Arc<AtomicBool>,
    events: mpsc::Receiver<u16>,
    worker: Option<JoinHandle<io::Result<()>>>,
}
impl Tray {
    pub fn start(icon: PathBuf, title: String, items: Vec<TrayItem>) -> io::Result<Self> {
        Self::start_dynamic(icon, title, Box::new(move || items.clone()))
    }
    pub fn start_dynamic(
        icon: PathBuf,
        title: String,
        items: Box<dyn Fn() -> Vec<TrayItem> + Send>,
    ) -> io::Result<Self> {
        let initial = items();
        validate_items(&initial)?;
        if title.contains('\0') {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid tooltip",
            ));
        }
        Self::start_worker(icon, title, items)
    }
    fn start_worker(
        icon: PathBuf,
        title: String,
        items: Box<dyn Fn() -> Vec<TrayItem> + Send>,
    ) -> io::Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let stopping = stop.clone();
        let (send, events) = mpsc::sync_channel(32);
        let (ready, wait) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("icem-tray".into())
            .spawn(move || {
                let result = NativeTray::new(&icon, &title);
                let mut native = match result {
                    Ok(native) => {
                        let _ = ready.send(Ok(()));
                        native
                    }
                    Err(error) => {
                        let _ = ready.send(Err(error));
                        return Ok(());
                    }
                };
                native.pump(&*items, send, &stopping)
            })?;
        match wait.recv() {
            Ok(Ok(())) => Ok(Self {
                stop,
                events,
                worker: Some(worker),
            }),
            result => {
                let _ = worker.join();
                Err(match result {
                    Ok(Err(e)) => e,
                    _ => io::Error::other("tray worker stopped before ready"),
                })
            }
        }
    }
    pub fn try_event(&self) -> Option<u16> {
        self.events.try_recv().ok()
    }
    pub fn stop(mut self) -> io::Result<()> {
        self.join()
    }
    fn join(&mut self) -> io::Result<()> {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| io::Error::other("tray worker panicked"))??;
        }
        Ok(())
    }
}
impl Drop for Tray {
    fn drop(&mut self) {
        if let Err(e) = self.join() {
            eprintln!("tray shutdown: {e}");
        }
    }
}
const CALLBACK: u32 = WM_APP + 37;
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}
struct NativeTray {
    hwnd: HWND,
    icon: HICON,
    data: NOTIFYICONDATAW,
    class: Vec<u16>,
    instance: HINSTANCE,
    installed: bool,
}
impl NativeTray {
    fn new(icon: &std::path::Path, title: &str) -> io::Result<Self> {
        let class = wide(&format!("iCemRustTray_{}", std::process::id()));
        // SAFETY: null asks for this executable's module; no pointer is retained.
        let instance = unsafe { GetModuleHandleW(ptr::null()) };
        let wc = WNDCLASSW {
            lpfnWndProc: Some(DefWindowProcW),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            ..WNDCLASSW::default()
        };
        // SAFETY: initialized class data and stable UTF-16 name valid for call; OS copies class.
        if unsafe { RegisterClassW(&wc) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut result = Self {
            hwnd: ptr::null_mut(),
            icon: ptr::null_mut(),
            data: NOTIFYICONDATAW::default(),
            class,
            instance,
            installed: false,
        };
        // SAFETY: registered class; hidden top-level window is needed for TaskbarCreated broadcast.
        result.hwnd = unsafe {
            CreateWindowExW(
                0,
                result.class.as_ptr(),
                result.class.as_ptr(),
                0,
                0,
                0,
                0,
                0,
                ptr::null_mut(),
                ptr::null_mut(),
                instance,
                ptr::null(),
            )
        };
        if result.hwnd.is_null() {
            return Err(io::Error::last_os_error());
        }
        use std::os::windows::ffi::OsStrExt;
        let path: Vec<u16> = icon
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        // SAFETY: NUL terminated file path; LR_LOADFROMFILE returns an owned icon.
        result.icon = unsafe {
            LoadImageW(
                ptr::null_mut(),
                path.as_ptr(),
                IMAGE_ICON,
                0,
                0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE,
            )
        };
        if result.icon.is_null() {
            return Err(io::Error::last_os_error());
        }
        result.data = NOTIFYICONDATAW {
            cbSize: size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: result.hwnd,
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage: CALLBACK,
            hIcon: result.icon,
            ..NOTIFYICONDATAW::default()
        };
        let text: Vec<u16> = title.encode_utf16().take(127).collect();
        result.data.szTip[..text.len()].copy_from_slice(&text);
        result.install()?;
        Ok(result)
    }
    fn install(&mut self) -> io::Result<()> {
        // SAFETY: data contains this thread's live hidden window and owned icon.
        if unsafe { Shell_NotifyIconW(NIM_ADD, &self.data) } == 0 {
            return Err(io::Error::last_os_error());
        }
        self.installed = true;
        Ok(())
    }
    fn pump(
        &mut self,
        items: &dyn Fn() -> Vec<TrayItem>,
        send: mpsc::SyncSender<u16>,
        stop: &AtomicBool,
    ) -> io::Result<()> {
        let taskbar = wide("TaskbarCreated");
        // SAFETY: static registered-message string; OS returns a message ID.
        let taskbar = unsafe { RegisterWindowMessageW(taskbar.as_ptr()) };
        while !stop.load(Ordering::Acquire) {
            let mut msg = MSG::default();
            // SAFETY: valid initialized MSG; dispatch only on the owning GUI thread.
            while unsafe { PeekMessageW(&mut msg, ptr::null_mut(), 0, 0, PM_REMOVE) } != 0 {
                if msg.hwnd == self.hwnd && msg.message == taskbar && taskbar != 0 {
                    self.installed = false;
                    self.install()?;
                } else if msg.hwnd == self.hwnd && msg.message == CALLBACK {
                    match msg.lParam as u32 {
                        WM_LBUTTONDBLCLK => {
                            if let Some(item) = items().first() {
                                let _ = send.try_send(item.id);
                            }
                        }
                        WM_RBUTTONUP | WM_CONTEXTMENU => {
                            if let Some(id) = self.menu(&items())? {
                                let _ = send.try_send(id);
                            }
                        }
                        _ => {}
                    }
                } else {
                    // SAFETY: MSG came from PeekMessageW on this thread; default window proc only.
                    unsafe {
                        TranslateMessage(&msg);
                        DispatchMessageW(&msg);
                    }
                }
            }
            thread::sleep(Duration::from_millis(20));
        }
        Ok(())
    }
    fn menu(&self, items: &[TrayItem]) -> io::Result<Option<u16>> {
        validate_items(items)?;
        // SAFETY: no preconditions; menu is owned until DestroyMenu below.
        let menu = unsafe { CreatePopupMenu() };
        if menu.is_null() {
            return Err(io::Error::last_os_error());
        }
        let result = (|| {
            for item in items {
                let label = wide(&item.label);
                // SAFETY: live menu; UTF-16 text copied by Windows during AppendMenuW.
                if unsafe { AppendMenuW(menu, MF_STRING, usize::from(item.id), label.as_ptr()) }
                    == 0
                {
                    return Err(io::Error::last_os_error());
                }
            }
            let mut point = POINT::default();
            // SAFETY: owned window/menu on same thread; initialized output point, null rect.
            let id = unsafe {
                GetCursorPos(&mut point);
                SetForegroundWindow(self.hwnd);
                let id = TrackPopupMenu(
                    menu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    point.x,
                    point.y,
                    0,
                    self.hwnd,
                    ptr::null(),
                );
                PostMessageW(self.hwnd, WM_NULL, 0, 0);
                id
            };
            Ok(u16::try_from(id).ok().filter(|id| *id != 0))
        })();
        // SAFETY: unique owned menu; TrackPopupMenu has returned, no remaining borrowers.
        unsafe {
            DestroyMenu(menu);
        }
        result
    }
}
impl Drop for NativeTray {
    fn drop(&mut self) {
        // SAFETY: resources belong exclusively to this thread. Remove icon before window/icon destruction.
        unsafe {
            if self.installed {
                Shell_NotifyIconW(NIM_DELETE, &self.data);
            }
            if !self.hwnd.is_null() {
                DestroyWindow(self.hwnd);
            }
            if !self.icon.is_null() {
                DestroyIcon(self.icon);
            }
            UnregisterClassW(self.class.as_ptr(), self.instance);
        }
    }
}

fn validate_items(items: &[TrayItem]) -> io::Result<()> {
    if items.is_empty()
        || items.len() > 32
        || items
            .iter()
            .any(|item| item.id == 0 || item.label.contains('\0'))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid tray menu",
        ));
    }
    let mut ids = std::collections::BTreeSet::new();
    if items.iter().any(|i| !ids.insert(i.id)) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "duplicate tray ID",
        ));
    }
    Ok(())
}
