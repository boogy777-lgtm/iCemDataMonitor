# ADR0019 — owned OPC DA callbacks in the external SDK

Accepted by root internal design review2026-10-24 for implementation; independent
native safety review and Windows runtime are required before adoption. This is
explicitly our generic external SDK extension, not unchanged upstream code.
Application/shared forbid, AGENTS and ADR0001 remain unchanged. No handwritten
vtable/transmute/unsafe impl Send/Sync is permitted.

Class: read-only DA2 callback/async subset on the existing group COM identity;
MMF/core/acquisition, browse and full DA3 are outside scope. Files:
SDK server/callbacks.rs, callback_queue.rs, callback_health.rs, client/callbacks.rs;
safe sidecar windows.rs and windows/callback_tests.rs.
Inv: owned bounded frames, correctly marshalled references, dispatch outside locks;
pure queue/health tests and actual Windows COM clients verify lifecycle/native bits.
N+1: mandatory notification service method, exhaustive event kind, explicit added
SDK file allowlist and reversible patch restoring original42 SHA256 inputs.

Public safe API: mandatory DaGroupService::notification(owned GroupState,DataSource)
returns owned active samples. CallbackMonitor is explicitly shared by every factory
server via DaServer::new_with_callbacks; owned health reports live workers/in-flight
count/oldest age/sticky failure. Subscription leases preserve caller apartment/group
lifetime. DataEvent contains only owned values/errors/quality/time/handles. Reentrant
handler gets a borrowed CallbackGroup with owned-result commands only: no group()
or object() accessor can retain callback-apartment proxies. Raw COM/VARIANT stays SDK.

Native operations only in declared ours SDK adapters: generated CPC/CP/AsyncIO2 and
IOPCDataCallback implementations, typed QI, standard GIT create/register/get/revoke,
callback calls and bounded ABI array decode/encode. Each operation has SAFETY proof.
ComObject/DaGroup stay !Send/!Sync; windows macro's existing default agility/IMarshal
is retained. Advise registers callback in caller apartment. Only cookies/owned state
cross threads. Each worker initializes MTA, obtains GIT proxy there and releases it
before apartment teardown. Reentrant client registers group/server in caller GIT,
retrieves current-apartment proxies inside callback, never exposes those references.
Unadvise/RemoveGroup close queues without waiting for in-flight worker or joining
self. GIT/proxy references and worker health budget remain until actual exit. Dispatch,
Release and revoke occur outside membership/state/model locks. Foreign teardown is
health-monitored as well. One object implements CP/CPC with same identity, no cycle.

Limits:32groups/process,4096items/batch,4sinks/group,128workers including retired,
32async preparations+retained/canceled requests/group,1latest periodic slot+
32reserved completions/sink,1in-flight. Read/Refresh reserve the group slot and all
current-sink credits under the existing membership mutex before SDK-owned preparation;
RAII rollback releases errors/unwind, commit transfers credits, late Advise does not
join an already preparing request. Pre-entrypoint COM/RPC unmarshalling is outside
this bound; no whole-process RSS guarantee follows.
Arc<Frame> fanout, frame<=512KiB, stacks256KiB (<=32MiB reserved). Pending frames
<=512MiB for32groups; additional baseline/in-flight/periodic/native buffers finite.
Service must bound its allocation before returning ownership; array/byte-array values
outside profile. Atomic all-sink reservation rolls back before failure. Periodic
coalescing cannot consume completion credits; live accepted completion/cancel events
are not discarded. Disconnect explicitly ends pending delivery; in-flight may finish.
Late/repeated cancel fails. Dead COM sink retires instead of accepting undeliverable
work. Refreshed snapshots invalidate stale periodic frames without moving timer.

SDK only reports health, never exits process. Independent safe application watchdog
checks every25ms, exit70 if callback/foreign teardown>=2s or health failed, including
idle IPC and Host interval60000. Retired hung workers remain charged. Nonjoining SDK
Drop plus process isolation preserves other Host consumers; parent own-child kill/reap
remains final containment. No guarantee that arbitrary COM call itself returns in time.

Callback rate only checks the owned current model; acquisition stays200ms. Minimum
revised rate10ms returns OPC_S_UNSUPPORTEDRATE for smaller request. Activity/client
handle mutations invalidate pending periodic frames. Bitwise F32/F64 comparison;
timestamp-only change is not an exception, delivered source FILETIME stays exact.
Locale0/deadband0 profile: unsupported locale/range/nonfinite deadband reject before
commit; nonzero supported-range deadband =>OPC_E_DEADBANDNOTSUPPORTED. Enumerators
E_NOTIMPL and browse/full DA3 unsupported are explicit profile limitations.

Primary source: [OPC Foundation DA3 specification mirror](https://advosol.com/opcspecs/opcda3.00.pdf),
checked2026-10-24. §4.4.7 p87 allows callback before initiating call returns;
§4.4.7.1 pp88–90 Read=DEVICE ignores activity, early failures excluded from completion,
all-failed no callback. §4.4.7.3 pp94–95 inactive group/all inactive=>E_FAIL, refresh
updates baseline without shifting periodic deadline. §§4.4.7.4–6 pp96–98 late cancel
=>E_FAIL, Set/GetEnable without Advise=>CONNECT_E_NOCONNECTION, enable only affects
transaction0. §4.4.1.6 p56 unsupported nonzero deadband error; §4.4.3.2 p70 locale
server-specific. Async Write returns early per-item BADRIGHTS, no fake completion.

Verification: pure Linux queue/health tests; actual safe Windows COM two-sink native
values/NaN/source clock/activity/rate/async/profile/cancel/reentrant tests and a real
hung callback child with60s idle requiring exit70<=6s. Compile is not runtime PASS.
Cross-apartment/process gate stays OPEN until matching proxy/stubs. CI may use verified
pinned Core Components or reviewed upstream IDL+MIDL/cl on ephemeral Windows runner,
with license/registry-restore audit; no unverified installer is claimed installed.
MIT/NOTICE, original42 hashes and exact MSRV1.99.1 remain. Full reversible icem.patch
includes explicit additions and removes them on reverse.

## R2: CI-only external RPC acceptance boundary (before implementation)

Class: independent Windows x64 test clients and generated matching proxy/stub;
production SDK/application/native reader and installer payload are outside this class.
Inv: exact pinned IDL bytes/notices, generated ABI only, own proxy CLSID, refusal
of existing HKCU/HKLM 32/64 mappings and owned finally cleanup; mandatory CI gate
runs after warm-cache restore as well. No handwritten Rust/C++ COM ABI or vtables.
N+1: `tests/opc-da-interop/upstream.json`, `tools/Test-OpcDaInterop.ps1` and CI
command/cache guards are the edit points; hash mismatch and missing gate fail.

The external C++ STA acceptance executable may implement IOPCDataCallback using
MIDL-generated `opcda.h`, without FTM/agility, and pump COM calls on its owner STA.
This is a new, explicitly test-only native boundary. It does not extend ADR0001,
permit application unsafe, or add production COM calls outside the external SDK.
Windows SDK MIDL /env x64 /robust /Oicf generates headers/proxy/IID/dlldata from
unmodified upstream DA/Common IDL. MSVC builds these under RUNNER_TEMP, with own
PROXY_CLSID_IS `{A09B1F4C-8B64-44D1-B89F-0AA9A85F7981}`. No installer download.

Registration is ephemeral GitHub Actions runner only. The script checks all
prospective Interface roots and the private CLSID against both registry views
of both HKCU/HKLM, refuses any existing key, and writes only HKCU Software/Classes
x64. Each created root gets an unpredictable ownership marker before its children
are populated. Finally removes only roots still carrying that exact marker,
including partially installed mappings; it fails rather than delete an unowned
root. No DCOM/security/DACL changes. DLLs and client binaries are CI fixtures,
not production payload or distribution acceptance. IDL retains every original
notice and exact upstream hash; OPC metadata terms are separate from Rust MIT.
The specification License Grant is evidence, not a legal certification or full
DA3 compliance claim. Full P7/operator/hardware/distribution gates remain OPEN.

Primary references checked 2026-10-25:
- https://learn.microsoft.com/en-us/windows/win32/com/building-and-registering-a-proxy-dll
- https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regoverridepredefkey
- https://advosol.com/opcspecs/opcda3.00.pdf (License Grant, p2)
