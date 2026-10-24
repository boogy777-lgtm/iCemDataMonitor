//! iCem SDK extension: owned events and apartment-bound subscriptions (ADR0019).
use super::{ClientItemHandle, DaGroup, DataSource, GroupState, ItemError, ServerItemHandle};
use crate::abi::{from_abi_error, timestamp_from_abi, to_abi_error, value_from_abi};
use crate::{IOPCAsyncIO2, IOPCDataCallback, IOPCDataCallback_Impl, IOPCGroupStateMgt};
use opc_classic_types::{Error, ErrorCode, Result, Timestamp, Value};
use opc_classic_utils::{
    CoTaskMemArrayOut, ComApartment, NoCleanup,
    server::{borrow_input, catch_ffi},
};
use std::{marker::PhantomData, sync::Arc};
use windows::Win32::{
    Foundation::FILETIME,
    System::{
        Com::{IConnectionPoint, IConnectionPointContainer},
        Variant::VARIANT,
    },
};
use windows_core::{HRESULT, Interface, Result as AbiResult};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DataEventKind {
    DataChange,
    ReadComplete,
    WriteComplete,
    CancelComplete,
}
#[derive(Clone, Debug)]
pub struct DataEventItem {
    pub client_handle: ClientItemHandle,
    pub value: Value,
    pub quality: u16,
    pub timestamp: Timestamp,
    pub error: ErrorCode,
}
#[derive(Clone, Debug)]
pub struct DataEvent {
    pub kind: DataEventKind,
    pub transaction: u32,
    pub group_handle: u32,
    pub master_quality: ErrorCode,
    pub master_error: ErrorCode,
    pub items: Vec<DataEventItem>,
}
pub struct DataSubscription<'group, 'apartment> {
    point: IConnectionPoint,
    cookie: Option<u32>,
    registrations: Vec<GitRegistration>,
    _group: PhantomData<&'group DaGroup<'apartment>>,
    _apartment: PhantomData<&'apartment ComApartment>,
}
impl DataSubscription<'_, '_> {
    pub fn cookie(&self) -> Option<u32> {
        self.cookie
    }
    pub fn close(&mut self) -> Result<()> {
        if let Some(cookie) = self.cookie {
            // SAFETY: apartment-local point and owned subscription cookie.
            unsafe { self.point.Unadvise(cookie) }.map_err(from_abi_error)?;
            self.cookie = None;
        }
        self.registrations.clear();
        Ok(())
    }
}
impl Drop for DataSubscription<'_, '_> {
    fn drop(&mut self) {
        let _ = self.close();
    }
}
impl<'apartment> DaGroup<'apartment> {
    pub fn subscribe<'group>(
        &'group self,
        handler: Arc<dyn Fn(DataEvent) + Send + Sync>,
    ) -> Result<DataSubscription<'group, 'apartment>> {
        self.subscribe_result(Arc::new(move |event| {
            handler(event);
            Ok(())
        }))
    }
    fn subscribe_result<'group>(
        &'group self,
        handler: Arc<dyn Fn(DataEvent) -> Result<()> + Send + Sync>,
    ) -> Result<DataSubscription<'group, 'apartment>> {
        let container: IConnectionPointContainer = self.interface()?;
        // SAFETY: exact outgoing IID, apartment-local container.
        let point = unsafe { container.FindConnectionPoint(&IOPCDataCallback::IID) }
            .map_err(from_abi_error)?;
        let callback: IOPCDataCallback = Callback { handler }.into();
        // SAFETY: live typed callback; Advise retains its reference.
        let cookie = unsafe { point.Advise(&callback) }.map_err(from_abi_error)?;
        Ok(DataSubscription {
            point,
            cookie: Some(cookie),
            registrations: Vec::new(),
            _group: PhantomData,
            _apartment: PhantomData,
        })
    }
    pub fn unadvise(&self, cookie: u32) -> Result<()> {
        let container: IConnectionPointContainer = self.interface()?;
        // SAFETY: exact outgoing IID, apartment-local container.
        let point = unsafe { container.FindConnectionPoint(&IOPCDataCallback::IID) }
            .map_err(from_abi_error)?;
        // SAFETY: scalar cookie validated by server.
        unsafe { point.Unadvise(cookie) }.map_err(from_abi_error)
    }
    pub fn set_state(&self, state: &GroupState) -> Result<u32> {
        let group: IOPCGroupStateMgt = self.interface()?;
        let mut revised = 0;
        let active = state.active.into();
        // SAFETY: owned scalar input/output slots valid throughout call.
        unsafe {
            group.SetState(
                &state.update_rate,
                &mut revised,
                &active,
                &state.time_bias,
                &state.percent_deadband,
                &state.locale,
                &state.client_handle,
            )
        }
        .map_err(from_abi_error)?;
        Ok(revised)
    }
    pub fn async_read(
        &self,
        handles: &[ServerItemHandle],
        transaction: u32,
    ) -> Result<(u32, Vec<std::result::Result<(), ItemError>>)> {
        validate_count(handles.len())?;
        let interface: IOPCAsyncIO2 = self.interface()?;
        let raw = handles.iter().map(|h| h.raw()).collect::<Vec<_>>();
        let mut cancel = 0;
        let mut errors = CoTaskMemArrayOut::new(handles.len(), NoCleanup);
        // SAFETY: bounded owned input, valid task-allocation outputs.
        let call = unsafe {
            interface.Read(
                raw.len() as u32,
                raw.as_ptr(),
                transaction,
                &mut cancel,
                errors.as_mut_ptr(),
            )
        };
        // SAFETY: returned task allocation adopted once.
        let errors = unsafe { errors.into_array() };
        call.map_err(from_abi_error)?;
        Ok((cancel, decode_errors(errors?.as_slice())))
    }
    pub fn async_write(
        &self,
        values: &[super::WriteValue],
        transaction: u32,
    ) -> Result<(u32, Vec<std::result::Result<(), ItemError>>)> {
        validate_count(values.len())?;
        let interface: IOPCAsyncIO2 = self.interface()?;
        let handles = values
            .iter()
            .map(|v| v.server_handle.raw())
            .collect::<Vec<_>>();
        let payload = values
            .iter()
            .map(|v| crate::abi::value_to_abi(&v.value))
            .collect::<Result<Vec<_>>>()?;
        let mut cancel = 0;
        let mut errors = CoTaskMemArrayOut::new(values.len(), NoCleanup);
        // SAFETY: bounded parallel owned VARIANT/handle arrays, valid output slots.
        let call = unsafe {
            interface.Write(
                values.len() as u32,
                handles.as_ptr(),
                payload.as_ptr(),
                transaction,
                &mut cancel,
                errors.as_mut_ptr(),
            )
        };
        // SAFETY: returned task allocation ownership transferred once.
        let errors = unsafe { errors.into_array() };
        call.map_err(from_abi_error)?;
        Ok((cancel, decode_errors(errors?.as_slice())))
    }
    pub fn set_items_client_handles(
        &self,
        pairs: &[(ServerItemHandle, ClientItemHandle)],
    ) -> Result<Vec<std::result::Result<(), ItemError>>> {
        validate_count(pairs.len())?;
        let management: crate::IOPCItemMgt = self.interface()?;
        let handles = pairs.iter().map(|(h, _)| h.raw()).collect::<Vec<_>>();
        let clients = pairs.iter().map(|(_, c)| c.0).collect::<Vec<_>>();
        let mut errors = CoTaskMemArrayOut::new(pairs.len(), NoCleanup);
        // SAFETY: equal bounded owned arrays, valid output slot.
        let call = unsafe {
            management.SetClientHandles(
                pairs.len() as u32,
                handles.as_ptr(),
                clients.as_ptr(),
                errors.as_mut_ptr(),
            )
        };
        // SAFETY: returned task allocation adopted once.
        let errors = unsafe { errors.into_array() };
        call.map_err(from_abi_error)?;
        Ok(decode_errors(errors?.as_slice()))
    }
    pub fn refresh(&self, source: DataSource, transaction: u32) -> Result<u32> {
        let interface: IOPCAsyncIO2 = self.interface()?;
        let source = match source {
            DataSource::Cache => crate::OPC_DS_CACHE,
            DataSource::Device => crate::OPC_DS_DEVICE,
        };
        // SAFETY: typed enum and scalar arguments on apartment-local interface.
        unsafe { interface.Refresh2(source, transaction) }.map_err(from_abi_error)
    }
    pub fn cancel(&self, id: u32) -> Result<()> {
        let interface: IOPCAsyncIO2 = self.interface()?;
        // SAFETY: scalar ID on apartment-local typed interface.
        unsafe { interface.Cancel2(id) }.map_err(from_abi_error)
    }
    pub fn set_enable(&self, enabled: bool) -> Result<()> {
        let interface: IOPCAsyncIO2 = self.interface()?;
        // SAFETY: typed BOOL on apartment-local interface.
        unsafe { interface.SetEnable(enabled) }.map_err(from_abi_error)
    }
    pub fn enabled(&self) -> Result<bool> {
        let interface: IOPCAsyncIO2 = self.interface()?;
        // SAFETY: generated scalar output wrapper on apartment-local interface.
        unsafe { interface.GetEnable() }
            .map(|b| b.as_bool())
            .map_err(from_abi_error)
    }
}
fn validate_count(count: usize) -> Result<()> {
    if count == 0 || count > 4096 {
        Err(Error::invalid_argument("batch must contain1..4096 items"))
    } else {
        Ok(())
    }
}
fn decode_errors(errors: &[HRESULT]) -> Vec<std::result::Result<(), ItemError>> {
    errors
        .iter()
        .map(|e| {
            if e.is_ok() {
                Ok(())
            } else {
                Err(ItemError {
                    code: ErrorCode::from_raw(e.0),
                })
            }
        })
        .collect()
}
const MAX_EVENT_BYTES: usize = 512 * 1024;
fn validate_callback_value(value: &VARIANT, bytes: &mut usize) -> Result<()> {
    // SAFETY: callback supplies a valid borrowed VARIANT; inspect the BSTR arm only
    // for its exact tag. Its UTF-16 storage is borrowed, never cloned or transferred.
    let inner = unsafe { &value.Anonymous.Anonymous };
    if inner.vt == windows::Win32::System::Variant::VT_BSTR {
        let text = unsafe { &inner.Anonymous.bstrVal };
        // No valid UTF-8 conversion can be shorter than its UTF-16 unit count.
        if text.len() > MAX_EVENT_BYTES.saturating_sub(*bytes) {
            return Err(Error::invalid_argument("callback exceeds512KiB"));
        }
        for character in char::decode_utf16(text.iter().copied()) {
            let character =
                character.map_err(|_| Error::invalid_argument("invalid UTF-16 callback"))?;
            *bytes = bytes.saturating_add(character.len_utf8());
            if *bytes > MAX_EVENT_BYTES {
                return Err(Error::invalid_argument("callback exceeds512KiB"));
            }
        }
    } else if *bytes > MAX_EVENT_BYTES {
        return Err(Error::invalid_argument("callback exceeds512KiB"));
    }
    Ok(())
}
#[windows_core::implement(IOPCDataCallback)]
struct Callback {
    handler: Arc<dyn Fn(DataEvent) -> Result<()> + Send + Sync>,
}
impl Callback_Impl {
    #[allow(clippy::too_many_arguments)]
    fn receive(
        &self,
        kind: DataEventKind,
        t: u32,
        g: u32,
        q: HRESULT,
        e: HRESULT,
        n: u32,
        h: *const u32,
        v: *const VARIANT,
        qs: *const u16,
        ts: *const FILETIME,
        es: *const HRESULT,
    ) -> AbiResult<()> {
        catch_ffi(|| {
            if n > 4096 {
                return Err(Error::invalid_argument("callback exceeds4096"));
            }
            // SAFETY: COM parallel arrays readable for bounded count, copied before user handler.
            let (h, v, qs, ts, es) = unsafe {
                (
                    borrow_input(h, n)?,
                    borrow_input(v, n)?,
                    borrow_input(qs, n)?,
                    borrow_input(ts, n)?,
                    borrow_input(es, n)?,
                )
            };
            let mut bytes = std::mem::size_of::<DataEvent>()
                .saturating_add((n as usize).saturating_mul(std::mem::size_of::<DataEventItem>()));
            // Validate the complete payload before allocating any owned strings.
            for i in 0..n as usize {
                if es[i].is_ok() {
                    validate_callback_value(&v[i], &mut bytes)?;
                }
            }
            let mut items = Vec::with_capacity(n as usize);
            for i in 0..n as usize {
                items.push(DataEventItem {
                    client_handle: ClientItemHandle(h[i]),
                    value: if es[i].is_err() {
                        Value::Empty
                    } else {
                        value_from_abi(&v[i])?
                    },
                    quality: qs[i],
                    timestamp: timestamp_from_abi(ts[i]),
                    error: ErrorCode::from_raw(es[i].0),
                });
            }
            (self.handler)(DataEvent {
                kind,
                transaction: t,
                group_handle: g,
                master_quality: ErrorCode::from_raw(q.0),
                master_error: ErrorCode::from_raw(e.0),
                items,
            })
        })
        .map_err(to_abi_error)
    }
}
impl IOPCDataCallback_Impl for Callback_Impl {
    fn OnDataChange(
        &self,
        t: u32,
        g: u32,
        q: HRESULT,
        e: HRESULT,
        n: u32,
        h: *const u32,
        v: *const VARIANT,
        qs: *const u16,
        ts: *const FILETIME,
        es: *const HRESULT,
    ) -> AbiResult<()> {
        self.receive(DataEventKind::DataChange, t, g, q, e, n, h, v, qs, ts, es)
    }
    fn OnReadComplete(
        &self,
        t: u32,
        g: u32,
        q: HRESULT,
        e: HRESULT,
        n: u32,
        h: *const u32,
        v: *const VARIANT,
        qs: *const u16,
        ts: *const FILETIME,
        es: *const HRESULT,
    ) -> AbiResult<()> {
        self.receive(DataEventKind::ReadComplete, t, g, q, e, n, h, v, qs, ts, es)
    }
    fn OnWriteComplete(
        &self,
        t: u32,
        g: u32,
        e: HRESULT,
        n: u32,
        h: *const u32,
        es: *const HRESULT,
    ) -> AbiResult<()> {
        catch_ffi(|| {
            if n > 4096 {
                return Err(Error::invalid_argument("callback exceeds4096"));
            }
            // SAFETY: bounded parallel inputs copied to owned event before handler.
            let (h, es) = unsafe { (borrow_input(h, n)?, borrow_input(es, n)?) };
            let items = h
                .iter()
                .zip(es)
                .map(|(h, e)| DataEventItem {
                    client_handle: ClientItemHandle(*h),
                    value: Value::Empty,
                    quality: 0,
                    timestamp: Timestamp::from_ticks(0),
                    error: ErrorCode::from_raw(e.0),
                })
                .collect();
            (self.handler)(DataEvent {
                kind: DataEventKind::WriteComplete,
                transaction: t,
                group_handle: g,
                master_quality: ErrorCode::OK,
                master_error: ErrorCode::from_raw(e.0),
                items,
            })
        })
        .map_err(to_abi_error)
    }
    fn OnCancelComplete(&self, t: u32, g: u32) -> AbiResult<()> {
        catch_ffi(|| {
            (self.handler)(DataEvent {
                kind: DataEventKind::CancelComplete,
                transaction: t,
                group_handle: g,
                master_quality: ErrorCode::OK,
                master_error: ErrorCode::OK,
                items: Vec::new(),
            })
        })
        .map_err(to_abi_error)
    }
}
const GIT_CLASS: windows_core::GUID =
    windows_core::GUID::from_u128(0x00000323_0000_0000_c000_000000000046);
fn git() -> AbiResult<windows::Win32::System::Com::IGlobalInterfaceTable> {
    // SAFETY: standard GIT class, generated interface, current COM apartment initialized.
    unsafe {
        windows::Win32::System::Com::CoCreateInstance(
            &GIT_CLASS,
            None,
            windows::Win32::System::Com::CLSCTX_INPROC_SERVER,
        )
    }
}
struct GitRegistration {
    table: windows::Win32::System::Com::IGlobalInterfaceTable,
    cookie: u32,
}
impl Drop for GitRegistration {
    fn drop(&mut self) {
        // SAFETY: owned registration revoked in subscription originating apartment.
        let _ = unsafe { self.table.RevokeInterfaceFromGlobal(self.cookie) };
    }
}
fn retrieve<T: Interface>(cookie: u32) -> AbiResult<T> {
    let table = git()?;
    let mut raw = std::ptr::null_mut();
    // SAFETY: exact registered IID, valid initialized output slot.
    unsafe { table.GetInterfaceFromGlobal(cookie, &T::IID, &mut raw) }?;
    if raw.is_null() {
        return Err(windows_core::Error::from_hresult(
            windows::Win32::Foundation::E_POINTER,
        ));
    }
    // SAFETY: successful typed GIT retrieval transfers one reference for current apartment.
    Ok(unsafe { T::from_raw(raw) })
}
/// Borrowed callback-apartment command facade; no accessor exports native proxies.
pub struct CallbackGroup<'event> {
    inner: DaGroup<'event>,
}
impl CallbackGroup<'_> {
    pub fn state(&self) -> Result<GroupState> {
        self.inner.state()
    }
    pub fn set_state(&self, state: &GroupState) -> Result<u32> {
        self.inner.set_state(state)
    }
    pub fn set_enable(&self, enabled: bool) -> Result<()> {
        self.inner.set_enable(enabled)
    }
    pub fn unadvise(&self, cookie: u32) -> Result<()> {
        self.inner.unadvise(cookie)
    }
    pub fn remove_group(&self, force: bool) -> Result<()> {
        let (_, server, handle) = self.inner.callback_objects()?;
        // SAFETY: current callback apartment proxy and scalar owned handle.
        unsafe { server.RemoveGroup(handle, force) }.map_err(from_abi_error)
    }
}
fn reentrant_event(
    event: DataEvent,
    group_cookie: u32,
    server_cookie: u32,
    handle: u32,
    handler: &(dyn Fn(DataEvent, &CallbackGroup<'_>) + Send + Sync),
) -> Result<()> {
    let object = retrieve::<windows_core::IUnknown>(group_cookie).map_err(from_abi_error)?;
    let server = retrieve::<crate::IOPCServer>(server_cookie).map_err(from_abi_error)?;
    let inner = DaGroup::new(server, object, handle, false, 0);
    handler(event, &CallbackGroup { inner });
    Ok(())
}
impl<'apartment> DaGroup<'apartment> {
    pub fn subscribe_reentrant<'group>(
        &'group self,
        handler: Arc<dyn Fn(DataEvent, &CallbackGroup<'_>) + Send + Sync>,
    ) -> Result<DataSubscription<'group, 'apartment>> {
        let (object, server, handle) = self.callback_objects()?;
        let table = git().map_err(from_abi_error)?;
        // SAFETY: live group identity/exact IID in subscription caller apartment.
        let group_cookie =
            unsafe { table.RegisterInterfaceInGlobal(object, &windows_core::IUnknown::IID) }
                .map_err(from_abi_error)?;
        let group_registration = GitRegistration {
            table: table.clone(),
            cookie: group_cookie,
        };
        // SAFETY: live server interface/exact IID in subscription caller apartment.
        let server_cookie =
            unsafe { table.RegisterInterfaceInGlobal(server, &crate::IOPCServer::IID) }
                .map_err(from_abi_error)?;
        let server_registration = GitRegistration {
            table,
            cookie: server_cookie,
        };
        let mut subscription = self.subscribe_result(Arc::new(move |event| {
            reentrant_event(event, group_cookie, server_cookie, handle, &*handler)
        }))?;
        subscription.registrations.push(group_registration);
        subscription.registrations.push(server_registration);
        Ok(subscription)
    }
}

#[cfg(test)]
#[forbid(unsafe_code)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use windows::Win32::System::Com::{IConnectionPoint_Impl, IEnumConnections};
    use windows_core::{GUID, IUnknown, Ref};

    #[windows_core::implement(IConnectionPoint)]
    struct RetryPoint {
        attempts: Arc<AtomicUsize>,
    }
    impl IConnectionPoint_Impl for RetryPoint_Impl {
        fn GetConnectionInterface(&self) -> AbiResult<GUID> {
            Ok(IOPCDataCallback::IID)
        }
        fn GetConnectionPointContainer(&self) -> AbiResult<IConnectionPointContainer> {
            Err(windows_core::Error::from_hresult(
                windows::Win32::Foundation::E_NOTIMPL,
            ))
        }
        fn Advise(&self, _sink: Ref<IUnknown>) -> AbiResult<u32> {
            Ok(17)
        }
        fn Unadvise(&self, cookie: u32) -> AbiResult<()> {
            assert_eq!(cookie, 17);
            if self.attempts.fetch_add(1, Ordering::SeqCst) == 0 {
                Err(windows_core::Error::from_hresult(
                    windows::Win32::Foundation::E_FAIL,
                ))
            } else {
                Ok(())
            }
        }
        fn EnumConnections(&self) -> AbiResult<IEnumConnections> {
            Err(windows_core::Error::from_hresult(
                windows::Win32::Foundation::E_NOTIMPL,
            ))
        }
    }
    #[test]
    fn failed_unadvise_keeps_cookie_for_retry() -> Result<()> {
        let _apartment = ComApartment::mta()?;
        let attempts = Arc::new(AtomicUsize::new(0));
        let point = RetryPoint {
            attempts: attempts.clone(),
        }
        .into();
        let mut subscription = DataSubscription {
            point,
            cookie: Some(17),
            registrations: Vec::new(),
            _group: PhantomData,
            _apartment: PhantomData,
        };
        assert!(subscription.close().is_err());
        assert_eq!(subscription.cookie(), Some(17));
        subscription.close()?;
        assert_eq!(subscription.cookie(), None);
        subscription.close()?;
        assert_eq!(attempts.load(Ordering::SeqCst), 2);
        Ok(())
    }
    #[test]
    fn reentrant_git_failure_returns_error_instead_of_success_ack() -> Result<()> {
        let _apartment = ComApartment::mta()?;
        let calls = Arc::new(AtomicUsize::new(0));
        let handler_calls = calls.clone();
        let callback = windows_core::ComObject::new(Callback {
            handler: Arc::new(move |event| {
                reentrant_event(event, u32::MAX, u32::MAX, 1, &|_, _| {
                    handler_calls.fetch_add(1, Ordering::SeqCst);
                })
            }),
        });
        assert!(callback.OnCancelComplete(7, 1).is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        Ok(())
    }
    #[test]
    fn callback_bounds_aggregate_strings_before_handler() -> Result<()> {
        let _apartment = ComApartment::mta()?;
        let calls = Arc::new(AtomicUsize::new(0));
        let handler_calls = calls.clone();
        let callback = windows_core::ComObject::new(Callback {
            handler: Arc::new(move |_| {
                handler_calls.fetch_add(1, Ordering::SeqCst);
                Ok(())
            }),
        });
        let handles = [1, 2];
        let values = [
            VARIANT::from("a".repeat(270_000).as_str()),
            VARIANT::from("b".repeat(270_000).as_str()),
        ];
        let qualities = [0xc0, 0xc0];
        let times = [FILETIME::default(), FILETIME::default()];
        let errors = [HRESULT(0), HRESULT(0)];
        assert!(
            callback
                .OnDataChange(
                    0,
                    1,
                    HRESULT(0),
                    HRESULT(0),
                    2,
                    handles.as_ptr(),
                    values.as_ptr(),
                    qualities.as_ptr(),
                    times.as_ptr(),
                    errors.as_ptr()
                )
                .is_err()
        );
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        let values = [VARIANT::from("hello"), VARIANT::from(7_u16)];
        callback
            .OnReadComplete(
                7,
                1,
                HRESULT(0),
                HRESULT(0),
                2,
                handles.as_ptr(),
                values.as_ptr(),
                qualities.as_ptr(),
                times.as_ptr(),
                errors.as_ptr(),
            )
            .map_err(from_abi_error)?;
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        // Multi-byte UTF-8 expansion must also consume the budget.
        let text = VARIANT::from("界".repeat(180_000).as_str());
        assert!(validate_callback_value(&text, &mut 0).is_err());
        Ok(())
    }
}
