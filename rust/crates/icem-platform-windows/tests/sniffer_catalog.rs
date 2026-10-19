use icem_emulation::lan::{Registry, SignalDef, VectorDef};
use icem_platform_windows::{
    sniffer::{Capture, CaptureDevice},
    sniffer_catalog::{
        Catalog, MAX_CATALOG_BYTES, MAX_EQUIPMENT, MAX_SIGNALS_PER_VECTOR, MAX_VECTORS,
    },
};
use icem_protocols::ace::build;
use std::{collections::VecDeque, time::Duration};
fn definition(number: u32, bad: bool) -> VectorDef {
    let mut signals = vec![SignalDef {
        byte_offset: 4,
        size: 4,
        value_type: "FF".into(),
        das_factor: 2.,
        name: "pressure".into(),
        uom: "psi".into(),
    }];
    if bad {
        signals.push(SignalDef {
            byte_offset: 127,
            size: 1,
            value_type: "FF".into(),
            das_factor: 1.,
            name: "bad".into(),
            uom: "".into(),
        });
    }
    VectorDef {
        sv_number: number,
        name: format!("SV{number}"),
        equipment_type: "unit".into(),
        signals,
    }
}
fn catalog() -> Result<Catalog, String> {
    Catalog::new(Registry::from_vectors(vec![
        definition(1, false),
        definition(2, true),
    ]))
}
#[test]
fn stable_ids_equipment_atomic_failure_preview_and_idle_rates() -> Result<(), String> {
    let mut c = catalog()?;
    let mut f = build([1; 6], 0xffff_ffff, 1, &20f32.to_bits().to_be_bytes());
    c.observe(&f, Duration::ZERO);
    let v = c.vectors(Duration::from_secs(1));
    assert_eq!(v[0].id, 1);
    assert_eq!(v[0].signal_count, 1);
    assert_eq!(v[0].age_sec, 1.);
    assert_eq!(c.preview(1)[0].1, 40.);
    assert_eq!(c.equipment()[0].number, -1);
    c.advance(Duration::from_secs(1)); // Legacy first stats rotation establishes the window.
    c.observe(&f, Duration::from_millis(1500));
    c.advance(Duration::from_secs(2));
    assert_eq!(c.vectors(Duration::from_secs(2))[0].rate, 1.);
    c.advance(Duration::from_secs(3));
    assert_eq!(c.equipment()[0].rate, 0.);
    f[24..28].copy_from_slice(&2u32.to_be_bytes());
    c.observe(&f, Duration::from_secs(3));
    assert_eq!(c.counters().decode_errors, 1);
    assert!(c.signals(2).is_empty());
    assert_eq!(c.preview(1)[0].1, 40.);
    f[24..28].copy_from_slice(&0xdead_beefu32.to_be_bytes());
    c.observe(&f, Duration::from_secs(4));
    assert_eq!(c.vectors(Duration::from_secs(4))[2].id, 3);
    assert!(c.vectors(Duration::from_secs(4))[2].unknown);
    assert_eq!(c.counters().unknown_vectors, 1);
    assert_eq!(c.equipment()[0].vector_count, 3);
    assert_eq!(c.counters().prefixes, [4, 0]);
    assert!(c.preview(99).is_empty());
    Ok(())
}
#[test]
fn untrusted_unknown_cardinality_and_catalog_memory_are_bounded() -> Result<(), String> {
    let mut c = Catalog::new(Registry::default())?;
    for i in 0..MAX_VECTORS + 50 {
        c.observe(&build([0; 6], i as u32, i as u32, &[]), Duration::ZERO);
    }
    assert_eq!(c.vectors(Duration::ZERO).len(), MAX_VECTORS);
    assert_eq!(c.equipment().len(), MAX_EQUIPMENT);
    assert!(c.counters().tracking_limit_reached);
    assert_eq!(c.counters().unknown_vectors, (MAX_VECTORS + 50) as u64);
    let mut large = definition(1, false);
    large.name = "x".repeat(MAX_CATALOG_BYTES as usize + 1);
    assert!(Catalog::new(Registry::from_vectors(vec![large])).is_err());
    let mut many = definition(1, false);
    many.signals = vec![many.signals[0].clone(); MAX_SIGNALS_PER_VECTOR + 1];
    assert!(Catalog::new(Registry::from_vectors(vec![many])).is_err());
    assert!(
        Catalog::new(Registry::from_vectors(
            (0..=MAX_VECTORS)
                .map(|i| definition(i as u32, false))
                .collect()
        ))
        .is_err()
    );
    Ok(())
}
#[test]
fn file_catalog_enforces_read_budget_and_reports_unavailable_input()
-> Result<(), Box<dyn std::error::Error>> {
    struct Temp(std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }
    let file = Temp(std::env::temp_dir().join(format!("icem-host-catalog-{}", std::process::id())));
    std::fs::write(&file.0, b"{\"serviceVectors\":[]}")?;
    assert!(Catalog::load(&file.0).is_ok());
    std::fs::write(&file.0, vec![b' '; MAX_CATALOG_BYTES as usize + 1])?;
    assert!(Catalog::load(&file.0).is_err_and(|e| e.contains("2 MiB")));
    std::fs::write(&file.0, [0xff, 0xfe])?;
    assert!(Catalog::load(&file.0).is_err());
    std::fs::write(&file.0, b"{")?;
    assert!(Catalog::load(&file.0).is_err());
    Ok(())
}
struct Device {
    frames: VecDeque<Option<Vec<u8>>>,
}
impl CaptureDevice for Device {
    fn open(&mut self, _: Option<&str>) -> Result<String, String> {
        Ok("test".into())
    }
    fn next_packet(&mut self) -> Result<Option<Vec<u8>>, String> {
        Ok(self.frames.pop_front().flatten())
    }
    fn close(&mut self) {}
}
#[test]
fn existing_capture_drains_paused_frames_without_decode_and_logs_bounded_witness()
-> Result<(), String> {
    let frame = build([0; 6], 17, 1, &5f32.to_bits().to_be_bytes()).to_vec();
    let mut owner = Capture::new(
        Device {
            frames: vec![Some(frame.clone()), None, Some(frame), None].into(),
        },
        None,
        true,
    )
    .with_catalog(catalog()?);
    owner.poll(Duration::ZERO);
    assert_eq!(
        owner.catalog().ok_or("catalog")?.counters().decoded_frames,
        1
    );
    assert!(owner.take_witness(Duration::ZERO).is_none());
    assert!(
        owner
            .take_witness(Duration::from_secs(1))
            .is_some_and(|w| w.contains("decode=1"))
    );
    assert!(owner.take_witness(Duration::from_secs(2)).is_none());
    owner.toggle()?;
    owner.poll(Duration::from_secs(3));
    assert_eq!(
        owner.catalog().ok_or("catalog")?.counters().decoded_frames,
        1
    );
    owner.toggle()?;
    owner.stop();
    assert!(owner.catalog().is_some());
    Ok(())
}
