# ARCHIVED: CONTRACT_iCemEmule file_ownership (E1/E2/E3, WinForms-эра)

> Перенесено из `iCemEmule/specs/CONTRACT_iCemEmule.yaml` 2026-08-21 по решению владельца.
> Историческая декомпозиция владения файлами: `Ui/MainForm.cs`, `Ui/*Tab.cs`, `Ui/TrayApp.cs`
> удалены в W11.4 (движок headless); `EmuSettings.cs` живёт в `iCemEmule.Core/Core`;
> супервизия процессов — `Station.Emulator.Core/EmuleSupervisor.cs`;
> `Program.cs` — верхний уровень `iCemEmule/`. НЕ нормативно — историческая справка.

```yaml
file_ownership:
  E1_plugin_domain:   [Core/DeviceTemplate.cs, Core/TemplateCatalog.cs, Core/SimulationEngine.cs,
                       Core/LantableRegistry.cs, Core/AcePayloadProvider.cs,
                       Core/UnitConversion.cs, Core/UnitConvertingPayloadProvider.cs,
                       Dfb/Blocks/F300HpvtBlock.cs]
  E1_plugin_assets:   [templates/firmware_registry.json, templates/firmware_variables.json]
  E2_transport:       [Emitters/ComSerialEmitter.cs, Emitters/AceLanEmitter.cs, Emitters/AceFrameBuilder.cs,
                       SelfTest/FrameRoundTrip.cs]
  E3_ui_shell:        [Ui/MainForm.cs, Ui/LanTab.cs, Ui/ComTab.cs, Ui/UnitsTab.cs, Ui/EmuSettings.cs, Ui/TrayApp.cs,
                       Program.cs]
  # Program.cs — ТОЛЬКО E3. args: "--selftest" ⇒ run SelfTest ∧ exit code = fails count.
  # F300HpvtBlock.cs отнесён к E1_plugin_domain: это DFB-имитация 14 firmware-таблиц (данные), не транспорт.
```
