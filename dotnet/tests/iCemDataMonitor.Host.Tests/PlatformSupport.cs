// 2026-09-01 (owner decree «Блок 2 п.4»): the host assembly declares itself
// Windows-only (SupportedOSPlatform) after the TFM dropped to plain net10.0;
// that annotation propagates CA1416 to every consumer. The tests exercise the
// same Win32-bound surface — same declaration, same rationale.
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.17763")]
