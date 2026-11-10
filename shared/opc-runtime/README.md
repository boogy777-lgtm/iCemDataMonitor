# OPC Runtime — shared MSI and lifecycle

This independent Windows MSI is shared by both iCem application products. Its
authoring, canonical component inventory, read-only foreign-owner guard, source
provenance, acceptance matrix, and lifecycle tooling are owned here; it does not
belong to either application family. Application product policy and the existing
Setup launcher remain separate.

`installer/` contains the Runtime MSI producer and its authored inputs/tests;
`tools/` contains the build-SDK setup and acceptance harness. Shared WiX helpers
and the single pinned WiX tool manifest are in `shared/installer/`. The source build is
pinned, Release-only, and requires the documented Windows toolchain. The
production MSI must retain `Launch Condition="Installed"`. The only test copy
allowed to remove that condition is created after the disposable GitHub-hosted
runner isolation checks; it must never be published or installed on a workstation.

The detailed history, exact acceptance cases, and current limitations are in
[Runtime acceptance](../../rust/docs/OPC_RUNTIME_ACCEPTANCE_2026-11-10.ru.md).
In particular, customized DCOM descriptor repair remains a confirmed failure;
this layout change does not fix or mask it. No local MSI installation was run.
