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
