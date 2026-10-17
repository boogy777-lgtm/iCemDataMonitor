use icem_web::{
    navigation::{Navigation, ShellSignals},
    proto::UiSection,
};
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
#[derive(Default)]
struct Signals {
    alive: AtomicBool,
    fail: AtomicBool,
    wakes: AtomicUsize,
}
impl ShellSignals for Signals {
    fn alive(&self) -> Result<bool, String> {
        Ok(self.alive.load(Ordering::Acquire))
    }
    fn show(&self) -> Result<bool, String> {
        self.wakes.fetch_add(1, Ordering::Relaxed);
        if self.fail.load(Ordering::Acquire) {
            Err("denied".into())
        } else {
            Ok(true)
        }
    }
}
#[test]
fn pending_survives_absent_shell_last_write_wins_and_query_is_read_clear() -> Result<(), String> {
    let s = Arc::new(Signals::default());
    let n = Navigation::new(s.clone());
    assert!(!n.navigate(UiSection::Web)?.ui_alive);
    n.navigate(UiSection::Status)?;
    s.alive.store(true, Ordering::Release);
    let r = n.navigate(UiSection::Unspecified)?;
    assert!(r.ui_alive);
    assert_eq!(r.pending, UiSection::Status as i32);
    assert_eq!(n.navigate(UiSection::Unspecified)?.pending, 0);
    assert_eq!(s.wakes.load(Ordering::Relaxed), 0);
    Ok(())
}
#[test]
fn failed_or_raced_show_keeps_pending_and_reports_not_alive() -> Result<(), String> {
    let s = Arc::new(Signals::default());
    s.alive.store(true, Ordering::Release);
    s.fail.store(true, Ordering::Release);
    let n = Navigation::new(s.clone());
    let r = n.navigate(UiSection::Web)?;
    assert!(!r.ui_alive);
    assert_eq!(
        n.navigate(UiSection::Unspecified)?.pending,
        UiSection::Web as i32
    );
    s.fail.store(false, Ordering::Release);
    assert!(n.navigate(UiSection::Status)?.ui_alive);
    assert_eq!(s.wakes.load(Ordering::Relaxed), 2);
    Ok(())
}
