use icem_protocols::ace::*;
#[test]
fn exact_wire_bytes_and_padding_trim() {
    let frame = build([0, 1, 2, 3, 4, 5], 0x12345678, 0x13120200, &[0xaa, 0xbb]);
    assert_eq!(
        &frame[..28],
        &[
            3, 0, 1, 0, 0, 32, 0, 1, 2, 3, 4, 5, 0, 138, 3, 3, 8, 0, 208, 13, 18, 52, 86, 120, 19,
            18, 2, 0
        ]
    );
    assert_eq!(&frame[28..31], &[0xaa, 0xbb, 0]);
    assert_eq!(
        parse(&frame).map(|f| (f.equipment, f.sv)),
        Some((0x12345678, 0x13120200))
    );
    assert!(
        build([0; 6], 0, 0, &[0xff; 200])[28..]
            .iter()
            .all(|x| *x == 255)
    );
}
#[test]
fn parser_matches_actual_legacy_predicate_and_all_short_prefixes() {
    let original = build([0; 6], 0, 0, &[]);
    for size in 0..152 {
        assert!(parse(&original[..size]).is_none());
    }
    let mut frame = original;
    frame[16] = 9;
    frame[12] = 255;
    assert!(parse(&frame).is_some());
    let mut tail = frame.to_vec();
    tail.push(42);
    assert!(parse(&tail).is_some());
    for pos in [0, 1, 2, 3, 4, 5, 14, 15, 16, 17, 18, 19] {
        let mut bad = original;
        bad[pos] ^= 0xff;
        assert!(parse(&bad).is_none(), "{pos}");
    }
}
#[test]
fn signal_decode_widths_signed_factor_and_invalid_spans() {
    assert_eq!(decode_native(&[0xff, 0xfe], 4, 2, "SI", 0.5), Some(-1.0));
    assert_eq!(
        decode_native(&[0x3f, 0x80, 0, 0], 4, 4, "FF", 10.0),
        Some(1.0)
    );
    for (offset, size, kind) in [
        (3, 1, "UI"),
        (i32::MAX, 8, "SL"),
        (4, -1, "UL"),
        (4, 3, "UI"),
        (4, 2, "UC"),
    ] {
        assert!(decode_native(&[0; 124], offset, size, kind, 1.0).is_none());
    }
}
