# iCem repository layout — безопасный первый этап

**Состояние:** первый самостоятельный инкремент shared OPC Runtime/packaging,
не полная миграция целевого дерева. Ветка
`refactor/repo-layout-luna-20261110`, PR [#51](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/51)
(draft, base `feat/opc-runtime-acceptance-20261110`). Основной PR #50 остаётся draft.

## Класс и инварианты

- **Class:** общие Runtime/MSI inputs и consumers, которыми пользуются обе family delivery chains; отдельный application MSI/Product policy — близкий nonmember.
- **Inv:** Runtime authoring и provenance inputs имеют один shared owner, а все consumers/resolution/routing смотрят на перемещённые файлы; MSI identity/paths/bytes/ownership и production `Launch Condition="Installed"` неизменны. Проверки: completeness test, source blob comparisons, safety/acceptance tests и baseline guard.
- **N+1:** следующий OPC Runtime test/build input добавляется только в `shared/opc-runtime/` и его workflow/routing; `test_shared_runtime_has_one_owner` и tracked-path completeness обнаружат забытый root/старый Rust consumer.

## Реализовано: old → new

- `rust/installer/opc-core/**` → `shared/opc-runtime/installer/**` (включая inventories, source patches, authoring и pure/native tests).
- `rust/tools/{Test-SharedOpcRuntime.ps1,Test-SharedOpcRuntimeSafety.ps1,Test-SharedOpcAcceptance.ps1,SharedOpcAcceptance.psm1,WindowsBuildSdk.psm1}` → `shared/opc-runtime/tools/`.
- `rust/tools/WindowsMsiDatabase.psm1` → `shared/installer/tools/WindowsMsiDatabase.psm1`.
- `rust/installer/.config/dotnet-tools.json` → единственный `shared/installer/.config/dotnet-tools.json`; Rust MSI, family fixtures и Runtime workflow восстановят WiX из этого общего manifest.
- `.github/workflows/rust-opc-runtime-acceptance.yml` → `.github/workflows/opc-runtime-acceptance.yml`; moved paths, workflow owner и ignored evidence destination обновлены.

Обновлены текущие builder/fixture consumers, routing rules/tests и installer/runtime docs. Точечные hash-сравнения подтвердили неизменность moved OPC security/category/enumeration patches, native ownership inventory, component-code list и `SharedRuntime.wxs`. Не менялись canonical OPC GUID/18-component inventory, DLL locations, registry/service authoring, native ownership, Runtime version/upgrade identity, внешние API/bytes и установочные пути. Production fresh-install condition и отказ foreign/shared-owner guards сохранены. Runtime descriptor repair остаётся известным **FAIL**, здесь не исправлялся.

## Что намеренно не перенесено

- .NET application projects/tests остаются в прежних root paths; перенос в `dotnet/src/tests` — следующий отдельный механический этап.
- Common Setup launcher остаётся в `rust/installer/bootstrapper` и `rust/tools/Build-WindowsSetup.ps1`. Его consumer `.github/workflows/release.yml` имеет exact baseline approval; изменение и перепривязка hash запрещены этим этапом. Wrapper/копия не создавались.
- Общие `routing.py`, `routing-rules.json` и `test_routing.py` остаются в `rust/tools/ci`; Rust cache suite там же. Их relocation в root `tools/ci` затрагивает gate call sites в baseline-approved `.github/workflows/ci.yml` и reusable `ci-inputs.yml`. Нужен отдельный согласованный approval/migration этап; текущий патч не меняет эти pins.
- `Burservis.iCem.DataMonitor` AddIn, frozen proto/SPA/catalogs и `rust/migration/source-manifest.json` не двигались и не копировались. Их содержимое/pins не менялись.

### Пути следующих шести задач Terra после этой стадии

1. DCOM repair: теперь `shared/opc-runtime/installer/{SharedRuntime.wxs,New-OpcRuntimeModule.ps1,native-runtime-inventory.json}` и `shared/opc-runtime/tools/{Test-SharedOpcRuntime.ps1,SharedOpcAcceptance.psm1}`. Четыре descriptor repair и независимый execute-side trusted guard всё ещё требуют отдельной реализации/семикейсовой приёмки.
2. Unicode provider: без изменения — `rust/crates/icem-emulation/src/identity.rs`, `rust/tools/ordinal-identity-baseline.json`; U+2C5F oracle/baseline не ослаблены.
3. OPC operator/coexistence: Runtime owner находится в `shared/opc-runtime`; application bootstrapper/family policy пока в прежних `rust/installer` и workflow paths. UX WIP не трогался.
4. UX/настоящая SDK AddIn build: прежние root `Burservis.iCem.DataMonitor/**` и `rust/tools/Build-LegacyAddin.ps1`/`rust/installer/LegacyAddin.psm1`; исходное mixed WIP не трогалось.
5. Реальная bidirectional/app acceptance: Runtime harness переехал под `shared/opc-runtime/tools`; остальные application release/acceptance paths прежние.
6. Signing/distribution: без изменения — существующие release workflow/docs; готовность/права/сертификаты не заявляются.

## Проверки

- Python 3.12 32-bit: `python -m unittest discover -s rust/tools/ci -p "test_*.py"` — **54/54 PASS**.
- `Verify-Baseline.ps1 -RequireComplete` — **PASS**, 487 существующих файлов + 26 разрешённых удалений из 513; 111 exact approvals; frozen proto/assets, D16/D17 и REST inventory проверены. Manifest и ledger pins не изменены.
- `Test-SharedOpcRuntimeSafety.ps1` — PASS: refusal до file/registry/service/MSI effects и production condition сохранён; `Test-SharedOpcAcceptance.ps1` — **22/22 PASS**.
- PowerShell parser: 18 moved OPC `.ps1/.psm1` — PASS.
- Registry guard tests compiled in **Release** (локальный SDK 10.0.302): 0 warnings/errors; **13 pure checks PASS**, только injected/read-only behavior. Это не MSI/runtime acceptance.
- `Test-WindowsSetupBuild.ps1` — PASS: обе family Setup fixture builds, exact embedded MSI/family, checksums и refusal contracts; MSI не запускались, существующий Rust 0.1.2 только прочитан.
- actionlint 1.7.12 — PASS для изменённого `opc-runtime-acceptance.yml`. Полный repo-wide запуск локально остановился на custom runner label `icem-sdk` в неизменённом `rust-addin-build.yml`; это не объявляется полным actionlint PASS.
- `git diff --check` — PASS.

`Test-BaselineGuard.ps1` не завершился полностью: локальная сессия без elevated token не смогла создать его symlink fixture. Hosted OPC workflow run [42880016939](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/42880016939) на head `687e3a8` был `in_progress` при последней проверке; результата/PASS нет. Полный source-pinned OPC Core/MSI build, таблицы фактически собранного Runtime MSI и установочная матрица локально не запускались. Никаких `msiexec`, локальных OPC registration/service/firewall операций, полного Cargo/.NET solution build или изменения установленного MSI не выполнялось. PR #51 — проверенный локальный layout increment, не готовый release и не завершённый target tree.

## История

- Parent/base: `c2724ccb80693f41309f81cea4d1f3155459bbe3`.
- Реализация этого инкремента: `748ea8735204d7ea444852da617af17ec18ee304`.
- Отчёт добавлен отдельным commit; итоговый HEAD/PR status проверяются в истории ветки и PR #51.
