use icem_emulation::lan::{
    BaseProvider, FIRMWARE_SVS, LEGACY_SVS, Registry, SignalDef, VectorDef, encode_ascii,
    encode_signal, patch_value,
};
use std::{
    error::Error,
    path::{Path, PathBuf},
};
fn templates() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../iCemEmulator/templates")
}
fn sig(offset: i32, size: i32, kind: &str) -> SignalDef {
    SignalDef {
        byte_offset: offset,
        size,
        value_type: kind.into(),
        das_factor: 1.0,
        name: "test".into(),
        uom: String::new(),
    }
}
#[test]
fn catalog_original_inventory_and_superframe_order() -> Result<(), Box<dyn Error>> {
    let root = templates();
    let registry = Registry::load(
        &root.join("lantable_registry.json"),
        Some(&root.join("firmware_registry.json")),
    )?;
    assert_eq!(registry.all().len(), 208);
    assert_eq!(
        registry
            .all()
            .values()
            .map(|v| v.signals.len())
            .sum::<usize>(),
        4196
    );
    let mut provider = BaseProvider::load(registry, &root.join("variable_registry.json"), None)?;
    assert_eq!(provider.active_sv_numbers(), LEGACY_SVS);
    assert_eq!(provider.superframe(1, 3.25).len(), 6);
    provider.include_firmware_tables = true;
    let expected: Vec<_> = LEGACY_SVS.into_iter().chain(FIRMWARE_SVS).collect();
    assert_eq!(provider.active_sv_numbers(), expected);
    let frame = provider.superframe(1, 3.25);
    assert_eq!(
        frame.iter().map(|v| v.sv_number).collect::<Vec<_>>(),
        expected
    );
    assert_eq!(frame, provider.superframe(u32::MAX, 3.25));
    // Equipment belongs to the outer ACE frame, never to the 124-byte body.
    assert!(frame.iter().any(|v| v.payload.iter().any(|b| *b != 0)));
    Ok(())
}
#[test]
fn codecs_keep_four_byte_prefix_truncation_and_bounds() {
    assert_eq!(
        encode_signal(305419896.0, "UI", 1.0, 1),
        Some(vec![0x12, 0x34, 0x56, 0x78])
    );
    let mut payload = [0xaa; 12];
    assert!(patch_value(&mut payload, &sig(5, 2, "UI"), 305419896.0));
    assert_eq!(
        payload,
        [
            0xaa, 0x12, 0x34, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa
        ]
    );
    for invalid in [
        sig(3, 4, "FF"),
        sig(i32::MAX, 4, "FF"),
        sig(4, -1, "FF"),
        sig(4, 13, "FF"),
    ] {
        let before = payload;
        assert!(!patch_value(&mut payload, &invalid, 42.0));
        assert_eq!(payload, before);
    }
    assert_eq!(
        encode_signal(2.5, "si", 1.0, 4),
        Some(2_i32.to_be_bytes().to_vec())
    );
    assert_eq!(
        encode_signal(3.5, "UL", 1.0, 4),
        Some(4_u32.to_be_bytes().to_vec())
    );
    assert_eq!(
        encode_signal(3.5, "HX", 100.0, 4),
        Some(4_u32.to_be_bytes().to_vec())
    );
    assert_eq!(
        encode_signal(3.5, "FF", 100.0, 4),
        Some(3.5_f32.to_be_bytes().to_vec())
    );
    assert_eq!(
        encode_signal(-1.0, "UI", 1.0, 4),
        Some(0_u32.to_be_bytes().to_vec())
    );
    assert_eq!(
        encode_signal(f64::INFINITY, "SI", 1.0, 4),
        Some(i32::MAX.to_be_bytes().to_vec())
    );
    assert_eq!(encode_signal(42.0, "?", 1.0, 1), Some(vec![0; 4]));
    assert_eq!(encode_signal(42.0, "UC", 1.0, -1), None);
    assert_eq!(encode_ascii("é水", 4), Some(vec![b'?', b'?', 0, 0]));
    assert_eq!(encode_ascii("ABCDE", 3), Some(b"ABC".to_vec()));
    assert_eq!(
        encode_signal(f64::NEG_INFINITY, "UC", 1.0, 12),
        Some(b"-Infinity\0\0\0".to_vec())
    );
}
#[test]
fn first_vector_and_signal_overlap_order_are_preserved() -> Result<(), Box<dyn Error>> {
    let mut first = VectorDef {
        sv_number: LEGACY_SVS[0],
        name: "first".into(),
        equipment_type: "Cementer".into(),
        signals: vec![sig(4, 4, "FF"), sig(4, 1, "?")],
    };
    first.signals[0].name = "PumpRate".into();
    let mut second = first.clone();
    second.name = "second".into();
    let registry = Registry::from_vectors(vec![first, second]);
    assert_eq!(
        registry.get(LEGACY_SVS[0]).map(|v| v.name.as_str()),
        Some("first")
    );
    let provider = BaseProvider::load(registry, &templates().join("variable_registry.json"), None)?;
    assert_eq!(
        &provider.superframe(0, 0.0)[0].payload[..4],
        &[0, 0x86, 0x66, 0x66]
    );
    Ok(())
}
#[test]
fn override_lookup_priority_and_firmware_alias_order() -> Result<(), Box<dyn Error>> {
    let root = std::env::temp_dir().join(format!("icem-lan-base-{}", std::process::id()));
    std::fs::create_dir_all(&root)?;
    let body = r#"{"variables":[{"name":"generic","display":"wire","simulation":{"base":11},"families":{"lan":{"wireName":"WIRE"}}},{"name":"wire","display":"later","simulation":{"base":22}},{"name":"elapsed","display":"clock","simulation":{"base":8,"format":"elapsed","togglePeriod":1}}]}"#;
    std::fs::write(root.join("variable_registry.json"), "{\"variables\":[]}")?;
    std::fs::write(root.join("variable_registry.emulator.json"), body)?;
    std::fs::write(
        root.join("firmware_variables.json"),
        r#"{"variables":[{"name":"wire","display":"firmware","simulation":{"base":33}},{"name":"f","display":"f","simulation":{"base":44},"families":{"lan":{"alias":"wire"}}}]}"#,
    )?;
    let mut signals = vec![sig(4, 4, "FF"), sig(8, 4, "FF")];
    signals[0].name = "wIrE".into();
    signals[1].name = "clock".into();
    let registry = Registry::from_vectors(vec![VectorDef {
        sv_number: LEGACY_SVS[0],
        name: "custom".into(),
        equipment_type: String::new(),
        signals,
    }]);
    let legacy = r#"{"serviceVectors":[{"svNumber":7,"name":"first","signals":[]},{"svNumber":7,"name":"duplicate","signals":[]}]}"#;
    let firmware = r#"{"serviceVectors":[{"svNumber":7,"name":"collision","signals":[]},{"svNumber":8,"name":"firmware","signals":[]}]}"#;
    std::fs::write(root.join("legacy.json"), legacy)?;
    std::fs::write(root.join("firmware.json"), firmware)?;
    let merged = Registry::load(&root.join("legacy.json"), Some(&root.join("firmware.json")))?;
    assert_eq!(merged.all().len(), 2);
    assert_eq!(merged.get(7).map(|v| v.name.as_str()), Some("first"));
    assert_eq!(merged.get(8).map(|v| v.name.as_str()), Some("firmware"));
    let provider = BaseProvider::load(registry, &root.join("variable_registry.json"), None)?;
    let payload = provider.superframe(0, 3.25)[0].payload;
    assert_eq!(&payload[..4], &44_f32.to_be_bytes());
    assert_eq!(&payload[4..8], &3.25_f32.to_be_bytes());
    std::fs::remove_dir_all(root)?;
    Ok(())
}

#[test]
fn decimal_catalog_factors_preserve_ieee_roundtrip() -> Result<(), Box<dyn Error>> {
    for decimal in [
        "6.36962437673933e-7",
        "0.000214019379058442",
        "0.0584415584415584",
        "7.27595761418e-12",
    ] {
        let json = format!(
            r#"{{"serviceVectors":[{{"svNumber":1,"name":"test","signals":[{{"name":"factor","byteOffset":4,"size":4,"valueType":"FF","dasFactor":{decimal}}}]}}]}}"#
        );
        let registry = Registry::from_json(&json)?;
        let actual = registry.get(1).ok_or("vector missing")?.signals[0].das_factor;
        assert_eq!(
            actual.to_bits(),
            decimal.parse::<f64>()?.to_bits(),
            "{decimal}"
        );
    }
    Ok(())
}
