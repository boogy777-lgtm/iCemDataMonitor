# CI: маршрутизация, общий gate и bounded Cargo cache

Scope — маршрутизация существующих проверок поверх принятого prerequisite CI-2:
main `25935c0481faf29f1e5ec15924cbc71e70203f62`, [PR21](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/21).
[CI-2a/D54](CI2_CONFIG_RACE.ru.md) исправляет canonical INI read/create race;
[CI-2b/D55](CI2_TEST_INFRA.ru.md) сохраняет synthetic register fixture и диагностику CI.
CI-1 PR20 и prerequisite CI-2 PR21 приняты в main
`9b9b747854098425281419086db0587c776ac870`; post-merge coordinator run
`39548701932` — PASS. CI-3 ниже подготовлен для отдельной Windows-проверки. Политика выпуска,
Rust 1.99.1/locked graph, unsafe/lints, .NET production, source-manifest, frozen
proto и SPA не меняются. E23 не входит в этот инкремент.

Class: изменённые пути относятся к пересекающимся категориям `legacy`, `rust`,
`shared-contracts`, `packaging`, `infrastructure`, `documentation`; близкий
nonmember — только явно разрешённая Markdown-документация без runtime inputs.
Inv: достоверный Git diff либо полный набор проверок; exact-hash approval metadata
делегируется только фактически изменённым source blobs того же diff. Ledger-only и
provenance-only — nonmembers. Полный неизменяемый baseline и routing selftests
обязательны; `CI gate` принимает только точные ожидаемые результаты.
N+1: новый путь/consumer добавляется в `tools/ci/routing-rules.json`; executable
coverage/dependency/workflow contracts в `tools/ci/test_routing.py` обнаруживают
неучтённые tracked paths, зависимости и пропущенные job связи.
Legacy job имеет один исполняемый источник в `legacy-build.yml`; диагностический
контракт проверяет отдельный `RoutingTests.test_legacy_diagnostics_contract`.

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
| Workflow/routing/manifest, недоказанный ledger или неизвестный путь | Да | Да | Да |
| Ledger с доказанными source approvals в том же diff | По всем source/остальным путям | По всем source/остальным путям | По всем source/остальным путям |
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

Raw `categories(path)` и `classify(paths)` сохраняют все шесть категорий и прежний
fail-closed результат для ledger. Только Git-aware CLI может убрать из dependency
classification ровно `rust/migration/legacy-divergences.json`, если докажет связь
metadata с реальными source changes. Один immutable context хранит проверенные
base/head и paths; PR object reads используют тот же merge-base, что и diff.
Manifest должен быть одним неизменённым regular Git blob в обеих ревизиях.
Manifest и оба ledger читаются из Git objects как strict UTF-8 JSON, с запретом
повторяющихся keys, проверкой формы/версии/pinned source commit, уникальных
canonical paths, baseline/approved hashes и структуры provenance. Каждый metadata
blob ограничен 1 MiB (текущие документы меньше 128 KiB); symlink/tree/missing object,
ошибка Git или любая неоднозначность оставляет ledger и полный набор workloads.
Git path queries используют `--literal-pathspecs`; source contents не загружаются.

Нужен минимум один добавленный/обновлённый approval без удаления существующих.
Каждый изменённый approval обязан относиться к пути фактического diff и regular
blob в обеих ревизиях: base hash равен прежнему approved hash либо immutable
manifest baseline hash для нового approval; head hash равен новому approved hash
и отличается от base. Existing baseline identity не меняется. Ledger-only,
format/reorder-only, provenance-only и approval к неизменённому/постороннему source
не проходят proof. Все исходные пути, кроме единственного доказанного ledger,
по-прежнему проходят JSON rules: independent Legacy approval запускает Legacy;
shared/frozen input, workflow или unknown path сохраняет полный dependency closure.
Новый компонент добавляется в JSON rules, без project-specific ветвей в proof.
Диагностика сохраняет исходные paths/categories и явно выводит `metadata_delegation`
с associated source paths; три `GITHUB_OUTPUT` booleans и gate schema неизменны.

Это proof зависимости metadata, а не новая baseline/provenance policy. В обоих
случаях — с делегированием и без него — всегда выполняются полный
`Verify-Baseline.ps1 -RequireComplete`, frozen/catalog/provenance проверки и
`Test-BaselineGuard.ps1`; их обязанности не переносятся в classifier. Provenance
commit не обязан равняться новому head (это создало бы циклическую ссылку); proof
не требует сетевого доступа или доступности указанного provenance commit.

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
states, consumer dependencies и coordinator/reusable wiring. Real Git subtests
проверяют updated/new independent Legacy approvals, mixed/shared/frozen/unknown
closures, base/head blob binding, invalid/missing/symlink/oversize/UTF-8 metadata,
JSON/path duplicates, ledger nonmembers, immutable manifest, unavailable Git,
PR merge-base вместо base tip, независимость от worktree и CLI diagnostics/outputs.
Fixture hashes используют Git path-aware canonical conversion, как `git add`;
отдельный Linux real-Git test с `core.autocrlf=true` и явными CRLF bytes проверяет
new/updated approvals и baseline hashes. Это контролируемая проверка CRLF, а не
доказательство запуска suite на Windows. Linux suite не
доказывает Windows runtime/MSI. Exact-head Windows verdict и PR provenance
фиксируются владельцем после независимого review; прежние E22 runs не являются
доказательством CI-1 и ускорение до новых измерений не заявляется.

На проверенном исходном состоянии main `protected=false`, required contexts пусты,
repository rulesets пусты; GET branch protection вернул 403 integration/admin
unavailable. Поэтому **CI gate подготовлен как единый required-check candidate,
но server-enforced обязательность ещё не установлена**. Workflow не может сам
гарантировать запрет merge; требование CI gate в branch protection/ruleset должно
быть отдельно включено владельцем с соответствующими правами.

Изменение baseline-файла `.github/workflows/ci.yml` записано в существующем
exact-hash ledger: [PR20](https://github.com/boogy777-lgtm/iCemDataMonitor/pull/20),
reviewed source commit `3f04ae0816c1d9286032a2b8bb8ed41d65a447d3`.
Весь suffix после `jobs:\n` из `.github/workflows/ci.yml` immutable main
`25935c0481faf29f1e5ec15924cbc71e70203f62` выделен побайтово в reusable
`legacy-build.yml`. Сохранены полный CI.slnf build/test, `--no-build`,
`Category!=Live`, blame-hang 10m и MSI smoke с PR2 `-NoPause`/30m limit.
Диагностика отдельно происходит из PR21/reviewed source
`843331ece3efa6e243fbe7dec357523054e389ea`: TRX без общего имени, console
Tee-Object с немедленным сохранением/возвратом LASTEXITCODE, allowlist provenance
и `if: always()` upload пяти allowlisted patterns с run ID/attempt и retention 7 days.
Coordinator остаётся побайтово CI-1 PR20 (`e5d19816563dadeb80967349821e0db6ae1ae15c`);
его ledger provenance не заменяется provenance исходного PR21 workflow.
Source manifest остаётся pinned, все 513 baseline entries, прежние 14 одобренных
отклонений и новый approval15 `RegisterMirrorTests.cs` из PR21 сохраняются.
Фактические exact-head CI runs фиксируются в PR;
неполный предварительный source commit без ledger не является принятым деревом.

Прежний CI-1 head `0283817` не принят: Rust run `39433195261`, job
`118664064048` завершился FAIL в
`concurrent_creation_publishes_only_complete_valid_file` (Windows NotFound).
Legacy 941 tests, MSI contracts и baseline этого head прошли, но общий gate FAIL.
CI-2 final head `45d8e93` получил Windows PASS с одной попытки: Legacy
`39440073028` (941/941 + MSI), Rust `39440073152` (246 tests + Chrome/differential)
и installer contracts `39440073117`. Проверено дерево merge PR21 в main;
эти результаты относятся к prerequisite CI-2 и не являются приёмкой обновлённого
CI-1. Post-merge main runs Legacy `39545433194` и Rust `39545433323` также
завершились SUCCESS с первой попытки; это подтверждает prerequisite CI-2. Обновлённый CI-1 затем принят в PR20; полный post-merge coordinator
`39548701932` на main `9b9b747854098425281419086db0587c776ac870` — PASS
(Legacy 941, Rust 246, Chrome/C# differential и MSI contracts). Это baseline CI-3,
а не доказательство его ускорения.


## CI-3: повторное использование Cargo downloads и debug build

Class: Windows x64 MSVC Cargo downloads/debug inputs в одном прежнем Rust job;
близкие nonmembers — browser, differential evidence, MSI, SDK и release payload.
Inv: cache hit/miss не меняет ни одной проверки. `cache_contract.py` исполняемо
проверяет allowlist путей, единственный совместимый fallback, locked/offline,
clippy `-D warnings`, непосредственный exit-code propagation и порядок guards.
N+1: новый Cargo input добавляется в `cache_identity.py`; общий Scaffold prefix
имеет один owner `Verify-ScaffoldInputs.ps1` и выполняется до restore debug, затем
снова из полного Scaffold. Изменения команд/flags и common input owners входят
в compatibility identity; stdlib mutation tests выявляют потерю обязательств.

Для stdlib tooling явно устанавливается Python 3.12 (`setup-python@v5`).
Job сохраняется целиком: install pin → identity/contracts → restore downloads →
единственный `cargo fetch --locked` → полный Inputs с `-RequireCompleteBaseline` →
restore debug → прежние native/logger/emulator preflights → полный Scaffold →
Chrome и evidence. После fetch все прежние Cargo проверки offline/locked.
Inputs содержит baseline guard, packaging guard, полный baseline, desktop guards,
metadata edition/MSRV и fmt. Полный Scaffold повторяет Inputs и прежние
check/build/test/clippy. У проверок нет cache-hit conditions; только save зависит
от успешного выполнения всех checks и отсутствия exact primary key hit.

`actions/cache/restore@v5` и `save@v5` имеют два bounded набора:

| Cache | Пути | Ключ и fallback |
|---|---|---|
| Cargo downloads | Только `$CARGO_HOME/registry/{index,cache,src}` и `git/{db,checkouts}` | Exact compatibility identity, включающая Cargo.lock; без restore prefix |
| Cargo debug | Только `rust/target/debug` | compatibility + source SHA; ровно один prefix полной compatibility identity |

Compatibility включает Windows/архитектуру, ImageOS/ImageVersion, полный
`rustc --version --verbose` (host/version), Cargo version, pin, lockfile, все
workspace Cargo.toml, absence/presence `.cargo/config[.toml]` по Cargo ancestor
search, actual workflow и scripts с командами/flags. Выбранные MSVC/SDK/include/lib
переменные входят только как hash, без публикации значений. Compiler/Cargo/native
flags/wrappers/target/profile overrides отклоняются fail closed.
Native tool/flag families (включая ARFLAGS/RANLIBFLAGS и RC, даже пустые) отклоняются в base,
HOST_/TARGET_ и target-suffix формах; SDK/include/lib paths остаются hash inputs.
Непустые Cargo configuration overrides и внешние Cargo configs также отклоняются; это узкий
контракт существующего job, не универсальный конфигуратор. Изменение этой политики
требует пересмотра identity и tests. CPU model не входит в compatibility key.

Весь Cargo home, `target/` целиком, release/dist, differential JSON/oracle
intermediates, browser/node, MSI и SDK outputs не кэшируются. Старые evidence
uploads сохраняются. `cargo-cache-evidence` (7 days) и job summary содержат только
keys/hit/matched key, source/compatibility identity, runner image/arch/compiler и
CPU count/model. Никаких dumps environment/config/credentials. Actual overhead
restore/save берётся из длительностей отдельных GHA steps; сравниваются также
полный job и неизменённые preflight/Scaffold/browser steps.

Исходное измерение до CI-3: run `39547068850`, Rust job `119047616914` —
14m11s; emulator preflight 8m22s, Scaffold 3m54s, browser 20s, fetch 15s.
Прежние cold runs варьировали примерно 8m55s–13m17s, поэтому одно сравнение
на разных runners не доказывает причину ускорения. Отдельные per-crate/browser
jobs не добавляются: browser занимает 20s и не оправдывает новый runner.

Локально проверяются stdlib suite (включая отрицательные mutations root/release
cache paths, broad fallback, conditional checks, пропущенные commands/baseline,
lost locked/offline/clippy и подавленные exit codes), actionlint, PowerShell
parse и полный Inputs. Windows cold/warm full coordinator runs для CI-3 **pending**.
План: cold PASS, затем review/docs-only evidence commit с тем же compatibility
identity и warm full coordinator PASS. Это разные source SHA при неизменных build
inputs; сравнение не называется same-exact-head. При image/key mismatch измерение
warm reuse не считается доказанным. Числа ускорения/экономии до actual runs не заявляются.
