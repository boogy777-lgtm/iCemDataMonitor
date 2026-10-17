use icem_config::emulator::EmuSettings;
use icem_emulation::{
    COMMANDS,
    control::process_line,
    lan::{BaseProvider, Registry, VectorData},
    runtime::Engine,
    runtime_ports::{ComEmitter, LanEmitter},
    simulation::{Template, Variable},
};
use std::{collections::BTreeSet, path::Path};
#[derive(Default)]
struct Com {
    name: Option<String>,
    lines: Vec<String>,
}
impl ComEmitter for Com {
    fn open(&mut self, name: &str) -> Result<(), String> {
        self.name = Some(name.into());
        self.lines.clear();
        Ok(())
    }
    fn close(&mut self) {
        self.name = None;
    }
    fn is_open(&self) -> bool {
        self.name.is_some()
    }
    fn port_name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    fn lines_sent(&self) -> u64 {
        self.lines.len() as u64
    }
    fn send_line(&mut self, line: &str) -> Result<(), String> {
        self.lines.push(line.into());
        Ok(())
    }
    fn ports(&self) -> Vec<String> {
        vec!["COM12".into()]
    }
}
#[derive(Default)]
struct Lan {
    name: Option<String>,
    frames: u64,
    busy: std::sync::atomic::AtomicBool,
}
impl LanEmitter for Lan {
    fn is_sending(&self) -> bool {
        self.busy.load(std::sync::atomic::Ordering::Acquire)
    }
    fn open(&mut self, name: &str) -> Result<(), String> {
        self.name = Some(name.into());
        self.frames = 0;
        Ok(())
    }
    fn close(&mut self) {
        self.name = None;
    }
    fn is_open(&self) -> bool {
        self.name.is_some()
    }
    fn adapter_name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    fn frames_sent(&self) -> u64 {
        self.frames
    }
    fn send_superframe(&mut self, v: &[VectorData], _: u32) -> Result<(), String> {
        self.frames += v.len() as u64;
        Ok(())
    }
    fn adapters(&self) -> Vec<String> {
        vec!["Ethernet".into()]
    }
    fn npcap_available(&self) -> bool {
        true
    }
}
type TestResult<T = ()> = Result<T, Box<dyn std::error::Error>>;
fn engine() -> TestResult<Engine<Com, Lan>> {
    let template = Template {
        name: "test".into(),
        device_type: "FLECS".into(),
        variables: vec![Variable {
            name: "Pressure".into(),
            base: 10.0,
            unit: "psi".into(),
            ..Default::default()
        }],
        ..Default::default()
    };
    let base = BaseProvider::load(
        Registry::default(),
        Path::new("/__icem_absent_runtime_test__/variables.json"),
        None,
    )?;
    Ok(Engine::new(
        EmuSettings::default(),
        vec![template],
        base,
        Com::default(),
        Lan::default(),
    ))
}
fn command(e: &mut Engine<Com, Lan>, line: &str) -> TestResult<serde_json::Value> {
    let reply = process_line(line, e);
    let json: serde_json::Value = serde_json::from_str(&reply.json)?;
    assert_eq!(json["ok"], true, "{line}: {json}");
    Ok(json)
}
#[test]
fn every_command_reaches_the_real_engine_and_channels_remain_independent() -> TestResult {
    let mut e = engine()?;
    e.start();
    e.tick();
    assert_eq!(e.com_emitter().lines_sent(), 0);
    assert_eq!(e.lan_emitter().frames_sent(), 0);
    let lines = [
        "status",
        "help",
        "profiles",
        "profile test",
        "vars com",
        "ports",
        "adapters",
        "setport COM12",
        "setadapter Ethernet",
        "setequipment 0x12345678",
        "setfirmware 1",
        "setemulate com 1",
        "setpreset 1",
        "setunit flow gal/min",
        "start com",
        "set com.Pressure 17",
        "get com.Pressure",
        "clear com.Pressure",
        "watch",
        "stop com",
        "quit",
    ];
    let mut visited = BTreeSet::new();
    for line in lines {
        command(&mut e, line)?;
        visited.insert(line.split_whitespace().next().ok_or("empty command")?);
        if line == "start com" {
            e.tick();
            assert_eq!(e.com_emitter().lines_sent(), 1);
            assert_eq!(e.lan_emitter().frames_sent(), 0);
        }
        if line == "set com.Pressure 17" {
            assert_eq!(command(&mut e, "get com.Pressure")?["data"]["value"], 17.0);
        }
    }
    assert_eq!(
        visited,
        COMMANDS.iter().copied().collect(),
        "a new command needs a runtime acceptance case"
    );
    assert!(e.quit_requested());
    command(&mut e, "start lan")?;
    e.tick();
    assert_eq!(e.com_emitter().lines_sent(), 1);
    assert!(e.lan_emitter().frames_sent() > 0);
    e.shutdown();
    assert!(!e.com_emitter().is_open());
    assert!(!e.lan_emitter().is_open());
    Ok(())
}
#[test]
fn malformed_command_does_not_stop_stream_or_change_settings() -> TestResult {
    let mut e = engine()?;
    command(&mut e, "setport COM12")?;
    command(&mut e, "start com")?;
    let settings = e.settings().clone();
    let revision = e.settings_revision();
    for line in [
        "{",
        "start unknown",
        "setpreset 4",
        "setunit missing ft",
        "get com.missing",
    ] {
        let reply = process_line(line, &mut e);
        let json: serde_json::Value = serde_json::from_str(&reply.json)?;
        assert_eq!(json["ok"], false, "{line}");
        assert_eq!(e.settings(), &settings);
        assert_eq!(e.settings_revision(), revision);
        assert!(e.com_emitter().is_open());
    }
    e.tick();
    assert_eq!(e.com_emitter().lines_sent(), 1);
    Ok(())
}

#[test]
fn busy_lan_burst_holds_model_clock_and_keeps_com_commands_running() -> TestResult {
    use std::sync::atomic::Ordering;
    let mut e = engine()?;
    command(&mut e, "setport COM12")?;
    command(&mut e, "setadapter Ethernet")?;
    command(&mut e, "start com")?;
    command(&mut e, "start lan")?;
    e.tick();
    let frames = e.lan_emitter().frames_sent();
    assert_eq!(e.lan_elapsed(), 1.);
    e.lan_emitter().busy.store(true, Ordering::Release);
    e.tick();
    e.tick();
    assert_eq!(e.lan_elapsed(), 1.);
    assert_eq!(e.lan_emitter().frames_sent(), frames);
    assert_eq!(e.com_emitter().lines_sent(), 3);
    command(&mut e, "status")?;
    e.lan_emitter().busy.store(false, Ordering::Release);
    e.tick();
    assert_eq!(e.lan_elapsed(), 2.);
    assert!(e.lan_emitter().frames_sent() > frames);
    Ok(())
}
