using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Modbus TCP layer options (TZ_ARCH_V2 §6, station.ini; W5 supplies the values —
/// defaults per IniOptions).
///
/// v3: the register map is RUNTIME-SIZED — Total is the CONFIGURED dictionary
/// size (ini total) used for the register file BEFORE the first bus snapshot;
/// the first fresh snapshot re-shapes the register file to the bus header's
/// self-describing regCount (bus = truth). The v1 heartbeat/reserved-register
/// options of the 14000-register family map are REMOVED with W3 — the heartbeat
/// lives in the bus header state block (reg 5, layout v4; AddIn-written) and is
/// mirrored verbatim.
///
/// v4 tick law: TickMs is EXTERNAL config (station.ini modbus_tick_ms,
/// operator directive) — PLC-style range [200..500], default 500. The tick
/// performs a plain overwrite EVERY cycle; Δ-based change detection is BANNED.
/// The ini contract validates [200..500] (IniOptions.Validate); the publisher
/// ctor guards &lt; 1 ⇒ throw (fail-fast, ¬ silent default).
/// </summary>
public class ModbusOptions
{
    public const string Section = "Modbus";

    public int Port { get; set; } = 502;

    /// <summary>Configured dictionary size in registers (ini total). Default =
    /// BusLayout.RegCount. Sizing law: initial register file = Total; the first
    /// fresh bus snapshot re-sizes to the runtime bus regCount.</summary>
    public int Total { get; set; } = BusLayout.RegCount;

    /// <summary>Modbus refresh tick in ms (station.ini modbus_tick_ms). Default
    /// 500; operator PLC-style range [200..500]. Drives the bus→register-file
    /// copy cadence (plain overwrite every cycle, ¬ Δ-detection).</summary>
    public int TickMs { get; set; } = 500;
}