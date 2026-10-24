use icem_bus::{DecodeError, Region, Snapshot, Value, Zone};

const FIXTURE: &[u8] = include_bytes!("../../../tests/fixtures/bus-v5-minimal.bin");

fn put_pair(bytes: &mut [u8], address: usize, bits: u32) {
    let offset = 64 + address * 2;
    bytes[offset..offset + 2].copy_from_slice(&(bits as u16).to_be_bytes());
    bytes[offset + 2..offset + 4].copy_from_slice(&((bits >> 16) as u16).to_be_bytes());
}

#[test]
fn typed_identity_is_address_not_variable_index() -> Result<(), DecodeError> {
    let snap = Snapshot::decode(FIXTURE)?;
    let values: Vec<_> = snap.variables().collect();
    assert_eq!(values.len(), 352);
    for (index, address) in [
        (0, 0),
        (3, 3),
        (4, 4),
        (5, 6),
        (101, 198),
        (102, 200),
        (202, 400),
        (301, 598),
        (302, 600),
        (351, 649),
    ] {
        assert_eq!(values[index].index, index);
        assert_eq!(values[index].id.register_address(), address);
        assert_eq!(values[index].id.to_string(), format!("var{address}"));
    }
    assert_eq!(values[0].value, Value::Word(7));
    assert_eq!(values[4].value, Value::ComUnixSeconds(1_700_000_000));
    assert_eq!(values[5].value, Value::Float32(1.0));
    assert!(matches!(values[302].value, Value::Word(_)));
    assert!(snap.variable(352).is_none());
    assert!(snap.variable(usize::MAX).is_none());
    Ok(())
}

#[test]
fn all_valid_shapes_have_complete_uncompressed_geometry() -> Result<(), DecodeError> {
    for count in [650_u16, 651, 10_001, u16::MAX] {
        let mut bytes = FIXTURE.to_vec();
        bytes[8..10].copy_from_slice(&count.to_le_bytes());
        bytes.resize(64 + usize::from(count) * 2, 0xa5);
        let snap = Snapshot::decode(&bytes)?;
        let mut cursor = 0;
        for region in Region::ALL {
            let view = snap.region(region);
            assert_eq!(usize::from(view.start_address), cursor);
            assert!(std::ptr::eq(
                view.words.as_ptr(),
                snap.words()[cursor..].as_ptr()
            ));
            cursor += view.words.len();
        }
        assert_eq!(cursor, usize::from(count));
        let mut cursor = 0;
        for variable in snap.variables() {
            assert_eq!(usize::from(variable.id.register_address()), cursor);
            cursor += match variable.value {
                Value::Word(_) => 1,
                Value::ComUnixSeconds(_) | Value::Float32(_) => 2,
            };
        }
        assert_eq!(cursor, usize::from(count));
        assert_eq!(snap.variables().count(), snap.variable_count());
    }
    assert_eq!(Zone::Com.range(), (4, 200));
    Ok(())
}

#[test]
fn float_bits_and_com_signed_time_keep_distinct_types() -> Result<(), DecodeError> {
    for bits in [
        0,
        0x8000_0000,
        1,
        0x007f_ffff,
        0x7f80_0000,
        0xff80_0000,
        0x7fc0_1234,
        0x7fa0_4321,
        0xffff_ffff,
    ] {
        let mut bytes = FIXTURE.to_vec();
        put_pair(&mut bytes, 4, bits);
        put_pair(&mut bytes, 6, bits);
        put_pair(&mut bytes, 200, bits);
        let snap = Snapshot::decode(&bytes)?;
        assert_eq!(snap.service().com_source_time(), bits.cast_signed());
        assert_eq!(snap.unix_seconds()?, bits.cast_signed());
        assert_eq!(snap.service().unit_number().to_bits(), bits);
        let values: Vec<_> = snap.variables().collect();
        assert!(matches!(values[4].value, Value::ComUnixSeconds(_)));
        for index in [4, 5, 102] {
            assert_eq!(values[index].value.raw_bits(), bits);
        }
        assert_eq!(snap.words()[4], bits as u16);
    }
    Ok(())
}

#[test]
fn independent_writer_events_do_not_borrow_com_time_or_invent_updates() -> Result<(), DecodeError> {
    let mut bytes = FIXTURE.to_vec();
    put_pair(&mut bytes, 4, 0); // Valid epoch, never an automatic missing marker.
    let before = Snapshot::decode(&bytes)?;
    let identical = Snapshot::decode(&bytes)?;
    assert_eq!(before.words(), identical.words());
    for zone in Zone::ALL {
        assert_eq!(before.zone(zone), identical.zone(zone));
    }
    put_pair(&mut bytes, 200, 0x8000_0000);
    bytes[19..27].copy_from_slice(&123_i64.to_le_bytes());
    bytes[27] = 1;
    bytes[41..45].copy_from_slice(&u32::MAX.to_le_bytes());
    // RAW metadata can change on an identical line, with unchanged values.
    bytes[28..36].copy_from_slice(&456_i64.to_le_bytes());
    bytes[45..49].copy_from_slice(&0_u32.to_le_bytes());
    let after = Snapshot::decode(&bytes)?;
    assert_eq!(after.service().com_source_time(), 0);
    assert_eq!(after.zone(Zone::Com), before.zone(Zone::Com));
    assert_eq!(after.zone(Zone::Lan).last_write_utc_ticks, 123);
    assert_eq!(after.zone(Zone::Lan).write_count, u32::MAX);
    assert_eq!(after.zone(Zone::Raw).last_write_utc_ticks, 456);
    assert_eq!(after.zone(Zone::Raw).write_count, 0);
    assert_eq!(
        after.region(Region::Raw).words,
        before.region(Region::Raw).words
    );
    assert_eq!(after.pair_bits(200)?, 0x8000_0000);
    assert_eq!(before.service().status(), after.service().status());
    assert_eq!(&bytes[49..64], &FIXTURE[49..64]);
    Ok(())
}

#[test]
fn only_exact_one_is_active_and_projection_never_changes_header_reserve() -> Result<(), DecodeError>
{
    for flag in [0, 1, 2, 255] {
        let mut bytes = FIXTURE.to_vec();
        bytes[18] = flag;
        bytes[49..64].fill(0xcc); // Decoder must not assign meaning or rewrite reserve.
        let original = bytes.clone();
        let snap = Snapshot::decode(&bytes)?;
        assert_eq!(snap.zone(Zone::Com).producer_active, flag == 1);
        let _ = snap.variables().count();
        let _ = snap.service().com_source_time();
        assert_eq!(bytes, original);
    }
    Ok(())
}
