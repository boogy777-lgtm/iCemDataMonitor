# iCem repository layout — поэтапная реорганизация

**Состояние:** stage 2 завершён локально и опубликован в draft PR #51; это всё ещё
не полная миграция целевого дерева и не release acceptance. Ветка
`refactor/repo-layout-luna-20261110`, PR [#51](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/51)
(draft, base `feat/opc-runtime-acceptance-20261110`). Основной PR #50 остаётся draft.

## Класс и инварианты

- **Class:** shared Runtime/Setup producer и family-gate/routing consumers; отдельные .NET/Rust MSI Product/payload policies и Rust cache/vendor/toolchain suite — nonmembers.
- **Inv:** common Setup имеет единственный `shared/installer` producer; common gate tooling — `tools/ci`; Rust-specific checks остаются в `rust/tools/ci`. Exact Setup CLI/EXE/resource names, embedded MSI/family/hash, ProductCode revalidation/refusal, checksums, no-overwrite, explicit consent и own-MSI `/x`, независимые gate graphs и immutable baseline сохранены. Проверки: ownership/discovery/routing tests, Release fixture builds, packaging/safety/oracle tests и `Verify-Baseline -RequireComplete`.
- **N+1:** новые common Setup inputs входят в `shared/installer`, common routing/cases — в `tools/ci`; `test_shared_setup_has_one_owner_and_direct_consumers`, suite discovery, tracked-path routing coverage и workflow gate tests ловят забытый consumer/path. Rust-only cache/vendor/toolchain case остаётся в `rust/tools/ci` и Rust gate.

## Реализовано: old → new

- `rust/installer/opc-core/**` → `shared/opc-runtime/installer/**` (включая inventories, source patches, authoring и pure/native tests).
- `rust/tools/{Test-SharedOpcRuntime.ps1,Test-SharedOpcRuntimeSafety.ps1,Test-SharedOpcAcceptance.ps1,SharedOpcAcceptance.psm1,WindowsBuildSdk.psm1}` → `shared/opc-runtime/tools/`.
- `rust/tools/WindowsMsiDatabase.psm1` → `shared/installer/tools/WindowsMsiDatabase.psm1`.
- `rust/installer/.config/dotnet-tools.json` → единственный `shared/installer/.config/dotnet-tools.json`; Rust MSI, family fixtures и Runtime workflow восстановят WiX из этого общего manifest.
- `.github/workflows/rust-opc-runtime-acceptance.yml` → `.github/workflows/opc-runtime-acceptance.yml`; moved paths, workflow owner и ignored evidence destination обновлены.

Обновлены текущие builder/fixture consumers, routing rules/tests и installer/runtime docs. PR review follow-up добавил `shared/installer/.config/dotnet-tools.json` в `opc-runtime-acceptance.yml::on.pull_request.paths` и regression `test_opc_runtime_trigger_covers_actual_inputs`: он читает именно trigger path-list и проверяет tracked Runtime installer/tools inputs, hash-sensitive `.gitattributes`, общий WiX manifest и MSI helper. Точечные hash-сравнения подтвердили неизменность moved OPC security/category/enumeration patches, native ownership inventory, component-code list и `SharedRuntime.wxs`. Не менялись canonical OPC GUID/18-component inventory, DLL locations, registry/service authoring, native ownership, Runtime version/upgrade identity, внешние API/bytes и установочные пути. Production fresh-install condition и отказ foreign/shared-owner guards сохранены. Runtime descriptor repair остаётся известным **FAIL**, здесь не исправлялся.

## Что намеренно не перенесено

- .NET application projects/tests пока остаются в root; их перенос в `dotnet/src/tests` — отдельный механический этап.
- Application-specific MSI/Product/payload modules остаются в исходных family trees; common Setup не принимает family policy из shared core.
- `Burservis.iCem.DataMonitor` AddIn, frozen proto/SPA/catalogs и `rust/migration/source-manifest.json` не двигались и не копировались. Их содержимое/pins не менялись.

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

## История

- Parent/base: `c2724ccb80693f41309f81cea4d1f3155459bbe3`.
- Реализация этого инкремента: `748ea8735204d7ea444852da617af17ec18ee304`.
- Отчёт добавлен отдельным commit; итоговый HEAD/PR status проверяются в истории ветки и PR #51.
