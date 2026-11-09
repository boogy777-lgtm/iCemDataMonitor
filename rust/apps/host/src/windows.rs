//! Host-only lifecycle. The independent emulator is never spawned or signalled here.
use icem_core::HostCommand;
use icem_desktop::HostTray;
use icem_windows_native::{NamedMutex, NativeError, OwnedEvent};
use std::{io, path::PathBuf, process::Command};
pub fn singleton(
    identity: &icem_platform_windows::HostIpcIdentity,
) -> Result<NamedMutex, NativeError> {
    NamedMutex::open_or_create(&identity.mutex)
}
pub enum Action {
    Panel,
    Web,
    Console(bool),
    Restart,
    Close,
}
pub struct Lifecycle {
    tray: Option<HostTray>,
    restart: OwnedEvent,
    close: OwnedEvent,
    owns_shell: bool,
}
impl Lifecycle {
    pub fn new(
        icon: Option<PathBuf>,
        identity: &icem_platform_windows::HostIpcIdentity,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let restart = OwnedEvent::create_reset(&identity.restart)?;
        let close = OwnedEvent::create_reset(&identity.shutdown)?;
        let tray = icon.map(HostTray::start).transpose()?;
        Ok(Self {
            tray,
            restart,
            close,
            owns_shell: identity.owns_shell,
        })
    }
    pub fn poll(&self) -> Result<Option<Action>, Box<dyn std::error::Error>> {
        if self.close.poll()? {
            return Ok(Some(Action::Close));
        }
        if self.restart.poll()? {
            return Ok(Some(Action::Restart));
        }
        let command = self
            .tray
            .as_ref()
            .map(HostTray::poll)
            .transpose()?
            .flatten();
        Ok(command.map(|command| match command {
            HostCommand::OpenPanel => Action::Panel,
            HostCommand::OpenWeb => Action::Web,
            HostCommand::SetConsoleVisible(visible) => Action::Console(visible),
            HostCommand::Restart => Action::Restart,
            HostCommand::Close => Action::Close,
        }))
    }

    pub fn remove_tray(&mut self) -> io::Result<()> {
        if let Some(tray) = self.tray.take() {
            tray.stop()?;
        }
        Ok(())
    }
    pub fn close_shell(&self) -> Result<(), NativeError> {
        if self.owns_shell {
            close_shell()?;
        }
        Ok(())
    }
}
pub fn open_panel(pipe: &str) -> io::Result<()> {
    let exe = std::env::current_exe()?.with_file_name("icem-shell-dev.exe");
    Command::new(exe).args(["--pipe", pipe]).spawn()?;
    Ok(())
}
pub fn open_web(url: &str) -> io::Result<()> {
    Command::new("rundll32.exe")
        .args(["url.dll,FileProtocolHandler", url])
        .spawn()?;
    Ok(())
}
pub fn close_shell() -> Result<(), NativeError> {
    icem_windows_native::signal_existing_event(icem_platform_windows::SHELL_SHUTDOWN).map(|_| ())
}
impl Drop for Lifecycle {
    fn drop(&mut self) {
        if let Err(error) = self.remove_tray() {
            diagnostic!("Host tray cleanup: {error}");
        }
        if let Err(error) = self.close_shell() {
            diagnostic!("Host shell cleanup: {error}");
        }
    }
}

pub struct ShellSignals;
impl icem_web::navigation::ShellSignals for ShellSignals {
    fn alive(&self) -> Result<bool, String> {
        icem_windows_native::event_exists(icem_platform_windows::SHELL_SHOW)
            .map_err(|e| e.to_string())
    }
    fn show(&self) -> Result<bool, String> {
        icem_windows_native::signal_existing_event(icem_platform_windows::SHELL_SHOW)
            .map_err(|e| e.to_string())
    }
}
