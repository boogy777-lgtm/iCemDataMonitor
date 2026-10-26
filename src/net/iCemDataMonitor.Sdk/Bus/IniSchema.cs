// iCemDataMonitor.Sdk.Bus — station.ini contract schema + validator (TZ_ARCH_V2 §6).
// Schema/validation only: ini PARSING + create-default live in
// iCemDataMonitor.Core\Config\IniConfig.cs (W5 insite-core, landed).
// Attribute-free by contract: the ini file is line-based key=value, NOT JSON —
// this record is the in-memory projection, not a serializer target.

namespace iCemDataMonitor.Sdk.Bus;

/// <summary>
/// station.ini options, defaults EXACTLY per TZ_ARCH_V2 §6.
/// Semantics: read ONCE at startup; change ⇒ restart (hot-reload forbidden by
/// operator). Invalid values ⇒ fail-fast with a clear log error (¬ silent default).
/// 2026-08-31 (owner decree): DEAD KEYS REMOVED — com_port and the [zones]
/// com/lan/raw base+len keys left the schema entirely: the AddIn never read
/// the ini (it lives on compile-time BusLayout.*BaseReg constants —
/// TelemetryServiceImpl.cs / RegisterDecoder.cs), so the keys were
/// parsed/validated/re-rendered with ZERO consumers. The zone geometry is
/// now compile-time (BusLayout); the ini carries only the v4-compat inert
/// wits/lan_ext keys (untouched BY DESIGN) and total. Removed keys are
/// RETIRED in the parser (accepted-and-ignored) so the live stand file keeps
/// loading across the upgrade.
/// total 10001 (D3 dictionary growth, user decision). total is RUNTIME DATA
/// (dynamic-regCount law v1.3.0): any value in [650..65535] is a valid bus
/// shape — future dictionary growth = ini-only, ¬ code change.
/// </summary>
/// <param name="ModbusOn">1 = Modbus TCP layer on (default 1 — owner decree
/// 2026-08-31 «modbus и web по дефолту включены»).</param>
/// <param name="OpcdaOn">1 = OPC DA projection on (default 0).</param>
/// <param name="LansnifferOn">1 = LAN sniffer on (wire reader only — the bus redirect LanBusSink was REMOVED 2026-08-27, producer-only decree).
/// Default 1 (owner decree 2026-09-01: «проверь дефолтные настройки config.ini — sniffer/modbus должны быть включены» — sniffer ON out of the box; Э5 row lansniffer_on).</param>
/// <param name="ModbusPort">Modbus TCP port (default 502).</param>
/// <param name="WebPort">Web layer port (default 8090).</param>
/// <param name="MmfName">Mapping name (default Global\iCemDataMonitorBus_v1 — must match the deployed AddIn writer).</param>
/// <param name="MmfPath">Persisted backing file (default %ProgramData%\Halliburton\iCem\DataMonitoring\Data.dat).</param>
/// <param name="WitsBase">v4 wits zone base (default 600) — absorbed into ace_full reserve in v5; inert by design (2026-08-31: retained).</param>
/// <param name="WitsLen">v4 wits zone length in regs, even (default 0 = zone off).</param>
/// <param name="LanExtBase">v4 lan_ext zone base (default 650) — absorbed into ace_full reserve in v5; inert by design (2026-08-31: retained).</param>
/// <param name="LanExtLen">v4 lan_ext zone length in regs, even (default 0 = zone off).</param>
/// <param name="Total">Total register count (default 10001) — RUNTIME DATA: any value in
/// [BusLayout.RegCountMin..BusLayout.RegCountMax] is a valid bus shape (dynamic-regCount law).</param>
/// <param name="ModbusTickMs">Modbus refresh tick in ms (default 500) — EXTERNAL config,
/// operator PLC-style range [200..500]. The publisher copies the bus snapshot into the
/// register file EVERY cycle (plain overwrite; Δ-based change detection BANNED).</param>
/// <param name="UiSetAsDefault">1 = Status window "Set as default" checkbox is ON at startup:
/// runtime layer toggles persist to station.ini (default 1). OPTIONAL key in the file —
/// absent ⇒ 1 (operator v2 files predate the key); the writer always emits it.</param>
/// <param name="WebOn">1 = Kestrel SPA dashboard on :web_port (index.html + gRPC-Web +
/// ring-history); 0 = ops surface only (/api/status*, /api/regs). The :web_port TCP
/// endpoint is listened on ALWAYS regardless of web_on — the flag only gates the SPA
/// dashboard surface. Default 1 (owner decree 2026-08-28 «по дефолту web должен быть
/// включён» — dashboard enabled out of the box; Э5 row web_on). OPTIONAL key:
/// absent ⇒ 1 (the tombstone-era «absent ⇒ 0» rule is superseded — migration law:
/// an old file without the key gains the dashboard, never loses it).</param>
/// <param name="WebRemoteOn">0 = bind :web_port loopback-only; 1 = ListenAnyIp :web_port
/// (remote browser inside the CLOSED security contour; non-discrimination — not a single
/// IP-guard exists; authorization = the contour itself). Default 0 = loopback-only.</param>
public sealed record IniOptions(
    int ModbusOn = 1,
    int OpcdaOn = 0,
    int LansnifferOn = 1, // 2026-09-01 owner decree: sniffer ON by default (was 0 = opt-in; Э5_config.csv amended)
    int ModbusPort = 502,
    int WebPort = 8090,
    string MmfName = @"Global\iCemDataMonitorBus_v1",
    string MmfPath = @"C:\ProgramData\Halliburton\iCem\DataMonitoring\Data.dat",
    int WitsBase = 600,
    int WitsLen = 0,
    int LanExtBase = 650,
    int LanExtLen = 0,
    int Total = 10001,
    int ModbusTickMs = 500,
    int UiSetAsDefault = 1,
    int WebOn = 1, // literal mirror of DefaultWebOn — equivalence pinned by Sdk.Tests (primary-ctor params cannot reference type members)
    int WebRemoteOn = 0)
{
    /// <summary>
    /// Single source of the web_on OPTIONAL-key default (owner decree
    /// 2026-08-28: «по дефолту web должен быть включён»). The record default,
    /// the strict parser's absent-key fallback (Core IniConfig.Parse →
    /// GetIntOrDefault) and the spec (Э5_config.csv row web_on) all read THIS
    /// constant — no literal 0/1 may drift at any of the three sites.
    /// </summary>
    public const int DefaultWebOn = 1;

    /// <summary>
    /// Validate options against the bus contract. Empty result = valid.
    /// Checks: flag values 0..1, port ranges, modbus_tick_ms [200..500]
    /// (PLC cadence), non-empty mapping name/path,
    /// zone base ≥ ZoneBaseMin (4), even zone lengths (CDAB pairs),
    /// total ∈ [650..65535] (dynamic-regCount law), both zone keys
    /// (wits/lan_ext — v4-compat, default 0 = off; 2026-08-31: the dead
    /// com/lan/raw keys are REMOVED from the schema — runtime zone geometry
    /// is compile-time BusLayout) inside 0..total-1, no zone overlap.
    /// total is RUNTIME DATA — NOT pinned to a compile-time constant.
    /// </summary>
    public static IReadOnlyList<string> Validate(IniOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var errors = new List<string>();

        CheckFlag(o.ModbusOn, "modbus_on", errors);
        CheckFlag(o.OpcdaOn, "opcda_on", errors);
        CheckFlag(o.LansnifferOn, "lansniffer_on", errors);
        CheckFlag(o.UiSetAsDefault, "ui_set_as_default", errors);
        CheckFlag(o.WebOn, "web_on", errors);
        CheckFlag(o.WebRemoteOn, "web_remote_on", errors);

        if (o.ModbusPort is < 1 or > 65535)
            errors.Add($"modbus_port out of range [1..65535]: {o.ModbusPort}");
        if (o.ModbusTickMs is < 200 or > 500)
            errors.Add($"modbus_tick_ms out of range [200..500] (PLC cadence): {o.ModbusTickMs}");
        if (o.WebPort is < 1 or > 65535)
            errors.Add($"web_port out of range [1..65535]: {o.WebPort}");

        if (string.IsNullOrWhiteSpace(o.MmfName))
            errors.Add("mmf_name must be non-empty");
        if (string.IsNullOrWhiteSpace(o.MmfPath))
            errors.Add("mmf_path must be non-empty");

        var zones = new (string Name, int Base, int Len)[]
        {
            ("wits", o.WitsBase, o.WitsLen),
            ("lan_ext", o.LanExtBase, o.LanExtLen),
        };

        // total is RUNTIME DATA (dynamic-regCount law): accepted range = [650..65535],
        // ¬ pinned to any compile-time constant. Zone fit checks run against o.Total.
        if (o.Total < BusLayout.RegCountMin || o.Total > BusLayout.RegCountMax)
            errors.Add($"total must be in [{BusLayout.RegCountMin}..{BusLayout.RegCountMax}] " +
                       $"(dynamic-regCount law): {o.Total}");

        foreach (var (name, @base, len) in zones)
        {
            if (@base < BusLayout.ZoneBaseMin)
                errors.Add($"zones.{name}_base must be >= {BusLayout.ZoneBaseMin} " +
                           $"(zone-map floor; header state regs 0..15 are separate): {@base}");
            if (len < 0)
                errors.Add($"zones.{name}_len must be non-negative (0 = zone off): {len}");
            if (len % 2 != 0)
                errors.Add($"zones.{name}_len must be even (float32 CDAB pairs): {len}");
            if (@base >= 0 && len >= 0)
            {
                // Fit law (§7): every zone key lives inside 0..total-1 — the BASE
                // itself must be inside the dictionary even when len=0 (zone off);
                // a non-empty zone additionally fits its last register.
                if ((long)@base > (long)o.Total - 1)
                    errors.Add($"zones.{name} out of range: base {@base} " +
                               $"exceeds 0..{o.Total - 1}");
                else if (len > 0 && (long)@base + len - 1 > (long)o.Total - 1)
                    errors.Add($"zones.{name} out of range: {@base}..{(long)@base + len - 1} " +
                               $"exceeds 0..{o.Total - 1}");
            }
        }

        // Pairwise overlap on half-open ranges [Base, Base + Len)
        for (int i = 0; i < zones.Length; i++)
        {
            for (int j = i + 1; j < zones.Length; j++)
            {
                var (nameA, baseA, lenA) = zones[i];
                var (nameB, baseB, lenB) = zones[j];
                long endA = (long)baseA + lenA, endB = (long)baseB + lenB;
                if (baseA < endB && baseB < endA)
                    errors.Add($"zone overlap: {nameA} [{baseA}..{endA - 1}] ∩ {nameB} [{baseB}..{endB - 1}]");
            }
        }

        return errors;
    }

    private static void CheckFlag(int value, string key, List<string> errors)
    {
        if (value is not (0 or 1))
            errors.Add($"{key} must be 0 or 1: {value}");
    }
}
