//! One pending visibility request. OS acceptance is not an actual-state acknowledgement.
use std::time::Duration;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Observation {
    Waiting,
    Confirmed(bool),
    TimedOut,
}
pub struct Request {
    target: bool,
    deadline: Duration,
}
impl Request {
    pub fn new(target: bool, now: Duration) -> Self {
        Self {
            target,
            deadline: now.saturating_add(Duration::from_secs(2)),
        }
    }
    pub fn observe(&self, actual: bool, minimized: bool, now: Duration) -> Observation {
        if actual == self.target && (!self.target || !minimized) {
            Observation::Confirmed(actual)
        } else if now >= self.deadline {
            Observation::TimedOut
        } else {
            Observation::Waiting
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn accepted_is_pending_until_actual_state_matches_and_timeout_is_finite() {
        let showing = Request::new(true, Duration::ZERO);
        assert_eq!(
            showing.observe(true, true, Duration::ZERO),
            Observation::Waiting
        );
        for target in [false, true] {
            let request = Request::new(target, Duration::from_secs(7));
            assert_eq!(
                request.observe(!target, false, Duration::from_secs(7)),
                Observation::Waiting
            );
            assert_eq!(
                request.observe(!target, false, Duration::from_secs(9)),
                Observation::TimedOut
            );
            assert_eq!(
                request.observe(target, false, Duration::from_secs(9)),
                Observation::Confirmed(target)
            );
        }
    }
}
