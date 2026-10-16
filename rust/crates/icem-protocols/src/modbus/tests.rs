use super::*;
use icem_bus::DecodeError;

fn snapshot(count: u16, active: [u8; 3]) -> Result<Snapshot, DecodeError> {
    let mut bytes = include_bytes!("../../../../tests/fixtures/bus-v5-minimal.bin").to_vec();
    bytes[8..10].copy_from_slice(&count.to_le_bytes());
    bytes.resize(64 + usize::from(count) * 2, 0);
    for (index, flag) in active.into_iter().enumerate() {
        bytes[18 + 9 * index] = flag;
    }
    for (index, pair) in bytes[64..].chunks_exact_mut(2).enumerate() {
        pair.copy_from_slice(&(index as u16).to_be_bytes());
    }
    Snapshot::decode(&bytes)
}

#[test]
fn read_write_wire_vectors_and_exception_precedence() -> Result<(), Exception> {
    let mut bank = RegisterBank::new(650)?;
    assert_eq!(
        bank.process(&[7, 6, 0, 4, 0xbe, 0xef]),
        [7, 6, 0, 4, 0xbe, 0xef]
    );
    for function in [3, 4] {
        assert_eq!(
            bank.process(&[7, function, 0, 4, 0, 2]),
            [7, function, 4, 0xbe, 0xef, 0, 0]
        );
        assert_eq!(
            bank.process(&[7, function, 2, 137, 0, 2]),
            [7, function | 0x80, 2]
        );
        assert_eq!(
            bank.process(&[7, function, 255, 255, 0, 0]),
            [7, function | 0x80, 3]
        );
        assert_eq!(
            bank.process(&[7, function, 255, 254, 0, 125]),
            [7, function | 0x80, 2]
        );
    }
    assert_eq!(
        bank.process(&[7, 16, 0, 5, 0, 2, 4, 0x12, 0x34, 0x56, 0x78]),
        [7, 16, 0, 5, 0, 2]
    );
    assert_eq!(
        bank.process(&[7, 3, 0, 4, 0, 3]),
        [7, 3, 6, 0xbe, 0xef, 0x12, 0x34, 0x56, 0x78]
    );
    assert_eq!(bank.process(&[7, 8]), [7, 0x88, 1]);
    assert_eq!(bank.process(&[]), [0, 0x80, 1]);
    assert_eq!(bank.process(&[7]), [0, 0x80, 1]);
    Ok(())
}

#[test]
fn truncated_write_never_changes_a_prefix_or_panics() -> Result<(), Exception> {
    for request in [
        vec![1, 6, 0, 4, 0xff, 0xff],
        vec![1, 16, 0, 4, 0, 2, 4, 1, 2, 3, 4],
    ] {
        for end in 2..request.len() {
            let mut bank = RegisterBank::new(650)?;
            assert_eq!(bank.process(&request[..end]), [1, request[1] | 0x80, 3]);
            assert!(bank.words().iter().all(|word| *word == 0));
        }
    }
    let mut bank = RegisterBank::new(650)?;
    assert_eq!(
        bank.process(&[1, 16, 2, 137, 0, 2, 4, 1, 2, 3, 4]),
        [1, 0x90, 2]
    );
    assert!(bank.words().iter().all(|word| *word == 0));
    assert_eq!(bank.process(&[1, 16, 0, 4, 0, 2, 2, 1, 2]), [1, 0x90, 3]);
    Ok(())
}

#[test]
fn maximum_quantities_and_last_address() -> Result<(), Exception> {
    let mut bank = RegisterBank::new(u16::MAX)?;
    assert_eq!(
        bank.process(&[255, 6, 255, 254, 0xca, 0xfe]),
        [255, 6, 255, 254, 0xca, 0xfe]
    );
    assert_eq!(
        bank.process(&[255, 4, 255, 254, 0, 1]),
        [255, 4, 2, 0xca, 0xfe]
    );
    assert_eq!(bank.process(&[255, 6, 255, 255, 0, 1]), [255, 0x86, 2]);
    assert_eq!(bank.process(&[1, 3, 0, 0, 0, 125]).len(), 253);
    assert_eq!(bank.process(&[1, 3, 0, 0, 0, 126]), [1, 0x83, 3]);
    let mut request = vec![1, 16, 0, 0, 0, 123, 246];
    request.extend(std::iter::repeat(0x5a).take(246));
    assert_eq!(bank.process(&request), [1, 16, 0, 0, 0, 123]);
    assert!(bank.words()[..123].iter().all(|word| *word == 0x5a5a));
    assert!(RegisterBank::new(3).is_err());
    Ok(())
}

#[test]
fn mirror_plain_overwrite_raw_flags_inactive_reserve_and_absent_hold(
) -> Result<(), Box<dyn std::error::Error>> {
    let mut bank = RegisterBank::new(10_001).map_err(|error| format!("{error:?}"))?;
    let snap = snapshot(650, [1, 0, 2])?; // 2 is not active; old ticks deliberately irrelevant.
    bank.tick(MirrorInput::Fresh(&snap));
    assert_eq!(bank.register_count(), 650);
    assert_eq!(bank.words()[4], 4);
    for address in [0_u16, 4, 200, 400, 600] {
        let [hi, lo] = address.to_be_bytes();
        assert_eq!(bank.process(&[1, 6, hi, lo, 0xbe, 0xef])[1], 6);
    }
    bank.tick(MirrorInput::Fresh(&snap));
    assert_eq!(bank.words()[0], 0);
    assert_eq!(bank.words()[4], 4); // overwritten even though bus data did not change
    assert_eq!(&bank.words()[200..201], &[0xbeef]);
    assert_eq!(bank.words()[400], 0xbeef);
    assert_eq!(bank.words()[600], 0xbeef);
    bank.process(&[1, 6, 0, 4, 0xca, 0xfe]);
    bank.tick(MirrorInput::LastGood(&snap));
    assert_eq!(bank.words()[4], 0xcafe);
    bank.tick(MirrorInput::Unavailable);
    assert_eq!(&bank.words()[..4], &[0; 4]);
    assert_eq!(bank.words()[4], 0xcafe);
    Ok(())
}

#[test]
fn resize_preserves_overlap_zeros_growth_and_matches_legacy_stale_order(
) -> Result<(), Box<dyn std::error::Error>> {
    let mut bank = RegisterBank::new(650).map_err(|error| format!("{error:?}"))?;
    bank.process(&[1, 6, 2, 137, 0xab, 0xcd]);
    let larger = snapshot(1_000, [0; 3])?;
    bank.tick(MirrorInput::Fresh(&larger));
    assert_eq!(bank.words()[649], 0xabcd);
    assert!(bank.words()[650..].iter().all(|word| *word == 0));
    let smaller = snapshot(650, [0; 3])?;
    bank.tick(MirrorInput::LastGood(&smaller)); // actual .NET calls ResizeToBus before stale return
    assert_eq!(bank.register_count(), 650);
    assert_eq!(bank.words()[649], 0xabcd);
    assert_eq!(bank.process(&[1, 3, 2, 138, 0, 1]), [1, 0x83, 2]);
    Ok(())
}

#[test]
fn all_function_bytes_and_small_bodies_are_total_and_writes_stay_bounded() -> Result<(), Exception>
{
    for function in 0..=u8::MAX {
        for length in 2..=9 {
            let mut body = vec![0xff; length];
            body[0] = 1;
            body[1] = function;
            let mut bank = RegisterBank::new(4)?;
            let result = bank.process(&body);
            assert_eq!(result.len(), 3);
            assert_eq!(result[1], function | 0x80);
            assert_eq!(bank.words(), &[0; 4]);
        }
    }
    Ok(())
}

#[test]
fn each_zone_mirrors_exact_words_and_preserves_other_zones(
) -> Result<(), Box<dyn std::error::Error>> {
    for (index, selected) in Zone::ALL.into_iter().enumerate() {
        let mut flags = [0; 3];
        flags[index] = 1;
        let snap = snapshot(650, flags)?;
        let mut bank = RegisterBank::new(650).map_err(|error| format!("{error:?}"))?;
        bank.tick(MirrorInput::Fresh(&snap));
        let (start, end) = selected.range();
        assert_eq!(&bank.words()[start..end], &snap.words()[start..end]);
        for other in Zone::ALL {
            if other != selected {
                let (start, end) = other.range();
                assert!(bank.words()[start..end].iter().all(|word| *word == 0));
            }
        }
        assert!(bank.words()[600..].iter().all(|word| *word == 0));
    }
    Ok(())
}
