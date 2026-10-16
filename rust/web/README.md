# UI preservation

dashboard/ preserves current host wwwroot at the pinned commit, with the D16/D17
inline theme predicate fixes described below; all other content stays byte-for-byte.
It is served by icem-web (Axum + tonic), using the original REST/gRPC-Web contract.
See docs/WEB_STATUS.ru.md for implemented calls and remaining acceptance gates.
Desktop shell/emulator must use GPUI and preserve all existing custom controls.
Do not replace the current Apple-style visual language with stock component styling.
Shell state and emulator COM/LAN/Units pages still need implementation and parity tests.
Preserve the current UI behavior before changing look, layout or protocol projections.

D16: первая точечная правка SPA — исправлена инверсия bool в inline SetUiTheme:
после bundle click визуальный light отправлял false. Теперь отправляется фактический
visual state. main.js/CSS/icons и внешний вид не меняются; original .NET index.html
не меняется. Verify-Baseline допускает ровно две замены D16/D17 и проверяет остальной HTML
целиком; Node VM regression и browser scenario проверяют обе темы.

D17: inline GetUiPrefs принимал только ASCII trailer с colon+space. Tonic отдаёт
валидный grpc-status:0. Parser теперь принимает colon с нулём/пробелами/tab, только
целый grpc-status=0; layout/styles не меняются. Node VM проверяет исходный failure
и исправленный разбор, browser — тему во второй независимой странице.
