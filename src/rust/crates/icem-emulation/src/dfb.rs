//! State-owning ports of the three original primitive DFB blocks.
//! Explicit dt; no wall clock, global mutable state, threads or I/O.
#[derive(Debug, Default, Clone)]
pub struct FirstOrderLag {
    value: f64,
}
impl FirstOrderLag {
    pub fn execute(&mut self, target: f64, dt: f64, tau: f64) -> f64 {
        // Math.Max propagates NaN and selects +0 over -0; f64::max differs for NaN.
        let divisor = dotnet_max(dt, tau);
        self.value += (target - self.value) * (dt / divisor);
        self.value
    }
}
#[derive(Debug, Default, Clone)]
pub struct KickoutLatch {
    tripped: bool,
    captured: f64,
}
impl KickoutLatch {
    pub fn execute(&mut self, pressure: f64, setting: f64, reset: bool) -> (bool, f64) {
        if reset {
            self.tripped = false;
            self.captured = 0.0;
        } else if !self.tripped && pressure >= setting {
            self.tripped = true;
            self.captured = pressure;
        }
        (self.tripped, self.captured)
    }
}
#[derive(Debug, Default, Clone)]
pub struct Totalizer {
    stage: f64,
    job: f64,
}
impl Totalizer {
    pub fn execute(&mut self, rate: f64, dt: f64, reset_stage: bool) -> (f64, f64) {
        self.stage += rate * dt;
        self.job += rate * dt;
        if reset_stage {
            self.stage = 0.0;
        }
        (self.stage, self.job)
    }
}

/// System.Math.Max: propagate NaN and prefer positive zero.
pub(crate) fn dotnet_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || a > b || (a == b && b.is_sign_negative()) {
        a
    } else {
        b
    }
}
