#![cfg(windows)]
use icem_windows_native::{OwnedEvent, signal_existing_event};
#[test]
fn clients_never_create_events_and_owner_is_auto_reset() -> Result<(), Box<dyn std::error::Error>> {
    let name = format!(r"Local\iCemRustEventTest_{}", std::process::id());
    assert!(!signal_existing_event(&name)?);
    {
        let event = OwnedEvent::create_reset(&name)?;
        assert!(!event.poll()?);
        assert!(signal_existing_event(&name)?);
        assert!(event.poll()?);
        assert!(!event.poll()?);
    }
    assert!(!signal_existing_event(&name)?);
    Ok(())
}
