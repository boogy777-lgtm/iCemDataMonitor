//! Shell consumption policy for legacy NavigateUi(Unspecified), a read-and-clear.
//! An accepted attempt is never rearmed by a reply, error or status reconnect.
//! A new show event is a new demand; events coalesce just like the Host pending slot.
pub struct NavigationPoll {
    needed: bool,
    in_flight: bool,
}
impl Default for NavigationPoll {
    fn default() -> Self {
        Self {
            needed: true,
            in_flight: false,
        }
    }
}
impl NavigationPoll {
    pub fn show(&mut self) {
        self.needed = true;
    }
    /// Queue refusal is known not to have reached the backend and keeps the demand.
    /// Once accepted, even a lost RPC response must not trigger an automatic retry.
    pub fn submit(
        &mut self,
        connected: bool,
        enqueue: impl FnOnce() -> Result<(), String>,
    ) -> Result<(), String> {
        if connected && self.needed && !self.in_flight {
            enqueue()?;
            self.needed = false;
            self.in_flight = true;
        }
        Ok(())
    }
    pub fn complete(&mut self) {
        // Preserve a show event received while the previous attempt was in flight.
        self.in_flight = false;
    }
}
