use icem_platform_windows::console::{Action, Controller, Device, Presence, classify, decide};

struct Fake {
    state: Presence,
    calls: Vec<&'static str>,
    refuses: bool,
    fails: bool,
    stop: bool,
}
impl Fake {
    fn new(state: Presence) -> Self {
        Self {
            state,
            calls: Vec::new(),
            refuses: false,
            fails: false,
            stop: false,
        }
    }
    fn effect(&mut self, name: &'static str, next: Presence) -> Result<(), String> {
        self.calls.push(name);
        if self.fails {
            return Err("access denied".into());
        }
        if !self.refuses {
            self.state = next;
        }
        Ok(())
    }
}
impl Device for Fake {
    fn probe(&self) -> Presence {
        self.state
    }
    fn attach(&mut self) -> Result<(), String> {
        self.effect("attach", Presence::Hidden)
    }
    fn show(&mut self) -> Result<(), String> {
        self.effect("show", Presence::Visible)
    }
    fn hide(&mut self) -> Result<(), String> {
        self.effect("hide", Presence::Hidden)
    }
    fn take_stop(&mut self) -> bool {
        std::mem::take(&mut self.stop)
    }
}
#[test]
fn complete_legacy_visibility_table() {
    for (presence, visible, action) in [
        (Presence::None, false, Action::Nothing),
        (Presence::None, true, Action::AttachAndShow),
        (Presence::Hidden, false, Action::Hide),
        (Presence::Hidden, true, Action::Show),
        (Presence::Visible, false, Action::Hide),
        (Presence::Visible, true, Action::Nothing),
    ] {
        assert_eq!(decide(presence, visible), action);
    }
    assert_eq!(classify(false, true), Presence::None);
    assert_eq!(classify(false, false), Presence::None);
    assert_eq!(classify(true, false), Presence::Hidden);
    assert_eq!(classify(true, true), Presence::Visible);
}
#[test]
fn headless_attach_and_repeated_show_hide_are_actual_states() -> Result<(), String> {
    let mut console = Controller::new(Fake::new(Presence::None));
    assert!(!console.set_visible(false)?);
    for _ in 0..3 {
        assert!(console.set_visible(true)?);
        assert!(console.set_visible(true)?);
        assert!(!console.set_visible(false)?);
    }
    assert_eq!(
        console.device_mut().calls,
        ["attach", "show", "hide", "show", "hide", "show", "hide"]
    );
    Ok(())
}
#[test]
fn refusal_and_os_error_never_become_optimistic_success() {
    let mut device = Fake::new(Presence::Hidden);
    device.refuses = true;
    let mut console = Controller::new(device);
    assert!(console.set_visible(true).is_err());
    assert!(!console.visible());
    console.device_mut().fails = true;
    assert_eq!(console.set_visible(true), Err("access denied".into()));
    assert!(!console.visible());
}
#[test]
fn next_command_reprobes_lost_window_and_stop_is_consumed_once() -> Result<(), String> {
    let mut console = Controller::new(Fake::new(Presence::Visible));
    console.device_mut().state = Presence::None;
    assert!(console.set_visible(true)?);
    assert_eq!(console.device_mut().calls, ["attach", "show"]);
    console.device_mut().stop = true;
    assert!(console.take_stop());
    assert!(!console.take_stop());
    Ok(())
}
