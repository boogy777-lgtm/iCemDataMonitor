# CI-1: независимая проверка компонентов и общий compatibility gate

Scope — только маршрутизация существующих проверок. CI-2 (стабильность тестов) и
CI-3 (cache/performance, разделение Rust Cargo/browser/per-crate jobs) не реализованы. Политика выпуска,
Rust 1.99.1/locked graph, unsafe/lints, .NET production, source-manifest, frozen
proto и SPA не меняются. E23 не входит в этот инкремент.

Class: изменённые пути относятся к пересекающимся категориям `legacy`, `rust`,
`shared-contracts`, `packaging`, `infrastructure`, `documentation`; близкий
nonmember — только явно разрешённая Markdown-документация без runtime inputs.
Inv: достоверный Git diff либо полный набор проверок; неизменяемый baseline и
routing selftests обязательны, `CI gate` принимает только точные ожидаемые результаты.
N+1: новый путь/consumer добавляется в `tools/ci/routing-rules.json`; executable
coverage/dependency/workflow contracts в `tools/ci/test_routing.py` обнаруживают
неучтённые tracked paths, зависимости и пропущенные job связи.

## Граф и матрица

`.github/workflows/ci.yml` запускается на каждом PR в main, push в main и manual
dispatch, без path filter. Всегда выполняются Windows `classify`, `baseline`
(`Test-BaselineGuard.ps1`, `Verify-Baseline.ps1 -RequireComplete`) и `routing-tests`.
Условные jobs вызывают относительные reusable workflows того же commit:
`legacy-build.yml`, `rust-migration.yml`, `rust-installer-contracts.yml`.
Последний job имеет постоянное имя **CI gate** и `if: always()`.

| Изменение | .NET build/test/MSI | Полный Rust + browser + differential | Installer contracts |
|---|---|---|---|
| Обычные Rust apps/crates/tools, Cargo/toolchain/assets | — | Да | Да |
| Известные независимые .NET project roots | Да | — | — |
| MMF/wire/proto/API/oracle/shared inputs | Да | Да | Да |
| Rust packaging implementation | — | Да | Да |
| Исходный installer или AddIn SDK inputs | Да | Да | Да |
| Workflow/routing/manifest/ledger, неизвестный путь | Да | Да | Да |
| Только разрешённая Markdown-документация | — | — | — |
| Невосстановимый base/diff, manual dispatch | Да | Да | Да |

Категории складываются: например, `iCemEmule.Core/**` — `legacy` и
`shared-contracts`; `rust/crates/icem-bus/**` — `rust` и `shared-contracts`;
`rust/installer/**` — `rust` и `packaging`. JSON содержит отдельную таблицу
category → workloads. Нет правила «все .NET изменения запускают Rust» или
«все Rust изменения запускают .NET». Неизвестный root/project/тип входа выбирает
все workloads; selftest требует явно пересмотреть новый tracked путь.

## Почему shared scopes шире proto

- `iCemEmule.Core/**`: C# differential oracles компилируют Core/Control/Dfb/Emitters;
  broad scope покрывает новые исходники этих семей без brittle пофайлового списка.
- `iCemDataMonitor.Sdk/Bus/**`, `Data/Bus/**`, decoder/register/Modbus interfaces,
  `Transport/Modbus*.cs` и models: MMF layout и wire projections являются
  межъязыковыми обязательствами. Rust `icem-bus`, `icem-protocols` и `contracts`
  также проверяют обе реализации. Остальные Rust crates сохраняют независимость.
- `Host/Grpc/**`, `Host.Core/Endpoints/**`, `iCemDataMonitor.Contracts/**`,
  `specs/**` и frozen `Host/wwwroot/**`: API, schema и сохранённая web surface.
  В частности, `icem-config/src/lib.rs` делает `include_str!` для
  `specs/dotnet-server/Э5_config.csv`.
- `templates/**`, `iCemEmulator/templates/**`, `Plugins.Icem/LantableRegistry.cs`
  и `Plugins.Icem/templates/**`: Host/emulator catalogs и действующий decoder.
- `docs/Wireshark/**` проверяется до любого docs-only shortcut:
  `tests/support/HostSnifferOracle.cs` читает настоящий `iCemCrio.pcapng.bin`.
- `assets/**`, root `installer/**`, `Burservis.iCem.DataMonitor/**`,
  Directory.Build props/targets и global.json: frozen icons, branding/bitmap
  generator/harvester и реальные SDK/build inputs. Весь общий `installer/**` и `Burservis.iCem.DataMonitor/**`, включая
  следующий неизвестный тип build input, запускают обе реализации.

Installer contracts проверяют синтетический AddIn и native MSI ownership.
Они не доказывают компиляцию с proprietary SDK и не публикуют fixture MSI.
Manual `rust-windows-release.yml` сохраняет реальный SDK/AddIn provenance,
полный payload, install/upgrade/downgrade/uninstall, smoke/browser и draft release
поведение; он вызывает тот же reusable installer workflow. Automatic PR trigger
перенесён в coordinator. `rust-addin-build.yml` и `release.yml` не изменены.

## Fail-closed diff и gate

Checkout classifier имеет полную историю. Event читается из `GITHUB_EVENT_PATH`,
SHA не интерполируется в shell: PR использует `merge-base(base.sha, head.sha)` →
head, push — before → after. Проверяются commit objects и SHA format; Git вызывается
как argv. `git diff --no-renames --name-only -z` сохраняет deleted/old renamed
пути, tab/newline и не зависит от Git quoting. Ошибка Git/недоступный base,
malformed event/diff/UTF-8 ведут к полному набору. Пустой список разрешён только
после успешного проверенного diff. Никакой недостоверный ввод не превращается
в успешный docs-only skip.

Gate получает `toJSON(needs)` через environment, проверяет точную схему шести
зависимостей и outputs `legacy/rust/packaging` со строками только `true/false`.
Classify/baseline/selftests должны завершиться `success`. Выбранный workload
должен иметь `success`, невыбранный — ровно `skipped`. Failure, cancelled,
unexpected skip, malformed/missing outputs и неожиданный запуск отклоняются.
Gate сам делает checkout и не зависит от успешной загрузки предыдущего artifact.
Classifier/selftest/gate ограничены 5 минутами, baseline — 10 минутами.

Coordinator concurrency содержит identity `ci` и branch/ref; только obsolete PR
может быть отменён. Reusable workflows не имеют конкурирующей cancellation group;
manual release group сохраняет прежнюю общую identity `rust-windows-release-manual`
и `cancel-in-progress: false`. Это сериализует выбор `next` между ветками. Manual
release не отменяется новым PR или самостоятельным reusable вызовом.

## Проверка и границы доказательства

Локально запускается Python stdlib suite:

```text
python -m unittest discover -s rust/tools/ci -p "test_*.py" -v
```

Suite покрывает матрицу, смешанные scopes, unknown paths, реальный temporary Git
rename/delete/PR merge-base/empty diff, NUL/tab/newline parser, unavailable/failed
Git, malformed inputs, CLI fallback, все комбинации workload selection и result
states, consumer dependencies и coordinator/reusable wiring. Linux suite не
доказывает Windows runtime/MSI. Exact-head Windows verdict и PR provenance
фиксируются владельцем после независимого review; прежние E22 runs не являются
доказательством CI-1 и ускорение до новых измерений не заявляется.

На проверенном исходном состоянии main `protected=false`, required contexts пусты,
repository rulesets пусты; GET branch protection вернул 403 integration/admin
unavailable. Поэтому **CI gate подготовлен как единый required-check candidate,
но server-enforced обязательность ещё не установлена**. Workflow не может сам
гарантировать запрет merge; требование CI gate в branch protection/ruleset должно
быть отдельно включено владельцем с соответствующими правами.

Изменение baseline-файла `.github/workflows/ci.yml` требует обновить существующую
exact-hash запись `migration/legacy-divergences.json` после публикации проверенного
source commit и создания реального PR. До этого обновления baseline ожидаемо
отклоняет новый hash; PR number/provenance не подставляются заранее. Source manifest
остаётся pinned, все 513 baseline entries и 14 одобренных отклонений сохраняются.
