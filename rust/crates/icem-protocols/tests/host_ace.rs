use icem_protocols::ace::{
    build,
    host::{DecodeError, Decoded, Kind, decode, header},
};
#[test]
fn host_witness_is_weaker_than_emitter_and_requires_exact_length() -> Result<(), &'static str> {
    let mut frame = build([1, 2, 3, 4, 5, 6], 0xfedcba98, 17, &[]);
    frame[0] = 5;
    frame[18] = 0;
    frame[16] = 9;
    let h = header(&frame).ok_or("test header")?;
    assert_eq!(h.equipment, 0xfedcba98);
    assert_eq!(h.vector, 17);
    assert_eq!(h.source, [1, 2, 3, 4, 5, 6]);
    for n in 0..152 {
        assert!(header(&frame[..n]).is_none());
    }
    assert!(header(&[frame.as_slice(), &[0]].concat()).is_none());
    Ok(())
}
#[test]
fn host_fixed_width_factor_text_sentinel_and_bounds() -> Result<(), DecodeError> {
    let mut f = build([0; 6], 1, 2, &[]);
    f[28..32].copy_from_slice(&20f32.to_bits().to_be_bytes());
    // Host FF applies factor; emulator decode_native intentionally does not.
    assert_eq!(decode(&f, 4, 1, Kind::Float32, 0.5)?.value, Some(10.));
    f[28..32].copy_from_slice(&(-9999f32).to_bits().to_be_bytes());
    assert!(decode(&f, 4, 4, Kind::Float32, 1.)?.sentinel);
    f[28..32].copy_from_slice(&0xffff_fffeu32.to_be_bytes());
    assert_eq!(decode(&f, 4, 4, Kind::Signed32, 0.5)?.value, Some(-1.));
    assert_eq!(
        decode(&f, 4, 4, Kind::Hex, 10.)?.text.as_deref(),
        Some("0xFFFFFFFE")
    );
    f[28..32].copy_from_slice(&[65, 255, 0, 66]);
    assert_eq!(
        decode(&f, 4, 4, Kind::Ascii, 1.)?.text.as_deref(),
        Some("A?")
    );
    assert_eq!(decode(&f, 200, 4, Kind::Float64, 1.)?, Decoded::default());
    assert_eq!(
        decode(&f, 127, 1, Kind::Float32, 1.),
        Err(DecodeError::TruncatedScalar)
    );
    assert_eq!(
        decode(&f, 4, -1, Kind::Ascii, 1.),
        Err(DecodeError::InvalidSpan)
    );
    assert_eq!(
        decode(&f, i32::MAX, 4, Kind::Float32, 1.),
        Err(DecodeError::InvalidSpan)
    );
    assert_eq!(Kind::parse("ff"), Kind::Unknown);
    Ok(())
}
