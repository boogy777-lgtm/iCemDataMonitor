using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using iCemDataMonitor.Data.Bus;
using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Bus;

namespace iCemDataMonitor.Host.Core.Endpoints;

/// <summary>
/// W6 register mirror endpoints: live table of the bus register file 0..599 for
/// the ops surface. Reads only — the POC never writes body regs.
/// 2026-08-27 (corrected decree): /api/regs/history is BACK (Э2 line 47) —
/// addr-keyed ring samples {ts,v} per address, 600 s @ 1 Hz; addresses must be
/// even and in 4..599; no addr ⇒ 400; ring not registered (web_on=0) ⇒ 503.
/// /api/regs reads the bus directly and keeps working unchanged.
/// </summary>
public static class RegisterMirrorEndpoints
{
    private const int SystemRegCount = 4;          // regs 0..3
    private const int PairStart = 4;               // first float-pair address
    private const int PairEndExclusive = 600;      // raw zone ends at 599

    /// <summary>
    /// HUMAN display names for the system scalars (W8) — the operator reads these,
    /// not code identifiers. The raw contract names stay in the bus specs.
    /// </summary>
    private static readonly string[] ScalarNames =
    {
        "COM port active",
        "Case state",
        "COM/LAN port state",
        "Heartbeat",
    };

    // Semantic decode of the header STATE block — tap-exposed values ONLY
    // (specs/iCemAddIn/Э2_contracts.csv). Invented states FORBIDDEN: a value
    // outside the vendor enum renders as "invalid", never a fabricated label.
    private static readonly string[] CaseStateLabels =
    {
        "Unknown / no active case",  // 0
        "NotStarted",                // 1
        "InProgress",                // 2
        "Completed",                 // 3
        "Suspended",                 // 4
        "SimulationRunning",         // 5
    };

    private static readonly Dictionary<int, string> CaseLanStateLabels = new()
    {
        [0] = "None",
        [1] = "Selected",
        [2] = "Streaming",
        [4] = "Recording",
        [8] = "LostConnection",
        [16] = "Pausing",
    };

    private static string? ScalarLabel(int reg, int value) => reg switch
    {
        0 => value == 0 ? "no COM device" : "COM active",
        1 => value >= 0 && value < CaseStateLabels.Length ? CaseStateLabels[value] : "invalid",
        2 => CaseLanStateLabels.TryGetValue(value, out var label) ? label : "invalid",
        _ => null,
    };

    /// <summary>Zone geometry matching the v5 contract (4..199 / 200..399 / 400..599).</summary>
    private static readonly (string Key, int Base, int Len, string Label)[] Zones =
    {
        ("com", BusLayout.ComStreamBaseReg, BusLayout.ComStreamRegCount, "COM"),
        ("lan", BusLayout.LanStreamCrioBaseReg, BusLayout.LanStreamCrioRegCount, "LAN"),
        ("raw", BusLayout.RawComLineBaseReg, BusLayout.RawComLineRegCount, "RAW"),
    };

    public static IEndpointRouteBuilder MapRegisterMirror(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/regs", (HttpContext ctx) =>
        {
            var reader = ctx.RequestServices.GetRequiredService<BusReader>();
            var registry = ctx.RequestServices.GetService<UnifiedVariableRegistry>();
            var snap = reader.LastSnapshot ?? reader.Snapshot();
            if (snap is null)
                return Results.Json(new { error = "bus not available" }, statusCode: 503);

            var zoneLiveness = new Dictionary<string, object>
            {
                ["com"] = Liveness(snap.ComMain),
                ["lan"] = Liveness(snap.Lan),
                ["raw"] = Liveness(snap.RawReserve),
            };

            var scalars = new List<object>(SystemRegCount);
            for (int r = 0; r < SystemRegCount; r++)
            {
                int v = r < snap.Words.Count ? snap.Words[r] : 0;
                scalars.Add(new
                {
                    reg = r,
                    name = ScalarNames[r],
                    value = v,
                    label = ScalarLabel(r, v),
                });
            }

            var namesByAddr = BuildNameLookup(registry);
            var zones = new Dictionary<string, object>();
            foreach (var (key, baseReg, len, label) in Zones)
            {
                zones[key] = BuildZone(snap, baseReg, len, label, key, namesByAddr);
            }

            return Results.Ok(new
            {
                system = new { scalars, liveness = (object?)null },
                zones,
                liveness = zoneLiveness,
                updatedAtUtc = snap.Stale ? (DateTime?)null : DateTime.UtcNow,
                stale = snap.Stale,
            });
        });

        // RESTORED 2026-08-27 (corrected decree): /api/regs/history — the
        // addr-keyed ring query (Э2 web_endpoint_regs_history). Multi-addr
        // query «?addr=4&addr=200»; each addr must be EVEN and inside 4..599
        // (odd / out of range / missing ⇒ 400); RingHistory absent from DI
        // (web_on=0) ⇒ 503 «history disabled»; otherwise 200 with an array of
        // {ts,v} per requested addr, oldest→newest (ring append order).
        app.MapGet("/api/regs/history", (HttpContext ctx) =>
        {
            var history = ctx.RequestServices.GetService<RingHistory>();
            if (history is null)
                return Results.Json(new { error = "history disabled (web layer is off)" }, statusCode: 503);

            var query = ctx.Request.Query["addr"].ToArray();
            if (query.Length == 0)
                return Results.Json(new { error = "missing addr — expected ?addr=N (repeatable)" }, statusCode: 400);

            var addrs = new List<int>(query.Length);
            foreach (var raw in query)
            {
                if (!int.TryParse(raw, out var addr)
                    || addr < PairStart
                    || addr >= PairEndExclusive
                    || addr % 2 != 0)
                {
                    return Results.Json(new
                    {
                        error = $"addr must be an even integer in [{PairStart}..{PairEndExclusive - 1}]: '{raw}'",
                    }, statusCode: 400);
                }
                addrs.Add(addr);
            }

            var result = new Dictionary<string, object>();
            foreach (var addr in addrs)
            {
                result[addr.ToString(CultureInfo.InvariantCulture)] = history.QueryAddr(addr)
                    .Select(s => new
                    {
                        ts = s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                        v = s.Value,
                    })
                    .ToList();
            }
            return Results.Ok(result);
        });

        return app;
    }

    /// <summary>
    /// Zone liveness for the /api/regs surface. AGE-GATED (2026-08-28 dishonest-
    /// surface fix): the raw activeFlag is the writer's OWN claim — it can stay 1
    /// forever after a writer death, and the web zones-table badge renders
    /// live/stale straight from this object (main.js), so an ungated flag would
    /// show «live» on a dead bus. active = the SAME verdict /api/status/summary
    /// lamps get (BusHealthMonitor → BusZoneInfo.IsActive: Active && age &lt; 5 s —
    /// ONE predicate + ONE constant, BusZoneInfo.MaxZoneAgeMs, no per-surface
    /// drift). producerFlag = the raw writer claim, exposed separately as a
    /// diagnostic field (additive — nothing reads it as liveness).
    /// </summary>
    private static object Liveness(BusZoneInfo info) => new
    {
        active = info.IsActive,
        producerFlag = info.Active,
        ageMs = info.AgeMs(),
        writeCount = info.WriteCount,
    };

    private static Dictionary<int, string> BuildNameLookup(UnifiedVariableRegistry? registry)
    {
        var dict = new Dictionary<int, string>();
        if (registry is null)
            return dict;

        foreach (var family in new[] { "flecs", "lan" })
        {
            foreach (var e in registry.GetRegisterEntriesV2(family))
            {
                if (e.Address >= PairStart && e.Address < PairEndExclusive)
                    dict[e.Address] = e.CanonicalName;
            }
        }
        return dict;
    }

    private static object BuildZone(BusSnapshot snap, int baseReg, int len, string label,
        string zoneKey, Dictionary<int, string> namesByAddr)
    {
        var pairs = new List<object>(len / RegisterMapV2.WordsPerFloat);
        int idx = 0;
        for (int addr = baseReg; addr < baseReg + len && addr + 1 < snap.Words.Count; addr += RegisterMapV2.WordsPerFloat, idx++)
        {
            var (value, kind) = DecodePair(snap.Words, addr);
            string? name = zoneKey switch
            {
                "raw" => $"field {idx}",
                _ => namesByAddr.TryGetValue(addr, out var n) ? n : null,
            };

            pairs.Add(new
            {
                idx,
                addr,
                addrEnd = addr + 1,
                name,
                value,
                kind,
            });
        }

        return new
        {
            label,
            baseReg,
            len,
            pairs,
        };
    }

    /// <summary>
    /// Decode a CDAB pair. Special cases: 4..5 = int32 raw unix seconds; 6..7
    /// (com idx 1) = UnitNum float. Everything else is float via CdabCodec.
    /// </summary>
    private static (object Value, string Kind) DecodePair(IReadOnlyList<ushort> words, int addr)
    {
        if (addr == 4 && addr + 1 < words.Count)
        {
            // int32 raw bits: low word @ addr, high word @ addr+1 (same layout as CDAB float)
            uint bits = (uint)words[addr] | ((uint)words[addr + 1] << 16);
            int signed = unchecked((int)bits);
            var dt = DateTimeOffset.FromUnixTimeSeconds(signed).UtcDateTime;
            return (dt.ToString("yyyy-MM-ddTHH:mm:ssZ"), "unix");
        }

        float f = CdabCodec.Combine(words[addr], words[addr + 1]);
        return (f, "float");
    }
}
