use icem_desktop::{Model, VariableRow, equipment_command, unit_command};
use icem_emulation::{
    Channel,
    control::{
        Command,
        application::{Equipment, NativeValue, Scope, Variable},
    },
};
use icem_web::{
    proto,
    shell::{BootBatch, Operation, Response},
};
fn boot(model: &mut Model, reset: bool, stages: &[(u32, &str, &str, bool)]) {
    model.apply_host(
        Operation::Boot,
        Ok(Response::Boot(BootBatch {
            reset,
            stages: stages
                .iter()
                .map(|(ordinal, id, title, completed)| proto::BootStage {
                    ordinal: *ordinal,
                    id: (*id).into(),
                    title: (*title).into(),
                    completed: *completed,
                    ..Default::default()
                })
                .collect(),
        })),
    );
}

use icem_emulation::control::{
    ControlResult, Execution,
    application::{ErrorCategory, Outcome},
    payload::*,
};
fn apply(model: &mut Model, payload: Payload) {
    let command = payload.command_name();
    model.apply_emulator(
        command,
        Execution {
            command,
            result: ControlResult::success(payload),
            outcome: Outcome::Completed,
            error: None,
        },
    );
}
fn variables(names: &[&str]) -> Payload {
    Payload::Variables(Variables {
        com: Some(ComVariables {
            variables: names
                .iter()
                .map(|name| ComVariable {
                    name: (*name).into(),
                    ..Default::default()
                })
                .collect(),
            ..Default::default()
        }),
        lan: None,
    })
}

#[test]
fn manual_value_commands_preserve_sv_native_names_and_finite_numbers() -> Result<(), String> {
    let model = Model {
        lan_variables: vec![VariableRow {
            identity: Signal::Lan {
                sv: 0x137,
                name: "Discharge pressure".into(),
            },
            ..Default::default()
        }],
        selected_lan: Some(0),
        ..Default::default()
    };
    assert_eq!(
        model.edit_command(true, Some("1,25"))?,
        Command::Set(
            Variable {
                scope: Scope::channel(Channel::Lan),
                name: "Discharge pressure".into(),
                sv: Some(0x137)
            },
            NativeValue::finite(1.25).map_err(str::to_owned)?
        )
    );
    for bad in ["NaN", "inf", "1e999", "invalid"] {
        assert!(model.edit_command(true, Some(bad)).is_err());
    }
    assert_eq!(
        equipment_command("0xFFFFFFFF")?,
        Command::SetEquipment(Equipment(u32::MAX))
    );
    assert!(equipment_command("4294967296").is_err());
    assert_eq!(
        unit_command(5, Some(" bar ")),
        Command::SetUnit {
            group: "pressure".into(),
            unit: Some("bar".into())
        }
    );
    assert_eq!(
        unit_command(0, None),
        Command::SetUnit {
            group: "flow".into(),
            unit: None
        }
    );
    Ok(())
}
#[test]
fn command_refusal_is_visible_and_does_not_project_requested_state() {
    let mut model = Model::default();
    apply(&mut model, Payload::Status(Box::default()));
    model.apply_emulator(
        "start",
        Execution::rejected(
            "start",
            ErrorCategory::Rejected,
            "no LAN adapter configured",
        ),
    );
    assert!(!model.emulator.lan.streaming);
    assert!(model.note.contains("no LAN adapter"));
    assert!(model.connected);
    model.apply_emulator(
        "status",
        Execution::rejected("status", ErrorCategory::Disconnected, "pipe disconnected"),
    );
    assert!(!model.connected);
}
#[test]
fn refresh_cannot_retarget_an_override_to_another_variable() {
    let mut model = Model {
        com_variables: vec![VariableRow {
            identity: Signal::Com {
                name: "Pressure".into(),
            },
            ..Default::default()
        }],
        selected_com: Some(0),
        ..Default::default()
    };
    apply(&mut model, variables(&["Flow", "Pressure"]));
    assert_eq!(model.selected_com, Some(1));
    apply(&mut model, variables(&["Flow"]));
    assert_eq!(model.selected_com, None);
    assert!(model.edit_command(false, Some("4")).is_err());
}
#[test]
fn profile_choices_keep_device_identity_and_variable_clicks_keep_signal_identity()
-> Result<(), String> {
    let mut model = Model::default();
    apply(
        &mut model,
        Payload::Profiles(
            ["A", "B"]
                .into_iter()
                .map(|d| ProfileInfo {
                    profile: Profile {
                        name: "Shared".into(),
                        device_type: d.into(),
                    },
                    ..Default::default()
                })
                .collect(),
        ),
    );
    assert_eq!(
        model.profiles[1].command(),
        Command::Profile {
            name: "Shared".into(),
            device_type: Some("B".into())
        }
    );
    model.lan_variables = vec![
        VariableRow {
            identity: Signal::Lan {
                sv: 2,
                name: "Pressure".into(),
            },
            value: "42".into(),
            ..Default::default()
        },
        VariableRow {
            identity: Signal::Lan {
                sv: 1,
                name: "Pressure".into(),
            },
            value: "12".into(),
            ..Default::default()
        },
    ];
    assert_eq!(
        model.select_variable(&Signal::Lan {
            sv: 1,
            name: "Pressure".into()
        }),
        Some("12".into())
    );
    assert!(matches!(
        model.edit_command(true, Some("14"))?,
        Command::Set(Variable { sv: Some(1), .. }, _)
    ));
    assert_eq!(
        model.select_variable(&Signal::Lan {
            sv: 0x999,
            name: "Pressure".into()
        }),
        None
    );
    assert!(model.edit_command(true, Some("14")).is_err());
    Ok(())
}
#[test]
fn boot_is_ordered_ready_is_authoritative_and_disconnect_starts_new_replay() {
    let mut m = Model::default();
    boot(
        &mut m,
        false,
        &[
            (2, "ready", "готовность", false),
            (1, "ini", "конфигурация", true),
        ],
    );
    assert!(!m.boot_ready);
    assert_eq!(
        m.boot_stages.keys().copied().collect::<Vec<_>>(),
        vec![1, 2]
    );
    boot(&mut m, false, &[(2, "ready", "готовность", true)]);
    assert!(m.boot_ready);
    assert_eq!(m.boot_stages.len(), 2);
    m.apply_host(Operation::Boot, Err("stream ended".into()));
    assert!(m.boot_error.is_some());
    assert!(!m.boot_ready);
    assert!(m.boot_stages.is_empty());
    m.apply_host(Operation::Status, Err("disconnected".into()));
    assert!(!m.boot_ready);
    assert!(m.boot_stages.is_empty());
}
#[test]
fn fresh_replay_replaces_old_ready_even_without_a_failed_status_probe() {
    let mut m = Model::default();
    boot(&mut m, true, &[(7, "ready", "old process", true)]);
    assert!(m.boot_ready);
    // A fast restart may never fail GetStatus. New stream replay is authoritative.
    boot(&mut m, true, &[(1, "ini", "new process", false)]);
    assert!(!m.boot_ready);
    assert_eq!(m.boot_stages.len(), 1);
    assert_eq!(m.boot_stages[&1].title, "new process");
    boot(&mut m, false, &[(2, "ready", "new ready", true)]);
    assert!(m.boot_ready);
    boot(&mut m, true, &[]);
    assert!(m.boot_stages.is_empty());
    assert!(!m.boot_ready);
    m.apply_host(Operation::Boot, Err("lag; reconnect".into()));
    assert!(m.boot_error.as_deref().is_some_and(|s| s.contains("lag")));
}
#[test]
fn navigation_uses_contract_sections_without_changing_connection_truth() {
    let mut m = Model {
        connected: true,
        ..Default::default()
    };
    for (section, panel) in [(1, Some(true)), (2, Some(false)), (0, None)] {
        m.apply_host(
            Operation::Navigate,
            Ok(Response::Navigate(proto::NavigateUiResponse {
                pending: section,
                ..Default::default()
            })),
        );
        assert_eq!(m.navigation, panel);
    }
    m.apply_host(Operation::Navigate, Err("unavailable".into()));
    assert!(m.connected);
    assert!(m.note.contains("unavailable"));
}

#[test]
fn save_failure_is_visible_after_application_and_does_not_look_like_rejection() {
    let mut model = Model::default();
    let mut execution = Execution {
        command: "setequipment",
        result: ControlResult::success(Payload::SetEquipment(42)),
        outcome: Outcome::Applied,
        error: None,
    };
    execution.saved(Err("disk full".into()));
    model.apply_emulator("setequipment", execution);
    assert!(model.note.contains("применено в памяти"));
    assert!(model.note.contains("disk full"));
    apply(
        &mut model,
        Payload::Status(Box::new(Status {
            lan: LanStatus {
                equipment: 42,
                ..Default::default()
            },
            ..Default::default()
        })),
    );
    assert!(
        model.note.contains("disk full"),
        "routine status cannot erase mutation diagnostics"
    );
}

#[test]
fn mismatched_emulator_reply_cannot_publish_status_or_endpoint_choices() {
    let mut model = Model::default();
    for (command, payload) in [
        (
            "status",
            Payload::Ports(vec![Endpoint {
                name: "foreign".into(),
                open: true,
            }]),
        ),
        ("ports", Payload::Adapters(Adapters::default())),
    ] {
        model.apply_emulator(
            command,
            Execution {
                command,
                result: ControlResult::success(payload),
                outcome: Outcome::Completed,
                error: None,
            },
        );
        assert!(!model.connected);
        assert!(model.ports.is_empty());
        assert!(model.emulator.status.is_none());
        assert!(model.note.contains("не соответствует"));
    }
    model.apply_emulator(
        "status",
        Execution {
            command: "ports",
            result: ControlResult::success(Payload::Status(Box::default())),
            outcome: Outcome::Completed,
            error: None,
        },
    );
    assert!(!model.connected);
}
#[test]
fn typed_display_retains_nonfinite_and_escaped_units_policy() {
    let mut model = Model::default();
    let odd = "quote\"slash\\newline\nnull\0DEL\u{7f}C1\u{85}Ж";
    let units = UnitStatus {
        preset: "Metric".into(),
        flow_override: Some(odd.into()),
        ..Default::default()
    };
    apply(
        &mut model,
        Payload::Status(Box::new(Status {
            units,
            com: ComStatus {
                lines_per_sec: f64::INFINITY,
                ..Default::default()
            },
            ..Default::default()
        })),
    );
    assert_eq!(model.emulator.com.rate, "—");
    assert!(
        model
            .emulator
            .status
            .as_ref()
            .is_some_and(|s| s.com.lines_per_sec == f64::INFINITY)
    );
    assert_eq!(
        model.emulator.units,
        concat!(
            "{\"preset\":\"Metric\",\"flowOverride\":\"quote\\\"slash\\\\newline\\nnull\\u0000DEL",
            "\u{7f}C1\u{85}Ж\",\"temperatureOverride\":null,\"densityOverride\":null,",
            "\"volumeOverride\":null,\"levelOverride\":null,\"pressureOverride\":null}"
        )
    );
    apply(
        &mut model,
        Payload::Variables(Variables {
            com: Some(ComVariables {
                variables: vec![ComVariable {
                    value: f64::NAN,
                    ..Default::default()
                }],
                ..Default::default()
            }),
            lan: Some(vec![
                LanVariable {
                    value: Some(f64::NEG_INFINITY),
                    ..Default::default()
                },
                LanVariable {
                    value: None,
                    ..Default::default()
                },
            ]),
        }),
    );
    assert_eq!(model.com_variables[0].value, "—");
    assert_eq!(model.lan_variables[0].value, "—");
    assert_eq!(model.lan_variables[1].value, "—");
}
#[test]
fn partial_and_unknown_outcomes_keep_last_known_state_and_warn_without_retry() {
    let mut model = Model::default();
    apply(
        &mut model,
        Payload::Status(Box::new(Status {
            lan: LanStatus {
                equipment: 42,
                ..Default::default()
            },
            ..Default::default()
        })),
    );
    for (outcome, label) in [
        (Outcome::AppliedWithError, "частично"),
        (Outcome::Unknown, "автоматический повтор отключён"),
    ] {
        let mut execution =
            Execution::rejected("setequipment", ErrorCategory::Rejected, "injected failure");
        execution.outcome = outcome;
        model.apply_emulator("setequipment", execution);
        assert!(model.note.contains(label));
        assert_eq!(model.emulator.equipment().as_deref(), Some("0x0000002A"));
    }
}

#[test]
fn all_21_payload_families_are_accepted_by_typed_presentation() {
    let cases = [
        ("status", Payload::Status(Box::default())),
        (
            "start",
            Payload::Start(Start::Com {
                port: Some("COM12".into()),
                already: false,
            }),
        ),
        (
            "stop",
            Payload::Stop(Stop {
                channel: Channel::Com,
                was_streaming: true,
            }),
        ),
        (
            "set",
            Payload::Set(Set {
                applied: vec![],
                value: 12.5,
            }),
        ),
        ("get", Payload::Get(Get::Com(ComVariable::default()))),
        ("clear", Payload::Clear(Clear { cleared: vec![] })),
        ("profiles", Payload::Profiles(vec![])),
        ("profile", Payload::Profile(Profile::default())),
        ("vars", Payload::Variables(Variables::default())),
        ("watch", Payload::Watch),
        ("quit", Payload::Quit),
        ("help", Payload::Help),
        ("ports", Payload::Ports(vec![])),
        ("adapters", Payload::Adapters(Adapters::default())),
        (
            "setport",
            Payload::SetPort(PortChanged {
                port: "COM12".into(),
                streaming: true,
            }),
        ),
        (
            "setadapter",
            Payload::SetAdapter(AdapterChanged {
                adapter: "Ethernet".into(),
                streaming: true,
            }),
        ),
        ("setequipment", Payload::SetEquipment(u32::MAX)),
        ("setfirmware", Payload::SetFirmware(false)),
        (
            "setemulate",
            Payload::SetEmulation(Emulation {
                channel: Channel::Lan,
                emulating: false,
            }),
        ),
        ("setpreset", Payload::SetPreset("Metric".into())),
        (
            "setunit",
            Payload::SetUnit(UnitChanged {
                group: "flow".into(),
                unit: None,
            }),
        ),
    ];
    let mut seen = std::collections::BTreeSet::new();
    for (name, payload) in cases {
        assert_eq!(payload.command_name(), name);
        seen.insert(name);
        let mut model = Model::default();
        model.apply_emulator(
            name,
            Execution {
                command: name,
                result: ControlResult::success(payload),
                outcome: Outcome::Completed,
                error: None,
            },
        );
        assert!(!model.note.contains('⚠'), "{name}: {}", model.note);
        if name == "status" {
            assert!(model.connected && model.emulator.status.is_some());
        }
    }
    assert_eq!(seen, icem_emulation::COMMANDS.iter().copied().collect());
}

#[test]
fn units_summary_has_frozen_native_field_order_for_all_seven_values() {
    let mut model = Model::default();
    apply(
        &mut model,
        Payload::Status(Box::new(Status {
            units: UnitStatus {
                preset: "Metric".into(),
                flow_override: Some("m3/min".into()),
                temperature_override: Some("°C".into()),
                density_override: Some("kg/m3".into()),
                volume_override: Some("m3".into()),
                level_override: Some("m".into()),
                pressure_override: Some("kPa".into()),
            },
            ..Default::default()
        })),
    );
    assert_eq!(
        model.emulator.units,
        r#"{"preset":"Metric","flowOverride":"m3/min","temperatureOverride":"°C","densityOverride":"kg/m3","volumeOverride":"m3","levelOverride":"m","pressureOverride":"kPa"}"#
    );
}
