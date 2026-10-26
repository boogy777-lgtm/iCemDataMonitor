use icem_emulation::{
    cement::Context,
    lan::{BaseProvider, FIRMWARE_SVS, LEGACY_SVS, Registry, SignalDef, VectorDef},
    lan_pipeline::{LanPipeline, convert_payload},
    units::{Group, Preset, Settings},
};
use std::path::Path;

type Result<T = ()> = std::result::Result<T, Box<dyn std::error::Error>>;
fn signal(name: &str, kind: &str, size: i32, uom: &str, factor: f64) -> SignalDef {
    SignalDef {
        byte_offset: 4,
        size,
        value_type: kind.into(),
        das_factor: factor,
        name: name.into(),
        uom: uom.into(),
    }
}
fn vector(sv: u32, signals: Vec<SignalDef>) -> VectorDef {
    VectorDef {
        sv_number: sv,
        name: "test vector".into(),
        equipment_type: "test".into(),
        signals,
    }
}
fn pipeline(signals: Vec<SignalDef>) -> Result<LanPipeline> {
    Ok(LanPipeline::new(BaseProvider::load(
        Registry::from_vectors(vec![vector(LEGACY_SVS[0], signals)]),
        Path::new("/__icem_missing_test_registry__/variable_registry.json"),
        None,
    )?))
}
fn first_float(pipeline: &mut LanPipeline) -> Result<f32> {
    let frames = pipeline.superframe(123, 4.0);
    Ok(f32::from_be_bytes(frames[0].payload[..4].try_into()?))
}
#[test]
fn frozen_cache_precedes_live_overrides_and_units_without_aliasing() -> Result {
    let mut p = pipeline(vec![signal("Pump A Rate", "FF", 4, "bbl/min", 1.0)])?;
    p.frozen = true;
    assert_eq!(first_float(&mut p)?, 3.0, "first frozen call generates");
    p.network
        .as_mut()
        .ok_or("missing network")?
        .pump_a
        .inputs
        .rate = 9.0;
    p.set_override(LEGACY_SVS[0], "Pump A Rate", 2.0);
    p.settings.overrides.insert(Group::Flow, "gal/min".into());
    assert_eq!(first_float(&mut p)?, 84.0);
    p.clear_override(LEGACY_SVS[0], "Pump A Rate");
    assert_eq!(first_float(&mut p)?, 126.0);
    let mut returned = p.native_superframe(999, 999.0);
    returned[0].payload[..4].copy_from_slice(&999f32.to_be_bytes());
    assert_eq!(
        first_float(&mut p)?,
        126.0,
        "returned bytes do not alias cache"
    );
    p.frozen = false;
    assert_eq!(first_float(&mut p)?, 378.0);
    p.frozen = true;
    p.settings.overrides.clear();
    assert_eq!(
        first_float(&mut p)?,
        9.0,
        "unfrozen call refreshes native cache"
    );
    Ok(())
}
#[test]
fn frozen_payload_list_and_current_active_sv_list_can_differ() -> Result {
    let mut p = pipeline(vec![])?;
    p.frozen = true;
    assert_eq!(p.superframe(1, 0.0).len(), 6);
    p.base.include_firmware_tables = true;
    assert_eq!(p.active_sv_numbers().len(), 20);
    assert_eq!(p.active_sv_numbers()[6..], FIRMWARE_SVS);
    assert_eq!(p.superframe(2, 60.0).len(), 6);
    p.frozen = false;
    assert_eq!(p.superframe(2, 60.0).len(), 20);
    p.frozen = true;
    p.base.include_firmware_tables = false;
    assert_eq!(p.active_sv_numbers(), LEGACY_SVS);
    assert_eq!(p.native_superframe(3, 0.0).len(), 20);
    Ok(())
}
#[test]
fn can_transmit_is_case_insensitive_but_override_dictionary_is_not() -> Result {
    let mut text = signal("Track", "UC", 120, "", 1.0);
    text.byte_offset = 8;
    let mut overflow = signal("Overflow", "FF", 4, "", 1.0);
    overflow.byte_offset = i32::MAX;
    let mut p = pipeline(vec![
        signal("Pump A Rate", "FF", 4, "bbl/min", 1.0),
        text,
        overflow,
    ])?;
    assert!(p.can_transmit(LEGACY_SVS[0], "pump a rate").is_ok());
    assert!(p.can_transmit(LEGACY_SVS[0], "Track").is_ok());
    assert!(p.can_transmit(LEGACY_SVS[0], "Overflow").is_err());
    assert!(p.can_transmit(0x1234, "Track").is_err());
    assert!(p.can_transmit(LEGACY_SVS[0], "Unknown").is_err());
    p.set_override(LEGACY_SVS[0], "pump a rate", 20.0);
    assert_eq!(first_float(&mut p)?, 3.0);
    p.set_override(LEGACY_SVS[0], "Pump A Rate", 20.0);
    assert_eq!(first_float(&mut p)?, 20.0);
    let mut snapshot = p.snapshot_overrides();
    snapshot.clear();
    assert_eq!(p.snapshot_overrides().len(), 2);
    p.clear_override(LEGACY_SVS[0], "pump a rate");
    assert_eq!(first_float(&mut p)?, 20.0);
    p.clear_override(LEGACY_SVS[0], "Pump A Rate");
    assert_eq!(first_float(&mut p)?, 3.0);
    Ok(())
}
#[test]
fn requesting_frames_never_executes_the_network_and_native_projection_refreshes_cache() -> Result {
    let mut p = pipeline(vec![signal("Mix Water Rate", "FF", 4, "bbl/min", 1.0)])?;
    let before = p.network.as_ref().ok_or("missing network")?.signals();
    assert_eq!(first_float(&mut p)?, 0.0);
    p.native_superframe(1, 30.0);
    assert_eq!(
        p.network.as_ref().ok_or("missing network")?.signals(),
        before
    );
    p.network
        .as_mut()
        .ok_or("missing network")?
        .execute(&mut Context::new(1.0, 1.0, 1, 7));
    let native = p.native_superframe(1, 1.0);
    assert_eq!(f32::from_be_bytes(native[0].payload[..4].try_into()?), 1.26);
    p.frozen = true;
    p.network
        .as_mut()
        .ok_or("missing network")?
        .mixer
        .outputs
        .mix_water_rate = 9.0;
    assert_eq!(first_float(&mut p)?, 1.26);
    Ok(())
}
#[test]
fn unit_integer_codec_uses_declared_width_and_inverse_factor_for_all_aliases() {
    let settings = Settings {
        preset: Preset::Imperial,
        ..Settings::default()
    };
    // Imperial flow remains bbl/min, so request gal/min explicitly.
    let mut settings = settings;
    settings.overrides.insert(Group::Flow, "gal/min".into());
    for kind in ["UL", "UI", "HX", "SI", "SL"] {
        for size in [1, 2, 4, 8] {
            let signed = matches!(kind, "SI" | "SL");
            let mut payload = [0xa5; 124];
            let raw = if signed {
                (-2i64).to_be_bytes()
            } else {
                2u64.to_be_bytes()
            };
            let width = size as usize;
            payload[..width].copy_from_slice(&raw[8 - width..]);
            let definition = vector(
                LEGACY_SVS[0],
                vec![signal("Synthetic", kind, size, "bbl/min", 0.5)],
            );
            convert_payload(&mut payload, &definition, &settings);
            let expected = if signed {
                (-84i64).to_be_bytes()
            } else {
                84u64.to_be_bytes()
            };
            assert_eq!(payload[..width], expected[8 - width..], "{kind}/{size}");
            assert!(payload[width..].iter().all(|&byte| byte == 0xa5));
        }
    }
}
#[test]
fn unit_converter_clamps_retains_unknowns_and_preserves_firmware_native_density() {
    let mut settings = Settings::default();
    settings.overrides.insert(Group::Flow, "gal/min".into());
    for (kind, input, expected) in [("UL", 255u8, 255u8), ("SI", 127, 127), ("SI", 128, 128)] {
        let definition = vector(
            LEGACY_SVS[0],
            vec![signal("Synthetic", kind, 1, "bbl/min", 1.0)],
        );
        let mut payload = [0xa5; 124];
        payload[0] = input;
        convert_payload(&mut payload, &definition, &settings);
        assert_eq!(payload[0], expected);
    }
    for (kind, size) in [
        ("UC", 120),
        ("FF", 3),
        ("UL", 3),
        ("Unknown", 4),
        ("UL", -1),
    ] {
        let definition = vector(
            LEGACY_SVS[0],
            vec![signal("Synthetic", kind, size, "bbl/min", 1.0)],
        );
        let mut payload = [0xa5; 124];
        convert_payload(&mut payload, &definition, &settings);
        assert_eq!(payload, [0xa5; 124], "{kind}/{size}");
    }
    let definition = vector(LEGACY_SVS[0], vec![signal("Density", "FF", 4, "g/cc", 1.0)]);
    let mut payload = [0; 124];
    payload[..4].copy_from_slice(&1.89f32.to_be_bytes());
    let before = payload;
    settings.preset = Preset::FirmwareDefault;
    convert_payload(&mut payload, &definition, &settings);
    assert_eq!(payload, before);
}
#[test]
fn manual_small_integer_prefix_is_not_replaced_by_the_units_width_law() -> Result {
    let mut p = pipeline(vec![signal("Synthetic", "UL", 1, "bbl/min", 2.0)])?;
    p.network = None;
    p.set_override(LEGACY_SVS[0], "Synthetic", 2.0);
    assert_eq!(
        p.native_superframe(1, 0.0)[0].payload[0],
        0,
        "BE u32 prefix of 4 is 0"
    );
    p.settings.overrides.insert(Group::Flow, "gal/min".into());
    assert_eq!(p.superframe(1, 0.0)[0].payload[0], 0);
    Ok(())
}
