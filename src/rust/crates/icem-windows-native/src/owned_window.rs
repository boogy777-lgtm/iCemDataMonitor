//! Borrowed application windows only. Console/conhost windows are outside this class.
use crate::NativeError;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows_sys::Win32::{
    System::Threading::GetCurrentProcessId,
    UI::WindowsAndMessaging::{
        GetWindowThreadProcessId, IsIconic, IsWindow, IsWindowVisible, SW_HIDE, SW_RESTORE,
        SW_SHOW, ShowWindowAsync,
    },
};
/// Validate a live borrowed application window before every read/effect.
fn owned(
    window: &impl HasWindowHandle,
) -> Result<windows_sys::Win32::Foundation::HWND, NativeError> {
    let borrowed = window.window_handle().map_err(|_| NativeError {
        operation: "borrow_window",
        code: 6,
    })?;
    let RawWindowHandle::Win32(handle) = borrowed.as_raw() else {
        return Err(NativeError {
            operation: "non_windows_window",
            code: 50,
        });
    };
    let handle = handle.hwnd.get() as windows_sys::Win32::Foundation::HWND;
    // SAFETY: live borrowed handle; read-only probes precede any effect. No pointer escapes.
    unsafe {
        if IsWindow(handle) == 0 {
            return Err(NativeError {
                operation: "window_no_longer_exists",
                code: 6,
            });
        }
        let mut pid = 0;
        if GetWindowThreadProcessId(handle, &mut pid) == 0 {
            return Err(NativeError::last("window_owner"));
        }
        if pid != GetCurrentProcessId() {
            return Err(NativeError {
                operation: "foreign_window_refused",
                code: 5,
            });
        }
    }
    Ok(handle)
}
/// Accept an asynchronous OS request, NOT an acknowledgement of actual visibility.
/// No cached HWND, cross-thread wait, focus claim or transfer of window ownership.
pub fn request_window_visible(
    window: &impl HasWindowHandle,
    visible: bool,
) -> Result<(), NativeError> {
    let handle = owned(window)?;
    // SAFETY: validated borrowed window in this process. Async avoids reentrant GPUI callbacks.
    unsafe {
        let command = if !visible {
            SW_HIDE
        } else if IsIconic(handle) != 0 {
            SW_RESTORE
        } else {
            SW_SHOW
        };
        if ShowWindowAsync(handle, command) == 0 {
            return Err(NativeError::last("request_window_visibility"));
        }
    }
    Ok(())
}
/// Returns (visible, minimized); a visible minimized window is not yet restored.
pub fn window_visibility(window: &impl HasWindowHandle) -> Result<(bool, bool), NativeError> {
    let handle = owned(window)?;
    // SAFETY: read-only probe on the same live borrowed window, after owner validation.
    Ok(unsafe { (IsWindowVisible(handle) != 0, IsIconic(handle) != 0) })
}
#[cfg(test)]
mod tests {
    use super::*;
    use raw_window_handle::{HandleError, Win32WindowHandle, WindowHandle};
    use std::num::NonZeroIsize;
    use windows_sys::Win32::{
        Foundation::HWND,
        UI::WindowsAndMessaging::{
            CreateWindowExW, DestroyWindow, DispatchMessageW, GetDesktopWindow, MSG, PM_REMOVE,
            PeekMessageW, SW_MINIMIZE, ShowWindow, TranslateMessage, WS_OVERLAPPEDWINDOW,
            WS_VISIBLE,
        },
    };
    struct Window {
        handle: HWND,
        owned: bool,
    }
    impl HasWindowHandle for Window {
        fn window_handle(&self) -> Result<WindowHandle<'_>, HandleError> {
            let hwnd = NonZeroIsize::new(self.handle as isize).ok_or(HandleError::Unavailable)?;
            // SAFETY: test owns the created HWND until Drop, or borrows the live system desktop.
            Ok(unsafe {
                WindowHandle::borrow_raw(RawWindowHandle::Win32(Win32WindowHandle::new(hwnd)))
            })
        }
    }
    impl Drop for Window {
        fn drop(&mut self) {
            if self.owned {
                // SAFETY: only the test-created HWND, on its creating test thread.
                unsafe {
                    DestroyWindow(self.handle);
                }
            }
        }
    }
    fn wait_visibility(window: &Window, visible: bool) -> Result<(), Box<dyn std::error::Error>> {
        request_window_visible(window, visible)?;
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
        loop {
            // SAFETY: initialized MSG out buffer, this test thread's queue only.
            unsafe {
                let mut message: MSG = std::mem::zeroed();
                while PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_REMOVE) != 0 {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
            let (actual, minimized) = window_visibility(window)?;
            if actual == visible && (!visible || !minimized) {
                return Ok(());
            }
            if std::time::Instant::now() >= deadline {
                return Err("visibility deadline".into());
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
    }
    #[test]
    fn hide_show_restore_keeps_the_same_owned_window_and_refuses_foreign_window()
    -> Result<(), Box<dyn std::error::Error>> {
        let class: Vec<u16> = "STATIC\0".encode_utf16().collect();
        // SAFETY: null-terminated class/title; built-in STATIC class, no callback or borrowed payload.
        let handle = unsafe {
            CreateWindowExW(
                0,
                class.as_ptr(),
                class.as_ptr(),
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                0,
                0,
                300,
                200,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null(),
            )
        };
        if handle.is_null() {
            return Err(NativeError::last("test_create_window").into());
        }
        let window = Window {
            handle,
            owned: true,
        };
        for _ in 0..3 {
            wait_visibility(&window, false)?;
            wait_visibility(&window, false)?;
            wait_visibility(&window, true)?;
            // SAFETY: our live owned HWND, on this thread.
            unsafe {
                ShowWindow(window.handle, SW_MINIMIZE);
            }
            wait_visibility(&window, true)?;
            // SAFETY: read-only probes on the same live owned window.
            unsafe {
                assert_ne!(IsWindow(handle), 0);
                assert_eq!(IsIconic(handle), 0);
            }
        }
        // SAFETY: read-only lookup of the live system desktop, never owned/destroyed by the test.
        let desktop = unsafe { GetDesktopWindow() };
        let foreign = Window {
            handle: desktop,
            owned: false,
        };
        assert!(request_window_visible(&foreign, false).is_err());
        Ok(())
    }
}
