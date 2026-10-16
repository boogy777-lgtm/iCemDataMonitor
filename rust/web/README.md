# UI preservation

dashboard/ is a byte-for-byte copy of the current host wwwroot at the pinned commit.
It is not connected to a Rust server yet. It calls the original REST/gRPC-Web contract.
Shell state and emulator COM/LAN/Units pages still need implementation and parity tests.
Preserve the current UI behavior before changing look, layout or protocol projections.
