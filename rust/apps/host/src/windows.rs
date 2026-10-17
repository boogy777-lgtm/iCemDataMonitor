//! Host-only lifecycle. The independent emulator is never spawned or signalled here.
use icem_windows_native::{
    NamedMutex, NativeError, OwnedEvent, Tray, TrayItem, console_visible, set_console_visible,
};
use std::{io, path::PathBuf, process::Command};
pub fn singleton() -> Result<NamedMutex, NativeError> {
    NamedMutex::open_or_create(icem_platform_windows::HOST_MUTEX)
}
pub enum Action {
    Panel,
    Web,
    Console,
    Restart,
    Close,
}
pub struct Lifecycle {
    tray: Option<Tray>,
    restart: OwnedEvent,
    close: OwnedEvent,
}
impl Lifecycle {
    pub fn new(icon: Option<PathBuf>) -> Result<Self, Box<dyn std::error::Error>> {
        let restart = OwnedEvent::create_reset(icem_platform_windows::HOST_RESTART)?;
        let close = OwnedEvent::create_reset(icem_platform_windows::HOST_SHUTDOWN)?;
        let tray = icon
            .map(|icon| {
                Tray::start_dynamic(
                    icon,
                    "iCem DataMonitor".into(),
                    Box::new(|| {
                        vec![
                            TrayItem {
                                id: 1,
                                label: "Открыть панель".into(),
                            },
                            TrayItem {
                                id: 2,
                                label: "Открыть Web".into(),
                            },
                            TrayItem {
                                id: 3,
                                label: if console_visible() {
                                    "Скрыть консоль"
                                } else {
                                    "Показать консоль"
                                }
                                .into(),
                            },
                            TrayItem {
                                id: 4,
                                label: "Перезапуск".into(),
                            },
                            TrayItem {
                                id: 5,
                                label: "Закрыть".into(),
                            },
                        ]
                    }),
                )
            })
            .transpose()?;
        Ok(Self {
            tray,
            restart,
            close,
        })
    }
    pub fn poll(&self) -> Result<Option<Action>, NativeError> {
        if self.close.poll()? {
            return Ok(Some(Action::Close));
        }
        if self.restart.poll()? {
            return Ok(Some(Action::Restart));
        }
        Ok(self
            .tray
            .as_ref()
            .and_then(Tray::try_event)
            .and_then(|id| match id {
                1 => Some(Action::Panel),
                2 => Some(Action::Web),
                3 => Some(Action::Console),
                4 => Some(Action::Restart),
                5 => Some(Action::Close),
                _ => None,
            }))
    }
    pub fn remove_tray(&mut self) -> io::Result<()> {
        if let Some(tray) = self.tray.take() {
            tray.stop()?;
        }
        Ok(())
    }
}
pub fn open_panel(pipe: &str, url: &str) -> io::Result<()> {
    let exe = std::env::current_exe()?.with_file_name("icem-shell-dev.exe");
    Command::new(exe)
        .args(["--pipe", pipe, "--web-url", url])
        .spawn()?;
    Ok(())
}
pub fn open_web(url: &str) -> io::Result<()> {
    Command::new("rundll32.exe")
        .args(["url.dll,FileProtocolHandler", url])
        .spawn()?;
    Ok(())
}
pub fn toggle_console() -> Result<(), NativeError> {
    set_console_visible(!console_visible()).map(|_| ())
}
pub fn close_shell() -> Result<(), NativeError> {
    icem_windows_native::signal_existing_event(icem_platform_windows::SHELL_SHUTDOWN).map(|_| ())
}
