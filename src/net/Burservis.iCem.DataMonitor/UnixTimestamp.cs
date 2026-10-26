using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// com_stream idx0 timestamp law (contract v5.1, CHANGE 1 user decree
    /// 2026-08-18 — Э2b rows T01/T02). The flecs 'Time &amp; Date' value arrives
    /// NUMERIC as .NET UTC ticks (live evidence 2026-08-18: float(6.39e17) on
    /// regs 4..5 — the float path is RETIRED for idx0). The bus now carries
    /// UnixTimestampSeconds as RAW int32 bits in the CDAB word order (low word
    /// @ reg 4, high word @ reg 5, BE bytes per word): an int32/uint32 read of
    /// the pair in the consumer's standard word order yields unix seconds.
    /// Reading the pair as float32 shows garbage — ACCEPTED BY DECREE: the
    /// Modbus consumer owns read formatting.
    /// TOTAL function: any numeric input converts (no clamp, no throw — dumb
    /// translator); ticks at the epoch ⇒ 0; pre-epoch ticks ⇒ negative int32
    /// bits.
    /// ASYMMETRY (documented, Э2b row T03): idx1 'Unit Num' stays float32
    /// CDAB — small integer, float-exact, consumer reads float; the int
    /// defect was magnitude-driven (ticks ≈ 6.39e17) and existed ONLY at idx0.
    /// </summary>
    internal static class UnixTimestamp
    {
        /// <summary>DateTime ticks at 1970-01-01T00:00:00Z.</summary>
        public const long EpochTicks = 621355968000000000L;

        /// <summary>.NET ticks per second (100 ns units).</summary>
        public const long TicksPerSecond = 10000000L;

        /// <summary>.NET UTC ticks (as delivered by the poll path, double) →
        /// Unix timestamp seconds (int32, truncating sub-second resolution).</summary>
        public static int TicksToUnixSeconds(double ticks)
        {
            return (int)(((long)ticks - EpochTicks) / TicksPerSecond);
        }
    }
}
