use icem_web::{
    dashboard_client::{History, Read, Registers},
    shell::Selection,
};
use serde_json::{Value, json};
pub fn registers() -> Value {
    let mut zones = serde_json::Map::new();
    for (key, base, end) in [("com", 4, 200), ("lan", 200, 400), ("raw", 400, 600)] {
        let pairs: Vec<_> = (base..end).step_by(2).enumerate().map(|(idx, addr)| {
            json!({"idx":idx,"addr":addr,"addrEnd":addr+1,"name":if addr==4 {Some("Unix")} else if addr==6 {Some("Pressure")} else {None},
            "value":if addr==4 {json!("2026-10-17T12:00:00Z")} else {json!(12.25)}, "kind":if addr==4 {"unix"} else {"float"}})
        }).collect();
        zones.insert(
            key.into(),
            json!({"label":key,"baseReg":base,"len":end-base,"pairs":pairs}),
        );
    }
    let live = json!({"active":false,"producerFlag":false,"ageMs":0,"writeCount":0});
    json!({"system":{"scalars":(0..4).map(|reg| json!({"reg":reg,"name":"COM","value":1,"label":null})).collect::<Vec<_>>(),"liveness":null},
        "zones":zones,"liveness":{"com":live,"lan":live,"raw":live},"updatedAtUtc":"2026-10-17T12:00:00.000Z","stale":false})
}
pub fn response(history: Value) -> Read {
    let selected = Selection::new(
        history
            .as_object()
            .into_iter()
            .flat_map(|m| m.keys())
            .filter_map(|key| key.parse().ok())
            .collect(),
    )
    .unwrap_or_else(|e| panic!("selection: {e}"));
    Read {
        registers: Registers::decode(
            &serde_json::to_vec(&registers()).unwrap_or_else(|e| panic!("JSON: {e}")),
        )
        .unwrap_or_else(|e| panic!("registers: {e}")),
        history: History::decode(
            &serde_json::to_vec(&history).unwrap_or_else(|e| panic!("JSON: {e}")),
            &selected,
        ),
    }
}
