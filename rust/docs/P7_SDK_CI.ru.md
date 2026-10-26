# P7: проверка локального SDK extension

Class: vendored dependency SDK, не входящий в application workspace; обычные
workspace tests и промышленная COM-приёмка — соседние, отдельные проверки.
Inv: до сборки проверяется exact upstream closure и reviewed delta; Windows CI
явно выполняет SDK/sidecar runtime tests и Clippy, после сборки повторно проверяет
provenance и отсутствие изменений tracked vendor sources.
N+1: следующие SDK callback changes используют те же gates; отсутствие runtime
SDK tests нельзя скрыть успешным `cargo test --workspace`.

`Verify-ScaffoldInputs.ps1` запускает offline provenance verifier. Workflow
`rust-migration.yml` до тяжёлой desktop-сборки выбирает dependency package
`opc_da_bindings` явно, вместе с `icem-opc-da-sidecar`. SDK сохраняется отдельным
dependency workspace; application compiler/lint policy не меняется. Runtime
проверка выполняется на Windows, cross compile не подменяет её результат.

Проверки in-process COM не закрывают cross-process proxy/stub, внешних клиентов,
аппаратный стенд или полный P7. Конкретные результаты CI записываются в PR.

Контракт CI проверяет обязательность нового gate, его команды, флаги и immediate
exit-code propagation. Provenance verifier и upstream manifest входят в cache
identity. Локально routing/cache42/42 и `git diff --check` PASS; PowerShell и
Windows runtime выполняются CI. Успех cross compile не объявляется runtime PASS.

## Provenance newline defect, 2026-10-24

Class: temporary byte-exact reconstruction в `third-party/rust_opc/verify.py`;
checkout vendor и runtime SDK — соседние, отдельные границы. Inv: exact upstream
SHA256 и reviewed patch обязательны при любых newline defaults; executable
`tools/ci/test_vendor.py` проверяет Windows-style Git config и mutation rejection.
N+1: следующие SDK deltas проходят тот же verifier; existing CI unittest discovery
подхватывает regression без нового workflow gate или ослабления source guard.

Run `40098148886` остановился до debug restore на `upstream SHA256 mismatch:
Cargo.toml`. На Linux исходный verifier проходит с обычным Git config и падает
с тем же сообщением при `core.autocrlf=true`. Vendor checkout сохраняет LF через
свой `* -text`, но verifier копирует только upstream inputs во внешний temp:
там attributes отсутствуют, и reverse `git apply` записывает CRLF. Это изменение
временной реконструкции, а не несоответствие reviewed SDK source upstream pin.

Обе команды `git apply` теперь получают command-scoped `core.autocrlf=false`,
`core.eol=lf` и существующий vendor `.gitattributes` как `core.attributesFile`;
последнее исключает влияние global attributes. Git config пользователя не меняется.
Manifest hashes, patch, проверка полного closure и SHA256 алгоритм неизменны.
Regression делает настоящий Git checkout с autocrlf=true/eol=crlf/global text
attributes, подтверждает byte-identical vendor, воспроизводит CRLF при старой
plain reconstruction и проверяет успешную исправленную. Отдельно обязательны
отказы при мутации source, patch и expected hash. Это Linux reproduction Git
policy; actual Windows CI и SDK runtime должны быть выполнены отдельно.

## Fixture после SDK additions

Class: входы regression fixture `tools/ci/test_vendor.py`, не production verifier.
Inv: старая CRLF-ошибка воспроизводится с полным текущим patch input set;
source/patch/hash и добавленный SDK file mutation по-прежнему отвергаются.
N+1: fixture копирует весь vendor без checkout attributes, поэтому следующий
reviewed added file не требует отдельного списка или ослабления reverse patch.

Run `40128249313` выявил omission: fixture копировал только42 upstream inputs,
а новый reverse patch также удаляет4 reviewed SDK additions. Plain apply падал
до проверки CRLF. Fixture теперь включает эти файлы и намеренно исключает только
`.gitattributes`; actual verifier и original hashes не изменяются.

Локально Linux: полный `unittest discover -s rust/tools/ci -p 'test_*.py'`
44/44 PASS; standalone verifier с обычным config и с autocrlf=true/eol=crlf,
`cache_contract.py`, exact Rust1.99.1 `cargo fmt --all --check` и
`git diff --check` PASS. Rust implementation/SDK build inputs не менялись.

## R2 matching RPC proxy checkpoint (runtime pending)

`tools/Test-OpcDaInterop.ps1` is mandatory within the existing pre-GPUI SDK step,
including warm-cache runs. It verifies byte-exact pinned DA/Common IDL fixtures,
builds Windows SDK MIDL x64 /robust /Oicf sources with MSVC, and gives this test
proxy an independent CLSID. No Core Components installer is fetched or trusted.
Generated IID discovery drives refusal of preexisting mappings in HKCU/HKLM,
32/64 views; only owned ephemeral HKCU64 mappings are installed and removed.
The script invokes the existing ignored real-process Rust test explicitly with
`--ignored --nocapture --locked --offline`, propagating any failure. App/shared
unsafe policy, runtime vendor42 provenance and production payload are unchanged.

Source/Python contracts44/44 and diff-check PASS on Linux. There is no local
Windows MIDL/runtime; the first Actions run must validate build/registration/RPC.
STA external client and expanded RPC geometry/lifecycle coverage remain pending;
R2 stays OPEN. Cross compilation or a script source review is not runtime PASS.


R2 second checkpoint adds the independent generated-header C++ STA executable.
Its IOPCDataCallback rejects IMarshal and every event asserts the owner STA thread;
CoWaitForMultipleHandles dispatches COM calls with finite waits. Two sinks verify
4096-item native U16/I32/F32 NaN payload, writer FILETIME, current GOOD quality,
client/group handles and rate, CACHE activity, Sync/AsyncRead, Refresh, early
read-only Sync/AsyncWrite, queued cancellation/repeated/late rejection, Unadvise,
reentrant RemoveGroup and40 complete external server/group reconnect loops.
A reentrant Read/Cancel is queued behind its sole currently executing sink worker,
so successful cancellation does not depend on timing a racing completed Read.

The Rust harness owns both processes (sidecar60s deadline, STA child45s), kills/
reaps on failure and runs a bounded synthetic200ms IPC producer with one frame/ACK
at a time. New capture generations retain fixture raw values and writer FILETIME;
this avoids silently weakening GOOD-quality checks when acceptance exceeds5s.
No acquisition/MMF/source semantics change. Cleanup now attempts every owned key,
records creation before fallible marker writes, permits only its still-empty newly
created root after marker failure, refuses foreign content, and guarantees env/temp
cleanup even if one mapping cleanup fails.

Linux source checks: exact Rust1.99.1 Windows x64 all-target Clippy -D warnings
PASS; C++/MIDL/PowerShell runtime require actual Windows CI. R2 remains OPEN.


First actual Windows proxy attempt: run40171220913/job121102485283 stopped before
MIDL because PowerShell7 native argument quoting passed an extra leading quote to
cmd/VsDevCmd (`'""C:\Program' is not recognized`). This is harness setup evidence,
not a proxy/runtime pass. The correction uses a controlled temporary init.cmd,
ProcessStartInfo raw cmd `/d /s /c ""path""` quoting, concurrent private stdout/stderr
capture and a30s owned-child deadline; only PATH/INCLUDE/LIB/LIBPATH are imported.
No full environment is emitted. MIDL/CL/RPC and R2 remain pending actual CI.


## R2 actual Windows RPC gate — 2026-10-25

R2 software acceptance is closed for the documented x64 read-only DA2 profile.
[Run40171770730/job121104122819](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/40171770730/job/121104122819)
ran remote `75a19342905c270efd431e77519badbaeff7ee0c`, byte-identical tree
`cef5a5f18e1e2ee90f73ddee37ee0289ff7490bf` to local
`692dee3bd5356a4f0ef7685f702ae0f8dcc246b0`. Root independently observed the
mandatory `Verify reviewed OPC SDK and sidecar on Windows` step COMPLETED SUCCESS
from07:14:26 to07:15:19UTC. This is actual Windows runtime, not cross compilation.

Evidence at this checkpoint is the successful mandatory step plus its exact
checked commands/source. That step has no cache-hit condition, continue-on-error
or bypass: Test-OpcDaInterop must successfully generate/build/register its matching
proxy, run the explicitly ignored Rust process test and the independent C++ STA
executable, and finish owned registry/environment/temp cleanup. Direct named test
output can be attached once completed-job logs are available. The entire job was
still running later emulator checks when this SDK result was recorded; full CI
of this head and final integration are not claimed here.

The runtime gate covers real LOCAL_SERVER activation/group/items/native reads,
MTA Rust callbacks plus two independent apartment-bound C++ sinks. The latter
reject IMarshal and assert every callback executes on the initiating STA thread;
COM is pumped using bounded CoWaitForMultipleHandles, proving actual marshalling.
4096-item RPC arrays check U16, signed I32, raw F32 NaN payload, exact writer
FILETIME, strict current quality and client/group handles. Group activity/rate,
Sync/AsyncRead, Refresh, read-only Sync/AsyncWrite, successful queued Cancel plus
late/repeated rejection, Unadvise isolation, reentrant Unadvise/RemoveGroup and40
complete server/group connect-close cycles execute against the sidecar process.
These40 cycles detect exhaustion/leaks of32-slot group/server leases; they do not
establish a general native heap/RSS bound.

Fixture capture is kept fresh by a bounded200ms one-frame/ACK synthetic producer;
writer FILETIME and values remain unchanged. Own-child60s/45s watchdogs kill/reap
sidecar/client; no process-name termination or extra MMF source is used. Callback
borrowed handlers are cleared by RAII before any failure-path proxy teardown can
pump COM, preventing retained sinks from accessing partially destroyed Group state.

Proxy inputs are exact separately pinned/noticed IDLs, compiled by installed
Windows SDK MIDL x64 /robust /Oicf and MSVC; ABI declarations are generated, no
handwritten Rust/C++ vtables/transmute/unsafe Send/Sync. Private proxy factory
CLSID and ownership-marked HKCU64 mappings refuse existing HKCU/HKLM32/64 keys;
cleanup attempts all owned keys even on partial failure and never deletes foreign
content. No DCOM/DACL/security weakening, installer download or production payload.
Application/shared forbid, runtime SDK original42 hashes/four-file allowlist,
core/acquisition/other adapters/.NET/AddIn/proto/assets remain unchanged by R2.

Source/local checks: exact Rust1.99.1 Windows x64 SDK+sidecar all-target Clippy
-D warnings, workspace rustfmt, Python CI44/44, cache contract and diff-check PASS.
Full P7, UA/MQTT Windows acceptance, final-head full CI, industrial third-party
clients, actual MMF/AddIn/hardware, x86/DCOM/operator deployment and distribution
terms remain separate gates. OPC notices/license grant are preserved; this is not
legal certification, production distribution acceptance or full DA3 compliance.
