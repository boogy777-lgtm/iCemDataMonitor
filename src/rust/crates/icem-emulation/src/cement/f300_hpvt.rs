use super::*;
#[derive(Debug, Default, Clone)]
pub struct F300Hpvt {
    pub inputs: F300HpvtInputs,
    pub outputs: F300HpvtOutputs,
}
impl F300Hpvt {
    pub(crate) fn execute(&mut self, flow_ref: f64, ctx: &mut Context) {
        let flow = flow_ref;

        self.outputs.f300_density_hz = 500.0 + (ctx.random.next_double() - 0.5) * 4.0;
        self.outputs.f300_mb_density = 8.7;
        self.outputs.f300_mb_mass_flow = 1200.0;
        self.outputs.f300_mb_temp = 40.0;
        self.outputs.f300_recirc_density_ppg = 15.8 + 0.1 * f64::sin(ctx.elapsed_seconds * 0.1);
        self.outputs.mb_f300_volume_flow = 6.0;
        self.outputs.mb_dens_hpvt = 1.19;
        self.outputs.mb_den_c_hpvt = 9.9;
        self.outputs.mb_freq_hpvt = 300.0;
        self.outputs.mb_tflow_hpvt = 25.0;
        self.outputs.mb_tcase_hpvt = 30.0;
        self.outputs.mb_cpc_hpvtt = 0.00002;
        self.outputs.mb_vlpo_hpvt = 0.35;

        for i in 0..self.outputs.wtr_test_rate_actual.len() {
            self.outputs.wtr_test_rate_actual[i] = 3.0 + (ctx.random.next_double() - 0.5) * 0.05;
        }
        self.outputs.water_test_setpoint = 3.0;
        self.outputs.start_water_highest_value = 50.0;
        self.outputs.full_open_highest_value = 220.0;
        self.outputs.avg_stage_rate_water_test = 100.0;
        self.outputs.auto_test_stage_number = 0;
        self.outputs.actual_cem_rate = 3.2;
        self.outputs.hfiltered = 6.5;
        self.outputs.hard_drive_space_used = 45.0;
        self.outputs.cpu_load = 35.0;
        self.outputs.hmi_temp_c = 25.0;
        self.outputs.chassis_temp_c = 30.0;
        self.outputs.cement_fso_setpoint = 25.0;
        self.outputs.water_fso_setpoint = 25.0;
        self.outputs.tub_level_setpoint_ft = 6.5;
        self.outputs.mix_water_rate_percent = 60.0;
        self.outputs.mag_meter_flow_hz = 100.0;
        self.outputs.extra_rate1_flow_hz = 60.0;
        self.outputs.extra_rate2_flow_hz = 60.0;

        let mix_gpm = flow * 42.0;
        self.outputs.mix_water_rate_gpm = mix_gpm;
        self.outputs.recirc_flow_rate_gpm = (flow + 0.05) * 42.0;
        self.outputs.vwdot_star = mix_gpm * 0.7;
        self.outputs.vcdot_star = mix_gpm * 0.3;
        self.outputs.v12dot_star = mix_gpm;
        self.outputs.md1dot_hat = 950.0 + (ctx.random.next_double() - 0.5) * 10.0;

        self.outputs.tub_level_ma = 12.0;
        self.outputs.ps_pressure_ma = 12.0;
        self.outputs.ds_pressure_ma = 12.0;

        self.outputs.rho12_hat_cem2 = 15.9;
        self.outputs.h2_hat2 = 6.4;
        self.outputs.h2_star_new = 6.5;
        self.outputs.h2_d_hat = 6.5;
        self.outputs.ve_h2_last = 6.5;
        self.outputs.water_height = 6.5;
        self.outputs.mag_hat_slurry = 15.8;
        self.outputs.vcdot_star_ho = self.outputs.vcdot_star;
        self.outputs.vcdot_hat = self.outputs.vcdot_star;
        self.outputs.v12dot_hat = self.outputs.v12dot_star - 1.0;
        self.outputs.vindot_star = 2.0;
        self.outputs.vsdot_lp = 60.0;
        self.outputs.v12airdot_hat = 0.5;
        self.outputs.md1dot_hat_cvol = 940.0;
        self.outputs.md1dot_hat_p = 960.0;
        self.outputs.vd2dot_hat = 59.0;
        self.outputs.vd2dot_hat_p = 58.0;
        self.outputs.vhp_density_hp2 = 15.8;
        self.outputs.d_vcement_den2 = 0.02;
        self.outputs.d_time_constant = 7.0;
        self.outputs.d_range_max = 0.5;
        self.outputs.d_num_st_dev = 2.0;
        self.outputs.p_out = 55.0;
        self.outputs.i_out = 10.0;
        self.outputs.fault_number = 0.0;
        self.outputs.fault_percent = 0.0;
        self.outputs.fault_detect_slurry = 0.0;
        self.outputs.fault_detect_height = 0.0;
        self.outputs.flow_coeff_fault = 0.9;
        self.outputs.flow_coeff_fault_ave_top = 0.9;
        self.outputs.flow_coeff_fault_ave_bottom = 0.8;
        self.outputs.ddt_instant_valve_coeff_est = 0.01;
        self.outputs.instant_valve_coeff = 0.01;
        self.outputs.water_valve_cv_est = 42.0;
        self.outputs.cv_dead_zone = 5.0;
        self.outputs.wtr_dead_zone_ave = 5.0;
        self.outputs.coeff_c = 0.9;
        self.outputs.ps_pressure_offset = 0.0;
        self.outputs.ds_pressure_offset = 0.0;
        self.outputs.ps_pump_offset = 0.0;
        self.outputs.heating_cooling = 0.0;
        self.outputs.vhp_water_percent_premix = 65.0;
        self.outputs.vhp_water_percent_downh = 45.0;

        self.outputs.hmi_value_ht400_ds_life = 1000.0;
        self.outputs.hmi_value_ht400_ps_life = 1200.0;
        self.outputs.hmi_value_ht400_ds_last_repack = 300.0;
        self.outputs.hmi_value_ht400_ps_last_repack = 400.0;
        self.outputs.hmi_value4x4_life = 4000.0;
        self.outputs.hmi_value6x5_life = 6000.0;
        self.outputs.combined_pump_life_vol = 150.0;
        self.outputs.mix_water_pump_life = 80.0;
        self.outputs.recirc_pump_life = 70.0;
        self.outputs.hp_pump_life_ps = 100.0;
        self.outputs.hp_pump_life_ds = 100.0;
        self.outputs.hp_combined_pump_life = 200.0;
        self.outputs.cf4x4_pump_hrs = 500.0;
        self.outputs.cf6x5_pump_hrs = 800.0;

        self.outputs.view_blend_number = 1;
        self.outputs.current_blend_number = 1;
        self.outputs.target_blend_number = 1;
        for i in 0..self.outputs.target_blend.len() {
            self.outputs.target_blend[i] = 15.5 + (i + 1) as f64 * 0.2;
        }
        self.outputs.current_blend_density = 15.8;
        self.outputs.current_blend_yield = 1.0;
        self.outputs.current_blend_wtr_req = 5.0;

        self.outputs.xf300_ok = true;
        self.outputs.xf300_data_read = true;
        self.outputs.x_hpvt_data_read = true;
        self.outputs.x_sd_enabled = true;
        self.outputs.xf300_modbus_assigned = true;
        self.outputs.xf300_analog_assigned = false;
        self.outputs.xf300_frequency_assigned = false;
        self.outputs.xf300_flow1 = false;
        self.outputs.xf300_flow2 = false;
        self.outputs.x_ball_up_warning = false;
        self.outputs.x_yoke_on = false;
        self.outputs.x_star_sim = false;
        self.outputs.x_water_test_active = false;
        self.outputs.x_defragging = false;
        self.outputs.x_filter_dynamic = false;
        self.outputs.x_pump_with_maint = false;
        self.outputs.x_mud_cup_error = false;
        self.outputs.x_blink_recirc_rate = false;
        self.outputs.x_water_auto_man = true;
        self.outputs.x_master_water_auto_man = true;
        self.outputs.x_cement_auto_man = true;
    }
}
