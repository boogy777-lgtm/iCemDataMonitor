# Профили ordinal identity — промежуточная политика

`icem-emulation` использует одну фиксированную таблицу на весь процесс и на все
`Key`/сравнения. Переключение при работе процесса запрещено: уже построенные
ключи `BTreeMap` иначе могли бы потерять согласованность. Это не Unicode full
fold, не нормализация и не изменение ASCII-only протокольных токенов.

| Профиль сборки | Доказанный тестовый provider | Выбор Cargo |
|---|---|---|
| исторический | Windows 10.0.19045, .NET 10.0.13, ICU, sort 24985 | без feature |
| второй | Windows 10.0.26100, .NET 10.0.14, ICU, sort 31129 | `--features icem-emulation/ordinal-icu-26100` |

Выбор **явный при сборке**, не эвристика по версии Windows или поиск .NET во
время запуска. На неизвестных OS/BCL/NLS/ICU этот документ не обещает
совместимость. Перенос бинарника на другую машину сам по себе не переключает
таблицу; выбор для целевой системы и отдельный gate перед развертыванием
остаются обязанностью сборки/владельца. Существующий `Build-WindowsRelease.ps1`
пока не выбирает второй профиль и не маркирует MSI им; это не готовый
универсальный Setup и не подтверждение production deployment.

`tests/fixtures/ordinal-icu-19045.json` — независимый полный oracle VM,
SHA-256 `2aee7c7a28df6793d88054b12be89a44ee9b562f981b737e83e2c9f0e345c0f3`.
`tests/fixtures/ordinal-icu-26100.json` — независимый полный oracle hosted run
[42946436601](https://github.com/boogy777-lgtm/iCemDataMonitor/actions/runs/42946436601),
artifact `13823686027`, SHA-256
`3cd7e208de9b2d1e898f37fc753f47f774df89a4d86b11acff28f15b4e5a96c2`.
Оба файла сохранены побайтно; новый `identity_table_icu_26100.rs` выведен из
полной второй карты, не из списка отличающихся codepoints. Исторические
`identity_table.rs`, `ordinal-identity-baseline.json` и генератор не изменены.

Тест всегда сравнивает **обе** таблицы с обеими полными захваченными картами;
Windows-gated тест дополнительно сравнивает все 1 112 064 scalars активной
сборки с независимым текущим BCL, включая Unicode-width, metadata provider и
контекстные witnesses с NUL/supplementary/combining characters. Мismatch и
неизвестная metadata — FAIL, не автоматическая регенерация. Hosted Rust workflow
явно выбирает второй профиль; локальный `Verify-Scaffold.ps1` сохраняет
исторический default и принимает `-OrdinalIcu26100` для второго. Подтверждение
каждого provider требует реального выполненного теста на соответствующем host,
а не только сравнения с захваченным JSON.
