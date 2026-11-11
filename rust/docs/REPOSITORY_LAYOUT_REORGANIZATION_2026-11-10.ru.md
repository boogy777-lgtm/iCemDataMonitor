# iCem repository layout — поэтапная реорганизация

**Текущее состояние:** stage 3 source candidate опубликован в draft PR #51; scoped
.NET/AddIn/shared-input relocations и их consumers завершены. Это не release
acceptance и не полное исполнение всего буквального layout target: незавершённый
tail перечислен отдельно ниже. Ветка
`refactor/repo-layout-luna-20261110`, PR [#51](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/51)
(draft, base `feat/opc-runtime-acceptance-20261110`). Основной PR #50 остаётся draft.

## Класс и инварианты

- **Class:** shared Runtime/Setup producer и family-gate/routing consumers; отдельные .NET/Rust MSI Product/payload policies и Rust cache/vendor/toolchain suite — nonmembers.
- **Inv:** common Setup имеет единственный `shared/installer` producer; common gate tooling — `tools/ci`; Rust-specific checks остаются в `rust/tools/ci`. Exact Setup CLI/EXE/resource names, embedded MSI/family/hash, ProductCode revalidation/refusal, checksums, no-overwrite, explicit consent и own-MSI `/x`, независимые gate graphs и immutable baseline сохранены. Проверки: ownership/discovery/routing tests, Release fixture builds, packaging/safety/oracle tests и `Verify-Baseline -RequireComplete`.
- **N+1:** новые common Setup inputs входят в `shared/installer`, common routing/cases — в `tools/ci`; `test_shared_setup_has_one_owner_and_direct_consumers`, suite discovery, tracked-path routing coverage и workflow gate tests ловят забытый consumer/path. Rust-only cache/vendor/toolchain case остаётся в `rust/tools/ci` и Rust gate.

## Историческая запись этапов 1–2

Следующий old → new список и утверждения «пока остаётся» ниже фиксируют состояние
ранних stage 1/2 commits, а не актуальное дерево. Их текущий статус переопределён
completion matrix и literal-target tail в stage 3 ниже.

## Реализовано на stage 1/2: old → new

- `rust/installer/opc-core/**` → `shared/opc-runtime/installer/**` (включая inventories, source patches, authoring и pure/native tests).
- `rust/tools/{Test-SharedOpcRuntime.ps1,Test-SharedOpcRuntimeSafety.ps1,Test-SharedOpcAcceptance.ps1,SharedOpcAcceptance.psm1,WindowsBuildSdk.psm1}` → `shared/opc-runtime/tools/`.
- `rust/tools/WindowsMsiDatabase.psm1` → `shared/installer/tools/WindowsMsiDatabase.psm1`.
- `rust/installer/.config/dotnet-tools.json` → единственный `shared/installer/.config/dotnet-tools.json`; Rust MSI, family fixtures и Runtime workflow восстановят WiX из этого общего manifest.
- `.github/workflows/rust-opc-runtime-acceptance.yml` → `.github/workflows/opc-runtime-acceptance.yml`; moved paths, workflow owner и ignored evidence destination обновлены.

Обновлены текущие builder/fixture consumers, routing rules/tests и installer/runtime docs. PR review follow-up добавил `shared/installer/.config/dotnet-tools.json` в `opc-runtime-acceptance.yml::on.pull_request.paths` и regression `test_opc_runtime_trigger_covers_actual_inputs`: он читает именно trigger path-list и проверяет tracked Runtime installer/tools inputs, hash-sensitive `.gitattributes`, общий WiX manifest и MSI helper. Точечные hash-сравнения подтвердили неизменность moved OPC security/category/enumeration patches, native ownership inventory, component-code list и `SharedRuntime.wxs`. Не менялись canonical OPC GUID/18-component inventory, DLL locations, registry/service authoring, native ownership, Runtime version/upgrade identity, внешние API/bytes и установочные пути. Production fresh-install condition и отказ foreign/shared-owner guards сохранены. Runtime descriptor repair остаётся известным **FAIL**, здесь не исправлялся.

## Что на stage 2 ещё не было перенесено (историческая запись)

- На том stage .NET application projects/tests оставались в root; stage 3 перенёс их в `dotnet/src/` и `dotnet/tests/`.
- Application-specific MSI/Product/payload modules остаются в исходных family trees; common Setup не принимает family policy из shared core.
- На том stage AddIn, proto и catalogs не двигались; stage 3 перенёс единственный AddIn в `shared/icem-addin/`, proto в `shared/contracts/`, catalogs в раздельные `shared/catalogs/{host,emulator}/`. Исторический `rust/migration/source-manifest.json` не перемещался и не менялся.

## Stage 2: common Setup и CI tooling

- `rust/installer/bootstrapper/{Setup.cs,Setup.csproj}` → `shared/installer/bootstrapper/` (100% identical blobs).
- `rust/tools/{Build-WindowsSetup.ps1,Test-WindowsSetup.ps1,Test-WindowsSetupBuild.ps1}` → `shared/installer/tools/`; оба release families вызывают producer напрямую, Rust release helper и fixture workflow также указывают общую локацию. Retired paths не оставлены forwarding wrappers.
- `rust/tools/ci/{routing.py,routing-rules.json,test_routing.py}` → `tools/ci/`. Workflows `ci.yml`, `legacy-ci.yml`, `ci-inputs.yml` вызывают root tooling/common suite; Rust cache/vendor/encoding tests остаются в `rust/tools/ci` и исполняются только под Rust family workflow. Routing rules классифицируют всё `shared/installer/**` как shared-contracts (README исключён), сохраняя отдельные application jobs и exact gate schemas/dependencies.
- `rust/migration/legacy-divergences.json`: обновлены только exact approvals `.github/workflows/ci.yml` и `release.yml` — исходные baseline blob hashes и schema/policy сохранены; новые `approved_blob_sha1` совпадают с текущими Git blobs. Причина — необходимые прямые call-site изменения принятого layout в PR #51. Source-only commit `7afdce73cb7839af8b8f5174cde01cf4cc574767`; затем exact approval/provenance commit `638f5ec4126f677d78aac518807d7d1ad7670c37`. `source-manifest.json` не менялся. Это narrow exact approval, не blanket reset и не перепривязка прочих approvals.
- `shared/installer/README.md`, `rust/installer/README.md`, `rust/docs/CI_CD.ru.md` описывают новую ownership/discovery. Family MSI authoring/policy не переносились в shared core; поведение launcher и содержимое C# `Setup.cs` не менялись.

### Пути следующих шести задач Terra после этой стадии

1. DCOM repair: `shared/opc-runtime/installer/{SharedRuntime.wxs,New-OpcRuntimeModule.ps1,native-runtime-inventory.json}` и `shared/opc-runtime/tools/{Test-SharedOpcRuntime.ps1,SharedOpcAcceptance.psm1}`. Четыре descriptor repair и независимый execute-side trusted guard всё ещё требуют отдельной реализации/семикейсовой приёмки.
2. Unicode provider: без изменения — `rust/crates/icem-emulation/src/identity.rs`, `rust/tools/ordinal-identity-baseline.json`; U+2C5F oracle/baseline не ослаблены.
3. OPC operator/coexistence: Runtime owner находится в `shared/opc-runtime`; общий launcher/build теперь в `shared/installer`, Rust MSI/family policy — в `rust/installer`, .NET MSI policy — в `installer/` и its release path. UX WIP не трогался.
4. UX/настоящая SDK AddIn build: прежние root `Burservis.iCem.DataMonitor/**` и `rust/tools/Build-LegacyAddin.ps1`/`rust/installer/LegacyAddin.psm1`; исходное mixed WIP не трогалось.
5. Реальная bidirectional/app acceptance: Runtime harness переехал под `shared/opc-runtime/tools`; остальные application release/acceptance paths прежние.
6. Signing/distribution: без изменения — существующие release workflow/docs; готовность/права/сертификаты не заявляются.

## Проверки

- Stage 1, Python 3.12 32-bit: общий+Rust suite до разделения — **55/55 PASS**, включая реальную OPC Runtime `on.pull_request.paths` coverage.
- `Verify-Baseline.ps1 -RequireComplete` — **PASS**, 487 существующих файлов + 26 разрешённых удалений из 513; 111 exact approvals; frozen proto/assets, D16/D17 и REST inventory проверены. Manifest и ledger pins не изменены.
- `Test-SharedOpcRuntimeSafety.ps1` — PASS: refusal до file/registry/service/MSI effects и production condition сохранён; `Test-SharedOpcAcceptance.ps1` — **22/22 PASS**.
- PowerShell parser: 18 moved OPC `.ps1/.psm1` — PASS.
- Registry guard tests compiled in **Release** (локальный SDK 10.0.302): 0 warnings/errors; **13 pure checks PASS**, только injected/read-only behavior. Это не MSI/runtime acceptance.
- `Test-WindowsSetupBuild.ps1` — PASS: обе family Setup fixture builds, exact embedded MSI/family, checksums и refusal contracts; MSI не запускались, существующий Rust 0.1.2 только прочитан.
- actionlint 1.7.12 — PASS для изменённого `opc-runtime-acceptance.yml`. Полный repo-wide запуск локально остановился на custom runner label `icem-sdk` в неизменённом `rust-addin-build.yml`; это не объявляется полным actionlint PASS.
- `git diff --check` — PASS.

`Test-BaselineGuard.ps1` для stage 1 не завершился полностью: локальная сессия без elevated token не смогла создать symlink fixture. Stage-1 hosted Runtime run `42881589176` на `b7cef45` завершился **FAIL**: build и шесть cases PASS, `descriptor-repair` FAIL; defect out of scope и не замаскирован. Полный source-pinned OPC Core/MSI build, таблицы фактически собранного Runtime MSI и install matrix локально не запускались. Никаких локальных `msiexec`, OPC registration/service/firewall операций, полного Cargo/.NET solution build или изменения установленного MSI не выполнялось.

### Проверки stage 2

- `$env:PYTHONDONTWRITEBYTECODE='1'; & 'C:\Python312-32\python.exe' -m unittest discover -s tools/ci -p 'test_*.py'` (repo root) — **36/36 PASS** (common routing, exact approval dependency proof, both family-gate graphs, moved Setup owner/consumers and suite coverage).
- `$env:PYTHONDONTWRITEBYTECODE='1'; & 'C:\Python312-32\python.exe' -m unittest discover -s tools/ci -p 'test_*.py'` (`rust/`) — **22/22 PASS** (Rust cache/vendor/encoding suite; still Rust-owned).
- `& 'C:\Users\HALLIBURTON\AppData\Local\Temp\opencode\pwsh-7.6.7-verified-20261110\pwsh.exe' -NoProfile -File .\shared\installer\tools\Test-WindowsSetupBuild.ps1` — обе .NET/Rust Release TEST ONLY fixture Setup сборки: 0 warnings/errors; exact embedded MSI/family/hash, selected ProductCode/refusal, no-overwrite/tamper/unsupported checks PASS. Existing Rust MSI `0.1.2` только обнаружен/read-only; никакой MSI не запускался. Это не turnkey/release acceptance.
- `& 'C:\Users\HALLIBURTON\AppData\Local\Temp\opencode\pwsh-7.6.7-verified-20261110\pwsh.exe' -NoProfile -File .\rust\tools\Test-WindowsPackaging.ps1` — PASS pure packaging/AddIn inventory/native ownership/UI/options/path/checksum contracts.
- `& 'C:\Users\HALLIBURTON\AppData\Local\Temp\opencode\pwsh-7.6.7-verified-20261110\pwsh.exe' -NoProfile -File .\shared\opc-runtime\tools\Test-SharedOpcRuntimeSafety.ps1` — PASS; `... -File .\shared\opc-runtime\tools\Test-SharedOpcAcceptance.ps1` — **22/22 PASS**.
- PowerShell 7.6.7 `Parser.ParseFile` — PASS для трёх moved Setup scripts. `actionlint.exe` 1.7.12 — PASS для шести изменённых workflows: `ci.yml`, `ci-inputs.yml`, `legacy-ci.yml`, `release.yml`, `rust-installer-contracts.yml`, `rust-migration.yml`.
- `& 'C:\Users\HALLIBURTON\AppData\Local\Temp\opencode\pwsh-7.6.7-verified-20261110\pwsh.exe' -NoProfile -File .\rust\tools\Verify-Baseline.ps1 -RequireComplete` — PASS: 487 existing, 26 approved deletions of 513 entries, 111 exact approvals; immutable proto/assets, D16/D17, REST inventory checked. `source-manifest.json` unchanged.
- `& 'C:\Users\HALLIBURTON\AppData\Local\Temp\opencode\pwsh-7.6.7-verified-20261110\pwsh.exe' -NoProfile -File .\rust\tools\Test-BaselineGuard.ps1` — **не PASS/не завершён**: positive/negative cases прошли до symlink case, где Windows вернул `Administrator privilege required`. Ничего не отключалось и не обходилось.
- `git diff --check` — PASS; полный release-family/hosted verdict для final stage-2 head не ожидался/не опрашивался.

Stage 2 изменил только layout/call sites и test discovery. Никаких MSI installation/uninstallation, registrations, services, firewall или full Cargo/.NET solution builds не выполнялось. Runtime `descriptor-repair` failure остаётся открытым. Целевое дерево ещё не завершено: .NET project/test relocation, AddIn producer/tools/tests relocation и отдельное решение по frozen proto/SPA/catalog ownership остаются вне этой стадии.

### Review follow-up: UTF-8 guard coverage после переноса

После stage 2 review обнаружил, что `rust/tools/ci/test_encoding.py` продолжал glob только Rust directory и поэтому перестал проверять common sources в `tools/ci`. Исправлен один существующий AST guard: он сканирует `.py` автоматически в обоих CI roots. Новый `test_scan_finds_implicit_io_in_a_new_common_source` подаёт synthetic common source с implicit `read_text()` и `write_text(encoding='cp1251')` и требует обнаружить оба нарушения; список filenames не поддерживается вручную, implementation guard не дублируется.

После follow-up: root common suite **36/36 PASS**, Rust suite **22/22 PASS** (Python 3.12); `git diff --check` PASS. Изменён только тестовый coverage и связанная документация: production/build code, approvals, baseline, source manifest, MSI и machine state не затрагивались. Hosted CI не опрашивался.

## Stage 3: завершение принятого layout в Luna

Stage 3 завершает согласованный .NET/AddIn/shared-input scope этого PR, но не весь
буквальный layout target. Это только перемещение владения, обновление
discovery/build/CI consumers и проверяемая provenance; source
contents менялись только в project/call-site paths, source guards, README и CI
routing. Namespaces, assembly/executable identities, MSI GUIDs, byte-level frozen
inputs, runtime installed paths и application behavior не менялись.

### Completion matrix и old → new

| Владение / класс | Старый путь | Канонический владелец | Проверка полноты |
|---|---|---|---|
| .NET production projects | root `iCemDataMonitor.*`, `iCemEmulator`, `iCemEmule.Core` | `dotnet/src/<project>/` | solution inventory, project references, path/source guards |
| .NET tests | root `iCemDataMonitor.*.Tests`, `iCemEmule.Tests` | `dotnet/tests/<project>/` | `dotnet/CI.slnf`, solution inventory, moved test locators |
| .NET solution/SDK/installer/tools | root `iCemDataMonitor.sln`, `CI.slnf`, `global.json`, `installer/`, `build.ps1`, `scripts/make-release.ps1` | `dotnet/{iCemDataMonitor.sln,CI.slnf,global.json,installer/,tools/}` | Release solution list, task/build/release consumers, package fixture |
| Real vendor AddIn source/test producer | root `Burservis.iCem.DataMonitor{,.Tests}/` | единственный `shared/icem-addin/{src,tests}/` | AddIn source inventory/provenance tests, legacy build/test workflows |
| AddIn build/provenance tools | `rust/tools/Build-LegacyAddin.ps1`, `rust/installer/LegacyAddin.psm1`, `rust/tools/Test-LegacyAddinInputs.ps1` | `shared/icem-addin/tools/` | SDK input, linked-source containment, exact source provenance tests |
| Frozen common contract | `iCemDataMonitor.Contracts/Protos/telemetry.proto` | `shared/contracts/telemetry.proto` | immutable pin/frozen proto guard; no duplicate source |
| Host catalog ownership | `iCemDataMonitor.Plugins.Icem/templates/**`, root `templates/{variable_registry,flecs_4.53,plc_basic}.json` | `shared/catalogs/host/**` | project content links, payload inventory, source-origin map |
| Emulator catalog ownership | `iCemEmulator/templates/**` | `shared/catalogs/emulator/**` | emulator project/test/oracle/payload consumers and source-origin map |
| Common frozen branding assets | root `assets/**` | `shared/assets/**` | exact origin blob checks and consumer-path validation |
| Common OPC Runtime and Setup | prior `rust/installer/opc-core/**`, Rust Setup/tool paths | `shared/opc-runtime/**`, `shared/installer/**` | prior stage 1/2 safety, acceptance and Setup fixtures |
| Common/Rust CI routing | `rust/tools/ci/{routing.py,routing-rules.json,test_routing.py}` | common `tools/ci/`; Rust-only cache/vendor checks remain `rust/tools/ci/` | 36 common + 22 Rust Python tests and workflow path/input guards |
| Frozen Rust SPA/assets and Rust-owned products | `rust/**` | remain `rust/**` | existing frozen predicates/oracles and product-specific inventories |

The exact 408 logical-origin moves and one-destination ownership map are
`migration/source-relocations.json`; the pinned historical `source-manifest.json`
remains unchanged. Host and emulator catalogs are deliberately separate even
where two historical lantable files have identical bytes. `rust/assets/**` remain
Rust product inputs; only the historical common root `assets/**` moved to
`shared/assets/**`. Rust D16/D17 SPA copies remain Rust-owned, separate from the
legacy Host SPA. `dotnet/installer/assets/**` remain .NET installer-specific
branding/bitmaps; they are not duplicated into the common icon root. Installed
MSI paths and each product's payload destinations are unchanged.

### Terra handoff: six task paths after layout completion

1. **DCOM descriptor repair:** `shared/opc-runtime/installer/{SharedRuntime.wxs,New-OpcRuntimeModule.ps1,native-runtime-inventory.json}` and `shared/opc-runtime/tools/{Test-SharedOpcRuntime.ps1,SharedOpcAcceptance.psm1}`. Four descriptor repair and trusted execute-side guards remain separate; the known repair failure is not fixed or suppressed here.
2. **Unicode provider:** unchanged `rust/crates/icem-emulation/src/identity.rs` and `rust/tools/ordinal-identity-baseline.json`; frozen oracle and Unicode behavior remain Terra scope.
3. **OPC operator/coexistence:** Runtime implementation under `shared/opc-runtime/`, common Setup under `shared/installer/`, Rust MSI family policy under `rust/installer/`, legacy .NET MSI policy under `dotnet/installer/`; no UX WIP changed.
4. **UX / real SDK AddIn build:** canonical source/tests/provenance are now `shared/icem-addin/{src,tests,tools}/`; real SDK-dependent build/visual acceptance remains external and was not simulated.
5. **Bidirectional/application acceptance:** shared Runtime harness under `shared/opc-runtime/tools/`; application release and Rust/.NET family acceptance stay in their respective owners, with runtime host paths unchanged.
6. **Signing/distribution:** existing release workflows and docs; signing credentials, certificates and distribution authorization are not claimed.

### Оставшийся tail буквального layout target (не выполнен этим PR)

- Rust tooling физически не реорганизован: существующие build/release scripts и
  домены `rust/tools/{acceptance,browser,ci,perf}` сохранены; common routing уже
  вынесен в `tools/ci/`, Rust-only cache/vendor/toolchain tests остаются в
  `rust/tools/ci/`. Перенос Rust build/perf tooling не заявляется.
- Rust test domains не переносились: `rust/tests/{fixtures,opc-da-interop,support,windows}`
  и product-owned test files остались в текущем Rust дереве. Installer/integration
  checks не перемещались в новые общие владельцы только ради симметрии.
- Документация остаётся разделённой между общими `docs/` и Rust product-owned
  `rust/docs/`; общего docs-root consolidation не было. Branding ownership
  завершён только для ранее общего корневого `assets/**` → `shared/assets/**`.
  `dotnet/installer/assets/**` и `rust/assets/**` остаются product-specific inputs,
  а не единым общим каталогом.
- Frozen legacy Host SPA и Rust D16/D17 SPA — разные owners и не объединялись;
  Rust SPA остаётся в `rust/`. Product MSI/payload policy также раздельны
  (`dotnet/installer/` и `rust/installer/`), хотя общий Setup принадлежит
  `shared/installer/`.

Это фактический незакрытый tail относительно более широкого буквального target.
В этом review follow-up новых переносов нет; stable stage-3 source candidate
передаётся Terra без заявления о полном target/release.

### Stage 3 validation and publication record

- Common Python 3.12 suite: **36/36 PASS**; Rust-owned Python 3.12 suite: **22/22 PASS**.
- `Test-WindowsPackaging.ps1`: **PASS**, including AddIn source boundary/provenance, payload roundtrip, checksum, native ownership/options and frozen path contracts.
- Runtime safety: **PASS**; Runtime acceptance fixture: **22/22 PASS**.
- `Test-WindowsSetupBuild.ps1`: **PASS**; both Release-only Setup fixture builds had 0 warnings/errors, with family/product/checksum/refusal checks. The fixtures compile only; no MSI install/uninstall/repair occurred.
- `dotnet sln dotnet/iCemDataMonitor.sln list`: **27 projects**; PowerShell 7.6.7 parser: **31/31 changed `.ps1/.psm1` files PASS**; `git diff --check`: **PASS**.
- Static project-input scan: **111 literal project references/content/icon/manifest paths PASS**; dynamic MSBuild properties and SDK-provided references were excluded (no build performed).
- `actionlint` 1.7.12: **PASS** on all four changed workflows using a temporary external config declaring the repository's existing `self-hosted, Windows, X64, icem-sdk` runner labels. No repository actionlint configuration changed.
- Baseline guard pure positive/negative cases pass through absence-probe failure; complete suite stops at the first symlink fixture because this process lacks Windows Administrator privilege (`Administrator privilege required`). No skip/bypass was added.
- `Verify-Baseline.ps1 -RequireComplete`: **PASS**, 487 verified existing files, 26 previously approved deletions from 513 pinned origins, and 139 exact approvals. Exactly 42 current logical paths changed approval (14 exact existing approvals refreshed, 28 new); provenance points to the source-only PR51 commit. The historical source commit and source-manifest hashes are unchanged; no delete approvals were added.
- В successful recovery continuation не запускались local Cargo
  compilation/check/test/clippy, полный .NET solution build, MSI mutation,
  registration/service/firewall operation или broad cleanup. До recovery была
  неуспешная Debug Cargo попытка; parent удалил только подтверждённые созданные
  Debug binaries. Это не Release validation; audit record:
  `artifacts/recovery/luna-debug-generated-binaries-20261111.json`. Full Rust/legacy
  verdict принадлежит hosted Release CI на exact published head. Descriptor repair
  и Unicode findings не менялись.

- Source-only commit: `241341dea6b7b0349eb2c8ed4e5bee8866e99c45` (`Complete repository layout relocation`).
- Exact approval/provenance commit: `61baf2cc484edfbe61a8b3b3e0b31b2559707c6b` (`Approve exact layout relocation blobs`); only the 42 changed logical baseline-owned paths were approved, with no delete approvals or unrelated approval refresh.
- Source/provenance chain confirmed: source `241341dea6b7b0349eb2c8ed4e5bee8866e99c45`, approval `61baf2cc484edfbe61a8b3b3e0b31b2559707c6b`. Предыдущий report HEAD перед этим document-only follow-up — `09b658a3efbd185f38ac95343dda3be910732fe0`; итоговый report-only commit и push проверяются по `git`/PR #51 (этот файл не может содержать hash собственного commit).

### Narrow hosted-review follow-up

- На exact `5834dfeb` hosted Rust CI run `42942169399`, packaging job `130266926859`, обнаружил неверный source locator в Rust install fixture: он искал `repo/installer/gen-payload.ps1` вместо уже staged `repo/dotnet/installer/gen-payload.ps1`. Job завершился до вызова Windows Installer; это path-resolution failure, не verdict об установке/поведении MSI. Common/Rust routing and baseline jobs прошли; package/AddIn pure checks и обе Setup Release fixtures также прошли. Полный job log: `artifacts/layout-ci-review/42942169399-packaging-130266926859.log`.
- Исправлены оба активных Rust build-time consumers: `rust/tools/Test-WindowsInstaller.ps1` и `rust/installer/WindowsInstaller.psm1`. Existing `Test-WindowsPackaging.ps1` теперь проверяет staged canonical fixture input, оба ожидаемых call sites, отсутствие retired generator path во всех Rust `.ps1/.psm1` consumers, а также positive/negative matcher cases. Logical origin `installer/gen-payload.ps1` в immutable provenance не менялся.
- Follow-up local checks: `Test-WindowsPackaging.ps1` PASS; common Python **36/36**, Rust Python **22/22**, PowerShell parser **3/3 changed scripts**, `git diff --check` PASS. Ни MSI, ни ledger, ни source manifest не менялись; no local Cargo/installer execution. На fix commit `17b3bf6e4e3ea8b5cdb7b315b937c1a007616431` dispatched `rust-installer-contracts.yml`, run **42943781526**; status не опрашивался, dispatch/queued не считается PASS.

## История

- Parent/base: `c2724ccb80693f41309f81cea4d1f3155459bbe3`.
- Реализация этого инкремента: `748ea8735204d7ea444852da617af17ec18ee304`.
- Отчёт добавлен отдельным commit; итоговый HEAD/PR status проверяются в истории ветки и PR #51.
