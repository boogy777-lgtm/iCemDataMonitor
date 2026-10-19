use icem_emulation::{
    Channel,
    control::{self, ControlResult, EmuControl, payload::*},
};
use serde_json::{Value, json};
#[derive(Default)]
struct Fake {
    calls: Vec<Value>,
}
impl Fake {
    fn call<T>(&mut self, method: &str, args: Value, data: T) -> ControlResult<T> {
        self.calls.push(json!({"method":method,"args":args}));
        if args
            .as_array()
            .is_some_and(|a| a.iter().any(|v| v.as_str() == Some("fail")))
        {
            ControlResult::fail("backend rejected")
        } else {
            ControlResult::success(data)
        }
    }
}
fn channel(c: &str) -> Channel {
    if c.eq_ignore_ascii_case("lan") {
        Channel::Lan
    } else {
        Channel::Com
    }
}
fn variable() -> ComVariable {
    ComVariable {
        name: "Pressure".into(),
        uom: "psi".into(),
        value: 12.5,
        overridden: true,
        exclude_from_telemetry: false,
    }
}
impl EmuControl for Fake {
    fn get_status(&mut self) -> Status {
        self.calls.push(json!({"method":"GetStatus","args":[]}));
        Status {
            pid: 7,
            version: "fixture".into(),
            uptime_sec: 3.5,
            com: ComStatus {
                lines_per_sec: 0.25,
                ..Default::default()
            },
            lan: LanStatus {
                frames_per_sec: 0.5,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn start_channel(&mut self, c: &str) -> ControlResult<Start> {
        self.call(
            "StartChannel",
            json!([c]),
            match channel(c) {
                Channel::Com => Start::Com {
                    port: Some("COM12".into()),
                    already: false,
                },
                Channel::Lan => Start::Lan {
                    adapter: Some("Ethernet".into()),
                    already: false,
                },
            },
        )
    }
    fn stop_channel(&mut self, c: &str) -> ControlResult<Stop> {
        self.call(
            "StopChannel",
            json!([c]),
            Stop {
                channel: channel(c),
                was_streaming: true,
            },
        )
    }
    fn set_variable(
        &mut self,
        c: Option<&str>,
        n: &str,
        v: f64,
        sv: Option<u32>,
    ) -> ControlResult<Set> {
        self.call(
            "SetVariable",
            json!([c, n, format!("{:016X}", v.to_bits()), sv]),
            Set {
                applied: vec![AppliedSignal {
                    signal: if c.is_some_and(|c| c.eq_ignore_ascii_case("lan")) {
                        Signal::Lan {
                            sv: sv.unwrap_or(42),
                            name: n.into(),
                        }
                    } else {
                        Signal::Com { name: n.into() }
                    },
                    uom: "psi".into(),
                }],
                value: 12.5,
            },
        )
    }
    fn clear_variable(
        &mut self,
        c: Option<&str>,
        n: &str,
        sv: Option<u32>,
    ) -> ControlResult<Clear> {
        self.call(
            "ClearVariable",
            json!([c, n, sv]),
            Clear {
                cleared: vec![c.map_or(Channel::Com, channel)],
            },
        )
    }
    fn get_variable(&mut self, c: Option<&str>, n: &str, sv: Option<u32>) -> ControlResult<Get> {
        self.call(
            "GetVariable",
            json!([c, n, sv]),
            if c.is_some_and(|c| c.eq_ignore_ascii_case("lan")) {
                Get::Lan {
                    sv: sv.unwrap_or(42),
                    name: n.into(),
                    uom: "psi".into(),
                    value: 12.5,
                    overridden: true,
                }
            } else {
                Get::Com(ComVariable {
                    name: n.into(),
                    ..variable()
                })
            },
        )
    }
    fn get_profiles(&mut self) -> ControlResult<Vec<ProfileInfo>> {
        self.call(
            "GetProfiles",
            json!([]),
            vec![ProfileInfo {
                profile: Profile {
                    device_type: "FLECS".into(),
                    name: "Test".into(),
                },
                variable_count: 1,
                active: true,
            }],
        )
    }
    fn set_profile(&mut self, d: Option<&str>, n: &str) -> ControlResult<Profile> {
        self.call(
            "SetProfile",
            json!([d, n]),
            Profile {
                device_type: d.unwrap_or("fixture").into(),
                name: n.into(),
            },
        )
    }
    fn get_variables(&mut self, c: Option<&str>) -> ControlResult<Variables> {
        self.call(
            "GetVariables",
            json!([c]),
            Variables {
                com: if c.is_none_or(|c| c.eq_ignore_ascii_case("com")) {
                    Some(ComVariables {
                        template: Some("Test".into()),
                        variables: vec![variable()],
                    })
                } else {
                    None
                },
                lan: if c.is_none_or(|c| c.eq_ignore_ascii_case("lan")) {
                    Some(vec![LanVariable {
                        sv: 42,
                        name: "Pressure".into(),
                        uom: "psi".into(),
                        value: Some(12.5),
                        value_type: "float".into(),
                        byte_offset: 4,
                        size: 4,
                        transmittable: true,
                        overridden: true,
                    }])
                } else {
                    None
                },
            },
        )
    }
    fn get_ports(&mut self) -> ControlResult<Vec<Endpoint>> {
        self.call(
            "GetPorts",
            json!([]),
            vec![Endpoint {
                name: "COM12".into(),
                open: true,
            }],
        )
    }
    fn get_adapters(&mut self) -> ControlResult<Adapters> {
        self.call(
            "GetAdapters",
            json!([]),
            Adapters {
                adapters: vec![Endpoint {
                    name: "Ethernet".into(),
                    open: true,
                }],
                npcap_available: true,
            },
        )
    }
    fn set_port(&mut self, p: &str) -> ControlResult<PortChanged> {
        self.call(
            "SetPort",
            json!([p]),
            PortChanged {
                port: p.into(),
                streaming: true,
            },
        )
    }
    fn set_adapter(&mut self, a: &str) -> ControlResult<AdapterChanged> {
        self.call(
            "SetAdapter",
            json!([a]),
            AdapterChanged {
                adapter: a.into(),
                streaming: true,
            },
        )
    }
    fn set_equipment(&mut self, e: u32) -> ControlResult<u32> {
        self.call("SetEquipment", json!([e]), e)
    }
    fn set_firmware(&mut self, on: bool) -> ControlResult<bool> {
        self.call("SetFirmware", json!([on]), on)
    }
    fn set_emulation(&mut self, c: &str, on: bool) -> ControlResult<Emulation> {
        self.call(
            "SetEmulation",
            json!([c, on]),
            Emulation {
                channel: channel(c),
                emulating: on,
            },
        )
    }
    fn set_preset(&mut self, p: i32) -> ControlResult<String> {
        self.call(
            "SetPreset",
            json!([p]),
            ["FirmwareDefault", "LanNative", "Metric", "Imperial"][p as usize].into(),
        )
    }
    fn set_unit(&mut self, g: &str, u: Option<&str>) -> ControlResult<UnitChanged> {
        self.call(
            "SetUnit",
            json!([g, u]),
            UnitChanged {
                group: g.into(),
                unit: u.map(str::to_owned),
            },
        )
    }
    fn request_quit(&mut self) {
        self.calls.push(json!({"method":"RequestQuit","args":[]}));
    }
}
fn actual(line: &str) -> Result<Value, Box<dyn std::error::Error>> {
    let mut ctl = Fake::default();
    let r = control::process_line(line, &mut ctl);
    Ok(
        json!({"line":line,"cmd":r.cmd,"reply":serde_json::from_str::<Value>(&r.json)?,"start_watch":r.start_watch,"quit":r.quit,"calls":ctl.calls}),
    )
}
#[test]
fn commands_and_bad_requests_are_total() -> Result<(), Box<dyn std::error::Error>> {
    let cases: Vec<String> =
        serde_json::from_str(include_str!("../../../tests/support/control-cases.json"))?;
    for line in cases {
        assert!(actual(&line)?["reply"]["ok"].is_boolean(), "{line}");
    }
    assert_eq!(control::COMMAND_NAMES.len(), 21);
    assert_eq!(
        actual("help")?["reply"]["data"]["commands"],
        json!(icem_emulation::COMMANDS)
    );
    Ok(())
}
#[test]
fn grouping_prefix_and_ack() -> Result<(), Box<dyn std::error::Error>> {
    assert_eq!(
        control::tokenize("get \"unfinished name"),
        ["get", "unfinished name"]
    );
    assert_eq!(
        actual("set LAN.X 2")?["calls"],
        json!([{"method":"SetVariable","args":["lan","X","4000000000000000",null]}])
    );
    assert_eq!(actual("watch")?["start_watch"], true);
    assert_eq!(actual("quit")?["quit"], true);
    assert_eq!(actual("status")?["reply"]["data"]["proto"], 1);
    assert_eq!(actual("get fail")?["reply"]["error"], "backend rejected");
    Ok(())
}
#[test]
fn original_dotnet_dispatch() -> Result<(), Box<dyn std::error::Error>> {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let bytes = if let Some(path) = std::env::var_os("ICEM_CONTROL_ORACLE") {
        std::fs::read(path)?
    } else if cfg!(windows) {
        let o = std::process::Command::new("pwsh")
            .args(["-NoProfile", "-File"])
            .arg(root.join("rust/tests/support/ControlOracle.ps1"))
            .arg("-RepoRoot")
            .arg(&root)
            .output()?;
        assert!(o.status.success(), "{}", String::from_utf8_lossy(&o.stderr));
        o.stdout
    } else {
        return Ok(());
    };
    let oracle: Value = serde_json::from_slice(&bytes)?;
    let rows = oracle["rows"].as_array().ok_or("missing rows")?;
    let mut native_errors = 0;
    for expected in rows {
        let line = expected["line"].as_str().ok_or("missing line")?;
        let mut got = actual(line)?;
        let mut expected = expected.clone();
        if expected["reply"]["error"]
            .as_str()
            .is_some_and(|e| e.starts_with("invalid JSON:"))
        {
            assert!(
                got["reply"]["error"]
                    .as_str()
                    .is_some_and(|e| e.starts_with("invalid JSON:"))
            );
            got["reply"]["error"] = json!("invalid JSON");
            expected["reply"]["error"] = json!("invalid JSON");
            native_errors += 1;
        }
        assert_eq!(got, expected, "{line}");
    }
    std::fs::create_dir_all(root.join("rust/target"))?;
    std::fs::write(
        root.join("rust/target/control-differential-report.json"),
        serde_json::to_vec_pretty(
            &json!({"result":"PASS","runtime":oracle["runtime"],"cases":rows.len(),"command_count":21,"native_json_diagnostic_cases":native_errors,"payload_families":21,"comparison":"full parsed domain-shaped envelope/data, backend method+exact IEEE arguments, ACK flags; only native JSON syntax diagnostic prose normalized; deterministic backend fixtures, not a legacy Engine oracle"}),
        )?,
    )?;
    Ok(())
}

#[test]
fn all_21_typed_commands_and_v1_adapter_use_the_same_execution_path()
-> Result<(), Box<dyn std::error::Error>> {
    use control::{
        Command,
        application::{Equipment, NativeValue, Scope, Variable},
    };
    use icem_emulation::{Channel, units::Preset};
    let variable = || Variable {
        scope: Scope::channel(Channel::Com),
        name: "X".into(),
        sv: None,
    };
    let cases = [
        (Command::Status, "status"),
        (Command::Start(Channel::Com.into()), "start com"),
        (Command::Stop(Channel::Lan.into()), "stop lan"),
        (
            Command::Set(variable(), NativeValue::finite(1.5)?),
            "set com.X 1.5",
        ),
        (Command::Get(variable()), "get com.X"),
        (Command::Clear(variable()), "clear com.X"),
        (Command::Profiles, "profiles"),
        (
            Command::Profile {
                device_type: None,
                name: "A".into(),
            },
            "profile A",
        ),
        (Command::Variables(Scope::default()), "vars"),
        (Command::Watch, "watch"),
        (Command::Quit, "quit"),
        (Command::Help, "help"),
        (Command::Ports, "ports"),
        (Command::Adapters, "adapters"),
        (Command::SetPort("COM12".into()), "setport COM12"),
        (
            Command::SetAdapter("Ethernet".into()),
            "setadapter Ethernet",
        ),
        (
            Command::SetEquipment(Equipment(u32::MAX)),
            "setequipment 0xFFFFFFFF",
        ),
        (Command::SetFirmware(true), "setfirmware 1"),
        (
            Command::SetEmulation(Channel::Lan.into(), false),
            "setemulate lan 0",
        ),
        (Command::SetPreset(Preset::Metric), "setpreset 2"),
        (
            Command::SetUnit {
                group: "pressure".into(),
                unit: Some("bar".into()),
            },
            "setunit pressure bar",
        ),
    ];
    let mut visited = std::collections::BTreeSet::new();
    for (typed, line) in cases {
        visited.insert(typed.name());
        let mut direct = Fake::default();
        let mut legacy = Fake::default();
        let result = control::execute(typed, &mut direct);
        let reply = control::process_line(line, &mut legacy);
        assert_eq!(result.v1(), reply, "{line}");
        assert_eq!(direct.calls, legacy.calls, "{line}");
        assert!(result.matches_request(result.command), "{line}");
        assert_eq!(
            result.result.data().map(Payload::command_name),
            Some(result.command),
            "{line}"
        );
    }
    assert_eq!(visited, control::COMMAND_NAMES.iter().copied().collect());
    Ok(())
}
#[test]
fn strict_native_values_do_not_change_v1_coercion_policy() -> Result<(), Box<dyn std::error::Error>>
{
    use control::{
        Command,
        application::{Equipment, NativeValue},
    };
    assert!(NativeValue::finite(f64::NAN).is_err());
    assert!(NativeValue::finite(f64::INFINITY).is_err());
    assert_eq!(
        NativeValue::finite(-0.0)?.get().to_bits(),
        (-0.0_f64).to_bits()
    );
    assert_eq!(
        control::parse_line("setfirmware 2").map_err(|r| r.json)?,
        Command::SetFirmware(true)
    );
    assert_eq!(
        control::parse_line("setequipment 2.9").map_err(|r| r.json)?,
        Command::SetEquipment(Equipment(2))
    );
    assert_eq!(actual("setpreset NaN")?["calls"][0]["args"][0], 0);
    assert_eq!(actual("setequipment NaN")?["calls"][0]["args"][0], 0);
    assert_eq!(actual("setfirmware NaN")?["calls"][0]["args"][0], true);
    Ok(())
}
#[test]
fn typed_budget_rejects_before_backend_effect() {
    let mut ctl = Fake::default();
    let result = control::execute(control::Command::SetAdapter("x".repeat(65_537)), &mut ctl);
    assert_eq!(result.outcome, control::application::Outcome::Rejected);
    assert_eq!(
        result.error.as_ref().map(|e| e.category),
        Some(control::application::ErrorCategory::InvalidInput)
    );
    assert!(ctl.calls.is_empty());
}

#[test]
fn v1_optional_data_and_error_envelope_remain_frozen() -> Result<(), Box<dyn std::error::Error>> {
    assert_eq!(
        control::to_value(true, "status", None, None),
        json!({"ok":true,"cmd":"status"})
    );
    assert_eq!(
        control::to_value(false, "get", None, None),
        json!({"ok":false,"cmd":"get","error":"error"})
    );
    let mut ctl = Fake::default();
    for line in ["start COM", "stop LAN", "setemulate LAN 1", "vars COM"] {
        let command = control::parse_line(line).map_err(|r| r.json)?;
        let name = command.name();
        let execution = control::execute(command, &mut ctl);
        assert!(execution.matches_request(name));
        let value: Value = serde_json::from_str(&execution.v1().json)?;
        if value["data"].get("channel").is_some() {
            assert!(matches!(
                value["data"]["channel"].as_str(),
                Some("com" | "lan")
            ));
        }
    }
    Ok(())
}
