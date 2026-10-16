use super::*;
#[derive(Debug, Clone)]
pub struct PumpSide {
    pub inputs: PumpSideInputs,
    pub outputs: PumpSideOutputs,
    pub parameters: PumpSideParameters,
    lag: FirstOrderLag,
    latch: KickoutLatch,
    totals: Totalizer,
}
impl Default for PumpSide {
    fn default() -> Self {
        Self {
            inputs: PumpSideInputs::default(),
            outputs: PumpSideOutputs::default(),
            parameters: PumpSideParameters::original_defaults(),
            lag: FirstOrderLag::default(),
            latch: KickoutLatch::default(),
            totals: Totalizer::default(),
        }
    }
}
impl PumpSide {
    pub(crate) fn execute(&mut self, reset: bool, ctx: &mut Context) {
        let rate = self.inputs.rate;
        let p = &self.parameters;
        let o = &mut self.outputs;
        let target = p.pressure_base + p.resistance * rate;
        o.filtered_pressure = self.lag.execute(target, ctx.dt, p.lag_tau_seconds);
        let noise = (ctx.random.next_double() * 2.0 - 1.0)
            * p.noise_sigma
            * dotnet_max(100.0, o.filtered_pressure.abs());
        o.raw_pressure = dotnet_max(0.0, o.filtered_pressure + noise);
        (o.kicked_out, o.pressure_at_ko) =
            self.latch
                .execute(o.raw_pressure, self.inputs.opko_setting, reset);
        (o.stage_total, o.job_total) = self.totals.execute(rate, ctx.dt, reset);
    }
}
#[derive(Debug, Clone)]
pub struct Mixer {
    pub inputs: MixerInputs,
    pub outputs: MixerOutputs,
    pub parameters: MixerParameters,
    density: f64,
    water_pot: f64,
    cement_pot: f64,
    totals: Totalizer,
}
impl Default for Mixer {
    fn default() -> Self {
        Self {
            inputs: MixerInputs::default(),
            outputs: MixerOutputs::default(),
            parameters: MixerParameters::original_defaults(),
            density: 0.0,
            water_pot: 0.0,
            cement_pot: 0.0,
            totals: Totalizer::default(),
        }
    }
}
impl Mixer {
    pub(crate) fn execute(&mut self, job_active: bool, reset: bool, ctx: &mut Context) {
        let active = if job_active { 1.0 } else { 0.0 };
        let p = &self.parameters;
        let i = &self.inputs;
        let o = &mut self.outputs;
        self.density +=
            (i.blend_target - self.density) * (ctx.dt / dotnet_max(ctx.dt, p.density_tau_seconds));
        let n1 = (ctx.random.next_double() * 2.0 - 1.0) * p.density_noise_sigma;
        let n2 = (ctx.random.next_double() * 2.0 - 1.0) * p.density_noise_sigma;
        o.recirc_density = dotnet_max(0.0, self.density + n1);
        o.downhole_density = dotnet_max(0.0, self.density - 0.1 + n2);
        o.mud_cup_density = dotnet_max(0.0, self.density + 0.2);
        o.blend_density = self.density;
        o.rho12_hat = self.density + 0.1;
        o.vhp_density_hp = self.density + 0.3;
        let wv = i.water_valve_cmd;
        let cv = i.cement_valve_cmd;
        o.water_vlv_cmd = wv;
        o.cement_vlv_cmd = cv;
        o.recirc_flow_vlv_cmd = wv;
        o.vol_water_percent = wv;
        let pt = dotnet_max(ctx.dt, p.pot_tau_seconds);
        self.water_pot += ((wv / 100.0).clamp(0.0, 1.0) * 100.0 - self.water_pot) * (ctx.dt / pt);
        self.cement_pot += ((cv / 100.0).clamp(0.0, 1.0) * 100.0 - self.cement_pot) * (ctx.dt / pt);
        o.water_pot = self.water_pot;
        o.cement_pot = self.cement_pot;
        let rate = active * p.water_rate_gain * (wv / 100.0);
        o.mix_water_rate = rate;
        o.vol_meter_rate = rate;
        o.recirc_flow_rate = rate + 0.05;
        o.mix_pump_press = 120.0 + 60.0 * (wv / 100.0) + (ctx.random.next_double() - 0.5) * 4.0;
        o.mix_water_mnfld_press =
            90.0 + 40.0 * (wv / 100.0) + (ctx.random.next_double() - 0.5) * 3.0;
        o.downhole_tub_level = 6.5 + (ctx.random.next_double() - 0.5) * 0.4;
        o.h2_hat = 5000.0 + (ctx.random.next_double() - 0.5) * 20.0;
        o.auto_or_manual_mix = if active > 0.0 { 1 } else { 0 };
        o.blend_mix_rate = rate;
        o.blend_yield = p.blend_yield_value;
        o.blend_wtr_req = p.blend_wtr_req_value;
        let (stage, job) = self.totals.execute(rate, ctx.dt, reset);
        o.mix_water_stage_total = stage;
        o.mix_water_job_total = job;
        o.vol_meter_stage_total = stage;
        o.vol_meter_job_total = job;
        o.stage_sacks = stage * p.sacks_per_bbl;
        o.job_sacks = job * p.sacks_per_bbl;
    }
}
#[derive(Debug, Default, Clone)]
pub struct RawIo {
    pub inputs: RawIoInputs,
    pub outputs: RawIoOutputs,
}
impl RawIo {
    pub(crate) fn execute(&mut self, ctx: &mut Context) {
        let o = &mut self.outputs;
        o.relay_out = 0;
        o.digital_inputs = 0;
        o.digital_outputs = 0;
        for v in &mut o.analog_in {
            *v = 25.0 + (ctx.random.next_double() - 0.5) * 2.0;
        }
        for v in &mut o.analog_out {
            *v = 50.0 + (ctx.random.next_double() - 0.5) * 1.0;
        }
        for (i, v) in o.count_in.iter_mut().enumerate() {
            *v = ((ctx.elapsed_seconds * 10.0 + (i * 137) as f64) as i64 % 10000) as f64;
        }
        // Spare slots retain their storage, including values assigned by a caller.
    }
}
#[derive(Debug, Clone)]
pub struct Temperature {
    pub inputs: TemperatureInputs,
    pub outputs: TemperatureOutputs,
    pub parameters: TemperatureParameters,
    display: f64,
    controller: f64,
}
impl Default for Temperature {
    fn default() -> Self {
        Self {
            inputs: TemperatureInputs::default(),
            outputs: TemperatureOutputs::default(),
            parameters: TemperatureParameters::original_defaults(),
            display: 0.0,
            controller: 0.0,
        }
    }
}
impl Temperature {
    pub(crate) fn execute(&mut self, ctx: &mut Context) {
        let tau = dotnet_max(ctx.dt, self.parameters.tau_seconds);
        let target = self.parameters.target + (ctx.random.next_double() - 0.5) * 0.2;
        self.display += (target - self.display) * (ctx.dt / tau);
        self.controller += (target - self.controller) * (ctx.dt / (tau * 0.8));
        self.outputs.display_temperature = self.display;
        self.outputs.controller_temperature = self.controller;
    }
}
#[derive(Debug, Clone)]
pub struct JobSequencer {
    pub inputs: JobSequencerInputs,
    pub outputs: JobSequencerOutputs,
    pub parameters: JobSequencerParameters,
    previous_active: bool,
    event: u32,
}
impl Default for JobSequencer {
    fn default() -> Self {
        Self {
            inputs: JobSequencerInputs::default(),
            outputs: JobSequencerOutputs::default(),
            parameters: JobSequencerParameters::original_defaults(),
            previous_active: false,
            event: 0,
        }
    }
}
impl JobSequencer {
    pub(crate) fn execute(&mut self, active: bool, reset: bool) {
        if reset {
            self.previous_active = false;
            self.event = 0;
            self.outputs.stage_index = 0;
        } else if active && !self.previous_active {
            self.outputs.stage_index = 1;
            self.event = self.event.wrapping_add(1);
        }
        self.previous_active = active;
        let o = &mut self.outputs;
        o.event_number = self.event;
        o.unit_number = self.parameters.unit_number_value;
        o.control_mode = 1;
        o.mix_mode = 2;
        o.track_id = 38534;
        o.track_descript = Some(format!("Unit {}", self.parameters.unit_number_value));
    }
}
