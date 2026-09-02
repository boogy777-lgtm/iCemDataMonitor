using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Zone staleness decision law (vendor-free test-seam — extracted from
    /// DataMonitorModule.UpdateStaleness as a PURE MOVE, behavior identical;
    /// bus register map v5 A4/A5 + Э2b rows B02/B09/B13/B17..B20):
    /// drop a zone's activeFlag ⇔ the zone WAS active ∧ its last bus write is
    /// BEYOND the idle timeout. Boundary: silence exactly at the timeout is NOT
    /// beyond (matches ComFeedLatch.IsSilentBeyond). Flap law: silence shorter
    /// than the timeout NEVER drops the flag (no bounce). ¬active ⇒ ¬drop (no
    /// repeated SetZoneActive posts — no bounce artifacts). STALE-HOLD: the
    /// decision governs the header flag ONLY — body cells are never zeroed.
    /// </summary>
    internal static class ZoneStaleness
    {
        /// <summary>True ⇔ the caller should post SetZoneActive(zone, false).</summary>
        public static bool ShouldDrop(bool zoneActive, DateTime lastWriteUtc, DateTime nowUtc, TimeSpan idleTimeout)
        {
            return zoneActive && (nowUtc - lastWriteUtc) > idleTimeout;
        }
    }
}
