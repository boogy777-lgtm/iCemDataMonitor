//! Process-scoped console ownership. CTRL_CLOSE is NOT cancellable; ADR 0009.
use crate::NativeError;
use std::{
    fs::{File, OpenOptions},
    marker::PhantomData,
    os::windows::io::AsRawHandle,
    rc::Rc,
    sync::atomic::{AtomicBool, Ordering},
};
use windows_sys::Win32::{
    Foundation::HWND,
    System::Console::{
        AllocConsole, FreeConsole, GetConsoleWindow, STD_ERROR_HANDLE, STD_INPUT_HANDLE,
        STD_OUTPUT_HANDLE, SetConsoleCP, SetConsoleCtrlHandler, SetConsoleOutputCP, SetStdHandle,
    },
    UI::WindowsAndMessaging::{
        BringWindowToTop, IsIconic, IsWindow, IsWindowVisible, SW_HIDE, SW_RESTORE, SW_SHOW,
        SetForegroundWindow, ShowWindow,
    },
};
static OWNER: AtomicBool = AtomicBool::new(false);
static CTRL_C: AtomicBool = AtomicBool::new(false);
/// No locks, allocation, logging, UI calls or waiting on the OS control-handler thread.
unsafe extern "system" fn handler(event: u32) -> i32 {
    if event == 0 {
        CTRL_C.store(true, Ordering::Release);
        1
    } else {
        0
    }
}
fn window() -> HWND {
    // SAFETY: OS-provided HWND, validated freshly; no cached handle or pointer escapes.
    unsafe {
        let window = GetConsoleWindow();
        if !window.is_null() && IsWindow(window) != 0 {
            window
        } else {
            std::ptr::null_mut()
        }
    }
}
pub fn console_state() -> (bool, bool) {
    let window = window();
    // SAFETY: fresh HWND, checked before read-only visibility probe.
    (
        !window.is_null(),
        !window.is_null() && unsafe { IsWindowVisible(window) != 0 },
    )
}
pub fn console_visible() -> bool {
    console_state().1
}
/// Sole mutable console capability. !Send/!Sync; the Host service thread owns it.
pub struct ConsoleControl {
    registered: bool,
    stdio: Option<[File; 3]>,
    thread_bound: PhantomData<Rc<()>>,
}
impl ConsoleControl {
    pub fn install() -> Result<Self, NativeError> {
        if OWNER
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(NativeError {
                operation: "console_owner_already_installed",
                code: 183,
            });
        }
        CTRL_C.store(false, Ordering::Release);
        let mut owner = Self {
            registered: false,
            stdio: None,
            thread_bound: PhantomData,
        };
        owner.rearm()?;
        Ok(owner)
    }
    /// Alloc/Free resets Windows' handler table. A headless process may have no console yet.
    pub fn rearm(&mut self) -> Result<(), NativeError> {
        if self.registered {
            // SAFETY: static callback target; removal doesn't access Rust-owned memory.
            if unsafe { SetConsoleCtrlHandler(Some(handler), 0) } == 0 {
                return Err(NativeError::last("unregister_console_control"));
            }
            self.registered = false;
        }
        // SAFETY: static ABI-correct callback, no captured/borrowed state.
        if unsafe { SetConsoleCtrlHandler(Some(handler), 1) } == 0 {
            let error = NativeError::last("register_console_control");
            if error.code == 6 && window().is_null() {
                return Ok(());
            }
            return Err(error);
        }
        self.registered = true;
        // SAFETY: process-local inherited ignore attribute; our callback is installed first.
        if unsafe { SetConsoleCtrlHandler(None, 0) } == 0 {
            return Err(NativeError::last("enable_console_ctrl_c"));
        }
        Ok(())
    }
    pub fn take_ctrl_c(&mut self) -> bool {
        CTRL_C.swap(false, Ordering::AcqRel)
    }
    pub fn attach(&mut self) -> Result<(), NativeError> {
        if !window().is_null() {
            return Ok(());
        }
        // SAFETY: affects only the calling process's console attachment.
        if unsafe { AllocConsole() } == 0 {
            let error = NativeError::last("allocate_console");
            if error.code != 5 {
                return Err(error);
            }
            // CREATE_NO_WINDOW can leave a console object without a visible HWND.
            // SAFETY: sole console capability serializes detach/allocate on this process.
            if unsafe { FreeConsole() } == 0 {
                return Err(NativeError::last("detach_windowless_console"));
            }
            self.registered = false;
            // SAFETY: this process just detached; no external PID/window is targeted.
            if unsafe { AllocConsole() } == 0 {
                return Err(NativeError::last("replace_windowless_console"));
            }
        }
        self.registered = false; // AllocConsole resets the OS handler table as well.
        self.rearm()?;
        self.rebind()?;
        // SAFETY: UTF-8 codepage affects this process's attached console only, best effort.
        unsafe {
            SetConsoleCP(65001);
            SetConsoleOutputCP(65001);
        }
        Ok(())
    }
    fn rebind(&mut self) -> Result<(), NativeError> {
        let output = OpenOptions::new()
            .read(true)
            .write(true)
            .open("CONOUT$")
            .map_err(io_error)?;
        let error = output.try_clone().map_err(io_error)?;
        let input = OpenOptions::new()
            .read(true)
            .write(true)
            .open("CONIN$")
            .map_err(io_error)?;
        // Store handles BEFORE installing any: a later failure must not leave a closed std handle.
        self.stdio = Some([input, output, error]);
        if let Some(files) = &self.stdio {
            for (id, file) in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE]
                .into_iter()
                .zip(files)
            {
                // SAFETY: live owned File handle retained through the ConsoleControl lifetime.
                if unsafe { SetStdHandle(id, file.as_raw_handle()) } == 0 {
                    return Err(NativeError::last("set_console_standard_handle"));
                }
            }
        }
        Ok(())
    }
    pub fn show(&mut self) -> Result<(), NativeError> {
        let window = window();
        if window.is_null() {
            return Err(NativeError {
                operation: "show_console_without_window",
                code: 6,
            });
        }
        // SAFETY: fresh console HWND. ShowWindow reports previous visibility, NOT success.
        unsafe {
            let command = if IsIconic(window) != 0 {
                SW_RESTORE
            } else {
                SW_SHOW
            };
            ShowWindow(window, SW_HIDE); // born-hidden STARTUPINFO recovery, as in legacy
            ShowWindow(window, command);
            if IsWindowVisible(window) != 0 {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
        }
        Ok(())
    }
    pub fn hide(&mut self) -> Result<(), NativeError> {
        let window = window();
        if !window.is_null() {
            // SAFETY: fresh validated process console HWND; outcome is reprobed by Controller.
            unsafe {
                ShowWindow(window, SW_HIDE);
            }
        }
        Ok(())
    }
}
fn io_error(error: std::io::Error) -> NativeError {
    NativeError {
        operation: "open_console_standard_device",
        code: error.raw_os_error().unwrap_or(1).cast_unsigned(),
    }
}
impl Drop for ConsoleControl {
    fn drop(&mut self) {
        if self.registered {
            // SAFETY: static callback; no heap state can be accessed by a concurrent invocation.
            unsafe {
                SetConsoleCtrlHandler(Some(handler), 0);
            }
        }
        if self.stdio.is_some() {
            for id in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
                // SAFETY: clear our installed standard handles before owned Files close.
                unsafe {
                    SetStdHandle(id, std::ptr::null_mut());
                }
            }
        }
        OWNER.store(false, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        os::windows::process::CommandExt,
        process::{Command, Stdio},
        time::{Duration, Instant},
    };
    #[test]
    fn isolated_console_owner_visibility_and_ctrl_c() -> Result<(), Box<dyn std::error::Error>> {
        const CHILD: &str = "ICEM_ISOLATED_CONSOLE_TEST";
        if std::env::var_os(CHILD).is_some() {
            // SAFETY: only this isolated child changes its own inherited ignore attribute.
            let ignored = unsafe { SetConsoleCtrlHandler(None, 1) };
            assert_ne!(ignored, 0);
            let mut owner = ConsoleControl::install()?;
            assert!(ConsoleControl::install().is_err());
            assert!(console_state().0);
            for _ in 0..3 {
                owner.hide()?;
                assert!(!console_visible());
                owner.show()?;
                assert!(console_visible());
            }
            // A callback cannot cancel CTRL_CLOSE. Do not claim X -> hide parity.
            // SAFETY: direct test of our static callback, no OS signal generated.
            assert_eq!(unsafe { handler(2) }, 0);
            assert!(!owner.take_ctrl_c());
            for _ in 0..2 {
                owner.rearm()?;
                // SAFETY: child has a fresh private console; group 0 targets only its console.
                let sent =
                    unsafe { windows_sys::Win32::System::Console::GenerateConsoleCtrlEvent(0, 0) };
                assert_ne!(sent, 0);
                let deadline = Instant::now() + Duration::from_secs(2);
                while !owner.take_ctrl_c() {
                    if Instant::now() >= deadline {
                        return Err("Ctrl+C not delivered".into());
                    }
                    std::thread::sleep(Duration::from_millis(10));
                }
                assert!(!owner.take_ctrl_c());
            }
            return Ok(());
        }
        let mut child = Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                "console::tests::isolated_console_owner_visibility_and_ctrl_c",
                "--nocapture",
            ])
            .env(CHILD, "1")
            .creation_flags(0x0000_0010) // CREATE_NEW_CONSOLE; never signal the test runner console.
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()?;
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(status) = child.try_wait()? {
                assert!(status.success());
                return Ok(());
            }
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err("isolated console test timed out".into());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}
