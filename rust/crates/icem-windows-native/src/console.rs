//! Host-owned diagnostic window. No conhost HWND is changed; see ADR 0011.
use crate::NativeError;
use std::{
    marker::PhantomData,
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicPtr, AtomicU64, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows_sys::{
    Win32::{
        Foundation::{HWND, LPARAM, LRESULT, WPARAM},
        System::{
            Console::FreeConsole, LibraryLoader::GetModuleHandleW, Threading::GetCurrentProcessId,
        },
        UI::{Controls::EM_SETLIMITTEXT, WindowsAndMessaging::*},
    },
    core::w,
};
const WAIT: Duration = Duration::from_secs(2);
const POLL: Duration = Duration::from_millis(10);
const MAX_TEXT: usize = 65_536;
const MAX_RECORD: usize = 1024;
const CLASS: windows_sys::core::PCWSTR = w!("iCem.Host.Diagnostics.v1");
static OWNER: AtomicBool = AtomicBool::new(false);
static WINDOW: AtomicPtr<std::ffi::c_void> = AtomicPtr::new(std::ptr::null_mut());
static STOP: AtomicBool = AtomicBool::new(false);
static DIAGNOSTICS: Mutex<Option<SyncSender<Vec<u16>>>> = Mutex::new(None);
static DROPPED: AtomicU64 = AtomicU64::new(0);
fn error(operation: &'static str, code: u32) -> NativeError {
    NativeError { operation, code }
}
/// Best-effort bounded screen transcript, independent of E12 file recording ON/OFF.
/// No I/O, waits, recursive logging, new threads or mutations are retried here.
pub fn console_diagnostic(message: &str) {
    if let Ok(queue) = DIAGNOSTICS.try_lock() {
        if let Some(queue) = &*queue {
            let mut text: Vec<u16> = message
                .encode_utf16()
                .take(MAX_RECORD)
                .map(|c| if c == 0 { 0xfffd } else { c })
                .collect();
            // Do not retain half of a surrogate pair at the cap.
            if text.last().is_some_and(|c| (0xd800..=0xdbff).contains(c)) {
                text.pop();
            }
            text.extend([13, 10]);
            if queue.try_send(text).is_err() {
                DROPPED.fetch_add(1, Ordering::Relaxed);
            }
        }
    } else {
        DROPPED.fetch_add(1, Ordering::Relaxed);
    }
}
fn live(window: HWND) -> bool {
    // SAFETY: read-only probes; opaque handle is never dereferenced or returned to applications.
    unsafe {
        let mut pid = 0;
        !window.is_null()
            && IsWindow(window) != 0
            && GetWindowThreadProcessId(window, &mut pid) != 0
            && pid == GetCurrentProcessId()
    }
}
pub fn console_state() -> (bool, bool) {
    let window = WINDOW.load(Ordering::Acquire);
    let exists = live(window);
    // SAFETY: read-only actual-state query; lifetime is owned by the window thread.
    (exists, exists && unsafe { IsWindowVisible(window) != 0 })
}
pub fn console_visible() -> bool {
    console_state().1
}
pub fn console_restored() -> bool {
    let window = WINDOW.load(Ordering::Acquire);
    // SAFETY: read-only fresh owned-window probe.
    live(window) && unsafe { IsIconic(window) == 0 }
}
/// Read only, before detachment; never use the inherited console HWND for effects.
pub fn inherited_console_visible() -> bool {
    // SAFETY: read-only OS console lookup; no attachment or window change.
    unsafe {
        let window = windows_sys::Win32::System::Console::GetConsoleWindow();
        !window.is_null() && IsWindowVisible(window) != 0
    }
}
// A static callback never borrows Rust state, locks, waits, allocates or performs I/O.
unsafe extern "system" fn procedure(window: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match message {
        WM_CLOSE => {
            // SAFETY: callback's own live HWND, on its creating thread. No destruction/CTRL_CLOSE.
            unsafe {
                ShowWindow(window, SW_HIDE);
            }
            0
        }
        WM_SIZE => {
            // SAFETY: this window's fixed child ID; resize on the same creating thread.
            unsafe {
                let edit = GetDlgItem(window, 1);
                if !edit.is_null() {
                    MoveWindow(
                        edit,
                        0,
                        0,
                        (lp as u32 & 0xffff) as i32,
                        ((lp as u32 >> 16) & 0xffff) as i32,
                        1,
                    );
                }
            }
            0
        }
        WM_CHAR if wp == 3 => {
            STOP.store(true, Ordering::Release);
            0
        }
        WM_QUERYENDSESSION => 1, // Never veto OS logoff/shutdown.
        WM_ENDSESSION if wp != 0 => {
            STOP.store(true, Ordering::Release);
            0
        }
        _ => {
            // SAFETY: forwards the unmodified callback arguments to the default procedure.
            unsafe { DefWindowProcW(window, message, wp, lp) }
        }
    }
}
struct Window {
    window: HWND,
    edit: HWND,
    instance: windows_sys::Win32::Foundation::HINSTANCE,
}
impl Window {
    fn create() -> Result<Self, NativeError> {
        // SAFETY: module is loaded for the entire process; class callback is static.
        let instance = unsafe { GetModuleHandleW(std::ptr::null()) };
        if instance.is_null() {
            return Err(NativeError::last("console_module"));
        }
        // SAFETY: zero initializes optional Win32 fields, with valid class/procedure/module below.
        let mut class: WNDCLASSW = unsafe { std::mem::zeroed() };
        class.lpfnWndProc = Some(procedure);
        class.hInstance = instance;
        class.lpszClassName = CLASS;
        // SAFETY: system cursor resource, no owned cursor to release.
        class.hCursor = unsafe { LoadCursorW(std::ptr::null_mut(), IDC_ARROW) };
        // SAFETY: all class pointers are static; scoped registration, no Rust reference in user data.
        if unsafe { RegisterClassW(&class) } == 0 {
            return Err(NativeError::last("register_console_window"));
        }
        // SAFETY: static UTF-16 strings, registered class, no callback payload or foreign HWND.
        let window = unsafe {
            CreateWindowExW(
                0,
                CLASS,
                w!("iCem DataMonitor Host — X: скрыть; Ctrl+C: остановить"),
                WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                860,
                480,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                instance,
                std::ptr::null(),
            )
        };
        if window.is_null() {
            let failure = NativeError::last("create_console_window");
            // SAFETY: no live windows exist for this owned registration.
            unsafe {
                UnregisterClassW(CLASS, instance);
            }
            return Err(failure);
        }
        let mut result = Self {
            window,
            edit: std::ptr::null_mut(),
            instance,
        };
        // SAFETY: built-in EDIT class, fixed child ID 1, parent owned on this thread.
        result.edit = unsafe {
            CreateWindowExW(
                0,
                w!("EDIT"),
                w!(""),
                WS_CHILD
                    | WS_VISIBLE
                    | WS_VSCROLL
                    | WS_HSCROLL
                    | ES_MULTILINE as u32
                    | ES_READONLY as u32
                    | ES_AUTOVSCROLL as u32
                    | ES_AUTOHSCROLL as u32,
                0,
                0,
                840,
                440,
                window,
                1 as _,
                instance,
                std::ptr::null(),
            )
        };
        if result.edit.is_null() {
            return Err(NativeError::last("create_console_transcript"));
        }
        // SAFETY: synchronous calls only to our child on its own thread; limits do not borrow pointers.
        unsafe {
            SendMessageW(result.edit, EM_SETLIMITTEXT, MAX_TEXT, 0);
        }
        // SAFETY: own newly created HWND; consume STARTUPINFO before requested visibility.
        unsafe {
            ShowWindow(window, SW_HIDE);
        }
        WINDOW.store(window, Ordering::Release);
        Ok(result)
    }
    fn visible(&mut self, visible: bool) -> Result<(), NativeError> {
        if !live(self.window) {
            return Err(error("console_window_lost", 6));
        }
        // SAFETY: sole owner acts on its own thread/HWND; ShowWindow return is previous state only.
        unsafe {
            ShowWindow(
                self.window,
                if !visible {
                    SW_HIDE
                } else if IsIconic(self.window) != 0 {
                    SW_RESTORE
                } else {
                    SW_SHOW
                },
            );
            if (IsWindowVisible(self.window) != 0) != visible
                || (visible && IsIconic(self.window) != 0)
            {
                return Err(error("console_visibility_refused", 5));
            }
            if visible {
                SetForegroundWindow(self.window);
            }
        }
        Ok(())
    }
    fn transcript(&mut self, text: &[u16]) {
        // SAFETY: same-thread child, NUL-terminated owned text remains live through the synchronous call.
        unsafe {
            SetWindowTextW(self.edit, text.as_ptr());
        }
    }
}
impl Drop for Window {
    fn drop(&mut self) {
        WINDOW.store(std::ptr::null_mut(), Ordering::Release);
        // SAFETY: destruction/unregister only on creating thread, only owned resources.
        unsafe {
            if live(self.window) {
                DestroyWindow(self.window);
            }
            UnregisterClassW(CLASS, self.instance);
        }
    }
}
struct Request {
    visible: bool,
    deadline: Instant,
    response: SyncSender<Result<(), NativeError>>,
}
struct Lifetime;
impl Drop for Lifetime {
    fn drop(&mut self) {
        if let Ok(mut queue) = DIAGNOSTICS.lock() {
            *queue = None;
        }
        OWNER.store(false, Ordering::Release);
    }
}
fn ui_loop(
    visible: bool,
    requests: Receiver<Request>,
    records: Receiver<Vec<u16>>,
    stopping: Arc<AtomicBool>,
    ready: SyncSender<Result<(), NativeError>>,
) -> Result<(), NativeError> {
    let mut window = match Window::create() {
        Ok(window) => window,
        Err(failure) => {
            let _ = ready.send(Err(failure));
            return Ok(());
        }
    };
    if let Err(failure) = window.visible(visible) {
        let _ = ready.send(Err(failure));
        return Ok(());
    }
    let _ = ready.send(Ok(()));
    let mut text = Vec::new();
    while !stopping.load(Ordering::Acquire) {
        if !live(window.window) {
            return Err(error("console_window_lost", 6));
        }
        // SAFETY: initialized out buffer; only this dedicated thread's message queue, bounded work.
        unsafe {
            let mut message: MSG = std::mem::zeroed();
            for _ in 0..64 {
                if PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_REMOVE) == 0 {
                    break;
                }
                if message.message == WM_QUIT {
                    return Err(error("console_message_loop_lost", 6));
                }
                // TranslateMessage produces WM_CHAR=3 for physical Ctrl+C on either edit or parent.
                if message.message == WM_CHAR && message.wParam == 3 {
                    STOP.store(true, Ordering::Release);
                    continue;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        for request in requests.try_iter().take(16) {
            if Instant::now() >= request.deadline {
                continue;
            }
            let _ = request.response.try_send(window.visible(request.visible));
        }
        let mut changed = false;
        let dropped = DROPPED.swap(0, Ordering::Relaxed);
        if dropped > 0 {
            text.extend(
                format!("[screen transcript dropped {dropped} records]\r\n").encode_utf16(),
            );
            changed = true;
        }
        for record in records.try_iter().take(16) {
            text.extend(record);
            changed = true;
        }
        if changed {
            if text.len() >= MAX_TEXT {
                let start = text.len() - MAX_TEXT / 2;
                let start = text[start..]
                    .iter()
                    .position(|c| *c == 10)
                    .map_or(start + 1, |i| start + i + 1);
                text.drain(..start);
            }
            text.push(0);
            window.transcript(&text);
            text.pop();
        }
        thread::sleep(POLL);
    }
    Ok(())
}
/// Sole mutable capability; !Send/!Sync. The UI thread owns all HWND effects.
/// Drop requests destruction and waits at most two seconds; explicit finish reports failure.
pub struct ConsoleControl {
    requests: SyncSender<Request>,
    stopping: Arc<AtomicBool>,
    finished: Receiver<Result<(), NativeError>>,
    thread: Option<JoinHandle<()>>,
    thread_bound: PhantomData<Rc<()>>,
}
impl ConsoleControl {
    pub fn install() -> Result<Self, NativeError> {
        Self::install_visible(false)
    }
    /// Called only after CLI dispatch and successful Host singleton acquisition.
    pub fn install_visible(visible: bool) -> Result<Self, NativeError> {
        if OWNER
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(error("console_owner_already_installed", 183));
        }
        let lifetime = Lifetime;
        // SAFETY: only this process detaches. No inherited/parent/conhost window is hidden or modified.
        if unsafe { FreeConsole() } == 0 {
            return Err(NativeError::last("detach_host_console"));
        }
        STOP.store(false, Ordering::Release);
        let (requests, receiver) = mpsc::sync_channel(16);
        let (records, record_receiver) = mpsc::sync_channel(64);
        let (ready, receipt) = mpsc::sync_channel(1);
        let (done, finished) = mpsc::sync_channel(1);
        let stopping = Arc::new(AtomicBool::new(false));
        let stop = stopping.clone();
        if let Ok(mut queue) = DIAGNOSTICS.lock() {
            *queue = Some(records);
        }
        let thread = thread::Builder::new()
            .name("host-diagnostics-window".into())
            .spawn(move || {
                let _lifetime = lifetime;
                let result = ui_loop(visible, receiver, record_receiver, stop, ready);
                let _ = done.send(result);
            })
            .map_err(|e| {
                error(
                    "start_console_window_thread",
                    e.raw_os_error().unwrap_or(1).cast_unsigned(),
                )
            })?;
        let mut owner = Self {
            requests,
            stopping,
            finished,
            thread: Some(thread),
            thread_bound: PhantomData,
        };
        receipt
            .recv_timeout(WAIT)
            .map_err(|_| error("console_start_deadline", 1460))??;
        // Keep owner alive until startup acknowledgement (also cleans up on failure).
        owner.rearm()?;
        Ok(owner)
    }
    pub fn take_ctrl_c(&mut self) -> bool {
        STOP.swap(false, Ordering::AcqRel)
    }
    pub fn rearm(&mut self) -> Result<(), NativeError> {
        if !console_state().0 {
            return Err(error("console_window_lost", 6));
        }
        Ok(())
    }
    pub fn attach(&mut self) -> Result<(), NativeError> {
        self.rearm()
    }
    fn set_visible(&mut self, visible: bool) -> Result<(), NativeError> {
        let (response, receipt) = mpsc::sync_channel(1);
        self.requests
            .try_send(Request {
                visible,
                deadline: Instant::now() + WAIT,
                response,
            })
            .map_err(|_| error("console_queue_unavailable", 170))?;
        receipt
            .recv_timeout(WAIT)
            .map_err(|_| error("console_visibility_deadline", 1460))?
    }
    pub fn show(&mut self) -> Result<(), NativeError> {
        self.set_visible(true)
    }
    pub fn hide(&mut self) -> Result<(), NativeError> {
        self.set_visible(false)
    }
    pub fn finish(&mut self) -> Result<(), NativeError> {
        if self.thread.is_none() {
            return Ok(());
        }
        self.stopping.store(true, Ordering::Release);
        let result = self
            .finished
            .recv_timeout(WAIT)
            .map_err(|_| error("console_shutdown_deadline", 1460));
        if result.is_ok()
            && let Some(thread) = self.thread.take()
        {
            thread
                .join()
                .map_err(|_| error("console_thread_panicked", 1))?;
        }
        result?
    }
}
impl Drop for ConsoleControl {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        os::windows::process::CommandExt,
        process::{Command, Stdio},
    };
    fn wait(mut condition: impl FnMut() -> bool) -> Result<(), Box<dyn std::error::Error>> {
        let deadline = Instant::now() + WAIT;
        while !condition() {
            if Instant::now() >= deadline {
                return Err("window test deadline".into());
            }
            thread::sleep(POLL);
        }
        Ok(())
    }
    #[test]
    fn isolated_host_window_close_restore_ctrl_c_loss_and_cleanup()
    -> Result<(), Box<dyn std::error::Error>> {
        const CHILD: &str = "ICEM_OWNED_HOST_WINDOW_TEST";
        if std::env::var_os(CHILD).is_some() {
            let mut owner = ConsoleControl::install()?;
            assert!(ConsoleControl::install().is_err());
            assert_eq!(console_state(), (true, false));
            let handle = WINDOW.load(Ordering::Acquire);
            // SAFETY: read-only probe of this isolated child's process attachment.
            assert!(unsafe { windows_sys::Win32::System::Console::GetConsoleWindow() }.is_null());
            for _ in 0..3 {
                owner.show()?;
                assert!(console_visible());
                // SAFETY: only the test child's own published window; asynchronous real WM_CLOSE path.
                assert_ne!(unsafe { PostMessageW(handle, WM_CLOSE, 0, 0) }, 0);
                wait(|| !console_visible())?;
                assert_eq!(WINDOW.load(Ordering::Acquire), handle);
                owner.show()?;
                owner.hide()?;
                owner.hide()?;
                owner.show()?;
                // SAFETY: request minimize of the child's own HWND; do not call sync window functions cross-thread.
                assert_ne!(unsafe { ShowWindowAsync(handle, SW_MINIMIZE) }, 0);
                wait(|| !console_restored())?;
                owner.show()?;
                assert!(console_restored());
            }
            for _ in 0..1000 {
                console_diagnostic(&"x".repeat(4096));
            }
            for _ in 0..2 {
                // SAFETY: actual window-message input path, only own HWND. Not a simulated CTRL_CLOSE handler.
                assert_ne!(unsafe { PostMessageW(handle, WM_CHAR, 3, 0) }, 0);
                wait(|| owner.take_ctrl_c())?;
                assert!(!owner.take_ctrl_c());
            }
            owner.hide()?;
            owner.finish()?;
            assert_eq!(console_state(), (false, false));
            // SAFETY: read-only probe after the UI thread has destroyed/joined its HWND.
            assert_eq!(unsafe { IsWindow(handle) }, 0);
            drop(owner);
            let mut successor = ConsoleControl::install_visible(true)?;
            assert!(console_visible());
            let handle = WINDOW.load(Ordering::Acquire);
            // SAFETY: inject message-loop loss into only this test-created HWND/thread.
            assert_ne!(unsafe { PostMessageW(handle, WM_QUIT, 0, 0) }, 0);
            wait(|| !console_state().0)?;
            assert!(successor.show().is_err());
            assert!(successor.finish().is_err());
            drop(successor);
            let mut last = ConsoleControl::install_visible(true)?;
            last.finish()?; // visible Quit, whereas first owner finished hidden.
            assert!(!OWNER.load(Ordering::Acquire));
            return Ok(());
        }
        let mut child = Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                "console::tests::isolated_host_window_close_restore_ctrl_c_loss_and_cleanup",
                "--nocapture",
            ])
            .env(CHILD, "1")
            .creation_flags(0x0000_0010)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()?;
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            if let Some(status) = child.try_wait()? {
                assert!(status.success());
                return Ok(());
            }
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err("isolated window child timed out".into());
            }
            thread::sleep(POLL);
        }
    }
}
