//! Host-owned pending section. Shell is a view; querying never signals show.
use crate::proto::{NavigateUiResponse, UiSection};
use std::sync::{Arc, Mutex};
pub trait ShellSignals: Send + Sync + 'static {
    fn alive(&self) -> Result<bool, String>;
    fn show(&self) -> Result<bool, String>;
}
struct Absent;
impl ShellSignals for Absent {
    fn alive(&self) -> Result<bool, String> {
        Ok(false)
    }
    fn show(&self) -> Result<bool, String> {
        Ok(false)
    }
}
#[derive(Clone)]
pub struct Navigation {
    pending: Arc<Mutex<UiSection>>,
    signals: Arc<dyn ShellSignals>,
}
impl Default for Navigation {
    fn default() -> Self {
        Self::new(Arc::new(Absent))
    }
}
impl Navigation {
    pub fn new(signals: Arc<dyn ShellSignals>) -> Self {
        Self {
            pending: Arc::new(Mutex::new(UiSection::Unspecified)),
            signals,
        }
    }
    pub fn navigate(&self, section: UiSection) -> Result<NavigateUiResponse, String> {
        if section == UiSection::Unspecified {
            let pending = {
                let mut p = self
                    .pending
                    .lock()
                    .map_err(|_| "navigation lock poisoned")?;
                std::mem::replace(&mut *p, UiSection::Unspecified)
            };
            return Ok(NavigateUiResponse {
                ui_alive: self.signals.alive().unwrap_or(false),
                pending: pending as i32,
            });
        }
        *self
            .pending
            .lock()
            .map_err(|_| "navigation lock poisoned")? = section;
        let alive = match self.signals.alive().and_then(|alive| {
            if alive {
                self.signals.show()
            } else {
                Ok(false)
            }
        }) {
            Ok(alive) => alive,
            Err(error) => {
                eprintln!("Shell navigation signal failed; pending retained: {error}");
                false
            }
        };
        Ok(NavigateUiResponse {
            ui_alive: alive,
            pending: section as i32,
        })
    }
}
