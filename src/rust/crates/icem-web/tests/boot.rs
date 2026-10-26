use icem_web::BootStages;
#[test]
fn stages_keep_ordinals_clone_snapshots_and_leave_failed_stage_unchecked() -> Result<(), String> {
    let b = BootStages::default();
    assert!(b.complete("missing", None).is_err());
    assert!(b.begin("  ", "invalid").is_err());
    b.begin("ini", "конфигурация")?;
    b.begin("composition", "композиция")?;
    let old = b.snapshot()?;
    b.complete("ini", Some("port=8090"))?;
    let now = b.snapshot()?;
    assert!(!old[0].completed);
    assert!(now[0].completed);
    assert!(!now[1].completed);
    assert_eq!(now[0].ordinal, 1);
    assert_eq!(now[1].ordinal, 2);
    b.begin("ini", "заново")?;
    let now = b.snapshot()?;
    assert!(!now[0].completed);
    assert_eq!(now[0].ordinal, 1);
    assert_eq!(now[0].detail, "port=8090");
    for n in 2..64 {
        b.begin(&n.to_string(), "stage")?;
    }
    assert_eq!(b.snapshot()?.len(), 64);
    assert!(b.begin("overflow", "stage").is_err());
    Ok(())
}
