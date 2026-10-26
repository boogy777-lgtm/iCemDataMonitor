use icem_desktop::{Model, navigation::NavigationPoll};
use icem_web::{
    proto,
    shell::{Operation, Response},
};

#[test]
fn lost_consume_reply_does_not_retry_or_consume_the_next_pending_section() -> Result<(), String> {
    let mut poll = NavigationPoll::default();
    let mut model = Model::default();
    // A fake Host read-and-clear. The first response is lost AFTER consumption.
    let mut pending = 1;
    let mut calls = 0;
    poll.submit(true, || {
        assert_eq!(std::mem::replace(&mut pending, 0), 1);
        calls += 1;
        Ok(())
    })?;
    model.apply_host(
        Operation::Navigate,
        Err("response lost after consume".into()),
    );
    poll.complete();
    assert!(model.navigation.is_none());
    assert!(model.note.contains("автоматический повтор отключён"));
    pending = 2; // a later Host command must survive repeated status/reconnect polls
    for connected in [false, true, true, false, true] {
        poll.submit(connected, || {
            calls += 1;
            pending = 0;
            Ok(())
        })?;
    }
    assert_eq!(calls, 1);
    assert_eq!(pending, 2);
    poll.show(); // only a new explicit show demand authorizes a new consume
    poll.submit(true, || {
        model.apply_host(
            Operation::Navigate,
            Ok(Response::Navigate(proto::NavigateUiResponse {
                pending: std::mem::replace(&mut pending, 0),
                ..Default::default()
            })),
        );
        calls += 1;
        Ok(())
    })?;
    assert_eq!(calls, 2);
    assert_eq!(model.navigation, Some(false));
    Ok(())
}

#[test]
fn startup_waits_for_connection_queue_refusal_is_safe_and_show_during_flight_survives()
-> Result<(), String> {
    let mut poll = NavigationPoll::default();
    poll.submit(false, || Err("must not enqueue while disconnected".into()))?;
    assert!(
        poll.submit(true, || Err("queue full; not sent".into()))
            .is_err()
    );
    let mut calls = 0;
    poll.submit(true, || {
        calls += 1;
        Ok(())
    })?;
    poll.show();
    poll.submit(true, || {
        calls += 1;
        Ok(())
    })?;
    assert_eq!(calls, 1); // one in flight
    poll.complete();
    poll.submit(true, || {
        calls += 1;
        Ok(())
    })?;
    assert_eq!(calls, 2); // reply didn't erase a newer show
    poll.complete();
    poll.submit(true, || {
        calls += 1;
        Ok(())
    })?;
    assert_eq!(calls, 2);
    Ok(())
}
