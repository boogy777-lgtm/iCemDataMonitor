# UI preservation

dashboard/ is a byte-for-byte copy of the current host wwwroot at the pinned commit.
It is served unchanged by icem-web (Axum + tonic), using the original REST/gRPC-Web contract.
See docs/WEB_STATUS.ru.md for implemented calls and remaining acceptance gates.
Desktop shell/emulator must use GPUI and preserve all existing custom controls.
Do not replace the current Apple-style visual language with stock component styling.
Shell state and emulator COM/LAN/Units pages still need implementation and parity tests.
Preserve the current UI behavior before changing look, layout or protocol projections.
