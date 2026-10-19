//! One Host console policy, independent of the source of a visibility command.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Presence {
    None,
    Hidden,
    Visible,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action {
    Nothing,
    AttachAndShow,
    Show,
    Hide,
}
pub const fn classify(has_window: bool, visible: bool) -> Presence {
    if !has_window {
        Presence::None
    } else if visible {
        Presence::Visible
    } else {
        Presence::Hidden
    }
}
pub const fn decide(presence: Presence, visible: bool) -> Action {
    match (presence, visible) {
        (Presence::None, true) => Action::AttachAndShow,
        (Presence::Hidden, true) => Action::Show,
        (Presence::Visible, true) | (Presence::None, false) => Action::Nothing,
        (Presence::Hidden, false) | (Presence::Visible, false) => Action::Hide,
    }
}
/// Native effects only. Test devices inject nondeterministic window/OS outcomes.
pub trait Device {
    fn probe(&self) -> Presence;
    fn attach(&mut self) -> Result<(), String>;
    fn show(&mut self) -> Result<(), String>;
    fn hide(&mut self) -> Result<(), String>;
    fn restored(&self) -> bool {
        true
    }
    fn check(&self) -> Result<(), String> {
        Ok(())
    }
    fn take_stop(&mut self) -> bool {
        false
    }
    fn rearm(&mut self) -> Result<(), String> {
        Ok(())
    }
}
pub struct Controller<D> {
    device: D,
}
impl<D: Device> Controller<D> {
    pub fn new(device: D) -> Self {
        Self { device }
    }
    pub fn visible(&self) -> bool {
        self.device.probe() == Presence::Visible
    }
    pub fn set_visible(&mut self, visible: bool) -> Result<bool, String> {
        self.device.check()?;
        let action = if visible && !self.device.restored() {
            Action::Show
        } else {
            decide(self.device.probe(), visible)
        };
        match action {
            Action::Nothing => {}
            Action::AttachAndShow => {
                self.device.attach()?;
                self.device.show()?;
            }
            Action::Show => self.device.show()?,
            Action::Hide => self.device.hide()?,
        }
        let actual = self.visible();
        if actual != visible {
            return Err(format!(
                "console requested visible={visible}, actual visible={actual}"
            ));
        }
        Ok(actual)
    }
    pub fn take_stop(&mut self) -> bool {
        self.device.take_stop()
    }
    pub fn rearm(&mut self) -> Result<(), String> {
        self.device.rearm()
    }
    pub fn device_mut(&mut self) -> &mut D {
        &mut self.device
    }
}
#[cfg(windows)]
impl Device for icem_windows_native::ConsoleControl {
    fn probe(&self) -> Presence {
        let (has_window, visible) = icem_windows_native::console_state();
        classify(has_window, visible)
    }
    fn attach(&mut self) -> Result<(), String> {
        self.attach().map_err(|e| e.to_string())
    }
    fn show(&mut self) -> Result<(), String> {
        self.show().map_err(|e| e.to_string())
    }
    fn hide(&mut self) -> Result<(), String> {
        self.hide().map_err(|e| e.to_string())
    }
    fn restored(&self) -> bool {
        icem_windows_native::console_restored()
    }
    fn check(&self) -> Result<(), String> {
        if icem_windows_native::console_state().0 {
            Ok(())
        } else {
            Err("console window lost".into())
        }
    }
    fn take_stop(&mut self) -> bool {
        self.take_ctrl_c()
    }
    fn rearm(&mut self) -> Result<(), String> {
        self.rearm().map_err(|e| e.to_string())
    }
}

#[cfg(not(windows))]
pub struct Unsupported;
#[cfg(not(windows))]
impl Device for Unsupported {
    fn probe(&self) -> Presence {
        Presence::None
    }
    fn attach(&mut self) -> Result<(), String> {
        Err("console requires Windows".into())
    }
    fn show(&mut self) -> Result<(), String> {
        Err("console requires Windows".into())
    }
    fn hide(&mut self) -> Result<(), String> {
        Err("console requires Windows".into())
    }
}
