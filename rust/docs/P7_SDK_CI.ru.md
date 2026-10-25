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
