// 2026-09-01 (owner decree «Блок 2 п.4»): the TFM dropped to PLAIN net10.0
// with the WinForms tray removal — the platform context that silenced CA1416
// came from net10.0-windows. The host is Windows-only BY DECREE (tray,
// console window, HKCU autostart, named pipes, MMF — the whole design is
// Win32), so the assembly-level declaration restores the analyser's platform
// context without per-callsite suppression noise.
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.17763")]
