//! iCem SDK extension: native DA callback boundary. See ADR0019.
use super::callback_health::{CallbackMonitor, WorkerLease};
use super::callback_queue::{DeliveryQueue, MAX_ITEMS, MAX_REQUESTS, MAX_SINKS};
use super::*;
use crate::{IOPCAsyncIO2_Impl, IOPCDataCallback};
use std::{
    collections::BTreeMap,
    sync::{
        Condvar,
        atomic::{AtomicBool, AtomicU64},
    },
    time::{Duration, Instant},
};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, CoCreateInstance, IConnectionPoint, IConnectionPoint_Impl,
    IConnectionPointContainer, IConnectionPointContainer_Impl, IEnumConnectionPoints,
    IEnumConnections, IGlobalInterfaceTable,
};
use windows_core::IUnknownImpl;
const NO_CONNECTION: HRESULT = HRESULT(0x80040200_u32.cast_signed());
const ADVISE_LIMIT: HRESULT = HRESULT(0x80040201_u32.cast_signed());
const CANNOT_CONNECT: HRESULT = HRESULT(0x80040202_u32.cast_signed());
const FAIL: HRESULT = HRESULT(0x80004005_u32.cast_signed());
const GIT_CLASS: GUID = GUID::from_u128(0x00000323_0000_0000_c000_000000000046);
#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Change,
    Refresh,
    Read,
    Cancel,
}
struct Frame {
    kind: Kind,
    transaction: u32,
    group: u32,
    samples: Vec<ServerSample>,
    revision: u64,
    epoch: u64,
}
struct Sink {
    queue: Mutex<DeliveryQueue<Arc<Frame>>>,
    wake: Condvar,
    closed: AtomicBool,
}
struct Members {
    sinks: BTreeMap<u32, Arc<Sink>>,
    requests: BTreeMap<u32, (Arc<Frame>, Vec<(u32, u32)>)>,
    next_cookie: u32,
    next_cancel: u32,
    closed: bool,
}
pub(crate) struct Callbacks {
    members: Mutex<Members>,
    enabled: AtomicBool,
    revision: AtomicU64,
    epoch: AtomicU64,
    monitor: CallbackMonitor,
}
fn error(code: HRESULT) -> AbiError {
    AbiError::from_hresult(code)
}
fn boundary<T>(f: impl FnOnce() -> AbiResult<T>) -> AbiResult<T> {
    serve(|| f().map_err(crate::abi::from_abi_error))
}
fn git() -> AbiResult<IGlobalInterfaceTable> {
    // SAFETY: standard GIT class, generated typed interface; caller initialized COM.
    unsafe { CoCreateInstance(&GIT_CLASS, None, CLSCTX_INPROC_SERVER) }
}
struct Registration<'a> {
    table: IGlobalInterfaceTable,
    cookie: u32,
    lease: &'a WorkerLease,
}
impl Drop for Registration<'_> {
    fn drop(&mut self) {
        self.lease.begin(Instant::now());
        // SAFETY: registration exclusively owned here; revocation runs in initialized apartment.
        if unsafe { self.table.RevokeInterfaceFromGlobal(self.cookie) }.is_err() {
            self.lease.fail();
        }
        self.lease.finish();
    }
}
struct Proxy<'a> {
    inner: Option<IOPCDataCallback>,
    lease: &'a WorkerLease,
}
impl Drop for Proxy<'_> {
    fn drop(&mut self) {
        self.lease.begin(Instant::now());
        drop(self.inner.take());
        self.lease.finish();
    }
}
impl Callbacks {
    pub(crate) fn new(monitor: CallbackMonitor) -> Arc<Self> {
        Arc::new(Self {
            members: Mutex::new(Members {
                sinks: BTreeMap::new(),
                requests: BTreeMap::new(),
                next_cookie: 1,
                next_cancel: 1,
                closed: false,
            }),
            enabled: AtomicBool::new(true),
            revision: AtomicU64::new(1),
            epoch: AtomicU64::new(1),
            monitor,
        })
    }
    pub(crate) fn invalidate(&self) {
        self.revision.fetch_add(1, Ordering::AcqRel);
    }
    pub(crate) fn close(&self) {
        if let Ok(mut m) = self.members.lock() {
            m.closed = true;
            for sink in m.sinks.values() {
                close_sink(sink);
            }
            m.sinks.clear();
            m.requests.clear();
        }
    }
    fn advise(
        self: &Arc<Self>,
        object: &IUnknown,
        service: Arc<dyn DaGroupService>,
        state: Arc<Mutex<GroupState>>,
    ) -> AbiResult<u32> {
        let lease = self.monitor.worker().ok_or_else(|| error(ADVISE_LIMIT))?;
        lease.begin(Instant::now());
        let callback: IOPCDataCallback = object.cast().map_err(|_| error(CANNOT_CONNECT))?;
        let table = git()?;
        // SAFETY: exact QI callback reference and IID in calling Advise apartment.
        let cookie = unsafe { table.RegisterInterfaceInGlobal(&callback, &IOPCDataCallback::IID) }?;
        // Release caller QI reference before worker startup: its Release is covered by busy lease.
        drop(callback);
        let result = self.spawn(cookie, lease.clone(), service, state);
        if result.is_err() {
            // SAFETY: spawn failure started no worker; caller still owns registration and busy lease.
            if unsafe { table.RevokeInterfaceFromGlobal(cookie) }.is_err() {
                lease.fail();
            }
        }
        result
    }
    fn spawn(
        self: &Arc<Self>,
        git_cookie: u32,
        lease: WorkerLease,
        service: Arc<dyn DaGroupService>,
        state: Arc<Mutex<GroupState>>,
    ) -> AbiResult<u32> {
        let mut m = self.members.lock().map_err(|_| error(E_UNEXPECTED))?;
        if m.closed || m.sinks.len() >= MAX_SINKS || m.next_cookie == u32::MAX {
            return Err(error(ADVISE_LIMIT));
        }
        let cookie = m.next_cookie;
        m.next_cookie += 1;
        let sink = Arc::new(Sink {
            queue: Mutex::new(DeliveryQueue::new()),
            wake: Condvar::new(),
            closed: AtomicBool::new(false),
        });
        let worker_sink = sink.clone();
        let owner = Arc::downgrade(self);
        std::thread::Builder::new()
            .name("opc-da-callback".into())
            .stack_size(256 * 1024)
            .spawn(move || {
                if std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    worker(git_cookie, &lease, &worker_sink, owner, service, state)
                }))
                .is_err()
                {
                    lease.fail();
                }
            })
            .map_err(|_| error(E_OUTOFMEMORY))?;
        m.sinks.insert(cookie, sink);
        Ok(cookie)
    }
    fn retire(&self, sink: &Sink) {
        if let Ok(mut m) = self.members.lock() {
            m.sinks
                .retain(|_, candidate| !std::ptr::eq(&**candidate, sink));
        }
    }
    fn unadvise(&self, cookie: u32) -> AbiResult<()> {
        let mut m = self.members.lock().map_err(|_| error(E_UNEXPECTED))?;
        let sink = m
            .sinks
            .remove(&cookie)
            .ok_or_else(|| error(NO_CONNECTION))?;
        close_sink(&sink);
        Ok(())
    }
    fn enable(&self, set: Option<bool>) -> AbiResult<bool> {
        let m = self.members.lock().map_err(|_| error(E_UNEXPECTED))?;
        if m.closed || m.sinks.is_empty() {
            return Err(error(NO_CONNECTION));
        }
        if let Some(value) = set {
            self.enabled.store(value, Ordering::Release);
            self.invalidate();
            if !value {
                for sink in m.sinks.values() {
                    sink.queue
                        .lock()
                        .map_err(|_| error(E_UNEXPECTED))?
                        .clear_latest();
                }
            }
        }
        Ok(self.enabled.load(Ordering::Acquire))
    }
    fn enqueue(&self, frame: Arc<Frame>) -> AbiResult<u32> {
        validate_frame(&frame.samples)?;
        let mut m = self.members.lock().map_err(|_| error(E_UNEXPECTED))?;
        if m.closed || m.sinks.is_empty() {
            return Err(error(NO_CONNECTION));
        }
        let live = m.sinks.clone();
        // Request/frame survives until ALL still-advised sinks have removed their completion.
        m.requests.retain(|_, (_, ids)| {
            ids.iter().any(|(cookie, id)| {
                live.get(cookie)
                    .is_some_and(|sink| sink.queue.lock().map(|q| q.pending(*id)).unwrap_or(true))
            })
        });
        if m.requests.len() >= MAX_REQUESTS || m.next_cancel == u32::MAX {
            return Err(error(ADVISE_LIMIT));
        }
        let sinks = m
            .sinks
            .iter()
            .map(|(c, s)| (*c, s.clone()))
            .collect::<Vec<_>>();
        let mut queues = Vec::with_capacity(sinks.len());
        for (_, sink) in &sinks {
            queues.push(sink.queue.lock().map_err(|_| error(E_UNEXPECTED))?);
        }
        let mut ids = Vec::with_capacity(sinks.len());
        for i in 0..queues.len() {
            match queues[i].reserve() {
                Ok(id) => ids.push((sinks[i].0, id)),
                Err(_) => {
                    for (q, (_, id)) in queues.iter_mut().zip(&ids) {
                        q.release(*id);
                    }
                    return Err(error(ADVISE_LIMIT));
                }
            }
        }
        if frame.kind == Kind::Refresh {
            self.epoch.fetch_add(1, Ordering::AcqRel);
            for q in &mut queues {
                q.clear_latest();
            }
        }
        for (q, (_, id)) in queues.iter_mut().zip(&ids) {
            q.complete(*id, frame.clone())
                .map_err(|_| error(E_UNEXPECTED))?;
        }
        let id = m.next_cancel;
        m.next_cancel += 1;
        m.requests.insert(id, (frame, ids));
        drop(queues);
        for (_, sink) in sinks {
            sink.wake.notify_one();
        }
        Ok(id)
    }
    fn cancel(&self, id: u32) -> AbiResult<()> {
        let mut m = self.members.lock().map_err(|_| error(E_UNEXPECTED))?;
        let (frame, ids) = m.requests.get(&id).ok_or_else(|| error(FAIL))?;
        let sinks = ids
            .iter()
            .filter_map(|(c, id)| m.sinks.get(c).map(|s| (s.clone(), *id)))
            .collect::<Vec<_>>();
        if sinks.is_empty() {
            return Err(error(FAIL));
        }
        let mut queues = Vec::with_capacity(sinks.len());
        for (s, _) in &sinks {
            queues.push(s.queue.lock().map_err(|_| error(E_UNEXPECTED))?);
        }
        if queues
            .iter()
            .zip(&sinks)
            .any(|(q, (_, id))| !q.cancellable(*id))
        {
            return Err(error(FAIL));
        }
        let frame = Arc::new(Frame {
            kind: Kind::Cancel,
            transaction: frame.transaction,
            group: frame.group,
            samples: Vec::new(),
            revision: 0,
            epoch: 0,
        });
        for (q, (_, id)) in queues.iter_mut().zip(&sinks) {
            q.cancel(*id, frame.clone())
                .map_err(|_| error(E_UNEXPECTED))?;
        }
        drop(queues);
        m.requests.remove(&id);
        Ok(())
    }
}
impl Drop for Callbacks {
    fn drop(&mut self) {
        self.close();
    }
}
fn close_sink(s: &Sink) {
    s.closed.store(true, Ordering::Release);
    if let Ok(mut q) = s.queue.lock() {
        q.close();
    }
    s.wake.notify_all();
}
fn worker(
    cookie: u32,
    lease: &WorkerLease,
    sink: &Sink,
    owner: std::sync::Weak<Callbacks>,
    service: Arc<dyn DaGroupService>,
    state: Arc<Mutex<GroupState>>,
) {
    let result = (|| -> AbiResult<()> {
        let _apartment = opc_classic_utils::ComApartment::mta().map_err(to_abi_error)?;
        let registration = Registration {
            table: git()?,
            cookie,
            lease,
        };
        let mut raw = ptr::null_mut();
        // SAFETY: registered exact callback IID, valid output slot, current MTA.
        unsafe {
            registration
                .table
                .GetInterfaceFromGlobal(cookie, &IOPCDataCallback::IID, &mut raw)
        }?;
        if raw.is_null() {
            return Err(error(E_UNEXPECTED));
        }
        // SAFETY: successful GIT output transfers one owned typed apartment-local reference.
        let proxy = Proxy {
            inner: Some(unsafe { IOPCDataCallback::from_raw(raw) }),
            lease,
        };
        lease.finish();
        let mut next = Instant::now();
        let mut last: Vec<ServerSample> = Vec::new();
        let mut version = 0;
        let mut last_group = None;
        while !sink.closed.load(Ordering::Acquire) {
            let frame = sink.queue.lock().map_err(|_| error(E_UNEXPECTED))?.pop();
            if let Some(frame) = frame {
                if frame.kind == Kind::Change
                    && owner.upgrade().is_none_or(|o| {
                        o.revision.load(Ordering::Acquire) != frame.revision
                            || o.epoch.load(Ordering::Acquire) != frame.epoch
                    })
                {
                    continue;
                }
                if frame.kind == Kind::Refresh {
                    last = frame.samples.clone();
                    last_group = Some(frame.group);
                }
                lease.begin(Instant::now());
                let called = invoke(
                    proxy.inner.as_ref().ok_or_else(|| error(E_UNEXPECTED))?,
                    &frame,
                );
                lease.finish();
                if called.is_err() {
                    close_sink(sink);
                    if let Some(o) = owner.upgrade() {
                        o.retire(sink);
                    }
                    break;
                }
                continue;
            }
            let Some(o) = owner.upgrade() else {
                break;
            };
            // Read revision before state: SetState increments it before unlocking state.
            let revision = o.revision.load(Ordering::Acquire);
            let group = state.lock().map_err(|_| error(E_UNEXPECTED))?.clone();
            if revision != version {
                last.clear();
                last_group = None;
                next = Instant::now();
                version = revision;
            }
            let now = Instant::now();
            if !group.active || !o.enabled.load(Ordering::Acquire) {
                next = now + Duration::from_millis(10);
                last.clear();
                last_group = None;
            } else if now >= next {
                let epoch = o.epoch.load(Ordering::Acquire);
                lease.begin(Instant::now());
                let samples = service
                    .notification(group.clone(), DataSource::Cache)
                    .map_err(to_abi_error);
                lease.finish();
                let samples = samples?;
                if samples.len() > MAX_ITEMS {
                    return Err(error(E_OUTOFMEMORY));
                }
                let samples = samples
                    .into_iter()
                    .filter_map(|s| s.result.ok())
                    .collect::<Vec<_>>();
                validate_frame(&samples)?;
                let changed = samples
                    .iter()
                    .enumerate()
                    .filter(|(i, s)| {
                        last_group != Some(group.client_handle)
                            || last.get(*i).is_none_or(|p| !same_sample(p, s))
                    })
                    .map(|(_, s)| s.clone())
                    .collect::<Vec<_>>();
                last = samples;
                last_group = Some(group.client_handle);
                next = now + Duration::from_millis(u64::from(group.update_rate.max(10)));
                if !changed.is_empty() {
                    let mut q = sink.queue.lock().map_err(|_| error(E_UNEXPECTED))?;
                    let _ = q.latest(Arc::new(Frame {
                        kind: Kind::Change,
                        transaction: 0,
                        group: group.client_handle,
                        samples: changed,
                        revision,
                        epoch,
                    }));
                    continue;
                }
            }
            drop(o);
            let q = sink.queue.lock().map_err(|_| error(E_UNEXPECTED))?;
            let _wait = sink
                .wake
                .wait_timeout(
                    q,
                    next.saturating_duration_since(Instant::now())
                        .min(Duration::from_millis(10))
                        .max(Duration::from_millis(1)),
                )
                .map_err(|_| error(E_UNEXPECTED))?;
        }
        Ok(())
    })();
    if result.is_err() {
        lease.fail();
    }
}
fn same_sample(a: &ServerSample, b: &ServerSample) -> bool {
    a.client_handle == b.client_handle
        && a.quality == b.quality
        && match (&a.value, &b.value) {
            (Value::F32(a), Value::F32(b)) => a.to_bits() == b.to_bits(),
            (Value::F64(a), Value::F64(b)) | (Value::Date(a), Value::Date(b)) => {
                a.to_bits() == b.to_bits()
            }
            _ => a.value == b.value,
        }
}
fn validate_frame(samples: &[ServerSample]) -> AbiResult<()> {
    if samples.len() > MAX_ITEMS {
        return Err(error(E_OUTOFMEMORY));
    }
    let mut bytes = samples
        .len()
        .saturating_mul(std::mem::size_of::<ServerSample>());
    for s in samples {
        bytes = bytes.saturating_add(match &s.value {
            Value::String(s) => s.len(),
            Value::Bytes(_) | Value::Array(_) => return Err(error(E_INVALIDARG)),
            _ => 0,
        });
        if bytes > 512 * 1024 {
            return Err(error(E_OUTOFMEMORY));
        }
    }
    Ok(())
}
fn invoke(callback: &IOPCDataCallback, frame: &Frame) -> AbiResult<()> {
    if frame.kind == Kind::Cancel {
        // SAFETY: current apartment proxy; owned scalar arguments.
        return unsafe { callback.OnCancelComplete(frame.transaction, frame.group) };
    }
    let values = frame
        .samples
        .iter()
        .map(|s| value_to_abi(&s.value))
        .collect::<Result<Vec<_>>>()
        .map_err(to_abi_error)?;
    let handles = frame
        .samples
        .iter()
        .map(|s| s.client_handle.0)
        .collect::<Vec<_>>();
    let qualities = frame.samples.iter().map(|s| s.quality).collect::<Vec<_>>();
    let times = frame
        .samples
        .iter()
        .map(|s| timestamp_to_abi(s.timestamp))
        .collect::<Vec<_>>();
    let errors = vec![HRESULT(0); values.len()];
    let quality = if qualities.iter().all(|q| q & 0xc0 == 0xc0) {
        HRESULT(0)
    } else {
        HRESULT(1)
    };
    // SAFETY: equal bounded parallel arrays/RAII VARIANTs own storage throughout foreign call.
    unsafe {
        match frame.kind {
            Kind::Change | Kind::Refresh => callback.OnDataChange(
                frame.transaction,
                frame.group,
                quality,
                HRESULT(0),
                values.len() as u32,
                handles.as_ptr(),
                values.as_ptr(),
                qualities.as_ptr(),
                times.as_ptr(),
                errors.as_ptr(),
            ),
            Kind::Read => callback.OnReadComplete(
                frame.transaction,
                frame.group,
                quality,
                HRESULT(0),
                values.len() as u32,
                handles.as_ptr(),
                values.as_ptr(),
                qualities.as_ptr(),
                times.as_ptr(),
                errors.as_ptr(),
            ),
            Kind::Cancel => Ok(()),
        }
    }
}
impl IConnectionPointContainer_Impl for DaGroupAdapter_Impl {
    fn EnumConnectionPoints(&self) -> AbiResult<IEnumConnectionPoints> {
        Err(error(E_NOTIMPL))
    }
    fn FindConnectionPoint(&self, iid: *const GUID) -> AbiResult<IConnectionPoint> {
        boundary(|| {
            // SAFETY: COM requires readable IID when nonnull; null rejected.
            if unsafe { iid.as_ref() } != Some(&IOPCDataCallback::IID) {
                return Err(error(NO_CONNECTION));
            }
            Ok(self.to_interface())
        })
    }
}
impl IConnectionPoint_Impl for DaGroupAdapter_Impl {
    fn GetConnectionInterface(&self) -> AbiResult<GUID> {
        Ok(IOPCDataCallback::IID)
    }
    fn GetConnectionPointContainer(&self) -> AbiResult<IConnectionPointContainer> {
        Ok(self.to_interface())
    }
    fn Advise(&self, sink: windows_core::Ref<IUnknown>) -> AbiResult<u32> {
        boundary(|| {
            self.callbacks.advise(
                sink.as_ref().ok_or_else(|| error(E_POINTER))?,
                self.service.clone(),
                self.state.clone(),
            )
        })
    }
    fn Unadvise(&self, cookie: u32) -> AbiResult<()> {
        boundary(|| self.callbacks.unadvise(cookie))
    }
    fn EnumConnections(&self) -> AbiResult<IEnumConnections> {
        Err(error(E_NOTIMPL))
    }
}
#[allow(clippy::not_unsafe_ptr_arg_deref)]
impl IOPCAsyncIO2_Impl for DaGroupAdapter_Impl {
    fn Read(
        &self,
        count: u32,
        handles: *const u32,
        transaction: u32,
        cancel: *mut u32,
        errors: *mut *mut HRESULT,
    ) -> AbiResult<()> {
        serve(|| {
            // SAFETY: validate and clear required outputs before service/queue mutation.
            unsafe {
                initialize_output(cancel)?;
                initialize_output(errors)?;
            }
            if count == 0 || count as usize > MAX_ITEMS {
                return Err(code_error(E_INVALIDARG));
            }
            // SAFETY: incoming array readable for bounded count.
            let handles = decode_handles(unsafe { borrow_input(handles, count)? });
            self.callbacks
                .enable(None)
                .map_err(crate::abi::from_abi_error)?;
            let state = self
                .state
                .lock()
                .map_err(|_| code_error(E_UNEXPECTED))?
                .clone();
            let samples = self
                .service
                .read(state.clone(), DataSource::Device, &handles)?;
            ensure_batch_len(samples.len(), handles.len())?;
            let mut out = CoTaskMemArrayBuilder::new(samples.len(), NoCleanup)?;
            let mut valid = Vec::with_capacity(samples.len());
            let mut failed = false;
            for s in samples {
                match s.result {
                    Ok(sample) => {
                        out.push(HRESULT(0)).map_err(|_| code_error(E_UNEXPECTED))?;
                        valid.push(sample);
                    }
                    Err(code) => {
                        failed = true;
                        out.push(error_code_to_abi(code))
                            .map_err(|_| code_error(E_UNEXPECTED))?;
                    }
                }
            }
            let out = out.finish()?;
            let id = if valid.is_empty() {
                0
            } else {
                self.callbacks
                    .enqueue(Arc::new(Frame {
                        kind: Kind::Read,
                        transaction,
                        group: state.client_handle,
                        samples: valid,
                        revision: 0,
                        epoch: 0,
                    }))
                    .map_err(crate::abi::from_abi_error)?
            };
            // SAFETY: outputs initialized above, allocation transfers only after successful queue commit.
            unsafe {
                cancel.write(id);
                errors.write(out.into_raw_parts().0);
            }
            batch_status(failed)
        })
    }
    fn Write(
        &self,
        count: u32,
        _handles: *const u32,
        _values: *const VARIANT,
        _transaction: u32,
        cancel: *mut u32,
        errors: *mut *mut HRESULT,
    ) -> AbiResult<()> {
        serve(|| {
            // SAFETY: required outputs initialized; read-only denial need not read payload pointers.
            unsafe {
                initialize_output(cancel)?;
                initialize_output(errors)?;
            }
            if count == 0 || count as usize > MAX_ITEMS {
                return Err(code_error(E_INVALIDARG));
            }
            self.callbacks
                .enable(None)
                .map_err(crate::abi::from_abi_error)?;
            let mut out = CoTaskMemArrayBuilder::new(count as usize, NoCleanup)?;
            for _ in 0..count {
                out.push(HRESULT(0xc0040006_u32.cast_signed()))
                    .map_err(|_| code_error(E_UNEXPECTED))?;
            }
            // SAFETY: owned all-failed output transfers once; cancel stays0 and no completion exists.
            unsafe {
                errors.write(out.finish()?.into_raw_parts().0);
            }
            batch_status(true)
        })
    }
    fn Refresh2(&self, source: tagOPCDATASOURCE, transaction: u32) -> AbiResult<u32> {
        boundary(|| {
            let source = if source == crate::OPC_DS_CACHE {
                DataSource::Cache
            } else if source == crate::OPC_DS_DEVICE {
                DataSource::Device
            } else {
                return Err(error(E_INVALIDARG));
            };
            self.callbacks.enable(None)?;
            let state = self.state.lock().map_err(|_| error(E_UNEXPECTED))?.clone();
            if !state.active {
                return Err(error(FAIL));
            }
            let samples = self
                .service
                .notification(state.clone(), source)
                .map_err(to_abi_error)?;
            if samples.len() > MAX_ITEMS {
                return Err(error(E_OUTOFMEMORY));
            }
            let samples = samples
                .into_iter()
                .filter_map(|s| s.result.ok())
                .collect::<Vec<_>>();
            if samples.is_empty() {
                return Err(error(FAIL));
            }
            self.callbacks.enqueue(Arc::new(Frame {
                kind: Kind::Refresh,
                transaction,
                group: state.client_handle,
                samples,
                revision: 0,
                epoch: 0,
            }))
        })
    }
    fn Cancel2(&self, id: u32) -> AbiResult<()> {
        boundary(|| self.callbacks.cancel(id))
    }
    fn SetEnable(&self, value: windows_core::BOOL) -> AbiResult<()> {
        boundary(|| self.callbacks.enable(Some(value.as_bool())).map(|_| ()))
    }
    fn GetEnable(&self) -> AbiResult<windows_core::BOOL> {
        boundary(|| self.callbacks.enable(None).map(Into::into))
    }
}
