use icem_desktop::{
    Model,
    logs::{LogView, Request},
};
use icem_logging::{Command, ControlError, Outcome, Reply, Status};
fn reply(enabled: Option<bool>, outcome: Outcome) -> Reply {
    Reply {
        outcome,
        status: Status {
            enabled,
            available: true,
            recording: enabled == Some(true),
            directory: "logs".into(),
            error: String::new(),
            operation: String::new(),
            written: 7,
        },
        clear: None,
    }
}
#[test]
fn state_is_owner_acknowledged_disconnect_retains_facts_and_blocks_mutations() {
    let mut model = Model::default();
    assert!(!model.host_logs.actionable());
    model
        .host_logs
        .apply(Ok(reply(Some(true), Outcome::Completed)));
    let clear = Request {
        local: false,
        command: Command::Clear,
    };
    assert!(model.can_submit_log(&clear));
    model.host_logs.pending = true;
    assert!(!model.can_submit_log(&clear));
    assert!(!model.can_submit_log(&Request {
        local: false,
        command: Command::SetEnabled(false)
    }));
    assert_eq!(model.host_logs.enabled(), Some(true));
    model
        .host_logs
        .apply(Err(ControlError::unknown("response lost; outcome unknown")));
    assert!(!model.host_logs.actionable());
    assert_eq!(model.host_logs.enabled(), Some(true));
    model
        .host_logs
        .apply(Ok(reply(Some(false), Outcome::Completed)));
    assert_eq!(model.host_logs.enabled(), Some(false));
    model.apply_host(
        icem_web::shell::Operation::Status,
        Err("Host disconnected".into()),
    );
    assert!(!model.host_logs.actionable());
    let mut partial = reply(Some(true), Outcome::Incomplete);
    partial.status.error = "partial clear: locked file".into();
    model.local_logs.apply(Ok(partial));
    assert!(model.local_logs.description().contains("partial clear"));
    assert!(!model.local_logs.description().contains("успешно"));
    assert_eq!(model.local_logs.enabled(), Some(true));
}
#[test]
fn unknown_preference_and_invalid_reply_do_not_invent_facts() {
    let mut view = LogView::default();
    let mut read = reply(None, Outcome::Completed);
    read.status.error = "invalid preference".into();
    view.apply(Ok(read.clone()));
    assert!(view.fresh);
    assert_eq!(view.enabled(), None);
    read.status.recording = true;
    view.apply(Ok(read));
    assert!(!view.fresh);
    assert_eq!(view.enabled(), None);
    assert!(view.description().contains("invalid preference"));
}
#[test]
fn missing_http_reply_is_not_fresh_and_rejected_is_distinct_from_unknown() {
    let mut view = LogView::default();
    view.apply(Ok(reply(Some(false), Outcome::Completed)));
    view.pending = true;
    view.apply(icem_web::log_control::decode_reply(
        &serde_json::json!({"ok":true}),
        Command::SetEnabled(true),
    ));
    assert!(!view.pending);
    assert!(!view.fresh);
    assert!(!view.actionable());
    assert_eq!(view.enabled(), Some(false));
    assert_eq!(
        view.note.matches("результат операции неизвестен").count(),
        1
    );
    view.apply(Err(ControlError::rejected("owner unavailable before call")));
    assert!(view.note.contains("отклонена"));
    assert!(!view.note.contains("неизвестен"));
    assert_eq!(view.enabled(), Some(false));
}

#[test]
fn repeated_background_reads_preserve_controls_and_description() {
    for enabled in [false, true] {
        let mut view = LogView::default();
        view.apply(Ok(reply(Some(enabled), Outcome::Completed)));
        let description = view.description();
        for _ in 0..100 {
            assert!(view.can_submit(Command::Get));
            view.submitted(Command::Get);
            assert!(!view.can_submit(Command::Get));
            assert!(view.actionable());
            assert_eq!(view.description(), description);
            view.apply(Ok(reply(Some(enabled), Outcome::Completed)));
            assert!(view.actionable());
            assert_eq!(view.description(), description);
        }
    }
}

#[test]
fn read_before_mutation_cannot_acknowledge_mutation_and_failures_recover() {
    for command in [Command::Clear, Command::SetEnabled(false)] {
        let mut view = LogView::default();
        view.apply(Ok(reply(Some(true), Outcome::Completed)));
        view.submitted(Command::Get);
        assert!(view.can_submit(command));
        view.submitted(command);
        assert!(!view.actionable());
        assert!(!view.can_submit(Command::Get));
        assert!(view.description().contains("выполняется"));
        view.apply(Ok(reply(Some(true), Outcome::Completed)));
        assert!(view.pending);
        assert!(!view.actionable());
        view.apply(Err(ControlError::unknown("lost")));
        assert!(!view.pending);
        assert!(!view.fresh);
        assert!(view.note.contains("автоматического повтора нет"));
        assert!(view.can_submit(Command::Get));
        view.submitted(Command::Get);
        view.apply(Err(ControlError::rejected("offline")));
        assert!(!view.actionable());
        assert!(view.note.contains("отклонена"));
        view.submitted(Command::Get);
        view.apply(Ok(reply(Some(false), Outcome::Completed)));
        assert!(view.actionable());
        assert_eq!(view.enabled(), Some(false));
        assert!(view.note.is_empty());
    }
}
