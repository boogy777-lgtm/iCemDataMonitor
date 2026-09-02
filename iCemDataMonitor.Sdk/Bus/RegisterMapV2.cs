// iCemDataMonitor.Sdk.Bus — Register Map v2 entry schema (TZ_ARCH_V2 §3).
// Schema only: registry PARSING/building belongs to W4 (insite-data); W2 (AddIn)
// consumes this record for com_main/raw_reserve addressing.

namespace iCemDataMonitor.Sdk.Bus;

/// <summary>
/// One registry v2 entry: canonical variable name → zone → absolute register address.
/// Addressing rule (TZ_ARCH_V2 §3): address = zoneBase + index × 2 (CDAB float pair,
/// low word at the even address). The registry index is the entry's 0-based position
/// within its zone's registry section — NOT stored here, only the resolved address.
/// </summary>
/// <param name="CanonicalName">Canonical name ("Lan/…" prefix for LAN entries). Raw zone entries use "R%addr%" placeholders.</param>
/// <param name="Zone">Zone the variable lives in.</param>
/// <param name="Address">Absolute register address (zone base + index × 2).</param>
public readonly record struct RegisterEntryV2(string CanonicalName, ZoneKind Zone, int Address);

/// <summary>
/// Register map v2 addressing helpers and overflow rule.
///
/// Overflow rule (TZ_ARCH_V2 §3 "mapping"): when the registry section for a zone
/// holds MORE entries than the zone's FloatCapacity, the map is truncated in
/// registry order — entries beyond capacity are dropped, the builder logs ONE
/// warning per dropped entry (count + names), and construction still succeeds.
/// ¬ exception (truncation is the spec'd behavior, not a configuration error).
/// lan_ext (layout v3) follows the SAME registry rule as lan: address =
/// 650 + index × 2, overflow beyond 3877 floats truncates + warns.
/// Raw zone (RawReserve) has no registry: it is positional only — first 75 numeric
/// tokens land at 450, 452, … (non-numeric tokens skipped, counter incremented).
/// Wits zone (600..649) is RESERVED in layout v3: no registry entries, no writes.
/// </summary>
public static class RegisterMapV2
{
    /// <summary>Registers per float32 (CDAB pair).</summary>
    public const int WordsPerFloat = 2;

    /// <summary>Absolute address of the index-th float in a zone: BaseReg + index × 2.</summary>
    public static int AddressFor(ZoneDescriptor zone, int index) => zone.BaseReg + index * WordsPerFloat;

    /// <summary>Absolute address using the spec-default zone bounds.</summary>
    public static int AddressFor(ZoneKind zone, int index) => AddressFor(ZoneDescriptor.DefaultFor(zone), index);

    /// <summary>True when the index-th float exceeds the zone capacity (⇒ truncate + warn).</summary>
    public static bool IsOverflow(ZoneDescriptor zone, int index) => index >= zone.FloatCapacity;
}
