//! HKCU Run entry, exact legacy value data and StartupApproved cleanup.
use crate::windows::NativeError;
use std::ptr;
use windows_sys::Win32::{
    Foundation::{ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, ERROR_SUCCESS},
    System::Registry::*,
};
fn wide(value: &str) -> Result<Vec<u16>, NativeError> {
    if value.is_empty() || value.contains('\0') {
        return Err(error("registry_name", 87));
    }
    Ok(value.encode_utf16().chain(std::iter::once(0)).collect())
}
fn error(operation: &'static str, code: u32) -> NativeError {
    NativeError { operation, code }
}
struct Key(HKEY);
impl Drop for Key {
    fn drop(&mut self) {
        // SAFETY: uniquely owns an opened/created registry handle; no predefined root stored.
        unsafe {
            RegCloseKey(self.0);
        }
    }
}
fn open(path: &[u16], create: bool) -> Result<Option<Key>, NativeError> {
    let mut key = ptr::null_mut();
    // SAFETY: initialized output, valid terminated HKCU-relative path; no pointer retained.
    let status = unsafe {
        if create {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                path.as_ptr(),
                0,
                ptr::null(),
                0,
                KEY_QUERY_VALUE | KEY_SET_VALUE,
                ptr::null(),
                &mut key,
                ptr::null_mut(),
            )
        } else {
            RegOpenKeyExW(
                HKEY_CURRENT_USER,
                path.as_ptr(),
                0,
                KEY_QUERY_VALUE | KEY_SET_VALUE,
                &mut key,
            )
        }
    };
    match status {
        ERROR_SUCCESS => Ok(Some(Key(key))),
        ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => Ok(None),
        code => Err(error("registry_open", code)),
    }
}
/// Paths/value/executable are injectable exactly as in the .NET AutostartManager.
/// Production callers choose the canonical names; tests use throwaway HKCU keys.
pub struct AutostartEntry {
    run: Vec<u16>,
    approved: Vec<u16>,
    name: Vec<u16>,
    expected: String,
}
impl AutostartEntry {
    pub fn new(run: &str, approved: &str, name: &str, exe: &str) -> Result<Self, NativeError> {
        if exe.is_empty() || exe.contains(['\0', '"']) {
            return Err(error("autostart_exe", 87));
        }
        Ok(Self {
            run: wide(run)?,
            approved: wide(approved)?,
            name: wide(name)?,
            expected: format!("\"{exe}\" --autostart"),
        })
    }
    pub fn is_enabled(&self) -> Result<bool, NativeError> {
        // Query-only probe: GetStatus does not create/open writable keys.
        let mut buffer = vec![0u16; 32768];
        let mut bytes = (buffer.len() * 2) as u32;
        // SAFETY: terminated names, bounded aligned UTF-16 output and initialized size.
        // RegGetValueW copies data and appends NUL for REG_SZ within the supplied capacity.
        let status = unsafe {
            RegGetValueW(
                HKEY_CURRENT_USER,
                self.run.as_ptr(),
                self.name.as_ptr(),
                RRF_RT_REG_SZ | RRF_ZEROONFAILURE,
                ptr::null_mut(),
                buffer.as_mut_ptr().cast(),
                &mut bytes,
            )
        };
        match status {
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => Ok(false),
            ERROR_SUCCESS => {
                let count = usize::try_from(bytes / 2)
                    .map_err(|_| error("registry_size", 87))?
                    .min(buffer.len());
                let end = buffer[..count]
                    .iter()
                    .position(|v| *v == 0)
                    .unwrap_or(count);
                let text =
                    String::from_utf16(&buffer[..end]).map_err(|_| error("registry_utf16", 13))?;
                Ok(text == self.expected)
            }
            // A non-string Run value cannot match the expected legacy string.
            1630 => Ok(false),
            code => Err(error("registry_read", code)),
        }
    }
    pub fn set_enabled(&self, enabled: bool) -> Result<(), NativeError> {
        let key = open(&self.run, true)?.ok_or_else(|| error("registry_create", 2))?;
        if enabled {
            let data: Vec<u16> = self
                .expected
                .encode_utf16()
                .chain(std::iter::once(0))
                .collect();
            let bytes = u32::try_from(data.len() * 2).map_err(|_| error("registry_size", 87))?;
            // SAFETY: live key; terminated name, initialized UTF-16 string including NUL.
            let status = unsafe {
                RegSetValueExW(
                    key.0,
                    self.name.as_ptr(),
                    0,
                    REG_SZ,
                    data.as_ptr().cast(),
                    bytes,
                )
            };
            if status != ERROR_SUCCESS {
                return Err(error("autostart_write", status));
            }
            drop(key);
            if let Some(approved) = open(&self.approved, false)? {
                delete_value(&approved, &self.name)?;
            }
        } else {
            delete_value(&key, &self.name)?;
        }
        Ok(())
    }
}
fn delete_value(key: &Key, name: &[u16]) -> Result<(), NativeError> {
    // SAFETY: live owned key; terminated value name, no key deletion or pointer retention.
    match unsafe { RegDeleteValueW(key.0, name.as_ptr()) } {
        ERROR_SUCCESS | ERROR_FILE_NOT_FOUND => Ok(()),
        code => Err(error("autostart_delete", code)),
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exact_value_truth_stale_path_and_idempotent_disable() -> Result<(), NativeError> {
        let root = format!(r"Software\iCemRustTests\Autostart_{}", std::process::id());
        let approved = format!(r"{root}\Approved");
        let entry =
            AutostartEntry::new(&root, &approved, "Host", r"C:\Program Files\iCem\host.exe")?;
        struct Cleanup(Vec<u16>);
        impl Drop for Cleanup {
            fn drop(&mut self) {
                // SAFETY: this test's unique throwaway HKCU subtree, never a production Run key.
                unsafe {
                    RegDeleteTreeW(HKEY_CURRENT_USER, self.0.as_ptr());
                }
            }
        }
        let _cleanup = Cleanup(wide(&root)?);
        assert!(!entry.is_enabled()?);
        entry.set_enabled(true)?;
        assert!(entry.is_enabled()?);
        let stale = AutostartEntry::new(&root, &approved, "Host", r"C:\old\host.exe")?;
        assert!(!stale.is_enabled()?);
        entry.set_enabled(false)?;
        assert!(!entry.is_enabled()?);
        entry.set_enabled(false)?;
        Ok(())
    }
}
