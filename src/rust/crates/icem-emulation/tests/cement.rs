use icem_emulation::cement::*;
#[test]
fn shared_wiring_latch_totals_and_sequencer_are_independent() {
    let mut n = CementNetwork::default();
    n.pump_a.inputs.rate = 12.0;
    n.pump_a.inputs.opko_setting = 1000.0;
    n.execute(&mut Context::new(1.0, 1.0, 1, 7));
    assert!(n.pump_a.outputs.kicked_out);
    assert_eq!(n.pump_a.inputs.rate, 12.0);
    assert_eq!(n.pump_a.outputs.stage_total, 12.0);
    assert_eq!(n.sequencer.outputs.event_number, 1);
    n.reset = true;
    n.pump_a.inputs.reset = false;
    n.execute(&mut Context::new(2.0, 1.0, 2, 7));
    assert!(!n.pump_a.outputs.kicked_out);
    assert_eq!(n.pump_a.outputs.stage_total, 0.0);
    assert_eq!(n.pump_a.outputs.job_total, 24.0);
    assert_eq!(n.sequencer.outputs.event_number, 0);
    assert_eq!(n.mixer.outputs.mix_water_stage_total, 0.0);
    assert_eq!(n.mixer.outputs.mix_water_job_total, 2.52);
    n.reset = false;
    n.execute(&mut Context::new(3.0, 1.0, 3, 7));
    assert_eq!(
        n.sequencer.outputs.event_number, 0,
        "reset with active=true preserves prevActive"
    );
    n.job_active = false;
    n.execute(&mut Context::new(4.0, 1.0, 4, 7));
    assert_eq!(n.mixer.outputs.mix_water_rate, 0.0);
    assert_eq!(n.mixer.outputs.recirc_flow_rate, 0.05);
    assert_eq!(n.pump_a.outputs.job_total, 48.0);
    assert_eq!(n.sequencer.outputs.control_mode, 1);
    assert_eq!(n.sequencer.outputs.mix_mode, 2);
    n.job_active = true;
    n.execute(&mut Context::new(5.0, 1.0, 5, 7));
    assert_eq!(n.sequencer.outputs.event_number, 1);
}
#[test]
fn inventory_preserves_local_storage_and_effective_reads() -> Result<(), Box<dyn std::error::Error>>
{
    let mut n = CementNetwork::default();
    n.f300_hpvt.inputs.flow_ref = 999.0;
    n.pump_a.inputs.reset = true;
    n.mixer.inputs.job_active = false;
    n.raw_io.outputs.spare_analog_in[0] = 42.5;
    n.execute(&mut Context::new(1.0, 1.0, 1, 7));
    let signals = n.signals();
    assert!(!signals.iter().any(|s| s.path.starts_with("Network.")));
    let flow = signals
        .iter()
        .find(|s| s.path == "F300Hpvt.FlowRef")
        .ok_or("missing flow port")?;
    assert_eq!(flow.stored, SignalValue::Float(999.0));
    assert_eq!(flow.value, SignalValue::Float(1.26));
    assert_eq!(flow.source, Some("Mixer.MixWaterRate"));
    let reset = signals
        .iter()
        .find(|s| s.path == "PumpA.Reset")
        .ok_or("missing reset port")?;
    assert_eq!(reset.stored, SignalValue::Bool(true));
    assert_eq!(reset.value, SignalValue::Bool(false));
    assert_eq!(n.wire_value("start job"), 1.0);
    assert_eq!(n.wire_value("AI Spare 1 Scaled"), 42.5);
    assert_eq!(n.wire_value("AI Spare 5 Scaled"), 25.0);
    assert_eq!(n.wire_value("Mix Water Rate gpm"), 1.26 * 42.0);
    let unique: std::collections::BTreeSet<_> = WIRE_SIGNALS
        .iter()
        .map(|s| s.name.to_ascii_lowercase())
        .collect();
    assert_eq!(unique.len(), WIRE_SIGNALS.len());
    Ok(())
}
#[test]
fn seeded_random_replays_across_tick_wrap_and_network_clones() {
    let mut a = CementNetwork::default();
    let mut b = a.clone();
    for tick in [0, 1, 2147483647, 2147483648, i64::MAX, i64::MIN] {
        a.execute(&mut Context::new(1.0, 0.25, tick, i32::MIN));
        b.execute(&mut Context::new(1.0, 0.25, tick, i32::MIN));
        assert_eq!(a.signals(), b.signals());
        let mut first = Context::new(0.0, 1.0, tick, 7);
        let mut wrapped = Context::new(0.0, 1.0, tick.wrapping_add(2147483648), 7);
        for _ in 0..60 {
            assert_eq!(
                first.random.next_double().to_bits(),
                wrapped.random.next_double().to_bits()
            );
        }
    }
}
