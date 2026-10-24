use icem_bus::Value as BusValue;
use icem_opc_da_codec::{READY, Received, read_next_frame};
use opc_classic_types::{Error, ErrorCode, Guid, Result, Timestamp, Value, ValueType};
use opc_classic_utils::{
    ComApartment,
    server::{ClassFactory, LocalClassRegistration},
};
use opc_da_bindings::{
    client::{
        ClientItemHandle, DataSource, GroupOptions, ItemSpec, ServerItemHandle, ServerState,
        ServerStatus,
    },
    server::{
        DaGroupService, DaServer, DaService, ServerAddedItem, ServerItemResult, ServerSample,
    },
};
use std::{
    collections::{BTreeMap, BTreeSet},
    io::Write,
    sync::{
        Arc, Mutex, MutexGuard,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Instant, SystemTime},
};
const INVALID_HANDLE: ErrorCode = ErrorCode::from_raw(0xc0040001_u32.cast_signed());
const UNKNOWN_ITEM: ErrorCode = ErrorCode::from_raw(0xc0040007_u32.cast_signed());
const BAD_RIGHTS: ErrorCode = ErrorCode::from_raw(0xc0040006_u32.cast_signed());
const MAX_GROUPS: usize = 32;
const MAX_ITEMS: usize = 4096;
type Model = Arc<Mutex<Option<Received>>>;
fn lock<T>(mutex: &Mutex<T>) -> Result<MutexGuard<'_, T>> {
    mutex
        .lock()
        .map_err(|_| Error::unexpected("OPC DA state poisoned"))
}
fn canonical(value: BusValue) -> ValueType {
    match value {
        BusValue::Word(_) => ValueType::U16,
        BusValue::ComUnixSeconds(_) => ValueType::I32,
        BusValue::Float32(_) => ValueType::F32,
    }
}
fn value(value: BusValue) -> Value {
    match value {
        BusValue::Word(v) => Value::U16(v),
        BusValue::ComUnixSeconds(v) => Value::I32(v),
        BusValue::Float32(v) => Value::F32(v),
    }
}
fn address(id: &str) -> Option<u16> {
    let address: u16 = id.strip_prefix("var")?.parse().ok()?;
    (id == format!("var{address}")).then_some(address)
}
struct Item {
    address: u16,
    client: ClientItemHandle,
    active: bool,
}
struct Items {
    entries: BTreeMap<u32, Item>,
    next: u32,
}
struct Lease(Arc<AtomicUsize>);
impl Drop for Lease {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::AcqRel);
    }
}
fn lease(counter: &Arc<AtomicUsize>, max: usize) -> Result<Lease> {
    counter
        .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
            (n < max).then_some(n + 1)
        })
        .map_err(|_| Error::out_of_memory("DA object limit"))?;
    Ok(Lease(counter.clone()))
}
struct Limits {
    servers: Arc<AtomicUsize>,
    groups: Arc<AtomicUsize>,
}
impl Limits {
    fn new() -> Self {
        Self {
            servers: Arc::new(AtomicUsize::new(0)),
            groups: Arc::new(AtomicUsize::new(0)),
        }
    }
}
struct Group {
    _lease: Lease,
    model: Model,
    items: Mutex<Items>,
}
impl Group {
    fn added(
        &self,
        items: &[ItemSpec],
        commit: bool,
    ) -> Result<Vec<ServerItemResult<ServerAddedItem>>> {
        if items.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let model = lock(&self.model)?;
        let mut state = lock(&self.items)?;
        let mut result = Vec::with_capacity(items.len());
        for item in items {
            let sample = address(&item.item_id).and_then(|address| {
                model
                    .as_ref()?
                    .sample(address, Instant::now())
                    .map(|sample| (address, sample))
            });
            let Some((address, sample)) = sample else {
                result.push(ServerItemResult::failure(UNKNOWN_ITEM));
                continue;
            };
            let ty = canonical(sample.value);
            if !item.access_path.is_empty()
                || !item.blob.is_empty()
                || (item.requested_data_type != ValueType::EMPTY && item.requested_data_type != ty)
            {
                result.push(ServerItemResult::failure(ErrorCode::TYPE_MISMATCH));
                continue;
            }
            if commit && (state.entries.len() >= MAX_ITEMS || state.next == u32::MAX) {
                result.push(ServerItemResult::failure(ErrorCode::OUT_OF_MEMORY));
                continue;
            }
            let handle = state.next;
            if commit {
                state.next += 1;
                state.entries.insert(
                    handle,
                    Item {
                        address,
                        client: item.client_handle,
                        active: item.active,
                    },
                );
            }
            result.push(ServerItemResult::success(ServerAddedItem {
                server_handle: ServerItemHandle::from_raw(handle),
                canonical_data_type: ty,
                access_rights: 1,
                blob: Vec::new(),
            }));
        }
        Ok(result)
    }
    fn update(
        &self,
        handles: &[ServerItemHandle],
        mut operation: impl FnMut(&mut Item),
    ) -> Result<Vec<ServerItemResult<()>>> {
        if handles.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let mut items = lock(&self.items)?;
        Ok(handles
            .iter()
            .map(|handle| match items.entries.get_mut(&handle.raw()) {
                Some(item) => {
                    operation(item);
                    ServerItemResult::success(())
                }
                None => ServerItemResult::failure(INVALID_HANDLE),
            })
            .collect())
    }
}
impl DaGroupService for Group {
    fn add_items(&self, items: &[ItemSpec]) -> Result<Vec<ServerItemResult<ServerAddedItem>>> {
        self.added(items, true)
    }
    fn validate_items(
        &self,
        items: &[ItemSpec],
        _: bool,
    ) -> Result<Vec<ServerItemResult<ServerAddedItem>>> {
        self.added(items, false)
    }
    fn remove_items(&self, handles: &[ServerItemHandle]) -> Result<Vec<ServerItemResult<()>>> {
        if handles.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let mut items = lock(&self.items)?;
        Ok(handles
            .iter()
            .map(|h| {
                if items.entries.remove(&h.raw()).is_some() {
                    ServerItemResult::success(())
                } else {
                    ServerItemResult::failure(INVALID_HANDLE)
                }
            })
            .collect())
    }
    fn set_active(
        &self,
        handles: &[ServerItemHandle],
        active: bool,
    ) -> Result<Vec<ServerItemResult<()>>> {
        self.update(handles, |item| item.active = active)
    }
    fn set_client_handles(
        &self,
        handles: &[(ServerItemHandle, ClientItemHandle)],
    ) -> Result<Vec<ServerItemResult<()>>> {
        if handles.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let mut items = lock(&self.items)?;
        Ok(handles
            .iter()
            .map(|(h, c)| match items.entries.get_mut(&h.raw()) {
                Some(item) => {
                    item.client = *c;
                    ServerItemResult::success(())
                }
                None => ServerItemResult::failure(INVALID_HANDLE),
            })
            .collect())
    }
    fn set_data_types(
        &self,
        handles: &[(ServerItemHandle, ValueType)],
    ) -> Result<Vec<ServerItemResult<()>>> {
        if handles.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let model = lock(&self.model)?;
        let items = lock(&self.items)?;
        Ok(handles
            .iter()
            .map(|(h, ty)| match items.entries.get(&h.raw()) {
                Some(item) => match model
                    .as_ref()
                    .and_then(|m| m.sample(item.address, Instant::now()))
                {
                    Some(sample) if *ty == ValueType::EMPTY || *ty == canonical(sample.value) => {
                        ServerItemResult::success(())
                    }
                    _ => ServerItemResult::failure(ErrorCode::TYPE_MISMATCH),
                },
                None => ServerItemResult::failure(INVALID_HANDLE),
            })
            .collect())
    }
    fn read(
        &self,
        source: DataSource,
        handles: &[ServerItemHandle],
    ) -> Result<Vec<ServerItemResult<ServerSample>>> {
        if handles.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        let model = lock(&self.model)?;
        let items = lock(&self.items)?;
        let now = Instant::now();
        Ok(handles
            .iter()
            .map(|h| match items.entries.get(&h.raw()) {
                Some(item) => match model.as_ref().and_then(|m| m.sample(item.address, now)) {
                    Some(sample) => ServerItemResult::success(ServerSample {
                        client_handle: item.client,
                        timestamp: Timestamp::from_ticks(sample.filetime),
                        quality: if source == DataSource::Cache && !item.active {
                            0x1c
                        } else {
                            sample.quality
                        },
                        value: value(sample.value),
                    }),
                    None => ServerItemResult::failure(UNKNOWN_ITEM),
                },
                None => ServerItemResult::failure(INVALID_HANDLE),
            })
            .collect())
    }
    fn write(&self, values: &[(ServerItemHandle, Value)]) -> Result<Vec<ServerItemResult<()>>> {
        if values.len() > MAX_ITEMS {
            return Err(Error::out_of_memory("DA batch limit4096"));
        }
        Ok(values
            .iter()
            .map(|_| ServerItemResult::failure(BAD_RIGHTS))
            .collect())
    }
}
struct Service {
    _lease: Lease,
    limits: Arc<Limits>,
    model: Model,
    groups: Mutex<BTreeSet<u32>>,
    start: Timestamp,
}
impl Service {
    fn new(model: Model, limits: Arc<Limits>) -> Result<Arc<Self>> {
        Ok(Arc::new(Self {
            _lease: lease(&limits.servers, 32)?,
            limits,
            model,
            groups: Mutex::new(BTreeSet::new()),
            start: Timestamp::from_system_time(SystemTime::now())?,
        }))
    }
}
impl DaService for Service {
    fn status(&self) -> Result<ServerStatus> {
        let model = lock(&self.model)?;
        let groups = lock(&self.groups)?;
        Ok(ServerStatus {
            start_time: self.start,
            current_time: Timestamp::from_system_time(SystemTime::now())?,
            last_update_time: Timestamp::from_ticks(0),
            state: if model.as_ref().is_some_and(|m| m.current(Instant::now())) {
                ServerState::Running
            } else {
                ServerState::CommunicationFault
            },
            group_count: groups.len() as u32,
            bandwidth: 0,
            version: (0, 1, 0),
            vendor_info: "iCem read-only synchronous DA; browse/callbacks/DA3 unsupported".into(),
        })
    }
    fn add_group(&self, handle: u32, _: GroupOptions) -> Result<Arc<dyn DaGroupService>> {
        let group_lease = lease(&self.limits.groups, MAX_GROUPS)?;
        let mut groups = lock(&self.groups)?;
        if groups.len() >= MAX_GROUPS || !groups.insert(handle) {
            return Err(Error::out_of_memory("DA group limit32"));
        }
        Ok(Arc::new(Group {
            _lease: group_lease,
            model: Arc::clone(&self.model),
            items: Mutex::new(Items {
                entries: BTreeMap::new(),
                next: 1,
            }),
        }))
    }
    fn remove_group(&self, handle: u32, _: bool) -> Result<()> {
        lock(&self.groups)?.remove(&handle);
        Ok(())
    }
    fn error_string(&self, error: ErrorCode, _: u32) -> Result<String> {
        Ok(format!("iCem DA {error}"))
    }
}
/// Explicit per-user identity mapping only. No autoactivation, categories or DCOM edits.
fn registration(remove: bool) -> std::result::Result<(), String> {
    use icem_opc_da_codec::{CLSID, PROG_ID};
    let root = "HKCU\\Software\\Classes";
    if remove {
        for key in [
            format!("{root}\\CLSID\\{CLSID}"),
            format!("{root}\\{PROG_ID}"),
        ] {
            let status = std::process::Command::new("reg.exe")
                .args(["delete", &key, "/f", "/reg:64"])
                .status()
                .map_err(|e| e.to_string())?;
            if !status.success() {
                return Err(format!("Failed to remove owned OPC DA registry key: {key}"));
            }
        }
    } else {
        for (key, value) in [
            (
                format!("{root}\\CLSID\\{CLSID}"),
                "iCem Rust read-only DA synchronous profile",
            ),
            (format!("{root}\\CLSID\\{CLSID}\\ProgID"), PROG_ID),
            (
                format!("{root}\\{PROG_ID}"),
                "iCem Rust read-only DA synchronous profile",
            ),
            (format!("{root}\\{PROG_ID}\\CLSID"), CLSID),
        ] {
            let status = std::process::Command::new("reg.exe")
                .args([
                    "add", &key, "/ve", "/t", "REG_SZ", "/d", value, "/f", "/reg:64",
                ])
                .status()
                .map_err(|e| e.to_string())?;
            if !status.success() {
                return Err(format!(
                    "Failed to register owned OPC DA registry key: {key}"
                ));
            }
        }
    }
    Ok(())
}
pub fn run() -> std::result::Result<(), String> {
    if std::env::args().count() == 2 {
        match std::env::args().nth(1).as_deref() {
            Some("--register") => return registration(false),
            Some("--unregister") => return registration(true),
            _ => {}
        }
    }
    if std::env::args().nth(1).as_deref() != Some("--ipc-v1") || std::env::args().count() != 2 {
        return Err("Use --ipc-v1; sidecar must be owned by Host".into());
    }
    let apartment = ComApartment::mta().map_err(|e| e.to_string())?;
    let model = Arc::new(Mutex::new(None));
    let limits = Arc::new(Limits::new());
    let factory = ClassFactory::new({
        let model = model.clone();
        move || {
            let service: Arc<dyn DaService> = Service::new(model.clone(), limits.clone())?;
            Ok(DaServer::new(service).object())
        }
    });
    let id = Guid::new(
        0xc69b6b01,
        0x878c,
        0x4927,
        [0x8c, 0xc0, 0xc6, 0xba, 0x26, 0xc4, 0xb7, 0x1a],
    );
    let registration =
        LocalClassRegistration::register(&apartment, &id, factory).map_err(|e| e.to_string())?;
    let mut input = std::io::stdin().lock();
    let mut output = std::io::stdout().lock();
    // READY grants the first credit: Host must sample ages after receiving it.
    let mut credit_issued = Instant::now();
    output
        .write_all(READY)
        .and_then(|()| output.flush())
        .map_err(|e| e.to_string())?;
    let mut previous = None;
    let mut geometry = None;
    let mut generation = 0;
    loop {
        let frame = match read_next_frame(&mut input) {
            Ok(Some(frame)) => frame,
            Ok(None) => break,
            Err(error) => return Err(error.to_string()),
        };
        if previous.is_some_and(|n| frame.sequence <= n) {
            return Err("IPC sequence regression".into());
        }
        if frame.generation < generation || (generation > 0 && frame.snapshot.is_none()) {
            return Err("IPC capture generation/retention regression".into());
        }
        generation = frame.generation;
        if let Some(snapshot) = &frame.snapshot {
            if geometry.is_some_and(|count| count != snapshot.register_count()) {
                return Err("IPC geometry changed; restart required".into());
            }
            geometry = Some(snapshot.register_count());
        }
        previous = Some(frame.sequence);
        let sequence = frame.sequence;
        *lock(&model).map_err(|e| e.to_string())? = Some(Received::new(frame, credit_issued));
        // Anchor before granting the next credit, including a blocked ACK write.
        credit_issued = Instant::now();
        output
            .write_all(&sequence.to_le_bytes())
            .and_then(|()| output.flush())
            .map_err(|e| e.to_string())?;
    }
    registration.revoke().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use icem_opc_da_codec::Frame;
    use opc_da_bindings::client::DaClient;
    #[test]
    fn safe_com_client_reads_native_types_and_write_is_denied()
    -> std::result::Result<(), Box<dyn std::error::Error>> {
        let now = Instant::now();
        let snapshot = Arc::new(icem_bus::Snapshot::decode(include_bytes!(
            "../../../tests/fixtures/bus-v5-minimal.bin"
        ))?);
        let received = Received::new(
            Frame {
                sequence: 1,
                generation: 1,
                capture_age: Some(std::time::Duration::ZERO),
                completion_age: Some(std::time::Duration::ZERO),
                status: 1,
                snapshot: Some(snapshot),
            },
            now,
        );
        let model = Arc::new(Mutex::new(Some(received)));
        let apartment = ComApartment::mta()?;
        let server = DaServer::new(Service::new(model, Arc::new(Limits::new()))?);
        let client = DaClient::from_object(&apartment, &server.object())?;
        let group = client.add_group(GroupOptions::new("acceptance"))?;
        let items = group.add_items(&[
            ItemSpec::new("var0", 1),
            ItemSpec::new("var4", 2),
            ItemSpec::new("var10", 3),
            ItemSpec::new("var5", 4),
        ])?;
        assert!(items[3].is_err());
        let handles = items[..3]
            .iter()
            .map(|item| {
                item.as_ref()
                    .map(|i| i.server_handle)
                    .map_err(|e| e.to_string())
            })
            .collect::<std::result::Result<Vec<_>, _>>()?;
        let samples = group.read(DataSource::Cache, &handles)?;
        let samples = samples
            .iter()
            .map(|sample| sample.as_ref().map_err(|e| e.to_string()))
            .collect::<std::result::Result<Vec<_>, _>>()?;
        assert!(matches!(samples[0].value, Value::U16(7)));
        assert!(matches!(samples[1].value, Value::I32(1_700_000_000)));
        assert!(matches!(samples[2].value,Value::F32(v) if v.to_bits()==0x7fc01234));
        assert_eq!(samples[0].timestamp.ticks(), 0);
        assert!(samples[1].timestamp.ticks() > 0);
        let written = group.write(&[opc_da_bindings::client::WriteValue {
            server_handle: handles[0],
            value: Value::U16(1),
        }])?;
        assert!(written[0].is_err());
        group.close(false)?;
        Ok(())
    }
    #[test]
    fn validation_does_not_consume_capacity_and_device_ignores_item_activity()
    -> std::result::Result<(), Box<dyn std::error::Error>> {
        let snapshot = Arc::new(icem_bus::Snapshot::decode(include_bytes!(
            "../../../tests/fixtures/bus-v5-minimal.bin"
        ))?);
        let model = Arc::new(Mutex::new(Some(Received::new(
            Frame {
                sequence: 1,
                generation: 1,
                capture_age: Some(std::time::Duration::ZERO),
                completion_age: Some(std::time::Duration::ZERO),
                status: 1,
                snapshot: Some(snapshot),
            },
            Instant::now(),
        ))));
        let limits = Arc::new(Limits::new());
        let group = Group {
            _lease: lease(&limits.groups, MAX_GROUPS)?,
            model: model.clone(),
            items: Mutex::new(Items {
                entries: BTreeMap::new(),
                next: 1,
            }),
        };
        let items = vec![ItemSpec::new("var10", 1); MAX_ITEMS];
        assert!(group.add_items(&items)?.iter().all(|r| r.result.is_ok()));
        assert!(group.add_items(&items[..1])?[0].result.is_err());
        assert!(group.validate_items(&items[..1], false)?[0].result.is_ok());
        lock(&group.items)?.next = u32::MAX;
        assert!(group.validate_items(&items[..1], false)?[0].result.is_ok());
        assert_eq!(lock(&group.items)?.entries.len(), MAX_ITEMS);
        let handles = [ServerItemHandle::from_raw(1)];
        group.set_active(&handles, false)?;
        let cache = group.read(DataSource::Cache, &handles)?;
        let device = group.read(DataSource::Device, &handles)?;
        assert_eq!(cache[0].result.as_ref().map(|s| s.quality), Ok(0x1c));
        assert_eq!(device[0].result.as_ref().map(|s| s.quality), Ok(0xc0));
        Ok(())
    }
    #[test]
    fn global_group_leases_and_per_server_handles_are_bounded() -> Result<()> {
        let limits = Arc::new(Limits::new());
        let model = Arc::new(Mutex::new(None));
        let first = Service::new(model.clone(), limits.clone())?;
        let second = Service::new(model, limits.clone())?;
        let mut groups = Vec::new();
        groups.push(first.add_group(1, GroupOptions::new("first"))?);
        // Each COM identity has its own handle namespace, no cross-client collision.
        groups.push(second.add_group(1, GroupOptions::new("second"))?);
        for handle in 2..32 {
            groups.push(first.add_group(handle, GroupOptions::new(format!("g{handle}")))?);
        }
        assert!(second.add_group(2, GroupOptions::new("overflow")).is_err());
        first.remove_group(1, true)?;
        // A removed but client-held group still consumes the global resource budget.
        assert!(second.add_group(2, GroupOptions::new("held")).is_err());
        groups.remove(0);
        assert!(second.add_group(2, GroupOptions::new("released")).is_ok());
        Ok(())
    }
}
